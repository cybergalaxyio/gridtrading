using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Services;
using GridTrading.Domain;
using GridTrading.Domain.Strategies.Grid;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed partial class GridOrderLifecycle
{
    private enum SingleModeMaintenanceResult { ContinueEntryMaintenance, StopEntryMaintenance }
    private enum SingleModeMoveKind { Trail, Reset, Restore }
    private sealed record SingleModeMoveTarget(decimal CenterPrice, GridLevel FirstLevel, decimal Quantity);

    // Called under the account gate. Ordinary entry selection is allowed only
    // when a holding round needs its next entry; it must not chase a flat rebound.
    private async Task<SingleModeMaintenanceResult> MaintainSingleModeEntriesAsync(CycleEntity cycle,
        GridConfiguration config, ExecutionQuote quote, CancellationToken ct)
    {
        if (cycle.EntryGridMovePendingOrderId is not null)
        {
            await MoveAsync(cycle, config, null, ct);
            return SingleModeMaintenanceResult.StopEntryMaintenance;
        }
        var side = config.GridMode == GridMode.SellOnly ? OrderSide.Sell : OrderSide.Buy;
        if (!await CanPlaceNewEntryOrderAsync(cycle, config, side, ct))
            return SingleModeMaintenanceResult.StopEntryMaintenance;
        var active = await ActiveOrdersAsync(cycle.Id, ct);
        var entries = active.Where(x => x.Kind == "ENTRY").ToArray();
        if (active.Any(x => x.Status == "UNKNOWN" || (x.Status == "PENDING_EXCHANGE" &&
            (x.Kind != "ENTRY" || x.GridLevel == 0 || x.ExchangeOrderId != "pending"))) ||
            await HasUnappliedEntryFillsAsync(cycle, ct))
            return SingleModeMaintenanceResult.StopEntryMaintenance;
        if (!await IsFlatForMoveAsync(cycle, ct))
            return entries.Length == 0 ? SingleModeMaintenanceResult.ContinueEntryMaintenance
                : SingleModeMaintenanceResult.StopEntryMaintenance;
        if (active.Any(x => x.Kind != "ENTRY") || entries.Length > 1)
            return SingleModeMaintenanceResult.StopEntryMaintenance;

        var entry = entries.SingleOrDefault();
        if (entry is not null)
        {
            var unsentReset = entry.GridLevel != 0 && entry.Status == "PENDING_EXCHANGE" &&
                entry.ExchangeOrderId == "pending" && entry.FilledQuantity == 0m;
            if (!unsentReset && !GridOrderRules.CanMoveEntry(entry.Status, entry.FilledQuantity))
                return SingleModeMaintenanceResult.StopEntryMaintenance;
            if (!FreshEntryQuote(config, quote) ||
                CalculateMoveTarget(cycle, config, MoveKindForEntry(entry), entry, quote) is null)
                return SingleModeMaintenanceResult.StopEntryMaintenance;
            if (!await CanPlaceNewEntryOrderAsync(cycle, config, Enum.Parse<OrderSide>(entry.Side, true), ct))
                return SingleModeMaintenanceResult.StopEntryMaintenance;
        }
        await MoveAsync(cycle, config, entry, ct);
        return SingleModeMaintenanceResult.StopEntryMaintenance;
    }

    private static SingleModeMoveKind MoveKindForEntry(OrderEntity entry) =>
        entry.GridLevel == 0 ? SingleModeMoveKind.Trail : SingleModeMoveKind.Reset;

    private async Task<SingleModeMoveKind> DetermineMoveKindAsync(CycleEntity cycle, OrderEntity? entry,
        CancellationToken ct)
    {
        if (entry is not null) return MoveKindForEntry(entry);
        // A missing entry keeps the startup grid until a holding round has completed.
        var completedRound = await db.VirtualLots.AnyAsync(x => x.CycleId == cycle.Id && x.Status == "CLOSED", ct);
        return completedRound ? SingleModeMoveKind.Reset : SingleModeMoveKind.Restore;
    }

    private SingleModeMoveTarget? CalculateMoveTarget(CycleEntity cycle, GridConfiguration config,
        SingleModeMoveKind kind, OrderEntity? entry, ExecutionQuote quote)
    {
        var side = config.GridMode == GridMode.SellOnly ? OrderSide.Sell : OrderSide.Buy;
        var plan = cycle.EffectivePlan;
        var first = plan.Levels.Single(x => x.Side == side && x.LevelIndex == 0);
        var price = kind switch
        {
            SingleModeMoveKind.Trail when entry is not null => SingleModeEntryRules.TargetPrice(
                config, entry.Price, entry.CreatedAt, quote.Bid, quote.Ask, clock.GetUtcNow()),
            SingleModeMoveKind.Reset => SingleModeEntryRules.InitialEntryPrice(config, quote.Bid, quote.Ask),
            SingleModeMoveKind.Restore => first.EntryPrice,
            _ => throw new InvalidOperationException("Trailing requires a working entry.")
        };
        if (price is null) return null;

        // Move the effective center by the level-zero displacement, preserving
        // the frozen plan, spacing, and planned quantities at every level.
        var shifted = SingleModeEntryRules.ShiftPlan(plan, price.Value - first.EntryPrice);
        if (shifted.CenterPrice <= 0m || shifted.Levels.Any(x =>
            !ValidMoveQuantity(cycle, config, x.Side, x.EntryPrice, x.PlannedQuantity))) return null;
        var quantity = GridMath.AllowedOrderQuantity(side, first.PlannedQuantity, 0m, [],
            config.MaxNetLot, TradingService.RulesFor(config));
        if (!ValidMoveQuantity(cycle, config, side, price.Value, quantity)) return null;
        return new(shifted.CenterPrice,
            shifted.Levels.Single(x => x.Side == side && x.LevelIndex == 0), quantity);
    }

    private static bool ValidMoveQuantity(CycleEntity cycle, GridConfiguration config, OrderSide side,
        decimal price, decimal quantity)
    {
        var rules = TradingService.RulesFor(config);
        var tp = GridMath.TakeProfitPrice(side, price, config.TakeProfitPoints, rules.TickSize);
        return price > 0m && tp > 0m && MeetsProtectiveMinimum(cycle, rules, price, quantity) &&
            MeetsProtectiveMinimum(cycle, rules, tp, quantity);
    }

    private async Task<bool> IsFlatForMoveAsync(CycleEntity cycle, CancellationToken ct) =>
        cycle.ActualNetQuantity == 0m && cycle.ReconstructedNetQuantity == 0m &&
        !await db.VirtualLots.AnyAsync(x => x.CycleId == cycle.Id && x.Status != "CLOSED", ct);
}
