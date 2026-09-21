using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Services;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed partial class GridOrderLifecycle
{
    private async Task ApplyOrderSnapshotAsync(CycleEntity cycle, ExecutionReconciliationSnapshot snapshot, CancellationToken ct)
    {
        var orders = await db.Orders.Where(x => x.CycleId == cycle.Id).ToListAsync(ct);
        var executions = await db.Executions.Where(x => x.CycleId == cycle.Id).ToListAsync(ct);
        foreach (var order in orders)
        {
            var fills = executions.Where(x => x.OrderId == order.Id).ToArray();
            if (fills.Length > 0)
            {
                order.FilledQuantity = fills.Sum(x => x.Quantity);
                order.Quantity = Math.Max(order.Quantity, order.FilledQuantity);
            }
            var observed = snapshot.Orders?.SingleOrDefault(x => x.ClientOrderId == order.ClientOrderId);
            if (observed is not null)
            {
                if (observed.OriginalQuantity <= 0m || observed.Price <= 0m ||
                    observed.RemainingQuantity < 0m || observed.RemainingQuantity > observed.OriginalQuantity)
                    throw new TradingProblemException(503, "INVALID_ORDER_SNAPSHOT", "Exchange order quantities could not be verified.");
                var newlyConfirmed = order.ExchangeOrderId != observed.ExchangeOrderId ||
                    order.Status is "PENDING_EXCHANGE" or "UNKNOWN";
                // origSz describes one amendment generation. FilledQuantity describes
                // the logical order across all generations sharing the same CLOID.
                var precedingFills = fills.Where(x => VenueOrderId(x, order) != observed.ExchangeOrderId).Sum(x => x.Quantity);
                order.Quantity = Math.Max(order.FilledQuantity, precedingFills + observed.OriginalQuantity);
                order.Price = observed.Price;
                order.ExchangeOrderId = observed.ExchangeOrderId;
                order.Status = observed.Status == "NEW" && order.FilledQuantity > 0m ? "PARTIALLY_FILLED" : observed.Status;
                if (order.Status is "NEW" or "PARTIALLY_FILLED" && order.FilledQuantity >= order.Quantity) order.Status = "FILLED";
                if (observed.Status == "FILLED" && observed.FilledAt.HasValue)
                    order.FilledAt ??= observed.FilledAt.Value.ToUniversalTime();
                order.UpdatedAt = DateTimeOffset.UtcNow;
                if (newlyConfirmed && observed.Status is "NEW" or "PARTIALLY_FILLED" or "FILLED" or "CANCELLED")
                    await OrderPlacementNotifications.RecordAsync(db, Selection(cycle), order, ct,
                        quantity: observed.OriginalQuantity);
            }
            else if (snapshot.OpenOrdersByClientId.TryGetValue(order.ClientOrderId, out var oid))
            {
                var newlyConfirmed = order.ExchangeOrderId != oid || order.Status is "PENDING_EXCHANGE" or "UNKNOWN";
                order.ExchangeOrderId = oid;
                if (order.Status == "PENDING_EXCHANGE") order.Status = "NEW";
                if (newlyConfirmed) await OrderPlacementNotifications.RecordAsync(db, Selection(cycle), order, ct);
            }
            else if (snapshot.Orders is not null && order.Status is "NEW" or "PARTIALLY_FILLED")
            {
                order.Status = "UNKNOWN"; // Absence from the book is not cancel confirmation.
            }
            var filledAt = OrderCompletion.FindFilledAt(order.Quantity, fills.Select(x => (x.Quantity, x.OccurredAt)));
            if (filledAt.HasValue) order.FilledAt = filledAt;
        }
        foreach (var update in snapshot.OrderUpdates)
        {
            var order = orders.FirstOrDefault(x => x.ExchangeOrderId == update.ExchangeOrderId);
            if (order is null) continue;
            // A confirmed terminal event carries a completion time even when a
            // newer local reconciliation has already touched UpdatedAt.
            if (update.Status == "FILLED" && update.HasExchangeTimestamp) order.FilledAt ??= update.OccurredAt.ToUniversalTime();
            if (update.OccurredAt < order.UpdatedAt) continue;
            order.Status = update.Status;
            order.UpdatedAt = update.OccurredAt;
        }
        var lots = await db.VirtualLots.Where(x => x.CycleId == cycle.Id).ToListAsync(ct);
        foreach (var lot in lots)
        {
            var tp = orders.SingleOrDefault(x => x.Id == lot.TakeProfitOrderId);
            lot.ProtectionPending = lot.RemainingQuantity > 0m && (tp is null ||
                tp.Status is not ("NEW" or "PARTIALLY_FILLED") ||
                tp.Quantity - tp.FilledQuantity != lot.RemainingQuantity || tp.Price != lot.TakeProfitPrice);
        }
        await db.SaveChangesAsync(ct);
        await ConfirmMoveAuditsAsync(cycle, ct);
    }

    private static string VenueOrderId(ExecutionEntity fill, OrderEntity order)
    {
        if (!string.IsNullOrEmpty(fill.ExchangeOrderId)) return fill.ExchangeOrderId;
        // Legacy Hyperliquid execution IDs already contain the exact generation OID.
        var parts = fill.ExchangeExecutionId.Split(':');
        return parts.Length == 5 && parts[0] == "hl" ? parts[2] : order.ExchangeOrderId;
    }

    private async Task RecoverLotProtectionAsync(CycleEntity cycle, GridConfiguration config, CancellationToken ct)
    {
        var lots = await db.VirtualLots.Where(x => x.CycleId == cycle.Id).ToListAsync(ct);
        foreach (var lot in lots)
        {
            if (cycle.State is not ("RUNNING" or "PAUSED")) break;
            var entry = await db.Orders.SingleAsync(x => x.Id == lot.EntryOrderId, ct);
            // A TP fill seen during REST recovery must also cancel the entry tail.
            if (lot.FilledQuantity > lot.RemainingQuantity && entry.FilledQuantity < entry.Quantity &&
                entry.Status is "NEW" or "PARTIALLY_FILLED")
            {
                if (cycle.State == "PAUSED") await TryCancelPausedEntriesAsync(cycle, ct);
                else await environments.Adapter(cycle.ExecutionEnvironmentId).CancelOrdersAsync(Selection(cycle), [entry], ct);
            }
            if (!lot.ProtectionPending || !MeetsProtectiveMinimum(cycle, GridInstrumentRules.FromConfiguration(config),
                lot.TakeProfitPrice, lot.RemainingQuantity)) continue;
            await EnsureLotProtectionAsync(cycle, config, entry, lot, ct);
        }
        await db.SaveChangesAsync(ct);
    }
}
