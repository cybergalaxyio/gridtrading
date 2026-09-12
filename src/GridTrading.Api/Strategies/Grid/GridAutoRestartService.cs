using GridTrading.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Strategies.Grid;

public sealed class GridAutoRestartService(IServiceScopeFactory scopeFactory, ILogger<GridAutoRestartService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using (var scope = scopeFactory.CreateScope())
            await scope.ServiceProvider.GetRequiredService<GridStrategyWorkflow>().RecoverInterruptedAutoRestartsAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Automatic grid restart tick failed."); }
        }
    }

    public async Task TickAsync(CancellationToken ct)
    {
        string[] pending;
        using (var scope = scopeFactory.CreateScope())
            pending = await scope.ServiceProvider.GetRequiredService<TradingDbContext>().Operations.AsNoTracking()
                .Where(x => x.Type == "AUTO_RESTART" && x.Status == "ACCEPTED").Select(x => x.Id).ToArrayAsync(ct);
        foreach (var id in pending)
        {
            // One failure must not prevent another strategy's restart.
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<GridStrategyWorkflow>().ProcessAutoRestartAsync(id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Automatic restart {OperationId} could not be processed.", id); }
        }
    }
}
