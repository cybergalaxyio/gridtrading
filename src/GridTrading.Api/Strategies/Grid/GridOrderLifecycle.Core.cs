using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Services;
using GridTrading.Domain;
using GridTrading.Domain.Strategies.Grid;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed partial class GridOrderLifecycle
{
    public async Task MaintainEntryOrdersAsync(CycleEntity cycle, GridConfiguration config, ExecutionQuote? quote, CancellationToken ct)
    {
        if (cycle.State != "RUNNING") return;
        var selection = Selection(cycle);
        var adapter = environments.Adapter(selection.EnvironmentId);
        quote ??= await adapter.GetQuoteAsync(selection, config.Symbol, ct);
        var plan = JsonSerializer.Deserialize<GridPlan>(cycle.FrozenPlanJson, JsonSupport.Options)!;
        var active = await ActiveOrdersAsync(cycle.Id, ct);
        var openLots = await db.VirtualLots.Where(x => x.CycleId == cycle.Id && x.Status != "CLOSED").ToListAsync(ct);
        if (openLots.Any(x => x.ProtectionPending && x.TakeProfitOrderId != null)) return;
        var created = new List<OrderEntity>();

        foreach (var side in new[] { OrderSide.Buy, OrderSide.Sell })
        {
            if ((config.GridMode == GridMode.BuyOnly && side == OrderSide.Sell) ||
                (config.GridMode == GridMode.SellOnly && side == OrderSide.Buy)) continue;
            var sideName = side.ToString().ToUpperInvariant();
            var currentEntries = active.Where(x => x.Kind == "ENTRY" && x.Side == sideName).ToList();
            var occupiedLevels = GridOrderRules.OccupiedLevels(side, openLots.Select(x =>
                (Enum.Parse<OrderSide>(x.Side, true), x.GridLevel, x.Status == "CLOSED")));
            var level = GridMath.SelectWorkingEntryLevel(plan, side, quote.Mid, occupiedLevels);

            if (currentEntries.Count > 0)
            {
                if (currentEntries.Any(x => !GridOrderRules.CanMoveEntry(x.Status, x.FilledQuantity))) continue;
                if (level is not null && currentEntries.Count == 1 && currentEntries[0].GridLevel == level.LevelIndex) continue;
                await adapter.CancelOrdersAsync(selection, currentEntries, ct);
                active.RemoveAll(currentEntries.Contains);
            }
            if (level is null) continue;

            var reservations = active.Select(x => new ActiveOrderReservation(
                Enum.Parse<OrderSide>(x.Side, true), x.Quantity - x.FilledQuantity));
            var quantity = GridMath.AllowedOrderQuantity(side, level.PlannedQuantity, cycle.ActualNetQuantity,
                reservations, config.MaxNetLot, TradingService.RulesFor(config));
            if (quantity <= 0m) continue;
            var order = CreateEntry(cycle, config.Symbol, level, quantity);
            db.Orders.Add(order);
            active.Add(order);
            created.Add(order);
        }

        if (created.Count == 0) return;
        await db.SaveChangesAsync(ct);
        await adapter.PlaceOrdersAsync(selection, config, created, ct);
    }

    private async Task CreateOrAmendTakeProfitAsync(CycleEntity cycle, GridConfiguration config, OrderEntity entry,
        ExecutionEntity execution, CancellationToken ct, bool allowExchangeActions = true)
    {
        var entrySide = Enum.Parse<OrderSide>(entry.Side, true);
        var lot = await db.VirtualLots.FirstOrDefaultAsync(x => x.EntryOrderId == entry.Id && x.Status != "CLOSED", ct);
        if (lot is null)
        {
            lot = new VirtualLotEntity
            {
                Id = Ids.New("lot"), CycleId = cycle.Id, EntryOrderId = entry.Id, Side = entry.Side,
                Status = "TP_ACCUMULATING", GridLevel = entry.GridLevel, EntryFillPrice = execution.Price,
                FilledQuantity = execution.Quantity, RemainingQuantity = execution.Quantity, EntryFee = execution.Fee
            };
            db.VirtualLots.Add(lot);
        }
        else
        {
            var remaining = lot.RemainingQuantity + execution.Quantity;
            lot.EntryFillPrice = (lot.EntryFillPrice * lot.RemainingQuantity + execution.Price * execution.Quantity) / remaining;
            lot.FilledQuantity += execution.Quantity;
            lot.RemainingQuantity = remaining;
            lot.EntryFee += execution.Fee;
        }
        lot.TakeProfitPrice = GridMath.TakeProfitPrice(entrySide, lot.EntryFillPrice,
            config.TakeProfitPoints, TradingService.RulesFor(config).TickSize);
        lot.ProtectionPending = true;
        // Commit the execution, lot, and protection intent together BEFORE external I/O.
        await db.SaveChangesAsync(ct);
        if (allowExchangeActions && cycle.State is "RUNNING" or "PAUSED")
            await EnsureLotProtectionAsync(cycle, config, entry, lot, ct);
    }

    private async Task EnsureLotProtectionAsync(CycleEntity cycle, GridConfiguration config, OrderEntity entry,
        VirtualLotEntity lot, CancellationToken ct)
    {
        if (lot.RemainingQuantity <= 0m)
        {
            lot.ProtectionPending = false;
            return;
        }
        var rules = TradingService.RulesFor(config);
        var tp = lot.TakeProfitOrderId is null ? null : await db.Orders.SingleAsync(x => x.Id == lot.TakeProfitOrderId, ct);
        if (tp is not null && tp.Status is "NEW" or "PARTIALLY_FILLED" &&
            tp.Quantity - tp.FilledQuantity == lot.RemainingQuantity && tp.Price == lot.TakeProfitPrice)
        {
            lot.ProtectionPending = false;
            await db.SaveChangesAsync(ct);
            return;
        }
        lot.ProtectionPending = true;
        if (!MeetsProtectiveMinimum(cycle, rules, lot.TakeProfitPrice, lot.RemainingQuantity))
        {
            await db.SaveChangesAsync(ct);
            await HandleUnprotectableRemainderAsync(cycle, entry, lot, ct);
            return;
        }
        // UNKNOWN must first be resolved by a venue observation. A FILLED status can
        // precede its executions; never replace it before those fills have arrived.
        if (tp is not null && tp.Status is "UNKNOWN" or "PENDING_EXCHANGE" or "FILLED") return;
        var place = tp is null || tp.Status is "CANCELLED" or "REJECTED";
        if (tp is null)
        {
            tp = new OrderEntity
            {
                Id = Ids.New("order"), CycleId = cycle.Id, ClientOrderId = Ids.New($"tp-{entry.GridLevel}"),
                ExchangeOrderId = "pending", Symbol = entry.Symbol, Side = entry.Side == "BUY" ? "SELL" : "BUY",
                Kind = "TAKE_PROFIT", Status = "PENDING_EXCHANGE", GridLevel = entry.GridLevel,
                Price = lot.TakeProfitPrice, Quantity = lot.RemainingQuantity,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Orders.Add(tp);
            lot.TakeProfitOrderId = tp.Id;
        }
        else if (place)
        {
            tp.Status = "PENDING_EXCHANGE";
            tp.ExchangeOrderId = "pending";
            tp.Price = lot.TakeProfitPrice;
            tp.Quantity = tp.FilledQuantity + lot.RemainingQuantity;
        }
        lot.Status = "TP_PENDING";
        await db.SaveChangesAsync(ct);
        var selection = Selection(cycle);
        var adapter = environments.Adapter(selection.EnvironmentId);
        try
        {
            if (place) await adapter.PlaceOrdersAsync(selection, config, [tp], ct);
            else if (adapter is IOrderAmendmentAdapter amendments)
                await amendments.AmendOrderAsync(selection, config, tp, lot.TakeProfitPrice,
                    tp.FilledQuantity + lot.RemainingQuantity, ct);
            else throw new TradingProblemException(422, "PROTECTIVE_ORDER_REJECTED",
                "The execution adapter cannot atomically amend a fragmented take-profit order.");
            lot.ProtectionPending = tp.Status is not ("NEW" or "PARTIALLY_FILLED") ||
                tp.Price != lot.TakeProfitPrice || tp.Quantity - tp.FilledQuantity != lot.RemainingQuantity;
            await db.SaveChangesAsync(ct);
        }
        catch (TradingProblemException ex) when (ex.Code == "PROTECTIVE_ORDER_REJECTED")
        {
            await HandleProtectiveOrderRejectionAsync(cycle, selection, adapter, ex, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && (ex is HttpRequestException or TaskCanceledException ||
            ex is TradingProblemException problem && problem.Code is "EXCHANGE_HTTP_ERROR" or "EXCHANGE_ACTION_REJECTED"))
        {
            tp.Status = "UNKNOWN";
            await db.SaveChangesAsync(ct); // Keep the intent; never turn an uncertain send into a fresh order.
        }
    }

    private static bool MeetsProtectiveMinimum(CycleEntity cycle, InstrumentRules rules, decimal price, decimal quantity)
    {
        var minimumNotional = cycle.ExecutionEnvironmentId == ExecutionEnvironmentIds.HyperliquidTestnet
            ? Math.Max(rules.MinOrderNotional, HyperliquidInfoClient.MinimumOrderNotional)
            : rules.MinOrderNotional;
        return quantity >= rules.MinOrderQuantity && price * quantity >= minimumNotional;
    }

    private async Task HandleUnprotectableRemainderAsync(CycleEntity cycle, OrderEntity entry,
        VirtualLotEntity lot, CancellationToken ct)
    {
        var selection = Selection(cycle);
        var adapter = environments.Adapter(selection.EnvironmentId);
        if (await db.VirtualLots.AnyAsync(x => x.CycleId == cycle.Id && x.Id != lot.Id &&
            x.ProtectionPending && x.TakeProfitOrderId != null && x.RemainingQuantity > 0m, ct))
        {
            // Do not turn an unconfirmed amendment into a fabricated exposure failure.
            // Pending protection blocks new entries until reconciliation establishes the facts.
            db.RiskAlerts.Add(new RiskAlertEntity
            {
                Id = Ids.New("alert"), CycleId = cycle.Id, Severity = "WARNING", Code = "PROTECTION_RECONCILIATION_REQUIRED",
                Message = "Unprotected exposure cannot be confirmed until pending TP amendments are reconciled.", CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
            return;
        }
        var exception = new TradingProblemException(422, "PROTECTIVE_ORDER_REJECTED",
            $"Filled Entry {entry.Side[0]}{entry.GridLevel} leaves {lot.RemainingQuantity} unprotected because its " +
            $"take-profit value is below the venue minimum.");
        await HandleProtectiveOrderRejectionAsync(cycle, selection, adapter, exception, ct);
    }

    private async Task<bool> HandleProtectiveOrderRejectionAsync(CycleEntity cycle, ExecutionSelection selection,
        IExecutionAdapter adapter, TradingProblemException exception, CancellationToken ct)
    {
        var affectedNotionalUsdt = await AffectedUnprotectedNotionalAsync(cycle.Id, ct);
        var threshold = Math.Max(0m, DeserializeConfig(cycle).FaultExposureThresholdUsdt);
        var exceedsThreshold = affectedNotionalUsdt > threshold;

        if (!exceedsThreshold && cycle.State != "FAULT")
        {
            db.RiskAlerts.Add(new RiskAlertEntity
            {
                Id = Ids.New("alert"), CycleId = cycle.Id, Severity = "WARNING",
                Code = "PROTECTIVE_ORDER_BELOW_FAULT_THRESHOLD",
                Message = $"Cycle {cycle.Id} 的止盈保护未能建立，但受影响未保护仓位约为 " +
                    $"{affectedNotionalUsdt:F2} USD，未超过 FAULT 阈值 {threshold:F2} USD。" +
                    $"原因 [PROTECTIVE_ORDER_REJECTED]：{exception.Message}。Cycle 保持 {cycle.State}，策略不因本次影响停止。",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
            return false;
        }

        if (cycle.State != "FAULT")
        {
            cycle.State = "FAULT";
            cycle.StateVersion++;
            db.RiskAlerts.Add(new RiskAlertEntity
            {
                Id = Ids.New("alert"), CycleId = cycle.Id, Severity = "CRITICAL",
                Code = "PROTECTIVE_ORDER_REJECTED",
                Message = $"Cycle {cycle.Id} 进入 FAULT：无法建立止盈保护。" +
                    $"受影响未保护仓位约为 {affectedNotionalUsdt:F2} USD，已超过 FAULT 阈值 {threshold:F2} USD。" +
                    $"原因 [PROTECTIVE_ORDER_REJECTED]：{exception.Message}。" +
                    "系统正在撤销活动 Entry；请核对实际仓位和保护单。",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }

        // This cleanup is caused by the rejected protective order, not by the FAULT state.
        // Changing a cycle to FAULT elsewhere must remain side-effect free.
        var activeEntries = (await ActiveOrdersAsync(cycle.Id, ct))
            .Where(x => x.Kind == "ENTRY")
            .ToArray();
        if (activeEntries.Length > 0)
            await adapter.CancelOrdersAsync(selection, activeEntries, ct);
        return true;
    }

    private async Task<decimal> AffectedUnprotectedNotionalAsync(string cycleId, CancellationToken ct)
    {
        var lots = await db.VirtualLots
            .Where(x => x.CycleId == cycleId && x.Status != "CLOSED" && x.RemainingQuantity > 0m)
            .ToListAsync(ct);
        var takeProfits = await db.Orders
            .Where(x => x.CycleId == cycleId && x.Kind == "TAKE_PROFIT")
            .ToDictionaryAsync(x => x.Id, ct);
        return CalculateUnprotectedNotional(lots, takeProfits);
    }

    internal static decimal CalculateUnprotectedNotional(
        IReadOnlyList<VirtualLotEntity> lots,
        IReadOnlyDictionary<string, OrderEntity> takeProfits)
    {
        var affectedFromLots = lots.Sum(lot =>
        {
            var protectedQuantity = lot.TakeProfitOrderId is not null &&
                takeProfits.TryGetValue(lot.TakeProfitOrderId, out var tp) &&
                tp.Status is "PENDING_EXCHANGE" or "NEW" or "PARTIALLY_FILLED" or "UNKNOWN"
                    ? Math.Max(0m, tp.Quantity - tp.FilledQuantity)
                    : 0m;
            var unprotectedQuantity = Math.Max(0m, lot.RemainingQuantity - protectedQuantity);
            return Math.Abs(lot.TakeProfitPrice * unprotectedQuantity);
        });
        if (affectedFromLots > 0m) return affectedFromLots;

        return takeProfits.Values.Where(x => x.Status == "REJECTED")
            .Sum(x => Math.Abs(x.Price * (x.Quantity - x.FilledQuantity)));
    }


    private async Task CloseLotAsync(CycleEntity cycle, OrderEntity tp, ExecutionEntity execution, CancellationToken ct, bool allowExchangeActions = true)
    {
        var lot = await db.VirtualLots.SingleOrDefaultAsync(x => x.TakeProfitOrderId == tp.Id, ct);
        if (lot is null) return;
        var closed = Math.Min(lot.RemainingQuantity, execution.Quantity);
        lot.RemainingQuantity -= closed;
        lot.ExitFee += execution.Fee;
        cycle.RealisedCyclePnl += lot.Side == "BUY"
            ? (execution.Price - lot.EntryFillPrice) * closed
            : (lot.EntryFillPrice - execution.Price) * closed;
        if (lot.RemainingQuantity <= 0m)
        {
            lot.Status = "CLOSED";
            lot.ProtectionPending = false;
        }
        if (!allowExchangeActions || cycle.State is not ("RUNNING" or "PAUSED")) return;

        var entry = await db.Orders.SingleOrDefaultAsync(x => x.Id == lot.EntryOrderId, ct);
        if (entry is null || entry.FilledQuantity <= 0m || entry.FilledQuantity >= entry.Quantity ||
            entry.Status is not ("PENDING_EXCHANGE" or "NEW" or "PARTIALLY_FILLED" or "UNKNOWN")) return;

        // Once this lot starts realizing profit, accepting a late tail fill could require a new
        // protective order below the venue minimum. Cancel the Entry remainder before the next
        // working level is selected.
        await db.SaveChangesAsync(ct);
        var selection = Selection(cycle);
        await environments.Adapter(selection.EnvironmentId).CancelOrdersAsync(selection, [entry], ct);
    }

    private async Task CancelExpiredPartialEntriesAsync(CycleEntity cycle, GridConfiguration config,
        IReadOnlySet<string> openClientIds, CancellationToken ct)
    {
        if (config.PartialFillCancelAfterMinutes <= 0) return;
        var orders = await db.Orders.Where(x => x.CycleId == cycle.Id && x.Kind == "ENTRY" &&
            x.Status == "PARTIALLY_FILLED" && x.FilledQuantity > 0m && x.FilledQuantity < x.Quantity).ToListAsync(ct);
        var orderIds = orders.Select(x => x.Id).ToArray();
        // SQLite cannot translate Min over DateTimeOffset. Keep the database query
        // selective, then calculate the first fill per order in memory.
        var fillTimes = await db.Executions.AsNoTracking()
            .Where(x => orderIds.Contains(x.OrderId))
            .Select(x => new { x.OrderId, x.OccurredAt })
            .ToListAsync(ct);
        var firstFills = fillTimes
            .GroupBy(x => x.OrderId)
            .ToDictionary(x => x.Key, x => x.Min(fill => fill.OccurredAt));
        var expired = orders.Where(x => openClientIds.Contains(x.ClientOrderId) &&
            firstFills.TryGetValue(x.Id, out var first) &&
            GridMath.PartialFillCancellationDue(first, DateTimeOffset.UtcNow, config.PartialFillCancelAfterMinutes)).ToArray();
        if (expired.Length == 0) return;
        var selection = Selection(cycle);
        await environments.Adapter(selection.EnvironmentId).CancelOrdersAsync(selection, expired, ct);
    }

    public static OrderEntity CreateEntry(CycleEntity cycle, string symbol, GridLevel level, decimal quantity) => new()
    {
        Id = Ids.New("order"), CycleId = cycle.Id,
        ClientOrderId = Ids.New($"grid-{level.Side.ToString()[0]}-{level.LevelIndex}"), ExchangeOrderId = "pending",
        Symbol = symbol, Side = level.Side.ToString().ToUpperInvariant(), Kind = "ENTRY", Status = "PENDING_EXCHANGE",
        GridLevel = level.LevelIndex, Price = level.EntryPrice, Quantity = quantity,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };
}
