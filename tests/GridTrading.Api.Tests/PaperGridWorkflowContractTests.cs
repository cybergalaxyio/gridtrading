using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Exchanges.Paper;
using GridTrading.Api.Infrastructure;
using GridTrading.Api.Services;
using GridTrading.Api.Strategies.Grid;
using GridTrading.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Tests;

public sealed class PaperGridWorkflowContractTests
{
    [Fact]
    public async Task FragmentedEntryCreatesFragmentedTpAndKeepsLevelOccupied()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
        await using var db = new TradingDbContext(options);
        await db.Database.EnsureCreatedAsync(ct);
        var market = new MarketState();
        market.Tick();
        var center = market.Snapshot("SOLUSDT").Mid;
        var config = new GridConfiguration
        {
            Symbol = "SOLUSDT", TickSize = .001m, QuantityStep = .01m,
            MinOrderQuantity = .01m, MinOrderNotional = 1m, CenterPrice = center,
            MaxLevelsPerSide = 10, InitialGapPoints = 10m, GridSpacingPoints = 10m,
            TakeProfitPoints = 20m, BaseLotSize = .57m, MaxNetLot = 20m
        };
        var plan = GridMath.BuildPlan(config, TradingService.RulesFor(config));
        var now = DateTimeOffset.UtcNow;
        var cycle = new CycleEntity
        {
            Id = "cycle-paper", StrategyId = "strategy-paper",
            ExecutionEnvironmentId = ExecutionEnvironmentIds.PaperLocal,
            ExecutionAccountId = PaperExecutionAdapter.AccountId,
            State = "RUNNING", FixedCenterPrice = center,
            FrozenConfigurationJson = JsonSerializer.Serialize(config, JsonSupport.Options),
            FrozenPlanJson = JsonSerializer.Serialize(plan, JsonSupport.Options),
            ExitReason = "", StartedAt = now, LastReconciledAt = now
        };
        var level = plan.Levels.Single(x => x.Side == OrderSide.Sell && x.LevelIndex == 7);
        var entry = new OrderEntity
        {
            Id = "entry-s7", CycleId = cycle.Id, ClientOrderId = "entry-s7", ExchangeOrderId = "paper-s7",
            Symbol = config.Symbol, Side = "SELL", Kind = "ENTRY", Status = "NEW", GridLevel = 7,
            Price = level.EntryPrice, Quantity = .57m, CreatedAt = now, UpdatedAt = now
        };
        db.Cycles.Add(cycle);
        db.Orders.Add(entry);
        await db.SaveChangesAsync(ct);

        var adapter = new PaperExecutionAdapter(market, db);
        var lifecycle = new GridOrderLifecycle(db, new ExecutionEnvironmentRegistry([adapter]),
            new ExecutionAccountOperationGate());
        var fills = new[]
        {
            Fill("paper-fill-1", .16m, now),
            Fill("paper-fill-2", .18m, now.AddSeconds(1)),
            Fill("paper-fill-3", .23m, now.AddSeconds(2))
        };

        var processed = await lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId, fills, ct);

        Assert.Equal(3, processed);
        var takeProfits = await db.Orders.Where(x => x.Kind == "TAKE_PROFIT" && x.GridLevel == 7).ToListAsync(ct);
        Assert.Equal(3, takeProfits.Count);
        Assert.Equal(.57m, takeProfits.Sum(x => x.Quantity));
        Assert.Equal(3, await db.VirtualLots.CountAsync(x => x.GridLevel == 7 && x.Status == "TP_PENDING", ct));
        Assert.DoesNotContain(await db.Orders.Where(x => x.Kind == "ENTRY" &&
            (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED")).ToListAsync(ct), x => x.GridLevel == 7);
    }

    [Fact]
    public async Task ExpiredPartialEntryReconciliationWorksWithSqliteAndCancelsRemainder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
        await using var db = new TradingDbContext(options);
        await db.Database.EnsureCreatedAsync(ct);
        var market = new MarketState();
        market.Tick();
        var center = market.Snapshot("SOLUSDT").Mid;
        var config = new GridConfiguration
        {
            Symbol = "SOLUSDT", TickSize = .001m, QuantityStep = .01m,
            MinOrderQuantity = .01m, MinOrderNotional = 1m, CenterPrice = center,
            MaxLevelsPerSide = 10, InitialGapPoints = 10m, GridSpacingPoints = 10m,
            TakeProfitPoints = 20m, BaseLotSize = .2m, MaxNetLot = 20m,
            PartialFillCancelAfterMinutes = 1
        };
        var plan = GridMath.BuildPlan(config, TradingService.RulesFor(config));
        var now = DateTimeOffset.UtcNow;
        var cycle = new CycleEntity
        {
            Id = "cycle-partial", StrategyId = "strategy-paper",
            ExecutionEnvironmentId = ExecutionEnvironmentIds.PaperLocal,
            ExecutionAccountId = PaperExecutionAdapter.AccountId,
            State = "PAUSED", FixedCenterPrice = center, ActualNetQuantity = .1m,
            FrozenConfigurationJson = JsonSerializer.Serialize(config, JsonSupport.Options),
            FrozenPlanJson = JsonSerializer.Serialize(plan, JsonSupport.Options),
            ExitReason = "", StartedAt = now, LastReconciledAt = now
        };
        var entry = new OrderEntity
        {
            Id = "entry-partial", CycleId = cycle.Id, ClientOrderId = "entry-partial",
            ExchangeOrderId = "paper-partial", Symbol = config.Symbol, Side = "BUY",
            Kind = "ENTRY", Status = "PARTIALLY_FILLED", GridLevel = 3,
            Price = center, Quantity = .2m, FilledQuantity = .1m,
            CreatedAt = now.AddMinutes(-5), UpdatedAt = now.AddMinutes(-5)
        };
        db.Cycles.Add(cycle);
        db.Orders.Add(entry);
        db.Executions.Add(new ExecutionEntity
        {
            Id = "execution-partial", ExchangeExecutionId = "fill-partial",
            CycleId = cycle.Id, OrderId = entry.Id, Side = "BUY", Price = center,
            Quantity = .1m, Fee = 0m, OccurredAt = now.AddMinutes(-5)
        });
        await db.SaveChangesAsync(ct);

        var adapter = new PaperExecutionAdapter(market, db);
        var lifecycle = new GridOrderLifecycle(db, new ExecutionEnvironmentRegistry([adapter]),
            new ExecutionAccountOperationGate());

        await lifecycle.ReconcileAsync(cycle, ct);

        Assert.Equal("CANCELLED", entry.Status);
    }

    private static NormalizedExecutionFill Fill(string id, decimal quantity, DateTimeOffset at) =>
        new(id, "paper-s7", "entry-s7", "SELL", 145m, quantity, 0m, at);
}
