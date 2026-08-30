using System.Text.Json;
using GridTrading.Api.Contracts;
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
    public async Task StartFailureCreatesCriticalAlertWithOriginalCause()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
        await using var db = new TradingDbContext(options);
        await db.Database.EnsureCreatedAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var config = FaultConfig();
        var plan = GridMath.BuildPlan(config, TradingService.RulesFor(config));
        db.Strategies.Add(new StrategyEntity
        {
            Id = "strategy-start-fault", Name = "Start fault", Symbol = config.Symbol,
            DefaultExecutionEnvironmentId = FaultingExecutionAdapter.EnvironmentId,
            DefaultExecutionAccountId = FaultingExecutionAdapter.AccountId,
            ConfigurationJson = JsonSerializer.Serialize(StrategyRequest.Default, JsonSupport.Options),
            CreatedAt = now, UpdatedAt = now
        });
        await db.SaveChangesAsync(ct);

        var previews = new PreviewStore();
        previews.Items["preview-start-fault"] = new PreviewCacheItem(
            "preview-start-fault", "strategy-start-fault", 1,
            FaultingExecutionAdapter.EnvironmentId, FaultingExecutionAdapter.AccountId,
            now.AddMinutes(5), config, plan);
        var adapter = new FaultingExecutionAdapter(
            new TradingProblemException(503, "VENUE_UNAVAILABLE", "venue refused the initial order batch"));
        var registry = new ExecutionEnvironmentRegistry([adapter]);
        var lifecycle = new GridOrderLifecycle(db, registry, new ExecutionAccountOperationGate());
        var workflow = new GridStrategyWorkflow(db, new MarketState(), previews, registry, lifecycle, null!);

        var problem = await Assert.ThrowsAsync<TradingProblemException>(() => workflow.StartCycleAsync(
            "strategy-start-fault",
            new StartCycleRequest("preview-start-fault", config.CenterPrice,
                new OperatorConfirmation(true, true, FaultingExecutionAdapter.EnvironmentId)),
            "start-fault-key", ct));

        Assert.Equal("VENUE_UNAVAILABLE", problem.Code);
        var cycle = await db.Cycles.SingleAsync(ct);
        Assert.Equal("FAULT", cycle.State);
        Assert.True(cycle.IsTerminal);
        var alert = await db.RiskAlerts.SingleAsync(ct);
        Assert.Equal("CRITICAL", alert.Severity);
        Assert.Equal("START_FAILED", alert.Code);
        Assert.Contains(cycle.Id, alert.Message);
        Assert.Contains("VENUE_UNAVAILABLE", alert.Message);
        Assert.Contains("venue refused the initial order batch", alert.Message);
    }

    [Fact]
    public async Task FlattenResidualCreatesCriticalAlertWithResidualQuantity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
        await using var db = new TradingDbContext(options);
        await db.Database.EnsureCreatedAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var config = FaultConfig();
        var cycle = new CycleEntity
        {
            Id = "cycle-flatten-fault", StrategyId = "strategy-flatten-fault",
            ExecutionEnvironmentId = FaultingExecutionAdapter.EnvironmentId,
            ExecutionAccountId = FaultingExecutionAdapter.AccountId, State = "FAULT", StateVersion = 3,
            FrozenConfigurationJson = JsonSerializer.Serialize(config, JsonSupport.Options),
            FrozenPlanJson = "{}", ExitReason = "", StartedAt = now, LastReconciledAt = now
        };
        db.Cycles.Add(cycle);
        await db.SaveChangesAsync(ct);

        var adapter = new FaultingExecutionAdapter(flattenResidual: .25m);
        var registry = new ExecutionEnvironmentRegistry([adapter]);
        var lifecycle = new GridOrderLifecycle(db, registry, new ExecutionAccountOperationGate());
        var workflow = new GridStrategyWorkflow(db, new MarketState(), new PreviewStore(), registry, lifecycle, null!);

        var problem = await Assert.ThrowsAsync<TradingProblemException>(() => workflow.CommandAsync(
            cycle.Id, "CLOSE", "test", "flatten-fault-key", cycle.StateVersion, false, ct));

        Assert.Equal("FLATTEN_INCOMPLETE", problem.Code);
        Assert.Equal("FAULT", cycle.State);
        var alert = await db.RiskAlerts.SingleAsync(ct);
        Assert.Equal("CRITICAL", alert.Severity);
        Assert.Equal("FLATTEN_RESIDUAL_POSITION", alert.Code);
        Assert.Contains(cycle.Id, alert.Message);
        Assert.Contains("0.25", alert.Message);
    }

    [Fact]
    public async Task FragmentedEntryAmendsOneTpAndKeepsLevelOccupied()
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
        var takeProfit = Assert.Single(takeProfits);
        Assert.Equal(.57m, takeProfit.Quantity);
        var lot = await db.VirtualLots.SingleAsync(x => x.GridLevel == 7 && x.Status == "TP_PENDING", ct);
        Assert.Equal(.57m, lot.FilledQuantity);
        Assert.Equal(.57m, lot.RemainingQuantity);
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

    private static GridConfiguration FaultConfig() => new()
    {
        Symbol = "SOLUSDT", TickSize = .1m, QuantityStep = .1m,
        MinOrderQuantity = .1m, MinOrderNotional = 1m, CenterPrice = 100m,
        MaxLevelsPerSide = 2, InitialGapPoints = 1m, GridSpacingPoints = 1m,
        TakeProfitPoints = 1m, BaseLotSize = 1m, MaxTradeLot = 1m, MaxNetLot = 5m
    };

    private sealed class FaultingExecutionAdapter(
        TradingProblemException? placeFailure = null,
        decimal flattenResidual = 0m) : IExecutionAdapter
    {
        public const string EnvironmentId = "fault-test";
        public const string AccountId = "fault-account";
        public ExecutionEnvironmentDescriptor Environment { get; } =
            new(EnvironmentId, "FAULT_TEST", "TEST", "Fault test");

        public Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExecutionAccountDescriptor>>([new(AccountId, EnvironmentId, "Fault account", true)]);
        public Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection selection, string symbol,
            decimal? referencePrice, CancellationToken ct) => Task.FromResult(new ExecutionInstrument(
                symbol, EnvironmentId, 0, 1, 100m, .1m, .1m, .1m, 1m, 10, 0m, 0m, "TEST", DateTimeOffset.UtcNow));
        public Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection selection, string symbol, CancellationToken ct) =>
            Task.FromResult(new ExecutionQuote(99m, 101m, 100m, DateTimeOffset.UtcNow));
        public Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection selection, string symbol, CancellationToken ct) =>
            GetQuoteAsync(selection, symbol, ct);
        public Task PlaceOrdersAsync(ExecutionSelection selection, GridConfiguration config,
            IEnumerable<OrderEntity> orders, CancellationToken ct) => placeFailure is null
                ? Task.CompletedTask : Task.FromException(placeFailure);
        public Task CancelOrdersAsync(ExecutionSelection selection, IEnumerable<OrderEntity> orders, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<decimal> FlattenAsync(ExecutionSelection selection, CycleEntity cycle,
            GridConfiguration config, CancellationToken ct) => Task.FromResult(flattenResidual);
        public Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection selection, CycleEntity cycle,
            GridConfiguration config, CancellationToken ct) => Task.FromResult(new ExecutionReconciliationSnapshot(
                [], [], [], new Dictionary<string, string>(), new ExecutionPosition(0m, 0m, 0m)));
    }
}
