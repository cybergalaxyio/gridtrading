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

    private async Task CreateTakeProfitAsync(CycleEntity cycle, GridConfiguration config, OrderEntity entry,
        ExecutionEntity execution, CancellationToken ct)
    {
        var entrySide = Enum.Parse<OrderSide>(entry.Side, true);
        var tpSide = entrySide == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        var price = GridMath.TakeProfitPrice(entrySide, execution.Price, config.TakeProfitPoints, TradingService.RulesFor(config).TickSize);
        var tp = new OrderEntity
        {
            Id = Ids.New("order"), CycleId = cycle.Id, ClientOrderId = Ids.New($"tp-{entry.GridLevel}"), ExchangeOrderId = "pending",
            Symbol = entry.Symbol, Side = tpSide.ToString().ToUpperInvariant(), Kind = "TAKE_PROFIT", Status = "PENDING_EXCHANGE",
            GridLevel = entry.GridLevel, Price = price, Quantity = execution.Quantity,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Orders.Add(tp);
        db.VirtualLots.Add(new VirtualLotEntity
        {
            Id = Ids.New("lot"), CycleId = cycle.Id, EntryOrderId = entry.Id, TakeProfitOrderId = tp.Id, Side = entry.Side,
            Status = "TP_PENDING", GridLevel = entry.GridLevel, EntryFillPrice = execution.Price,
            FilledQuantity = execution.Quantity, RemainingQuantity = execution.Quantity,
            TakeProfitPrice = price, EntryFee = execution.Fee
        });
        await db.SaveChangesAsync(ct);
        var selection = Selection(cycle);
        var adapter = environments.Adapter(selection.EnvironmentId);
        try
        {
            await adapter.PlaceOrdersAsync(selection, config, [tp], ct);
        }
        catch (TradingProblemException ex) when (ex.Code == "PROTECTIVE_ORDER_REJECTED")
        {
            await HandleProtectiveOrderRejectionAsync(cycle, selection, adapter, ex, ct);
        }
    }

    private async Task HandleProtectiveOrderRejectionAsync(CycleEntity cycle, ExecutionSelection selection,
        IExecutionAdapter adapter, TradingProblemException exception, CancellationToken ct)
    {
        if (cycle.State != "FAULT")
        {
            cycle.State = "FAULT";
            cycle.StateVersion++;
            db.RiskAlerts.Add(new RiskAlertEntity
            {
                Id = Ids.New("alert"), CycleId = cycle.Id, Severity = "CRITICAL",
                Code = "PROTECTIVE_ORDER_REJECTED",
                Message = $"A take-profit order was rejected after fallback; inspect the actual position. Venue: {exception.Message}",
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
    }

    private async Task CloseLotAsync(CycleEntity cycle, OrderEntity tp, ExecutionEntity execution, CancellationToken ct)
    {
        var lot = await db.VirtualLots.SingleOrDefaultAsync(x => x.TakeProfitOrderId == tp.Id, ct);
        if (lot is null) return;
        var closed = Math.Min(lot.RemainingQuantity, execution.Quantity);
        lot.RemainingQuantity -= closed;
        lot.ExitFee += execution.Fee;
        cycle.RealisedCyclePnl += lot.Side == "BUY"
            ? (execution.Price - lot.EntryFillPrice) * closed
            : (lot.EntryFillPrice - execution.Price) * closed;
        if (lot.RemainingQuantity <= 0m) lot.Status = "CLOSED";
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
