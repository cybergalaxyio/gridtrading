using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Exchanges.Hyperliquid;
using GridTrading.Api.Hubs;
using GridTrading.Api.Strategies.Grid;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed record HyperliquidUserFillsMessage(string User, bool IsSnapshot, IReadOnlyList<JsonElement> Fills);
public sealed record HyperliquidMidPricesMessage(IReadOnlyDictionary<string, string> Mids);
public sealed record HyperliquidOrderUpdatesMessage(IReadOnlyList<JsonElement> Updates);

public static class HyperliquidWebSocketProtocol
{
    public static byte[] SubscribeUserFills(string user) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        method = "subscribe",
        subscription = new { type = "userFills", user, aggregateByTime = false }
    });

    public static byte[] SubscribeOrderUpdates(string user) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        method = "subscribe",
        subscription = new { type = "orderUpdates", user }
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

    public static bool TryReadOrderUpdates(JsonElement message, out HyperliquidOrderUpdatesMessage? result)
    {
        result = null;
        if (!message.TryGetProperty("channel", out var channel) || channel.GetString() != "orderUpdates" ||
            !message.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return false;

        var updates = data.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("order", out _) && x.TryGetProperty("status", out _))
            .Select(x => x.Clone()).ToArray();
        result = new HyperliquidOrderUpdatesMessage(updates);
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
                await RunConnections(accounts, stoppingToken);
                retrySeconds = 1;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Hyperliquid WebSocket disconnected; reconnecting in {DelaySeconds}s.", retrySeconds);
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

    private async Task RunConnections(IReadOnlyList<SubscriptionAccount> accounts, CancellationToken ct)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var connections = new List<Task> { RunConnection(null, connectionCts.Token) };
        connections.AddRange(accounts.Select(account => RunConnection(account, connectionCts.Token)));

        var completed = await Task.WhenAny(connections);
        connectionCts.Cancel();
        try { await Task.WhenAll(connections); }
        catch (OperationCanceledException) when (connectionCts.IsCancellationRequested) { }

        await completed;
    }

    private async Task RunConnection(SubscriptionAccount? account, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(WebSocketEndpoint(), ct);

        if (account is null)
            await Send(socket, HyperliquidWebSocketProtocol.SubscribeAllMids(), ct);
        else
        {
            await Send(socket, HyperliquidWebSocketProtocol.SubscribeUserFills(account.User), ct);
            await Send(socket, HyperliquidWebSocketProtocol.SubscribeOrderUpdates(account.User), ct);
        }

        logger.LogInformation("Hyperliquid {Feed} WebSocket connected{Account}.",
            account is null ? "market" : "userFills/orderUpdates",
            account is null ? "" : $" for account {account.AccountId}");
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var receiveTask = ReceiveLoop(socket, account, connectionCts.Token);
        var heartbeatTask = HeartbeatLoop(socket, connectionCts.Token);
        var completed = await Task.WhenAny(receiveTask, heartbeatTask);
        connectionCts.Cancel();
        socket.Abort();

        try { await Task.WhenAll(receiveTask, heartbeatTask); }
        catch (OperationCanceledException) when (connectionCts.IsCancellationRequested) { }

        await completed;
        if (!ct.IsCancellationRequested)
            throw new WebSocketException("Hyperliquid WebSocket closed.");
    }

    private async Task ReceiveLoop(ClientWebSocket socket, SubscriptionAccount? account, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var message = await ReceiveText(socket, ct);
            if (message is null) return;
            try
            {
                using var document = JsonDocument.Parse(message);
                if (account is not null &&
                    HyperliquidWebSocketProtocol.TryReadUserFills(document.RootElement, out var update) && update is not null)
                {
                    if (string.Equals(account.User, update.User, StringComparison.OrdinalIgnoreCase))
                        await ProcessFills(account.AccountId, update, ct);
                }
                else if (account is not null && HyperliquidWebSocketProtocol.TryReadOrderUpdates(
                    document.RootElement, out var orders) && orders is not null)
                {
                    await ProcessOrderUpdates(account.AccountId, orders, ct);
                }
                else if (account is null && HyperliquidWebSocketProtocol.TryReadAllMids(
                    document.RootElement, out var mids) && mids is not null)
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
        var adapter = scope.ServiceProvider.GetRequiredService<HyperliquidExecutionAdapter>();
        var lifecycle = scope.ServiceProvider.GetRequiredService<GridOrderLifecycle>();
        var normalized = await adapter.NormalizeFillsAsync(accountId, update.Fills, ct);
        var processed = await lifecycle.ProcessFillsAsync(accountId, normalized, ct);
        if (processed > 0)
            logger.LogInformation("Processed {FillCount} Hyperliquid WebSocket fill(s) for account {AccountId}{Snapshot}.",
                processed, accountId, update.IsSnapshot ? " from snapshot" : "");
    }

    private async Task ProcessOrderUpdates(string accountId, HyperliquidOrderUpdatesMessage update, CancellationToken ct)
    {
        if (update.Updates.Count == 0) return;
        using var scope = scopeFactory.CreateScope();
        var adapter = scope.ServiceProvider.GetRequiredService<HyperliquidExecutionAdapter>();
        var lifecycle = scope.ServiceProvider.GetRequiredService<GridOrderLifecycle>();
        var normalized = await adapter.NormalizeOrderUpdatesAsync(accountId, update.Updates, ct);
        var processed = await lifecycle.ProcessOrderUpdatesAsync(accountId, normalized, ct);
        if (processed > 0)
            logger.LogInformation("Processed {OrderUpdateCount} Hyperliquid WebSocket order update(s) for account {AccountId}.",
                processed, accountId);
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
