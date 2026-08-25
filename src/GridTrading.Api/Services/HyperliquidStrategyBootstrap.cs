using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class HyperliquidStrategyBootstrap(IServiceScopeFactory scopeFactory) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var account = await db.HyperliquidAccounts.SingleOrDefaultAsync(
            x => x.Id == HyperliquidAccountBootstrap.DefaultAccountId && x.Enabled, ct);
        if (account is null || await db.Strategies.AnyAsync(x => x.ExchangeAccountId == account.Id && !x.Archived, ct)) return;

        var request = StrategyRequest.Default with
        {
            Name = "Hyperliquid Testnet SOL Grid",
            ExchangeAccountId = account.Id
        };
        var now = DateTimeOffset.UtcNow;
        db.Strategies.Add(new StrategyEntity
        {
            Id = Ids.New("strategy"), Name = request.Name, ExchangeAccountId = request.ExchangeAccountId,
            Symbol = request.Symbol, ConfigurationJson = JsonSerializer.Serialize(request, JsonSupport.Options),
            CreatedAt = now, UpdatedAt = now
        });
        db.AuditLogs.Add(new AuditEntity
        {
            ResourceId = account.Id, Action = "TESTNET_STRATEGY_BOOTSTRAPPED", Actor = "system",
            Detail = "Created a non-running Testnet strategy template for the configured API Wallet account.", OccurredAt = now
        });
        await db.SaveChangesAsync(ct);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
