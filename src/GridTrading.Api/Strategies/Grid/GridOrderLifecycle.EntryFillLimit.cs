using GridTrading.Api.Data;
using GridTrading.Api.Services;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed partial class GridOrderLifecycle
{
    // The last check sits immediately before each adapter submission. Maintenance
    // also enforces the limit on resting entries after fills and reconciliation.
    public async Task PlaceNewEntryOrdersAsync(CycleEntity cycle, GridConfiguration config,
        IEnumerable<OrderEntity> orders, CancellationToken ct)
    {
        if (cycle.EntryGridMovePendingOrderId is not null) return;
        var selection = Selection(cycle);
        var adapter = environments.Adapter(selection.EnvironmentId);
        foreach (var order in orders.ToArray())
        {
            if (order.Kind != "ENTRY" || order.Status != "PENDING_EXCHANGE") continue;
            var side = Enum.Parse<OrderSide>(order.Side, true);
            if ((config.GridMode == GridMode.BuyOnly && side == OrderSide.Sell) ||
                (config.GridMode == GridMode.SellOnly && side == OrderSide.Buy)) continue;
            if (!await CanPlaceNewEntryOrderAsync(cycle, config, side, ct)) continue;
            // An unsent deeper entry from the previous holding round must be
            // reset by maintenance before it can reach the venue after going flat.
            if (config.GridMode != GridMode.TwoWay && order.GridLevel != 0 &&
                await IsFlatForMoveAsync(cycle, ct)) continue;
            // Recheck immediately before each submission, including persisted intents
            // recovered after a restart. TP placement does not use this entry-only gate.
            var quote = await adapter.GetQuoteAsync(selection, config.Symbol, ct);
            if (!FreshEntryQuote(config, quote)) continue;
            await adapter.PlaceOrdersAsync(selection, config, [order], ct);
            await ConfirmMoveAuditsAsync(cycle, ct);
        }
    }

    // Callers hold the existing account gate. This query deliberately reads only
    // order history: fragmented executions never become separate counted orders.
    public async Task<bool> CanPlaceNewEntryOrderAsync(CycleEntity cycle, GridConfiguration config,
        OrderSide side, CancellationToken ct)
    {
        var status = await ReadEntryFillLimitAsync(cycle, config, side, ct);
        if (status is null) return true;
        var sideName = status.Side;
        if (status.Reason == "HISTORY_NOT_READY")
        {
            var code = $"ENTRY_FILL_HISTORY_NOT_READY_{sideName}";
            if (!await db.RiskAlerts.AnyAsync(x => x.CycleId == cycle.Id && x.Code == code && !x.Acknowledged, ct))
            {
                db.RiskAlerts.Add(new RiskAlertEntity
                {
                    Id = Ids.New("alert"), CycleId = cycle.Id, Severity = "WARNING", Code = code,
                    Message = $"{sideName} Entry deferred: completed order history has no verified FilledAt. " +
                        "Reconcile order history before placing new entries on this side.",
                    CreatedAt = clock.GetUtcNow()
                });
                await db.SaveChangesAsync(ct);
            }
            return false;
        }
        await CancelEntriesAtFillLimitAsync(cycle, sideName, ct);
        return false;
    }

    // Shared with the dashboard: observing a hold never cancels or submits orders.
    public async Task<EntryHold?> ReadEntryFillLimitAsync(CycleEntity cycle, GridConfiguration config,
        OrderSide side, CancellationToken ct)
    {
        if (!config.EntryFillLimitEnabled) return null;
        if (config.EntryFillWindowMinutes <= 0 || config.MaxEntryFillsPerSide <= 0)
            throw new TradingProblemException(422, "ENTRY_FILL_LIMIT_INVALID",
                "Lookback Window and Max Filled Entries per Side must be positive integers.");

        var now = clock.GetUtcNow();
        var window = TimeSpan.FromMinutes(config.EntryFillWindowMinutes);
        var cutoff = now - DateTimeOffset.MinValue < window ? DateTimeOffset.MinValue : now - window;
        var sideName = side.ToString().ToUpperInvariant();
        var market = HyperliquidTradingClient.ToCoin(config.Symbol);
        var candidates = await (from order in db.Orders.AsNoTracking()
                                join historyCycle in db.Cycles.AsNoTracking() on order.CycleId equals historyCycle.Id
                                where historyCycle.StrategyId == cycle.StrategyId &&
                                      historyCycle.ExecutionEnvironmentId == cycle.ExecutionEnvironmentId &&
                                      historyCycle.ExecutionAccountId == cycle.ExecutionAccountId &&
                                      order.Kind == "ENTRY" && order.Side == sideName &&
                                      (order.FilledAt == null || (order.FilledAt > cutoff && order.FilledAt <= now))
                                select new { order.Symbol, order.Status, order.Quantity, order.FilledQuantity, order.FilledAt })
            .ToListAsync(ct);
        var history = candidates.Where(x => HyperliquidTradingClient.ToCoin(x.Symbol) == market).ToArray();
        // Decimal comparisons stay in memory: SQLite stores quantities as lossless text.
        if (history.Any(x => x.FilledAt == null &&
            (x.Status == "FILLED" || (x.Quantity > 0m && x.FilledQuantity >= x.Quantity))))
            return new(sideName, "HISTORY_NOT_READY");
        var fills = history.Where(x => x.FilledAt.HasValue).Select(x => x.FilledAt!.Value).Order().ToArray();
        if (fills.Length < config.MaxEntryFillsPerSide) return null;
        // Enough fills must expire to leave fewer than the maximum, even if already over it.
        var resumeAt = fills[fills.Length - config.MaxEntryFillsPerSide] + window;
        return new(sideName, "FILL_LIMIT", resumeAt, fills.Length, config.MaxEntryFillsPerSide);
    }

    private async Task CancelEntriesAtFillLimitAsync(CycleEntity cycle, string sideName, CancellationToken ct)
    {
        var entries = (await ActiveOrdersAsync(cycle.Id, ct))
            .Where(x => x.Kind == "ENTRY" && x.Side == sideName && x.Quantity > x.FilledQuantity).ToArray();
        // PENDING_EXCHANGE is an unsent intent; a transport-uncertain send is UNKNOWN.
        foreach (var entry in entries.Where(x => x.Status == "PENDING_EXCHANGE"))
        {
            entry.Status = "CANCELLED";
            entry.UpdatedAt = clock.GetUtcNow();
        }
        await db.SaveChangesAsync(ct);
        var working = entries.Where(x => x.Status != "CANCELLED").ToArray();
        if (working.Length == 0) return;
        try
        {
            var selection = Selection(cycle);
            await environments.Adapter(selection.EnvironmentId).CancelOrdersAsync(selection, working, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested &&
            error is TradingProblemException or HttpRequestException or TaskCanceledException)
        {
            var code = $"ENTRY_FILL_LIMIT_CANCEL_PENDING_{sideName}";
            if (!await db.RiskAlerts.AnyAsync(x => x.CycleId == cycle.Id && x.Code == code && !x.Acknowledged, ct))
            {
                db.RiskAlerts.Add(new RiskAlertEntity
                {
                    Id = Ids.New("alert"), CycleId = cycle.Id, Severity = "WARNING", Code = code,
                    Message = $"{sideName} Entry fill limit reached. Entry cancellation will be retried on Sync. {error.Message}",
                    CreatedAt = clock.GetUtcNow()
                });
                await db.SaveChangesAsync(ct);
            }
        }
    }
}
