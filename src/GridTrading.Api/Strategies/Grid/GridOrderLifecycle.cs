using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Domain;
using GridTrading.Domain.Strategies.Grid;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed class GridOrderLifecycle(
    TradingDbContext db,
    ExecutionEnvironmentRegistry environments,
    ExecutionAccountOperationGate accountGate)
{
    public Task<int> ProcessFillsAsync(string accountId, IReadOnlyList<NormalizedExecutionFill> fills, CancellationToken ct) =>
        accountGate.RunAsync(accountId, () => ProcessFillsCoreAsync(accountId, fills, ct), ct);

    private async Task<int> ProcessFillsCoreAsync(string accountId, IReadOnlyList<NormalizedExecutionFill> fills, CancellationToken ct)
    {
        var processed = 0;
        var affected = new Dictionary<string, (CycleEntity Cycle, GridConfiguration Config)>();
        foreach (var fill in fills.OrderBy(x => x.OccurredAt))
        {
            var cycle = await FindCycleAsync(accountId, fill.ExchangeOrderId, fill.ClientOrderId, ct);
            if (cycle is null) continue;
            var config = DeserializeConfig(cycle);
            if (!await ApplyFillAsync(cycle, config, fill, ct)) continue;
            processed++;
            affected[cycle.Id] = (cycle, config);
        }

        foreach (var item in affected.Values.Where(x => x.Cycle.State == "RUNNING"))
            await MaintainEntryOrdersAsync(item.Cycle, item.Config, quote: null, ct);
        return processed;
    }

    public Task<int> ProcessOrderUpdatesAsync(string accountId, IReadOnlyList<NormalizedOrderUpdate> updates, CancellationToken ct) =>
        accountGate.RunAsync(accountId, () => ProcessOrderUpdatesCoreAsync(accountId, updates, ct), ct);

    private async Task<int> ProcessOrderUpdatesCoreAsync(string accountId, IReadOnlyList<NormalizedOrderUpdate> updates, CancellationToken ct)
    {
        var processed = 0;
        var affected = new Dictionary<string, (CycleEntity Cycle, GridConfiguration Config)>();
        foreach (var update in updates)
        {
            var order = await FindOrderAsync(accountId, update.ExchangeOrderId, update.ClientOrderId, ct);
            if (order is null) continue;
            var cycle = await db.Cycles.SingleAsync(x => x.Id == order.CycleId, ct);
            order.ExchangeOrderId = string.IsNullOrWhiteSpace(update.ExchangeOrderId) ? order.ExchangeOrderId : update.ExchangeOrderId;
            order.Status = update.Status;
            order.UpdatedAt = update.OccurredAt;
            processed++;
            if (update.Status == "CANCELLED" && update.FilledQuantity <= order.FilledQuantity && cycle.State == "RUNNING")
                affected[cycle.Id] = (cycle, DeserializeConfig(cycle));
        }
        await db.SaveChangesAsync(ct);
        foreach (var item in affected.Values)
            await MaintainEntryOrdersAsync(item.Cycle, item.Config, quote: null, ct);
        return processed;
    }

    public Task<decimal> ReconcileAsync(CycleEntity cycle, CancellationToken ct) =>
        accountGate.RunAsync(cycle.ExecutionAccountId, () => ReconcileCoreAsync(cycle, ct), ct);

    private async Task<decimal> ReconcileCoreAsync(CycleEntity cycle, CancellationToken ct)
    {
        var config = DeserializeConfig(cycle);
        var selection = Selection(cycle);
        var adapter = environments.Adapter(selection.EnvironmentId);
        var snapshot = await adapter.ReconcileAsync(selection, cycle, config, ct);

        foreach (var fill in snapshot.Fills.OrderBy(x => x.OccurredAt))
            await ApplyFillAsync(cycle, config, fill, ct);
        foreach (var update in snapshot.OrderUpdates)
        {
            var order = await FindOrderAsync(selection.AccountId, update.ExchangeOrderId, update.ClientOrderId, ct);
            if (order is null) continue;
            order.Status = update.Status;
            order.FilledQuantity = Math.Max(order.FilledQuantity, update.FilledQuantity);
            order.UpdatedAt = update.OccurredAt;
        }

        var pending = await db.Orders.Where(x => x.CycleId == cycle.Id && x.Status == "PENDING_EXCHANGE").ToListAsync(ct);
        foreach (var order in pending)
        {
            if (!snapshot.OpenOrdersByClientId.TryGetValue(order.ClientOrderId, out var exchangeId)) continue;
            order.ExchangeOrderId = exchangeId;
            order.Status = "NEW";
            order.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await adapter.PlaceOrdersAsync(selection, config, pending.Where(x => x.Status == "PENDING_EXCHANGE"), ct);

        var openClientIds = snapshot.OpenOrdersByClientId.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var order in await ActiveOrdersAsync(cycle.Id, ct))
        {
            if (order.Status == "PENDING_EXCHANGE" || openClientIds.Contains(order.ClientOrderId) || order.FilledQuantity >= order.Quantity) continue;
            order.Status = "CANCELLED";
            order.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await CancelExpiredPartialEntriesAsync(cycle, config, openClientIds, ct);

        cycle.ActualNetQuantity = snapshot.Position.Quantity;
        cycle.ReconstructedNetQuantity = await ReconstructedPositionAsync(cycle.Id, ct);
        cycle.LastReconciledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        if (cycle.State == "RUNNING") await MaintainEntryOrdersAsync(cycle, config, quote: null, ct);

        var finalFee = Math.Abs(snapshot.Position.PositionValue) * config.TakerFeeRate;
        var slippage = Math.Abs(snapshot.Position.PositionValue) * config.EstimatedExitSlippagePct / 100m;
        return GridMath.CalculateBasketPnl(new BasketPnlInput(cycle.RealisedCyclePnl, snapshot.Position.UnrealizedPnl,
            cycle.PaidFees, config.IncludeFunding ? cycle.AccruedFunding : 0m, finalFee, slippage)).LiquidationPnl;
    }

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

    private async Task<bool> ApplyFillAsync(CycleEntity cycle, GridConfiguration config, NormalizedExecutionFill fill, CancellationToken ct)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.CycleId == cycle.Id &&
            (x.ExchangeOrderId == fill.ExchangeOrderId ||
             (fill.ClientOrderId != null && x.ClientOrderId == fill.ClientOrderId)), ct);
        if (order is null || await db.Executions.AnyAsync(x => x.ExchangeExecutionId == fill.ExecutionId, ct)) return false;
        if (!string.IsNullOrWhiteSpace(fill.ExchangeOrderId)) order.ExchangeOrderId = fill.ExchangeOrderId;
        var terminalBeforeFill = order.Status is "CANCELLED" or "REJECTED";
        var execution = new ExecutionEntity
        {
            Id = Ids.New("execution"), ExchangeExecutionId = fill.ExecutionId, CycleId = cycle.Id, OrderId = order.Id,
            Side = fill.Side, Price = fill.Price, Quantity = fill.Quantity, Fee = fill.Fee, OccurredAt = fill.OccurredAt
        };
        db.Executions.Add(execution);
        order.FilledQuantity = Math.Min(order.Quantity, order.FilledQuantity + fill.Quantity);
        if (!terminalBeforeFill) order.Status = order.FilledQuantity >= order.Quantity ? "FILLED" : "PARTIALLY_FILLED";
        order.UpdatedAt = DateTimeOffset.UtcNow;
        cycle.PaidFees += fill.Fee;
        cycle.ActualNetQuantity += fill.Side == "BUY" ? fill.Quantity : -fill.Quantity;
        cycle.ReconstructedNetQuantity += fill.Side == "BUY" ? fill.Quantity : -fill.Quantity;

        if (order.Kind == "ENTRY") await CreateTakeProfitAsync(cycle, config, order, execution, ct);
        else if (order.Kind == "TAKE_PROFIT") await CloseLotAsync(cycle, order, execution, ct);
        await db.SaveChangesAsync(ct);
        return true;
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
        await environments.Adapter(selection.EnvironmentId).PlaceOrdersAsync(selection, config, [tp], ct);
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

    private async Task<CycleEntity?> FindCycleAsync(string accountId, string exchangeOrderId, string? clientOrderId, CancellationToken ct)
    {
        var order = await FindOrderAsync(accountId, exchangeOrderId, clientOrderId, ct);
        return order is null ? null : await db.Cycles.SingleAsync(x => x.Id == order.CycleId, ct);
    }

    private async Task<OrderEntity?> FindOrderAsync(string accountId, string exchangeOrderId, string? clientOrderId, CancellationToken ct) =>
        await (from order in db.Orders
               join cycle in db.Cycles on order.CycleId equals cycle.Id
               where cycle.ExecutionAccountId == accountId && !cycle.IsTerminal &&
                     (order.ExchangeOrderId == exchangeOrderId || (clientOrderId != null && order.ClientOrderId == clientOrderId))
               select order).FirstOrDefaultAsync(ct);

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

    private Task<List<OrderEntity>> ActiveOrdersAsync(string cycleId, CancellationToken ct) =>
        db.Orders.Where(x => x.CycleId == cycleId &&
            (x.Status == "PENDING_EXCHANGE" || x.Status == "NEW" || x.Status == "PARTIALLY_FILLED" || x.Status == "UNKNOWN"))
            .ToListAsync(ct);

    private async Task<decimal> ReconstructedPositionAsync(string cycleId, CancellationToken ct) =>
        (await db.Executions.Where(x => x.CycleId == cycleId).ToListAsync(ct))
            .Sum(x => x.Side == "BUY" ? x.Quantity : -x.Quantity);

    private static GridConfiguration DeserializeConfig(CycleEntity cycle) =>
        JsonSerializer.Deserialize<GridConfiguration>(cycle.FrozenConfigurationJson, JsonSupport.Options)!;
    private static ExecutionSelection Selection(CycleEntity cycle) =>
        new(cycle.ExecutionEnvironmentId, cycle.ExecutionAccountId);

    public static OrderEntity CreateEntry(CycleEntity cycle, string symbol, GridLevel level, decimal quantity) => new()
    {
        Id = Ids.New("order"), CycleId = cycle.Id,
        ClientOrderId = Ids.New($"grid-{level.Side.ToString()[0]}-{level.LevelIndex}"), ExchangeOrderId = "pending",
        Symbol = symbol, Side = level.Side.ToString().ToUpperInvariant(), Kind = "ENTRY", Status = "PENDING_EXCHANGE",
        GridLevel = level.LevelIndex, Price = level.EntryPrice, Quantity = quantity,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };
}
