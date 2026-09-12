using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Exchanges.Paper;
using GridTrading.Api.Hubs;
using GridTrading.Api.Infrastructure;
using GridTrading.Api.Services;
using GridTrading.Api.Strategies.Grid;
using GridTrading.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridTrading.Api.Tests;

public sealed class GridAutoRestartTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("BASKET_TAKE_PROFIT", "CURRENT_MID")]
    [InlineData("BASKET_STOP_LOSS", "CURRENT_MID")]
    [InlineData("BASKET_TAKE_PROFIT", "MANUAL")]
    [InlineData("BASKET_STOP_LOSS", "MANUAL")]
    public async Task RestartUsesCorrectCenterAndOnlyStartsOnce(string reason, string mode)
    {
        await using var f = await Fixture.CreateAsync(mode);
        await f.CloseAsync(reason);
        var pending = await f.PendingAsync();
        f.ResetWorkflow();
        f.Adapter.Quote = new(101.11m, 101.14m, 101.125m, DateTimeOffset.UtcNow);
        await f.Workflow.ProcessAutoRestartAsync(pending.Id, Ct);
        var next = await f.Db.Cycles.SingleAsync(x => !x.IsTerminal, Ct);
        Assert.NotEqual(f.Cycle.Id, next.Id);
        Assert.Equal(f.Cycle.ExecutionAccountId, next.ExecutionAccountId);
        Assert.Equal(f.Cycle.ExecutionEnvironmentId, next.ExecutionEnvironmentId);
        Assert.Equal(mode == "MANUAL" ? 100.015m : 101.125m, next.FixedCenterPrice);
        var plan = JsonSerializer.Deserialize<GridPlan>(next.FrozenPlanJson, JsonSupport.Options)!;
        Assert.Equal(mode == "MANUAL" ? 99.96m : 101.05m, plan.Levels.Single(x => x.Side == OrderSide.Buy && x.LevelIndex == 0).EntryPrice);
        Assert.Equal(mode == "MANUAL" ? 100.07m : 101.20m, plan.Levels.Single(x => x.Side == OrderSide.Sell && x.LevelIndex == 0).EntryPrice);
        Assert.Equal("COMPLETED", pending.Status);
        await f.Workflow.ProcessAutoRestartAsync(pending.Id, Ct);
        Assert.Equal(2, await f.Db.Cycles.CountAsync(Ct));
        Assert.Equal(2, f.Adapter.PreflightCount);
    }

    [Theory]
    [InlineData(false, false, "BASKET_TAKE_PROFIT")]
    [InlineData(true, false, "BASKET_TAKE_PROFIT")]
    [InlineData(false, true, "BASKET_STOP_LOSS")]
    [InlineData(true, true, "OPERATOR_CLOSE")]
    public async Task OnlyEnabledAutomaticBasketCloseQueuesRestart(bool enabled, bool automatic, string reason)
    {
        await using var f = await Fixture.CreateAsync(enabled: enabled);
        await f.CloseAsync(reason, automatic);
        Assert.Empty(await f.Db.Operations.Where(x => x.Type == "AUTO_RESTART").ToListAsync(Ct));
    }

    [Fact]
    public async Task EmergencyAndOperatorPauseDoNotRestart()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Workflow.CommandAsync(f.Cycle.Id, "EMERGENCY_FLATTEN", "BASKET_TAKE_PROFIT", "emergency", null, true, Ct);
        Assert.Empty(await f.Db.Operations.Where(x => x.Type == "AUTO_RESTART").ToListAsync(Ct));
        await using var paused = await Fixture.CreateAsync();
        await paused.Workflow.CommandAsync(paused.Cycle.Id, "PAUSE_ENTRIES", "pause", "pause", null, false, Ct);
        await paused.CloseAsync("BASKET_TAKE_PROFIT");
        Assert.Empty(await paused.Db.Operations.Where(x => x.Type == "AUTO_RESTART").ToListAsync(Ct));
    }

    [Fact]
    public async Task PendingRestartHonorsDisabledSetting()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CloseAsync("BASKET_TAKE_PROFIT");
        var pending = await f.PendingAsync();
        await f.Workflow.UpdateStrategyAsync(f.Strategy.Id, f.Request with { AutoRestart = false }, Ct);
        await f.Workflow.ProcessAutoRestartAsync(pending.Id, Ct);
        Assert.Equal("CANCELLED", pending.Status);
        Assert.Single(await f.Db.Cycles.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("STALE")]
    [InlineData("POSITION")]
    [InlineData("ORDER")]
    [InlineData("PREFLIGHT")]
    public async Task UnsafeRestartFailsOnceWithoutSendingNewOrders(string condition)
    {
        await using var f = await Fixture.CreateAsync();
        await f.CloseAsync("BASKET_TAKE_PROFIT");
        var pending = await f.PendingAsync();
        if (condition == "STALE") f.Adapter.Quote = f.Adapter.Quote with { AsOf = DateTimeOffset.UtcNow.AddMinutes(-1) };
        if (condition == "POSITION") f.Cycle.ActualNetQuantity = .1m;
        if (condition == "ORDER") (await f.Db.Orders.FirstAsync(Ct)).Status = "UNKNOWN";
        if (condition == "PREFLIGHT") f.Adapter.FailPreflight = true;
        await f.Db.SaveChangesAsync(Ct);
        await f.Workflow.ProcessAutoRestartAsync(pending.Id, Ct);
        await f.Workflow.ProcessAutoRestartAsync(pending.Id, Ct);
        Assert.Equal("FAILED", pending.Status);
        Assert.Single(await f.Db.Cycles.ToListAsync(Ct));
        Assert.Single(await f.Db.RiskAlerts.Where(x => x.Code == "AUTO_RESTART_FAILED").ToListAsync(Ct));
    }

    [Fact]
    public async Task FailedFlattenNeverQueuesRestart()
    {
        await using var f = await Fixture.CreateAsync();
        f.Adapter.FlattenResidual = .1m;
        await Assert.ThrowsAsync<TradingProblemException>(() => f.CloseAsync("BASKET_STOP_LOSS"));
        Assert.False(f.Cycle.IsTerminal);
        Assert.Equal("FAULT", f.Cycle.State);
        Assert.Empty(await f.Db.Operations.Where(x => x.Type == "AUTO_RESTART").ToListAsync(Ct));
    }

    [Fact]
    public async Task ManualStartSupersedesPendingRestart()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CloseAsync("BASKET_TAKE_PROFIT");
        var pending = await f.PendingAsync();
        var preview = await f.Workflow.CreatePreviewAsync(new(f.Strategy.Id, f.Strategy.Version, 0m, null, null), Ct);
        await f.Workflow.StartCycleAsync(f.Strategy.Id, new(preview.Id, preview.Plan.CenterPrice, new(true, true, "PAPER")), "manual-start", Ct);
        await f.Workflow.ProcessAutoRestartAsync(pending.Id, Ct);
        Assert.Equal("CANCELLED", pending.Status);
        Assert.Equal(2, await f.Db.Cycles.CountAsync(Ct));
    }

    [Fact]
    public async Task InterruptedStartIsFlaggedWithoutResubmission()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CloseAsync("BASKET_TAKE_PROFIT");
        var pending = await f.PendingAsync();
        pending.Status = "PROCESSING";
        var next = new CycleEntity
        {
            Id = "interrupted-cycle", StrategyId = f.Strategy.Id, State = "STARTING",
            FrozenConfigurationJson = f.Cycle.FrozenConfigurationJson, FrozenPlanJson = f.Cycle.FrozenPlanJson,
            ExitReason = "", StartedAt = DateTimeOffset.UtcNow
        };
        f.Db.Cycles.Add(next);
        f.Db.Operations.Add(new OperationEntity
        {
            Id = "interrupted-start", CommandId = "interrupted-command", ResourceId = next.Id,
            Type = "START_CYCLE", Status = "ACCEPTED", IdempotencyKey = $"auto-start:{f.Cycle.Id}",
            RequestHash = "test", ErrorCode = "", AcceptedAt = DateTimeOffset.UtcNow
        });
        await f.Db.SaveChangesAsync(Ct);
        f.ResetWorkflow();
        await f.Workflow.RecoverInterruptedAutoRestartsAsync(Ct);
        Assert.Equal("FAILED", pending.Status);
        Assert.Equal("FAULT", next.State);
        Assert.True(next.OperatorResetRequired);
        await f.Workflow.ProcessAutoRestartAsync(pending.Id, Ct);
        Assert.Equal(2, await f.Db.Cycles.CountAsync(Ct));
        Assert.Equal(1, f.Adapter.PreflightCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmedFlattenMustOffsetHistoricalLotsBeforeRestart(bool mismatch)
    {
        await using var f = await Fixture.CreateAsync();
        var entry = await f.Db.Orders.SingleAsync(x => x.Side == "BUY", Ct);
        await f.Lifecycle.ProcessFillsAsync(f.Cycle.ExecutionAccountId,
            [new(Ids.New("fill"), entry.ExchangeOrderId, entry.ClientOrderId, "BUY", entry.Price, entry.Quantity, 0m, DateTimeOffset.UtcNow)], Ct);
        await f.CloseAsync("BASKET_STOP_LOSS");
        Assert.True(f.Cycle.IsTerminal);
        Assert.Equal(0m, f.Cycle.ActualNetQuantity);
        Assert.Equal(0m, f.Cycle.ReconstructedNetQuantity);
        var orders = await f.Db.Orders.Where(x => x.CycleId == f.Cycle.Id).ToListAsync(Ct);
        Assert.DoesNotContain(orders, x => x.Status is "NEW" or "PARTIALLY_FILLED" or "PENDING_EXCHANGE" or "UNKNOWN");
        Assert.Contains(orders, x => x.Kind == "FLATTEN" && x.Status == "FILLED" && x.FilledQuantity == entry.Quantity);
        var lot = await f.Db.VirtualLots.SingleAsync(Ct);
        Assert.Equal(entry.Quantity, lot.RemainingQuantity);
        var executions = await f.Db.Executions.Where(x => x.CycleId == f.Cycle.Id).ToListAsync(Ct);
        Assert.Equal(0m, executions.Sum(x => x.Side == "BUY" ? x.Quantity : -x.Quantity));
        if (mismatch)
        {
            lot.RemainingQuantity += .1m;
            await f.Db.SaveChangesAsync(Ct);
        }
        var pending = await f.PendingAsync();
        await f.Workflow.ProcessAutoRestartAsync(pending.Id, Ct);
        Assert.Equal(mismatch ? "FAILED" : "COMPLETED", pending.Status);
        Assert.Equal(mismatch ? 1 : 2, await f.Db.Cycles.CountAsync(Ct));
    }

    [Fact]
    public async Task MultipleAutomaticRoundsUseFreshQuotesAndLatestParameters()
    {
        await using var f = await Fixture.CreateAsync();
        var previous = f.Cycle;
        for (var round = 1; round <= 3; round++)
        {
            await f.Workflow.CommandAsync(previous.Id, "CLOSE", "BASKET_TAKE_PROFIT", $"close-{round}", null, false, Ct, automaticClose: true);
            f.Adapter.Quote = new(100m + round, 100.03m + round, 100.015m + round, DateTimeOffset.UtcNow);
            await f.Workflow.UpdateStrategyAsync(f.Strategy.Id, f.Request with { InitialGapPoints = 20m + round }, Ct);
            var pending = await f.Db.Operations.SingleAsync(x => x.Type == "AUTO_RESTART" && x.Status == "ACCEPTED", Ct);
            await f.Workflow.ProcessAutoRestartAsync(pending.Id, Ct);
            previous = await f.Db.Cycles.SingleAsync(x => !x.IsTerminal, Ct);
            Assert.Equal(100.015m + round, previous.FixedCenterPrice);
            var config = JsonSerializer.Deserialize<GridConfiguration>(previous.FrozenConfigurationJson, JsonSupport.Options)!;
            Assert.Equal(20m + round, config.InitialGapPoints);
            Assert.True(config.AutoRestart);
        }
        Assert.Equal(4, await f.Db.Cycles.CountAsync(Ct));
        Assert.Equal(3, await f.Db.Operations.CountAsync(x => x.Type == "AUTO_RESTART" && x.Status == "COMPLETED", Ct));
    }

    [Fact]
    public async Task PendingRestartSurvivesNewDatabaseContextAndTwoWorkersOnlyStartOnce()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CloseAsync("BASKET_TAKE_PROFIT");
        var pending = await f.PendingAsync();
        await using var leftDb = f.NewDbContext();
        await using var rightDb = f.NewDbContext();
        var left = f.WorkflowFor(leftDb);
        var right = f.WorkflowFor(rightDb);
        await Task.WhenAll(left.ProcessAutoRestartAsync(pending.Id, Ct), right.ProcessAutoRestartAsync(pending.Id, Ct));
        Assert.Equal(2, await f.Db.Cycles.CountAsync(Ct));
        Assert.Equal(1, await f.Db.Cycles.CountAsync(x => !x.IsTerminal, Ct));
    }

    [Fact]
    public async Task WorkerTickProcessesPersistedRequest()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CloseAsync("BASKET_STOP_LOSS");
        using var services = new ServiceCollection().AddLogging()
            .AddScoped(_ => f.NewDbContext())
            .AddScoped(sp => f.WorkflowFor(sp.GetRequiredService<TradingDbContext>()))
            .BuildServiceProvider();
        var worker = new GridAutoRestartService(services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<GridAutoRestartService>>());
        await worker.TickAsync(Ct);
        await worker.TickAsync(Ct);
        Assert.Equal(2, await f.Db.Cycles.CountAsync(Ct));
        Assert.Equal(1, await f.Db.Cycles.CountAsync(x => !x.IsTerminal, Ct));
    }

    [Fact]
    public async Task CompletedStartupRecoveryDoesNotStartAnotherCycle()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CloseAsync("BASKET_TAKE_PROFIT");
        var pending = await f.PendingAsync();
        await f.Workflow.ProcessAutoRestartAsync(pending.Id, Ct);
        // Simulate a stop after START_CYCLE committed but before AUTO_RESTART was marked completed.
        pending.Status = "PROCESSING";
        pending.CompletedAt = null;
        await f.Db.SaveChangesAsync(Ct);
        await f.Workflow.RecoverInterruptedAutoRestartsAsync(Ct);
        Assert.Equal("COMPLETED", pending.Status);
        Assert.Equal(2, await f.Db.Cycles.CountAsync(Ct));
        Assert.Empty(await f.Db.RiskAlerts.Where(x => x.Code == "AUTO_RESTART_FAILED").ToListAsync(Ct));
    }

    [Fact]
    public async Task RestartRefreshesClosedCycleReconciliationBeforeCheckingExposure()
    {
        await using var f = await Fixture.CreateAsync();
        await f.CloseAsync("BASKET_TAKE_PROFIT");
        // Executions already balance to zero, but the persisted summary is stale.
        f.Cycle.ReconstructedNetQuantity = .1m;
        await f.Db.SaveChangesAsync(Ct);
        var pending = await f.PendingAsync();
        await f.Workflow.ProcessAutoRestartAsync(pending.Id, Ct);
        Assert.Equal(0m, f.Cycle.ReconstructedNetQuantity);
        Assert.Equal("COMPLETED", pending.Status);
        Assert.Equal(2, await f.Db.Cycles.CountAsync(Ct));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new($"Data Source=restart-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        private readonly ServiceProvider services = new ServiceCollection().AddLogging().AddSignalR().Services.BuildServiceProvider();
        private readonly MarketState market = new();
        private readonly ExecutionAccountOperationGate gate = new();
        public TradingDbContext Db { get; private set; } = null!;
        public GridStrategyWorkflow Workflow { get; private set; } = null!;
        public GridOrderLifecycle Lifecycle { get; private set; } = null!;
        public QuoteAdapter Adapter { get; private set; } = null!;
        public StrategyEntity Strategy { get; private set; } = null!;
        public CycleEntity Cycle { get; private set; } = null!;
        public StrategyRequest Request { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync(string mode = "CURRENT_MID", bool enabled = true)
        {
            var f = new Fixture();
            await f.connection.OpenAsync(Ct);
            f.Db = new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync(Ct);
            f.Adapter = new(new PaperExecutionAdapter(f.market, f.Db));
            f.ResetWorkflow();
            f.Request = StrategyRequest.Default with
            {
                AutoRestart = enabled, CenterSuggestionMode = mode, ManualCenterPrice = mode == "MANUAL" ? 100.015m : null,
                MaxLevelsPerSide = 3, InitialGapPoints = 11m, GridSpacingPoints = 20m,
                GridSpacingStepPoints = 0m, BaseLotSize = 1m, LotSizeIncreasePercent = 0m,
                MaxNetLot = 10m, TakeProfitPoints = 10m
            };
            f.Strategy = await f.Workflow.CreateStrategyAsync(f.Request, Ct);
            var preview = await f.Workflow.CreatePreviewAsync(new(f.Strategy.Id, f.Strategy.Version, 0m, null, null), Ct);
            (_, f.Cycle) = await f.Workflow.StartCycleAsync(f.Strategy.Id,
                new(preview.Id, preview.Plan.CenterPrice, new(true, true, "PAPER")), "initial-start", Ct);
            return f;
        }
        public TradingDbContext NewDbContext() => new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection.ConnectionString).Options);
        public GridStrategyWorkflow WorkflowFor(TradingDbContext db)
        {
            var adapter = new QuoteAdapter(new PaperExecutionAdapter(market, db)) { Quote = Adapter.Quote };
            var registry = new ExecutionEnvironmentRegistry([adapter]);
            return new(db, market, new PreviewStore(), registry, new(db, registry, gate), services.GetRequiredService<IHubContext<TradingHub>>(), gate);
        }
        public void ResetWorkflow()
        {
            var registry = new ExecutionEnvironmentRegistry([Adapter]);
            Lifecycle = new(Db, registry, gate);
            Workflow = new(Db, market, new PreviewStore(), registry, Lifecycle, services.GetRequiredService<IHubContext<TradingHub>>(), gate);
        }
        public Task<OperationEntity> CloseAsync(string reason, bool automatic = true) => Workflow.CommandAsync(Cycle.Id, "CLOSE", reason, "close", null, false, Ct, automatic);
        public Task<OperationEntity> PendingAsync() => Db.Operations.SingleAsync(x => x.Type == "AUTO_RESTART", Ct);
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); await services.DisposeAsync(); }
    }

    private sealed class QuoteAdapter(PaperExecutionAdapter inner) : IExecutionAdapter
    {
        public ExecutionQuote Quote { get; set; } = new(100m, 100.03m, 100.015m, DateTimeOffset.UtcNow);
        public bool FailPreflight { get; set; }
        public decimal FlattenResidual { get; set; }
        public int PreflightCount { get; private set; }
        public ExecutionEnvironmentDescriptor Environment => inner.Environment;
        public Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct) => inner.GetAccountsAsync(ct);
        public async Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection s, string symbol, decimal? referencePrice, CancellationToken ct) =>
            (await inner.GetInstrumentAsync(s, symbol, referencePrice, ct)) with { TickSize = .01m };
        public Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection s, string symbol, CancellationToken ct) => Task.FromResult(Quote);
        public Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection s, string symbol, CancellationToken ct)
        {
            PreflightCount++;
            if (FailPreflight) throw new TradingProblemException(409, "TESTNET_POSITION_NOT_FLAT", "Existing venue position.");
            return Task.FromResult(Quote);
        }
        public Task PlaceOrdersAsync(ExecutionSelection s, GridConfiguration c, IEnumerable<OrderEntity> orders, CancellationToken ct) => inner.PlaceOrdersAsync(s, c, orders, ct);
        public Task CancelOrdersAsync(ExecutionSelection s, IEnumerable<OrderEntity> orders, CancellationToken ct) => inner.CancelOrdersAsync(s, orders, ct);
        public Task<decimal> FlattenAsync(ExecutionSelection s, CycleEntity cycle, GridConfiguration c, CancellationToken ct) => FlattenResidual != 0m ? Task.FromResult(FlattenResidual) : inner.FlattenAsync(s, cycle, c, ct);
        public Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection s, CycleEntity cycle, GridConfiguration c, CancellationToken ct) => inner.ReconcileAsync(s, cycle, c, ct);
    }
}
