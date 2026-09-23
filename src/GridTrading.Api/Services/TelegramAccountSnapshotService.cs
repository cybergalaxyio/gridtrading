using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Strategies.Grid.Configuration;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class TelegramAccountSnapshotService(
    TradingDbContext db, HyperliquidMarketDataClient market,
    ILogger<TelegramAccountSnapshotService> logger)
{
    public async Task<string> ForCycleAsync(string? cycleId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cycleId)) return Unavailable("no account/symbol associated with this alert");
        try
        {
            var cycle = await db.Cycles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cycleId, ct);
            if (cycle is null) return Unavailable("cycle unavailable");
            var config = GridConfigurationCodec.ReadFrozen(cycle.FrozenConfigurationJson);
            return await ForAccountAsync(cycle.ExecutionAccountId, config?.Symbol, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Telegram account context unavailable for cycle {CycleId}: {ErrorType}",
                cycleId, ex.GetType().Name);
            return Unavailable("cycle account/symbol unavailable");
        }
    }

    public Task<string> ForOrderAsync(OrderPlacementNotificationEntity notification, CancellationToken ct) =>
        ForAccountAsync(notification.ExecutionAccountId ?? LegacyField(notification.Message, "Account"),
            notification.Symbol ?? LegacyField(notification.Message, "Symbol"), ct);

    private async Task<string> ForAccountAsync(string? accountId, string? symbol, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(symbol))
            return Unavailable("no account/symbol associated with this alert");
        if (accountId == ExecutionEnvironmentIds.PaperAccount)
            return Unavailable("paper account has no exchange account snapshot");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var state = await market.GetAccountStateAsync(accountId, symbol, timeout.Token);
            var coin = HyperliquidTradingClient.ToCoin(symbol);
            var side = state.NetPosition > 0m ? "LONG" : state.NetPosition < 0m ? "SHORT" : "FLAT";
            var leverage = state.AccountLeverage.HasValue
                ? FormattableString.Invariant($"{state.AccountLeverage.Value:0.####}x") : "Unavailable (equity or exposure unavailable)";
            return FormattableString.Invariant($"""
                Account Snapshot
                Account: {state.AccountId}
                Current Account Position ({coin}): {side} {Math.Abs(state.NetPosition):G29} {coin}
                Unrealized PNL ({coin}): {state.UnrealizedPnl:0.########} USDC
                Unified Account Leverage: {leverage}
                Leverage Scope: Native USDC perpetuals
                Account Available Balance: {state.AvailableBalance:0.########} USDC
                As of: {state.AsOf.UtcDateTime:yyyy-MM-dd HH:mm:ss 'UTC'}
                """);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Telegram account snapshot unavailable for {AccountId}/{Symbol}: {ErrorType}",
                accountId, symbol, ex.GetType().Name);
            return Unavailable("account snapshot could not be refreshed");
        }
    }

    private static string Unavailable(string reason) => $"""
        Account Snapshot: Unavailable ({reason})
        Current Account Position: Unavailable
        Unrealized PNL: Unavailable
        Unified Account Leverage: Unavailable
        Account Available Balance: Unavailable
        """;

    // Compatibility for queued notifications written before structured context existed.
    private static string? LegacyField(string message, string name) => message.Split('\n')
        .FirstOrDefault(line => line.StartsWith(name + ": ", StringComparison.Ordinal))?[(name.Length + 2)..].Trim();
}
