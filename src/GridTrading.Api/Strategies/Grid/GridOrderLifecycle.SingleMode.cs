using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Services;
using GridTrading.Domain;
using GridTrading.Domain.Strategies.Grid;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed partial class GridOrderLifecycle
{
    // Called under the account gate. True means this pass must not run ordinary
    // entry selection (in particular, a rebound must not chase an unfilled entry).
    private async Task<bool> MaintainSingleModeMoveAsync(CycleEntity cycle, GridConfiguration config,
        ExecutionQuote quote, CancellationToken ct)
    {
        if (cycle.EntryGridMovePendingOrderId is not null)
        {
            await CompleteSingleModeMoveAsync(cycle, config, ct);
            return true;
        }
        var side = config.GridMode == GridMode.SellOnly ? OrderSide.Sell : OrderSide.Buy;
        if (!await CanPlaceNewEntryOrderAsync(cycle, config, side, ct)) return true;
        var active = await ActiveOrdersAsync(cycle.Id, ct);
        var entries = active.Where(x => x.Kind == "ENTRY").ToArray();
        if (active.Any(x => x.Status == "UNKNOWN" || (x.Status == "PENDING_EXCHANGE" &&
            (x.Kind != "ENTRY" || x.GridLevel == 0 || x.ExchangeOrderId != "pending")))) return true;
        if (await HasUnappliedEntryFillsAsync(cycle, ct)) return true;
        if (!await IsFlatForMoveAsync(cycle, ct)) return entries.Length > 0;
        if (active.Any(x => x.Kind != "ENTRY")) return true;
        if (entries.Length == 0)
        {
            await RestoreFirstSingleModeEntryAsync(cycle, config, ct);
            return true;
        }
        if (entries.Length != 1) return true;
        var reset = entries[0].GridLevel != 0;
        var unsent = reset && entries[0].Status == "PENDING_EXCHANGE" && entries[0].ExchangeOrderId == "pending" && entries[0].FilledQuantity == 0m;
        if (!unsent && !GridOrderRules.CanMoveEntry(entries[0].Status, entries[0].FilledQuantity)) return true;
        if (!FreshEntryQuote(config, quote)) return true;
        var entry = entries[0];
        var level = cycle.EffectivePlan.Levels.Single(x => x.Side == side && x.LevelIndex == 0);
        var target = reset ? SingleModeEntryRules.InitialEntryPrice(config, quote.Bid, quote.Ask)
            : SingleModeEntryRules.TargetPrice(config, entry.Price, entry.CreatedAt, quote.Bid, quote.Ask, clock.GetUtcNow());
        if (target is null || !ValidMovedPlan(cycle, config, level, target.Value)) return true;
        if (!await CanPlaceNewEntryOrderAsync(cycle, config, Enum.Parse<OrderSide>(entry.Side, true), ct)) return true;

        cycle.EntryGridMovePendingOrderId = entry.Id;
        await db.SaveChangesAsync(ct); // Commit before cancellation; survives an interrupted request.
        await CompleteSingleModeMoveAsync(cycle, config, ct);
        return true;
    }

    private async Task CompleteSingleModeMoveAsync(CycleEntity cycle, GridConfiguration config, CancellationToken ct)
    {
        var entry = await db.Orders.SingleAsync(x => x.Id == cycle.EntryGridMovePendingOrderId, ct);
        var selection = Selection(cycle);
        var adapter = environments.Adapter(selection.EnvironmentId);
        var snapshot = await SynchronizeMoveAsync(cycle, config, ct);
        if (HasUnappliedSnapshotFills(snapshot) || await HasUnappliedEntryFillsAsync(cycle, ct)) return;
        var observed = snapshot.Orders?.SingleOrDefault(x => x.ClientOrderId == entry.ClientOrderId);
        var paper = selection.EnvironmentId == ExecutionEnvironmentIds.PaperLocal;
        var localIntent = observed is null && entry.ExchangeOrderId == "pending" && entry.FilledQuantity == 0m &&
            entry.Status is "PENDING_EXCHANGE" or "CANCELLED";
        var unsent = localIntent && entry.Status == "PENDING_EXCHANGE";
        if (!paper && !localIntent && observed is null) return; // Absence from the book is not a terminal acknowledgement.

        if (cycle.State != "RUNNING" || cycle.RiskPaused || cycle.OperatorPaused ||
            !await IsFlatForMoveAsync(cycle, ct) || entry.FilledQuantity > 0m)
        {
            await ClearMoveAsync(cycle, ct);
            return; // Fills have been recorded and protected; keep the grid where it was.
        }
        var side = Enum.Parse<OrderSide>(entry.Side, true);
        var reset = entry.GridLevel != 0;
        var sourceLevel = cycle.EffectivePlan.Levels.Single(x => x.Side == side && x.LevelIndex == 0);
        var quote = await adapter.GetQuoteAsync(selection, config.Symbol, ct);
        if (!FreshEntryQuote(config, quote)) return; // Retain the durable move until quotes recover.
        var target = reset ? SingleModeEntryRules.InitialEntryPrice(config, quote.Bid, quote.Ask)
            : SingleModeEntryRules.TargetPrice(config, entry.Price, entry.CreatedAt, quote.Bid, quote.Ask, clock.GetUtcNow());
        if (unsent || GridOrderRules.CanMoveEntry(entry.Status, entry.FilledQuantity))
        {
            if (target is null || !ValidMovedPlan(cycle, config, sourceLevel, target.Value) ||
                !await CanPlaceNewEntryOrderAsync(cycle, config, Enum.Parse<OrderSide>(entry.Side, true), ct))
            {
                await ClearMoveAsync(cycle, ct);
                return;
            }
            if (unsent)
            {
                entry.Status = "CANCELLED";
                entry.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }
            else await adapter.CancelOrdersAsync(selection, [entry], ct);
            snapshot = await SynchronizeMoveAsync(cycle, config, ct);
            if (HasUnappliedSnapshotFills(snapshot) || await HasUnappliedEntryFillsAsync(cycle, ct)) return;
            observed = snapshot.Orders?.SingleOrDefault(x => x.ClientOrderId == entry.ClientOrderId);
        }
        if (entry.Status is not ("CANCELLED" or "REJECTED" or "FILLED") ||
            (!paper && !localIntent && observed?.Status is not ("CANCELLED" or "REJECTED" or "FILLED")) ||
            snapshot.OpenOrdersByClientId.ContainsKey(entry.ClientOrderId)) return;
        if (cycle.State != "RUNNING" || cycle.RiskPaused || cycle.OperatorPaused ||
            entry.FilledQuantity > 0m || !await IsFlatForMoveAsync(cycle, ct))
        {
            await ClearMoveAsync(cycle, ct);
            return;
        }
        var active = await ActiveOrdersAsync(cycle.Id, ct);
        if (active.Count != 0) return;
        // Reprice from a fresh quote after the cancellation round trip.
        quote = await adapter.GetQuoteAsync(selection, config.Symbol, ct);
        if (!FreshEntryQuote(config, quote)) return;
        target = reset ? SingleModeEntryRules.InitialEntryPrice(config, quote.Bid, quote.Ask)
            : SingleModeEntryRules.TargetPrice(config, entry.Price, entry.CreatedAt, quote.Bid, quote.Ask, clock.GetUtcNow());
        if (target is null || !ValidMovedPlan(cycle, config, sourceLevel, target.Value))
        {
            await ClearMoveAsync(cycle, ct); // Ordinary maintenance can restore the unchanged grid.
            return;
        }
        if (!await CanPlaceNewEntryOrderAsync(cycle, config, side, ct)) return;
        await CommitSingleModeEntryAsync(cycle, config, sourceLevel, target.Value,
            reset ? $"flat reset from level {entry.GridLevel}" : $"entry {entry.Price}", ct);
    }

    private async Task<ExecutionReconciliationSnapshot> SynchronizeMoveAsync(CycleEntity cycle,
        GridConfiguration config, CancellationToken ct)
    {
        var snapshot = await environments.Adapter(cycle.ExecutionEnvironmentId).ReconcileAsync(Selection(cycle), cycle, config, ct);
        foreach (var fill in snapshot.Fills.OrderBy(x => x.OccurredAt))
            await ApplyFillAsync(cycle, config, fill, ct, allowExchangeActions: false);
        await ApplyFundingPaymentsCoreAsync(cycle.ExecutionAccountId, snapshot.FundingPayments, ct);
        await ApplyOrderSnapshotAsync(cycle, snapshot, ct);
        cycle.ActualNetQuantity = snapshot.Position.Quantity;
        cycle.ReconstructedNetQuantity = await ReconstructedPositionAsync(cycle.Id, ct);
        await db.SaveChangesAsync(ct);
        await RecoverLotProtectionAsync(cycle, config, ct);
        await UpdateRiskPauseAsync(cycle, ct);
        return snapshot;
    }

    private bool HasUnappliedSnapshotFills(ExecutionReconciliationSnapshot snapshot) =>
        snapshot.Orders?.Any(observed => db.Orders.Local.Any(order =>
            order.Kind == "ENTRY" && order.ClientOrderId == observed.ClientOrderId &&
            order.FilledQuantity < observed.OriginalQuantity - observed.RemainingQuantity)) == true;

    private async Task<bool> HasUnappliedEntryFillsAsync(CycleEntity cycle, CancellationToken ct) =>
        (await db.Orders.Where(x => x.CycleId == cycle.Id && x.Kind == "ENTRY" && x.Status == "FILLED")
            .ToListAsync(ct)).Any(x => x.FilledQuantity < x.Quantity);

    private async Task<bool> IsFlatForMoveAsync(CycleEntity cycle, CancellationToken ct) =>
        cycle.ActualNetQuantity == 0m && cycle.ReconstructedNetQuantity == 0m &&
        !await db.VirtualLots.AnyAsync(x => x.CycleId == cycle.Id && x.Status != "CLOSED", ct);

    private static bool ValidMovedPlan(CycleEntity cycle, GridConfiguration config, GridLevel sourceLevel, decimal target)
    {
        var shifted = SingleModeEntryRules.ShiftPlan(cycle.EffectivePlan, target - sourceLevel.EntryPrice);
        if (shifted.CenterPrice <= 0m || shifted.Levels.Any(x =>
            !ValidMoveQuantity(cycle, config, x.Side, x.EntryPrice, x.PlannedQuantity))) return false;
        var quantity = GridMath.AllowedOrderQuantity(sourceLevel.Side, sourceLevel.PlannedQuantity, 0m, [],
            config.MaxNetLot, TradingService.RulesFor(config));
        return ValidMoveQuantity(cycle, config, sourceLevel.Side, target, quantity);
    }

    private static bool ValidMoveQuantity(CycleEntity cycle, GridConfiguration config, OrderSide side,
        decimal price, decimal quantity)
    {
        var rules = TradingService.RulesFor(config);
        var tp = GridMath.TakeProfitPrice(side, price, config.TakeProfitPoints, rules.TickSize);
        return price > 0m && tp > 0m && MeetsProtectiveMinimum(cycle, rules, price, quantity) &&
            MeetsProtectiveMinimum(cycle, rules, tp, quantity);
    }

    private async Task ClearMoveAsync(CycleEntity cycle, CancellationToken ct)
    {
        cycle.EntryGridMovePendingOrderId = null;
        await db.SaveChangesAsync(ct);
    }

    private async Task ConfirmMoveAuditsAsync(CycleEntity cycle, CancellationToken ct)
    {
        var plannedMoves = await (from audit in db.AuditLogs
                                  join order in db.Orders on audit.ResourceId equals order.Id
                                  where order.CycleId == cycle.Id && audit.Action == "ENTRY_GRID_MOVE_PLANNED" &&
                                      (order.Status == "NEW" || order.Status == "PARTIALLY_FILLED" || order.Status == "FILLED")
                                  select audit).ToListAsync(ct);
        foreach (var planned in plannedMoves)
        {
            planned.Action = "ENTRY_GRID_MOVE_ORDER_CONFIRMED";
            db.AuditLogs.Add(new AuditEntity { ResourceId = cycle.Id, Action = "ENTRY_GRID_MOVED", Actor = "strategy",
                Detail = planned.Detail, OccurredAt = clock.GetUtcNow() });
        }
        if (plannedMoves.Count > 0) await db.SaveChangesAsync(ct);
    }
}
