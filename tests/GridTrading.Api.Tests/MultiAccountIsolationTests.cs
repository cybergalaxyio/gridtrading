using System.Collections.Concurrent;
using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Infrastructure;
using GridTrading.Api.Strategies.Grid.Configuration;
using GridTrading.Api.Services;
using GridTrading.Api.Strategies.Grid;
using GridTrading.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace GridTrading.Api.Tests;

public sealed class MultiAccountIsolationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IdenticalVenueFillIdsOnTwoAccountsCreateIndependentExecutionsAndProtection()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(Ct);
        var config = new GridConfiguration { Symbol = "SOL", CenterPrice = 100, TickSize = .1m, QuantityStep = .1m,
            MinOrderQuantity = .1m, MinOrderNotional = 1, MaxLevelsPerSide = 2, InitialGapPoints = 10,
            GridSpacingPoints = 10, TakeProfitPoints = 10, BaseLotSize = .2m, MaxNetLot = 5 };
        var plan = GridMath.BuildPlan(config, TradingService.RulesFor(config));
        foreach (var account in new[] { "account-a", "account-b" })
        {
            db.Cycles.Add(new CycleEntity { Id = account + "-cycle", StrategyId = account + "-strategy", ExecutionAccountId = account,
                ExecutionEnvironmentId = "test", State = "PAUSED", OperatorPaused = true, FrozenConfigurationJson = JsonSerializer.Serialize(config, JsonSupport.Options),
                FrozenPlanJson = JsonSerializer.Serialize(plan, JsonSupport.Options), ExitReason = "" });
            db.Orders.Add(new OrderEntity { Id = account + "-order", CycleId = account + "-cycle", ClientOrderId = account + "-client",
                ExchangeOrderId = "venue-order-1", Symbol = "SOL", Side = "BUY", Kind = "ENTRY", Status = "NEW", Price = 99, Quantity = .2m, GridLevel = 0 });
        }
        await db.SaveChangesAsync(Ct);
        var adapter = new RecordingAdapter();
        var lifecycle = new GridOrderLifecycle(db, new ExecutionEnvironmentRegistry([adapter]), new ExecutionAccountOperationGate());
        var fill = new NormalizedExecutionFill("same-venue-fill", "venue-order-1", null, "BUY", 99, .2m, .001m, DateTimeOffset.UtcNow);
        Assert.Equal(1, await lifecycle.ProcessFillsAsync("account-a", [fill], Ct));
        Assert.Empty(await db.Executions.Where(x => x.ExecutionAccountId == "account-b").ToListAsync(Ct));
        Assert.Equal(1, await lifecycle.ProcessFillsAsync("account-b", [fill], Ct));
        Assert.Equal(0, await lifecycle.ProcessFillsAsync("account-a", [fill], Ct));
        Assert.Equal(0, await lifecycle.ProcessFillsAsync("account-b", [fill], Ct));
        Assert.Equal(2, await db.Executions.CountAsync(Ct));
        Assert.Equal(2, await db.VirtualLots.CountAsync(Ct));
        Assert.Equal(new[] { "account-a", "account-b" }, adapter.Placements.Select(x => x.AccountId).ToArray());
        Assert.All(adapter.Placements, x => Assert.Equal("TAKE_PROFIT", x.Kind));
        var cycles = await db.Cycles.ToListAsync(Ct);
        Assert.All(cycles, cycle => Assert.Equal(.2m, cycle.ReconstructedNetQuantity));
    }

    [Fact]
    public async Task LegacyExecutionMigrationBackfillsOnceAndRetainsIdsAndDeduplication()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP INDEX "IX_Executions_ExecutionAccountId_ExchangeExecutionId";
            ALTER TABLE "Executions" DROP COLUMN "ExecutionAccountId";
            CREATE UNIQUE INDEX "IX_Executions_ExchangeExecutionId" ON "Executions" ("ExchangeExecutionId");
            INSERT INTO "Cycles" ("Id", "StrategyId", "ExecutionEnvironmentId", "ExecutionAccountId", "State", "StateVersion", "IsTerminal",
                "OperatorResetRequired", "OperatorPaused", "RiskPaused", "RiskRecoveryChecks", "FixedCenterPrice", "EntryGridPriceOffset",
                "ActualNetQuantity", "ReconstructedNetQuantity", "RealisedCyclePnl", "PaidFees", "AccruedFunding", "MaximumAdverseExcursion", "MaximumDrawdown",
                "FrozenConfigurationJson", "FrozenPlanJson", "ExitReason", "StartedAt", "LastReconciledAt")
            VALUES ('legacy-cycle', 'strategy', 'hyperliquid-mainnet', 'legacy-account', 'PAUSED', 1, 0, 0, 0, 0, 0, '0', '0',
                '0', '0', '0', '0', '0', '0', '0', '{}', '{}', '', '2026-09-17T00:00:00+00:00', '2026-09-17T00:00:00+00:00');
            INSERT INTO "Executions" ("Id", "ExchangeExecutionId", "ExchangeOrderId", "CycleId", "OrderId", "Side", "Price", "Quantity", "Fee", "OccurredAt")
            VALUES ('original-execution', 'original-venue-id', 'venue-order', 'legacy-cycle', 'order', 'BUY', '100', '1', '0.01', '2026-09-17T00:00:00+00:00');
            """;
        await command.ExecuteNonQueryAsync(Ct);
        await DatabaseCompatibility.EnsureExecutionSchemaAsync(db);
        await DatabaseCompatibility.EnsureExecutionSchemaAsync(db);
        var migrated = await db.Executions.SingleAsync(Ct);
        Assert.Equal("original-execution", migrated.Id);
        Assert.Equal("original-venue-id", migrated.ExchangeExecutionId);
        Assert.Equal("legacy-account", migrated.ExecutionAccountId);
        db.Executions.Add(new ExecutionEntity { Id = "other-account-execution", ExecutionAccountId = "other-account", ExchangeExecutionId = migrated.ExchangeExecutionId,
            CycleId = "other-cycle", OrderId = "other-order", Side = "BUY" });
        await db.SaveChangesAsync(Ct);
        db.Executions.Add(new ExecutionEntity { Id = "duplicate", ExecutionAccountId = "legacy-account", ExchangeExecutionId = migrated.ExchangeExecutionId,
            CycleId = "legacy-cycle", OrderId = "order", Side = "BUY" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task SupervisorAddsAndRemovesAccountsWithoutRestartingHealthyFeeds()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var desired = new ConcurrentDictionary<string, byte>();
        desired.TryAdd("market-mainnet", 0);
        desired.TryAdd("account-a", 0);
        var starts = new ConcurrentDictionary<string, int>();
        var stopped = new ConcurrentDictionary<string, bool>();
        var run = DynamicWorkerSupervisor.RunAsync<string>(
            _ => Task.FromResult<IReadOnlyCollection<string>>(desired.Keys.ToArray()), async (key, ct) =>
            {
                var count = starts.AddOrUpdate(key, 1, (_, value) => value + 1);
                if (key == "account-a" && count == 1) throw new IOException("simulated account disconnect");
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { stopped[key] = true; }
            }, NullLogger.Instance, stop.Token, TimeSpan.FromMilliseconds(20));
        try
        {
            await Until(() => starts.GetValueOrDefault("account-a") == 2);
            desired.TryAdd("account-b", 0);
            await Until(() => starts.ContainsKey("account-b"));
            desired.TryRemove("account-a", out _);
            await Until(() => stopped.ContainsKey("account-a"));
            Assert.Equal(1, starts["market-mainnet"]);
            Assert.Equal(1, starts["account-b"]);
            Assert.False(stopped.ContainsKey("account-b"));
        }
        finally { stop.Cancel(); await run; }
        Assert.True(stopped["account-b"]);
        Assert.True(stopped["market-mainnet"]);
    }

    [Fact]
    public async Task ReconciliationFailureOnOneAccountDoesNotPreventTheOtherFromUpdating()
    {
        var path = Path.Combine(Path.GetTempPath(), $"reconciliation-accounts-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging().AddSignalR();
            services.AddDbContext<TradingDbContext>(options => options.UseSqlite($"Data Source={path};Pooling=False"));
            services.AddSingleton<ExecutionAccountOperationGate>();
            services.AddSingleton<MarketState>();
            services.AddSingleton<PreviewStore>();
            var adapter = new RecordingAdapter { FailAccount = "account-a" };
            services.AddSingleton<IExecutionAdapter>(adapter);
            services.AddScoped<ExecutionEnvironmentRegistry>();
            services.AddScoped<GridOrderLifecycle>();
            services.AddScoped<GridConfigurationService>();
            services.AddScoped<GridStrategyWorkflow>();
            services.AddScoped<TradingService>();
            await using var provider = services.BuildServiceProvider();
            using (var setup = provider.CreateScope())
            {
                var db = setup.ServiceProvider.GetRequiredService<TradingDbContext>();
                await db.Database.EnsureCreatedAsync(Ct);
                foreach (var account in new[] { "account-a", "account-b" })
                    db.Cycles.Add(new CycleEntity { Id = account, StrategyId = account, ExecutionAccountId = account,
                        ExecutionEnvironmentId = "test", State = "FAULT", FrozenConfigurationJson = JsonSerializer.Serialize(new GridConfiguration { Symbol = "SOL" }, JsonSupport.Options),
                        FrozenPlanJson = "{}", ExitReason = "" });
                await db.SaveChangesAsync(Ct);
            }
            var service = new GridReconciliationService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<GridReconciliationService>.Instance);
            await Task.WhenAll(service.TickAccountAsync("account-a", Ct), service.TickAccountAsync("account-b", Ct));
            using var verify = provider.CreateScope();
            var cycles = await verify.ServiceProvider.GetRequiredService<TradingDbContext>().Cycles.ToListAsync(Ct);
            Assert.Equal(default, cycles.Single(x => x.ExecutionAccountId == "account-a").LastReconciledAt);
            Assert.NotEqual(default, cycles.Single(x => x.ExecutionAccountId == "account-b").LastReconciledAt);
        }
        finally { File.Delete(path); }
    }

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    private sealed class RecordingAdapter : IExecutionAdapter
    {
        public string? FailAccount { get; init; }
        public List<(string AccountId, string Kind)> Placements { get; } = [];
        public ExecutionEnvironmentDescriptor Environment { get; } = new("test", "PAPER", "TEST", "Test");
        public Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ExecutionAccountDescriptor>>(
            [new("account-a", "test", "A", true), new("account-b", "test", "B", true)]);
        public Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection selection, string symbol, decimal? referencePrice, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection selection, string symbol, CancellationToken ct) => Task.FromResult(new ExecutionQuote(99.9m, 100.1m, 100, DateTimeOffset.UtcNow));
        public Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection selection, string symbol, CancellationToken ct) => GetQuoteAsync(selection, symbol, ct);
        public Task PlaceOrdersAsync(ExecutionSelection selection, GridConfiguration config, IEnumerable<OrderEntity> orders, CancellationToken ct)
        {
            foreach (var order in orders) { Placements.Add((selection.AccountId, order.Kind)); order.ExchangeOrderId = selection.AccountId + order.Id; order.Status = "NEW"; }
            return Task.CompletedTask;
        }
        public Task CancelOrdersAsync(ExecutionSelection selection, IEnumerable<OrderEntity> orders, CancellationToken ct) => Task.CompletedTask;
        public Task<decimal> FlattenAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config, CancellationToken ct) =>
            selection.AccountId == FailAccount ? throw new IOException("Simulated account outage") :
                Task.FromResult(new ExecutionReconciliationSnapshot([], [], [], new Dictionary<string, string>(), new(0, 0, 0)));
    }
}
