using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Strategies.Grid;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class PaperExecutionService(IServiceScopeFactory scopeFactory, MarketState market) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(750));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await MatchOrdersAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch { }
        }
    }

    private async Task MatchOrdersAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var lifecycle = scope.ServiceProvider.GetRequiredService<GridOrderLifecycle>();
        var trading = scope.ServiceProvider.GetRequiredService<TradingService>();
        var cycles = await db.Cycles.Where(x => x.ExecutionEnvironmentId == ExecutionEnvironmentIds.PaperLocal &&
            !x.IsTerminal && (x.State == "RUNNING" || x.State == "PAUSED")).ToListAsync(ct);

        foreach (var cycle in cycles)
        {
            var config = JsonSerializer.Deserialize<GridConfiguration>(cycle.FrozenConfigurationJson, JsonSupport.Options)!;
            var quote = market.Snapshot(config.Symbol);
            if (quote.IsStale) continue;
            var active = await db.Orders.Where(x => x.CycleId == cycle.Id && x.Status == "NEW").ToListAsync(ct);
            var fills = active.Where(order => order.Side == "BUY" ? quote.Ask <= order.Price : quote.Bid >= order.Price)
                .Select(order => new NormalizedExecutionFill(
                    Ids.New("paper_fill"), order.ExchangeOrderId, order.ClientOrderId, order.Side,
                    order.Price, order.Quantity - order.FilledQuantity,
                    order.Price * (order.Quantity - order.FilledQuantity) * config.MakerFeeRate,
                    DateTimeOffset.UtcNow))
                .ToArray();
            if (fills.Length > 0)
                await lifecycle.ProcessFillsAsync(cycle.ExecutionAccountId, fills, ct);

            cycle.LastReconciledAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            var liquidationPnl = cycle.RealisedCyclePnl - cycle.PaidFees - cycle.AccruedFunding;
            var takeProfitTriggered = config.BasketTakeProfitUsdt > 0m && liquidationPnl >= config.BasketTakeProfitUsdt;
            var stopLossTriggered = GridMath.BasketStopLossTriggered(liquidationPnl, config.BasketStopLossUsdt);
            if (cycle.State is "RUNNING" or "PAUSED" && (takeProfitTriggered || stopLossTriggered))
            {
                var reason = takeProfitTriggered ? "BASKET_TAKE_PROFIT" : "BASKET_STOP_LOSS";
                await trading.Command(cycle.Id, "CLOSE", reason,
                    $"basket-{cycle.Id}-{cycle.StateVersion}", null, false, ct);
            }
        }
    }
}
