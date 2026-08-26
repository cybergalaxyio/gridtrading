using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Services;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed class GridReconciliationService(
    IServiceScopeFactory scopeFactory,
    ILogger<GridReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "GRID reconciliation tick failed."); }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var lifecycle = scope.ServiceProvider.GetRequiredService<GridOrderLifecycle>();
        var trading = scope.ServiceProvider.GetRequiredService<TradingService>();
        var cycles = await db.Cycles.Where(x => !x.IsTerminal &&
            (x.State == "RUNNING" || x.State == "PAUSED" || x.State == "CLOSING")).ToListAsync(ct);

        foreach (var cycle in cycles)
        {
            var config = JsonSerializer.Deserialize<GridConfiguration>(cycle.FrozenConfigurationJson, JsonSupport.Options)!;
            if (DateTimeOffset.UtcNow - cycle.LastReconciledAt <
                TimeSpan.FromSeconds(Math.Max(2, config.ReconcileIntervalSeconds))) continue;
            try
            {
                var liquidationPnl = await lifecycle.ReconcileAsync(cycle, ct);
                var takeProfit = config.BasketTakeProfitUsdt > 0m && liquidationPnl >= config.BasketTakeProfitUsdt;
                var stopLoss = GridMath.BasketStopLossTriggered(liquidationPnl, config.BasketStopLossUsdt);
                if (cycle.State is "RUNNING" or "PAUSED" && (takeProfit || stopLoss))
                {
                    var reason = takeProfit ? "BASKET_TAKE_PROFIT" : "BASKET_STOP_LOSS";
                    await trading.Command(cycle.Id, "CLOSE", reason,
                        $"basket-{cycle.Id}-{cycle.StateVersion}", null, false, ct);
                }
            }
            catch (TradingProblemException ex) when (ex.Code == "TESTNET_ORDER_REJECTED")
            {
                cycle.State = "FAULT";
                cycle.StateVersion++;
                db.RiskAlerts.Add(new RiskAlertEntity
                {
                    Id = Ids.New("alert"), CycleId = cycle.Id, Severity = "CRITICAL",
                    Code = "PROTECTIVE_ORDER_REJECTED",
                    Message = "The execution venue rejected a strategy order; inspect the actual position.",
                    CreatedAt = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync(ct);
                logger.LogError("GRID cycle {CycleId} entered FAULT after an order rejection.", cycle.Id);
            }
        }
    }
}
