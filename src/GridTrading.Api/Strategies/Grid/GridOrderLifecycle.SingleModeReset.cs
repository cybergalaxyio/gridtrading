using GridTrading.Api.Data;
using GridTrading.Api.Services;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed partial class GridOrderLifecycle
{
    private async Task RestoreFirstSingleModeEntryAsync(CycleEntity cycle, GridConfiguration config, CancellationToken ct)
    {
        // Reconcile even an empty book: a delayed fill must not start a new round.
        var snapshot = await SynchronizeMoveAsync(cycle, config, ct);
        if (cycle.State != "RUNNING" || cycle.RiskPaused || cycle.OperatorPaused ||
            HasUnappliedSnapshotFills(snapshot) || await HasUnappliedEntryFillsAsync(cycle, ct) ||
            !await IsFlatForMoveAsync(cycle, ct) || (await ActiveOrdersAsync(cycle.Id, ct)).Count != 0 ||
            await db.Orders.AnyAsync(x => x.CycleId == cycle.Id &&
                snapshot.OpenOrdersByClientId.Keys.Contains(x.ClientOrderId), ct)) return;
        var side = config.GridMode == GridMode.SellOnly ? OrderSide.Sell : OrderSide.Buy;
        if (!await CanPlaceNewEntryOrderAsync(cycle, config, side, ct)) return;
        var quote = await environments.Adapter(cycle.ExecutionEnvironmentId).GetQuoteAsync(Selection(cycle), config.Symbol, ct);
        if (!FreshEntryQuote(config, quote)) return;
        var first = cycle.EffectivePlan.Levels.Single(x => x.Side == side && x.LevelIndex == 0);
        var completedRound = await db.VirtualLots.AnyAsync(x => x.CycleId == cycle.Id && x.Status == "CLOSED", ct);
        // Before the first fill, keep the selected startup grid (including Manual).
        // After closing exposure, anchor the new first level to the current book.
        var target = completedRound ? SingleModeEntryRules.InitialEntryPrice(config, quote.Bid, quote.Ask) : first.EntryPrice;
        if (target is null || !ValidMovedPlan(cycle, config, first, target.Value)) return;
        await CommitSingleModeEntryAsync(cycle, config, first, target.Value,
            completedRound ? "flat reset without a working entry" : "restore first entry", ct);
    }

    private async Task CommitSingleModeEntryAsync(CycleEntity cycle, GridConfiguration config,
        GridLevel sourceLevel, decimal target, string reason, CancellationToken ct)
    {
        var delta = target - sourceLevel.EntryPrice;
        var shifted = SingleModeEntryRules.ShiftPlan(cycle.EffectivePlan, delta);
        var level = shifted.Levels.Single(x => x.Side == sourceLevel.Side && x.LevelIndex == 0);
        var quantity = GridMath.AllowedOrderQuantity(level.Side, level.PlannedQuantity, 0m, [],
            config.MaxNetLot, TradingService.RulesFor(config));
        if (!ValidMoveQuantity(cycle, config, level.Side, level.EntryPrice, quantity)) return;
        var replacement = CreateEntry(cycle, config.Symbol, level, quantity);
        replacement.CreatedAt = replacement.UpdatedAt = clock.GetUtcNow();
        cycle.EntryGridPriceOffset += delta;
        cycle.EntryGridMovePendingOrderId = null;
        db.Orders.Add(replacement);
        db.AuditLogs.Add(new AuditEntity
        {
            ResourceId = replacement.Id, Action = "ENTRY_GRID_MOVE_PLANNED", Actor = "strategy",
            Detail = $"Cycle {cycle.Id}, order {replacement.Id}: {level.Side} {reason} -> level 0 at {target}, quantity {quantity}; grid offset {cycle.EntryGridPriceOffset}.",
            OccurredAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(ct); // Grid reset and replacement intent commit atomically.
        await PlaceNewEntryOrdersAsync(cycle, config, [replacement], ct);
    }
}
