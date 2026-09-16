using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Domain;
using GridTrading.Domain.Strategies.Grid;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed partial class GridOrderLifecycle
{
    // One execution path for trailing, resetting, restoring, and interrupted moves.
    // The destination is recalculated after exchange I/O, never frozen before cancellation.
    private async Task MoveAsync(CycleEntity cycle, GridConfiguration config, OrderEntity? entry,
        CancellationToken ct)
    {
        if (cycle.EntryGridMovePendingOrderId is not null)
            entry = await db.Orders.SingleAsync(x => x.Id == cycle.EntryGridMovePendingOrderId, ct);
        else if (entry is not null)
        {
            cycle.EntryGridMovePendingOrderId = entry.Id;
            await db.SaveChangesAsync(ct); // Commit before cancellation; survives an interrupted request.
        }

        // Even an empty book needs synchronization: delayed fills must not start a new round.
        var snapshot = await SynchronizeMoveAsync(cycle, config, ct);
        if (HasUnappliedSnapshotFills(snapshot) || await HasUnappliedEntryFillsAsync(cycle, ct)) return;
        if (entry is not null)
        {
            if (!await CancelAndConfirmMoveEntryAsync(cycle, config, entry, snapshot, ct)) return;
        }
        else if (await AbortMoveIfIneligibleAsync(cycle, entry, ct)) return;
        if ((await ActiveOrdersAsync(cycle.Id, ct)).Count != 0) return;

        var side = config.GridMode == GridMode.SellOnly ? OrderSide.Sell : OrderSide.Buy;
        if (entry is null)
        {
            if (await db.Orders.AnyAsync(x => x.CycleId == cycle.Id &&
                snapshot.OpenOrdersByClientId.Keys.Contains(x.ClientOrderId), ct)) return;
            if (!await CanPlaceNewEntryOrderAsync(cycle, config, side, ct)) return;
        }
        var quote = await environments.Adapter(cycle.ExecutionEnvironmentId)
            .GetQuoteAsync(Selection(cycle), config.Symbol, ct);
        if (!FreshEntryQuote(config, quote)) return; // Keep any pending move until quotes recover.
        var kind = await DetermineMoveKindAsync(cycle, entry, ct);
        var target = CalculateMoveTarget(cycle, config, kind, entry, quote);
        if (target is null)
        {
            if (entry is not null) await ClearMoveAsync(cycle, ct);
            return; // Ordinary maintenance can restore the unchanged grid.
        }
        if (entry is not null && !await CanPlaceNewEntryOrderAsync(cycle, config, side, ct)) return;
        await CommitMoveAsync(cycle, config, target, MoveReason(kind, entry), ct);
    }

    private async Task<bool> CancelAndConfirmMoveEntryAsync(CycleEntity cycle, GridConfiguration config,
        OrderEntity entry, ExecutionReconciliationSnapshot snapshot, CancellationToken ct)
    {
        var observed = snapshot.Orders?.SingleOrDefault(x => x.ClientOrderId == entry.ClientOrderId);
        var paper = cycle.ExecutionEnvironmentId == ExecutionEnvironmentIds.PaperLocal;
        var localIntent = observed is null && entry.ExchangeOrderId == "pending" && entry.FilledQuantity == 0m &&
            entry.Status is "PENDING_EXCHANGE" or "CANCELLED";
        var unsent = localIntent && entry.Status == "PENDING_EXCHANGE";
        if (!paper && !localIntent && observed is null) return false;
        if (await AbortMoveIfIneligibleAsync(cycle, entry, ct)) return false;

        var selection = Selection(cycle);
        var adapter = environments.Adapter(selection.EnvironmentId);
        var quote = await adapter.GetQuoteAsync(selection, config.Symbol, ct);
        if (!FreshEntryQuote(config, quote)) return false;
        if (unsent || GridOrderRules.CanMoveEntry(entry.Status, entry.FilledQuantity))
        {
            if (CalculateMoveTarget(cycle, config, MoveKindForEntry(entry), entry, quote) is null ||
                !await CanPlaceNewEntryOrderAsync(cycle, config, Enum.Parse<OrderSide>(entry.Side, true), ct))
            {
                await ClearMoveAsync(cycle, ct);
                return false;
            }
            if (unsent)
            {
                entry.Status = "CANCELLED";
                entry.UpdatedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct);
            }
            else await adapter.CancelOrdersAsync(selection, [entry], ct);
            snapshot = await SynchronizeMoveAsync(cycle, config, ct);
            if (HasUnappliedSnapshotFills(snapshot) || await HasUnappliedEntryFillsAsync(cycle, ct)) return false;
            observed = snapshot.Orders?.SingleOrDefault(x => x.ClientOrderId == entry.ClientOrderId);
        }
        // Absence from the book alone is not a terminal acknowledgement.
        if (entry.Status is not ("CANCELLED" or "REJECTED" or "FILLED") ||
            (!paper && !localIntent && observed?.Status is not ("CANCELLED" or "REJECTED" or "FILLED")) ||
            snapshot.OpenOrdersByClientId.ContainsKey(entry.ClientOrderId)) return false;
        return !await AbortMoveIfIneligibleAsync(cycle, entry, ct);
    }

    private async Task<bool> AbortMoveIfIneligibleAsync(CycleEntity cycle, OrderEntity? entry, CancellationToken ct)
    {
        if (cycle.State == "RUNNING" && !cycle.RiskPaused && !cycle.OperatorPaused &&
            await IsFlatForMoveAsync(cycle, ct) && (entry is null || entry.FilledQuantity == 0m)) return false;
        // Synchronization has recorded and protected any fill. Keep its holding grid in place.
        if (entry is not null) await ClearMoveAsync(cycle, ct);
        return true;
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

    private static string MoveReason(SingleModeMoveKind kind, OrderEntity? entry) => kind switch
    {
        SingleModeMoveKind.Trail => $"entry {entry!.Price}",
        SingleModeMoveKind.Reset => entry is null ? "flat reset without a working entry" : $"flat reset from level {entry.GridLevel}",
        _ => "restore first entry"
    };

    private async Task CommitMoveAsync(CycleEntity cycle, GridConfiguration config,
        SingleModeMoveTarget target, string reason, CancellationToken ct)
    {
        var level = target.FirstLevel;
        var replacement = CreateEntry(cycle, config.Symbol, level, target.Quantity);
        replacement.CreatedAt = replacement.UpdatedAt = clock.GetUtcNow();
        cycle.EntryGridPriceOffset += target.CenterPrice - cycle.EffectivePlan.CenterPrice;
        cycle.EntryGridMovePendingOrderId = null;
        db.Orders.Add(replacement);
        db.AuditLogs.Add(new AuditEntity
        {
            ResourceId = replacement.Id, Action = "ENTRY_GRID_MOVE_PLANNED", Actor = "strategy",
            Detail = $"Cycle {cycle.Id}, order {replacement.Id}: {level.Side} {reason} -> level 0 at {level.EntryPrice}, quantity {target.Quantity}; grid offset {cycle.EntryGridPriceOffset}.",
            OccurredAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync(ct); // Center shift and replacement intent commit atomically.
        await PlaceNewEntryOrdersAsync(cycle, config, [replacement], ct);
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
