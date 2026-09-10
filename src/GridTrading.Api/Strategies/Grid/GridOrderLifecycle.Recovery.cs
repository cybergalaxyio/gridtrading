using GridTrading.Api.Data;
using GridTrading.Api.Execution;
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
                // origSz describes one amendment generation. FilledQuantity describes
                // the logical order across all generations sharing the same CLOID.
                var precedingFills = fills.Where(x => VenueOrderId(x, order) != observed.ExchangeOrderId).Sum(x => x.Quantity);
                order.Quantity = Math.Max(order.FilledQuantity, precedingFills + observed.OriginalQuantity);
                order.Price = observed.Price;
                order.ExchangeOrderId = observed.ExchangeOrderId;
                order.Status = observed.Status == "NEW" && order.FilledQuantity > 0m ? "PARTIALLY_FILLED" : observed.Status;
                if (order.Status is "NEW" or "PARTIALLY_FILLED" && order.FilledQuantity >= order.Quantity) order.Status = "FILLED";
                order.UpdatedAt = DateTimeOffset.UtcNow;
            }
            else if (snapshot.OpenOrdersByClientId.TryGetValue(order.ClientOrderId, out var oid))
            {
                order.ExchangeOrderId = oid;
                if (order.Status == "PENDING_EXCHANGE") order.Status = "NEW";
            }
            else if (snapshot.Orders is not null && order.Status is "NEW" or "PARTIALLY_FILLED")
            {
                order.Status = "UNKNOWN"; // Absence from the book is not cancel confirmation.
            }
        }
        foreach (var update in snapshot.OrderUpdates)
        {
            var order = orders.FirstOrDefault(x => x.ExchangeOrderId == update.ExchangeOrderId);
            if (order is null || update.OccurredAt < order.UpdatedAt) continue;
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
                await environments.Adapter(cycle.ExecutionEnvironmentId).CancelOrdersAsync(Selection(cycle), [entry], ct);
            if (!lot.ProtectionPending || !MeetsProtectiveMinimum(cycle, TradingService.RulesFor(config),
                lot.TakeProfitPrice, lot.RemainingQuantity)) continue;
            await EnsureLotProtectionAsync(cycle, config, entry, lot, ct);
        }
        await db.SaveChangesAsync(ct);
    }
}
