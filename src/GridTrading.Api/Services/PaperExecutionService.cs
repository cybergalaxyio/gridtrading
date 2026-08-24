using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Hubs;
using GridTrading.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class PaperExecutionService(IServiceScopeFactory scopeFactory, MarketState market, IHubContext<TradingHub> hub) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(750));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await MatchOrders(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { /* A later reconciliation tick retries; the API logger remains free of sensitive payloads. */ }
        }
    }

    private async Task MatchOrders(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<TradingService>();
        var cycles = await db.Cycles.Where(x => !x.IsTerminal && (x.State == "RUNNING" || x.State == "PAUSED")).ToListAsync(ct);
        foreach (var cycle in cycles)
        {
            var config = JsonSerializer.Deserialize<GridConfiguration>(cycle.FrozenConfigurationJson, JsonSupport.Options)!;
            var plan = JsonSerializer.Deserialize<GridPlan>(cycle.FrozenPlanJson, JsonSupport.Options)!;
            var quote = market.Snapshot(config.Symbol);
            if (quote.IsStale) continue;
            var active = await db.Orders.Where(x => x.CycleId == cycle.Id && x.Status == "NEW").ToListAsync(ct);
            foreach (var order in active)
            {
                var touched = order.Side == "BUY" ? quote.Ask <= order.Price : quote.Bid >= order.Price;
                if (!touched) continue;
                order.Status = "FILLED"; order.FilledQuantity = order.Quantity; order.UpdatedAt = DateTimeOffset.UtcNow;
                var fee = order.Price * order.Quantity * config.MakerFeeRate;
                cycle.PaidFees += fee;
                cycle.ActualNetQuantity += order.Side == "BUY" ? order.Quantity : -order.Quantity;
                cycle.ReconstructedNetQuantity = cycle.ActualNetQuantity;
                var execution = new ExecutionEntity
                {
                    Id = Ids.New("execution"), ExchangeExecutionId = Ids.New("paper_fill"), CycleId = cycle.Id,
                    OrderId = order.Id, Side = order.Side, Price = order.Price, Quantity = order.Quantity,
                    Fee = fee, OccurredAt = DateTimeOffset.UtcNow
                };
                db.Executions.Add(execution);

                if (order.Kind == "ENTRY") CreateTakeProfit(db, cycle, config, order, fee);
                else if (order.Kind == "TAKE_PROFIT") CloseVirtualLot(db, cycle, config, plan, order, fee);
                await hub.Clients.Group($"cycle:{cycle.Id}").SendAsync("ExecutionReceived", Envelope("ExecutionReceived", cycle.Id, cycle.StateVersion, execution), ct);
            }
            cycle.LastReconciledAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            var liquidationPnl = cycle.RealisedCyclePnl - cycle.PaidFees - cycle.AccruedFunding;
            if (cycle.State is "RUNNING" or "PAUSED" &&
                (liquidationPnl >= config.BasketTakeProfitUsdt || liquidationPnl <= -config.BasketStopLossUsdt))
            {
                var reason = liquidationPnl >= config.BasketTakeProfitUsdt ? "BASKET_TAKE_PROFIT" : "BASKET_STOP_LOSS";
                await service.Command(cycle.Id, "CLOSE", reason, $"basket-{cycle.Id}-{cycle.StateVersion}", null, false, ct);
            }
        }
    }

    private static void CreateTakeProfit(TradingDbContext db, CycleEntity cycle, GridConfiguration config, OrderEntity entry, decimal fee)
    {
        var entrySide = Enum.Parse<OrderSide>(entry.Side, true);
        var tpSide = entrySide == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;
        var price = GridMath.TakeProfitPrice(entrySide, entry.Price, config.TakeProfitPoints, TradingService.SolRules.TickSize);
        var active = db.Orders.Local.Where(x => x.CycleId == cycle.Id && x.Status == "NEW")
            .Select(x => new ActiveOrderReservation(Enum.Parse<OrderSide>(x.Side, true), x.Quantity - x.FilledQuantity));
        var quantity = GridMath.AllowedOrderQuantity(tpSide, entry.Quantity, cycle.ActualNetQuantity, active, config.MaxNetLot, TradingService.SolRules);
        if (quantity <= 0m) return;
        var tp = new OrderEntity
        {
            Id = Ids.New("order"), CycleId = cycle.Id, ClientOrderId = Ids.New($"tp-{entry.GridLevel}"), ExchangeOrderId = Ids.New("paper"),
            Symbol = entry.Symbol, Side = tpSide.ToString().ToUpperInvariant(), Kind = "TAKE_PROFIT", Status = "NEW",
            GridLevel = entry.GridLevel, Price = price, Quantity = quantity, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Orders.Add(tp);
        db.VirtualLots.Add(new VirtualLotEntity
        {
            Id = Ids.New("lot"), CycleId = cycle.Id, EntryOrderId = entry.Id, TakeProfitOrderId = tp.Id,
            Side = entry.Side, Status = "TP_PENDING", GridLevel = entry.GridLevel, EntryFillPrice = entry.Price,
            FilledQuantity = entry.Quantity, RemainingQuantity = quantity, TakeProfitPrice = price, EntryFee = fee
        });
    }

    private static void CloseVirtualLot(TradingDbContext db, CycleEntity cycle, GridConfiguration config, GridPlan plan, OrderEntity tp, decimal fee)
    {
        var lot = db.VirtualLots.SingleOrDefault(x => x.TakeProfitOrderId == tp.Id);
        if (lot is null) return;
        lot.RemainingQuantity = 0m; lot.Status = "CLOSED"; lot.ExitFee += fee;
        cycle.RealisedCyclePnl += lot.Side == "BUY"
            ? (tp.Price - lot.EntryFillPrice) * tp.Quantity
            : (lot.EntryFillPrice - tp.Price) * tp.Quantity;
        if (cycle.State != "RUNNING") return;
        var side = Enum.Parse<OrderSide>(lot.Side, true);
        var level = plan.Levels.Single(x => x.Side == side && x.LevelIndex == lot.GridLevel);
        var activeOrders = db.Orders.Local.Where(x => x.CycleId == cycle.Id && x.Status == "NEW")
            .Select(x => new ActiveOrderReservation(Enum.Parse<OrderSide>(x.Side, true), x.Quantity - x.FilledQuantity));
        var quantity = GridMath.AllowedOrderQuantity(side, level.PlannedQuantity, cycle.ActualNetQuantity, activeOrders, config.MaxNetLot, TradingService.SolRules);
        if (quantity <= 0m) return;
        db.Orders.Add(new OrderEntity
        {
            Id = Ids.New("order"), CycleId = cycle.Id, ClientOrderId = Ids.New($"reentry-{side}-{level.LevelIndex}"),
            ExchangeOrderId = Ids.New("paper"), Symbol = config.Symbol, Side = lot.Side, Kind = "ENTRY", Status = "NEW",
            GridLevel = level.LevelIndex, Price = level.EntryPrice, Quantity = quantity, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private static object Envelope(string type, string aggregateId, long version, object payload) => new
    {
        eventId = Ids.New("evt"), eventType = type, occurredAt = DateTimeOffset.UtcNow, aggregateType = "Cycle",
        aggregateId, aggregateVersion = version, sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), correlationId = Ids.New("corr"), payload
    };
}
