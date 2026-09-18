using GridTrading.Api.Data;
using GridTrading.Api.Services;
using GridTrading.Api.Strategies.Grid.Configuration;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed class GridReconciliationService(
    IServiceScopeFactory scopeFactory,
    ILogger<GridReconciliationService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => DynamicWorkerSupervisor.RunAsync(
        ActiveAccountsAsync, RunAccountAsync, logger, stoppingToken);

    private async Task<IReadOnlyCollection<string>> ActiveAccountsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TradingDbContext>().Cycles.AsNoTracking()
            .Where(x => !x.IsTerminal).Select(x => x.ExecutionAccountId).Distinct().ToArrayAsync(ct);
    }

    private async Task RunAccountAsync(string accountId, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try { await TickAccountAsync(accountId, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "GRID reconciliation failed for account {AccountId}.", accountId); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public async Task TickAccountAsync(string accountId, CancellationToken ct)
    {
        string[] cycles;
        using (var scope = scopeFactory.CreateScope())
            cycles = await scope.ServiceProvider.GetRequiredService<TradingDbContext>().Cycles.AsNoTracking()
                .Where(x => x.ExecutionAccountId == accountId && !x.IsTerminal &&
                    (x.State == "RUNNING" || x.State == "PAUSED" || x.State == "CLOSING" || x.State == "FAULT"))
                .Select(x => x.Id).ToArrayAsync(ct);

        foreach (var cycleId in cycles)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var lifecycle = scope.ServiceProvider.GetRequiredService<GridOrderLifecycle>();
            var trading = scope.ServiceProvider.GetRequiredService<TradingService>();
            var cycle = await db.Cycles.SingleAsync(x => x.Id == cycleId, ct);
            if (cycle.IsTerminal) continue;
            var config = GridConfigurationCodec.ReadFrozen(cycle.FrozenConfigurationJson);
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
                        $"basket-{cycle.Id}-{cycle.StateVersion}", null, false, ct, automaticClose: true);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "GRID reconciliation failed for cycle {CycleId} on account {AccountId}.", cycle.Id, accountId);
            }
        }
    }
}
