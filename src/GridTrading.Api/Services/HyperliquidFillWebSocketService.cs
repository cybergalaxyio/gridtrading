using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed record HyperliquidUserFillsMessage(string User, bool IsSnapshot, IReadOnlyList<JsonElement> Fills);
public sealed record HyperliquidMidPricesMessage(IReadOnlyDictionary<string, string> Mids);

public static class HyperliquidWebSocketProtocol
{
    public static byte[] SubscribeUserFills(string user) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        method = "subscribe",
        subscription = new { type = "userFills", user, aggregateByTime = false }
    });

    public static byte[] SubscribeAllMids() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        method = "subscribe",
        subscription = new { type = "allMids" }
    });

    public static byte[] Ping() => JsonSerializer.SerializeToUtf8Bytes(new { method = "ping" });

    public static bool TryReadUserFills(JsonElement message, out HyperliquidUserFillsMessage? result)
    {
        result = null;
        if (!message.TryGetProperty("channel", out var channel) || channel.GetString() != "userFills" ||
            !message.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.String ||
            !data.TryGetProperty("fills", out var fills) || fills.ValueKind != JsonValueKind.Array)
            return false;

        result = new HyperliquidUserFillsMessage(
            user.GetString()!,
            data.TryGetProperty("isSnapshot", out var snapshot) && snapshot.ValueKind == JsonValueKind.True,
            fills.EnumerateArray().Select(x => x.Clone()).ToArray());
        return true;
    }

    public static bool TryReadAllMids(JsonElement message, out HyperliquidMidPricesMessage? result)
    {
        result = null;
        if (!message.TryGetProperty("channel", out var channel) || channel.GetString() != "allMids" ||
            !message.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("mids", out var mids) || mids.ValueKind != JsonValueKind.Object)
            return false;

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in mids.EnumerateObject())
        {
            var value = item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString() : item.Value.ToString();
            if (!string.IsNullOrWhiteSpace(value)) values[item.Name] = value!;
        }
        result = new HyperliquidMidPricesMessage(values);
        return true;
    }
}

public sealed class HyperliquidFillWebSocketService(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    IHubContext<TradingHub> hub,
    HyperliquidMarketSubscriptionRegistry marketSubscriptions,
    ILogger<HyperliquidFillWebSocketService> logger) : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private const int MaximumMessageBytes = 4 * 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retrySeconds = 1;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var accounts = await LoadAccounts(stoppingToken);
                await RunConnection(accounts, stoppingToken);
                retrySeconds = 1;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Hyperliquid market/userFills WebSocket disconnected; reconnecting in {DelaySeconds}s.", retrySeconds);
                await Task.Delay(TimeSpan.FromSeconds(retrySeconds), stoppingToken);
                retrySeconds = Math.Min(30, retrySeconds * 2);
            }
        }
    }

    private async Task<IReadOnlyList<SubscriptionAccount>> LoadAccounts(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        return await db.HyperliquidAccounts
            .Where(x => x.Enabled && x.Environment == "TESTNET")
            .Select(x => new SubscriptionAccount(x.Id, x.AccountAddress))
            .ToListAsync(ct);
    }

    private async Task RunConnection(IReadOnlyList<SubscriptionAccount> accounts, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(WebSocketEndpoint(), ct);

        await Send(socket, HyperliquidWebSocketProtocol.SubscribeAllMids(), ct);
        foreach (var address in accounts.Select(x => x.User).Distinct(StringComparer.OrdinalIgnoreCase))
            await Send(socket, HyperliquidWebSocketProtocol.SubscribeUserFills(address), ct);

        logger.LogInformation("Hyperliquid market/userFills WebSocket connected for {AccountCount} account(s).", accounts.Count);
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiveTask = ReceiveLoop(socket, accounts, connectionCts.Token);
        var heartbeatTask = HeartbeatLoop(socket, connectionCts.Token);
        var completed = await Task.WhenAny(receiveTask, heartbeatTask);
        connectionCts.Cancel();
        socket.Abort();

        try { await Task.WhenAll(receiveTask, heartbeatTask); }
        catch (OperationCanceledException) when (connectionCts.IsCancellationRequested) { }

        await completed;
        if (!ct.IsCancellationRequested)
            throw new WebSocketException("Hyperliquid market/userFills WebSocket closed.");
    }

    private async Task ReceiveLoop(ClientWebSocket socket, IReadOnlyList<SubscriptionAccount> accounts, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var message = await ReceiveText(socket, ct);
            if (message is null) return;
            try
            {
                using var document = JsonDocument.Parse(message);
                if (HyperliquidWebSocketProtocol.TryReadUserFills(document.RootElement, out var update) && update is not null)
                {
                    var accountIds = accounts
                        .Where(x => string.Equals(x.User, update.User, StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.AccountId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    foreach (var accountId in accountIds)
                        await ProcessFills(accountId, update, ct);
                }
                else if (HyperliquidWebSocketProtocol.TryReadAllMids(document.RootElement, out var mids) && mids is not null)
                {
                    await BroadcastSelectedMids(mids, ct);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning("Ignoring malformed Hyperliquid WebSocket message: {ErrorType}.", ex.GetType().Name);
            }
        }
    }

    private async Task BroadcastSelectedMids(HyperliquidMidPricesMessage update, CancellationToken ct)
    {
        var asOf = DateTimeOffset.UtcNow;
        foreach (var symbol in marketSubscriptions.ActiveSymbols())
        {
            if (!update.Mids.TryGetValue(symbol, out var mid)) continue;
            await hub.Clients.Group(HyperliquidMarketGroups.Group(symbol))
                .SendAsync("HyperliquidMidPriceUpdated", new { symbol, mid, asOf }, ct);
        }
    }

    private async Task ProcessFills(string accountId, HyperliquidUserFillsMessage update, CancellationToken ct)
    {
        if (update.Fills.Count == 0) return;
        using var scope = scopeFactory.CreateScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<HyperliquidCycleCoordinator>();
        var processed = await coordinator.ProcessWebSocketFillsAsync(accountId, update.Fills, ct);
        if (processed > 0)
            logger.LogInformation("Processed {FillCount} Hyperliquid WebSocket fill(s) for account {AccountId}{Snapshot}.",
                processed, accountId, update.IsSnapshot ? " from snapshot" : "");
    }

    private static async Task HeartbeatLoop(ClientWebSocket socket, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(ct))
            await Send(socket, HyperliquidWebSocketProtocol.Ping(), ct);
    }

    private static async Task<string?> ReceiveText(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text)
            {
                if (result.EndOfMessage) return string.Empty;
                continue;
            }

            await stream.WriteAsync(buffer.AsMemory(0, result.Count), ct);
            if (stream.Length > MaximumMessageBytes)
                throw new InvalidDataException("Hyperliquid WebSocket message exceeded the configured size limit.");
            if (result.EndOfMessage) return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private static Task Send(ClientWebSocket socket, byte[] payload, CancellationToken ct) =>
        socket.SendAsync(payload, WebSocketMessageType.Text, true, ct);

    private Uri WebSocketEndpoint()
    {
        var configured = configuration["Hyperliquid:WebSocketUrl"] ?? "wss://api.hyperliquid-testnet.xyz/ws";
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "wss" ||
            endpoint.Host != "api.hyperliquid-testnet.xyz" || endpoint.AbsolutePath != "/ws" ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new InvalidOperationException("Hyperliquid WebSocket is locked to the official Testnet wss endpoint.");
        return endpoint;
    }

    private sealed record SubscriptionAccount(string AccountId, string User);
}
