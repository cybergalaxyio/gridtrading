using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Infrastructure;
using GridTrading.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using GridTrading.Api.Services;
using GridTrading.Api.Strategies.Grid;
using GridTrading.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Tests;

public sealed class GridRiskPauseTests
{
    [Fact]
    public async Task RejectionCancelsPartialEntryTailAndRecoveryRestoresEntriesAfterTwoSyncs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        await f.FillAsync(ct);
        Assert.Equal("PAUSED", f.Cycle.State);
        Assert.True(f.Cycle.RiskPaused);
        Assert.False(f.Cycle.IsOperatorPaused);
        Assert.Equal(["UNPROTECTED_EXPOSURE"], f.Cycle.EntryPauseReasons);
        Assert.Equal("CANCELLED", f.Entry.Status);
        Assert.Equal(.2m, f.Entry.FilledQuantity);
        Assert.Equal(0, f.Adapter.EntryPlacements);
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct);
        f.Adapter.RejectProtection = false;
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.Equal("NEW", tp.Status);
        Assert.Equal(.2m, tp.Quantity);
        Assert.True(f.Cycle.RiskPaused);
        Assert.Equal(1, f.Cycle.RiskRecoveryChecks);
        // A restart retains both the pause reason and the first successful check.
        f.Db.ChangeTracker.Clear();
        f.Cycle = await f.Db.Cycles.SingleAsync(ct);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.Equal("RUNNING", f.Cycle.State);
        Assert.False(f.Cycle.RiskPaused);
        Assert.True(f.Adapter.EntryPlacements > 0);
        Assert.Single(await f.Db.RiskAlerts.Where(x => x.Code == "ENTRY_RISK_PAUSED").ToListAsync(ct));
        Assert.Single(await f.Db.RiskAlerts.Where(x => x.Code == "ENTRY_RISK_RECOVERED").ToListAsync(ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ManualPauseBeforeOrDuringRiskPauseSurvivesRecovery(bool pauseBefore)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        if (pauseBefore)
        {
            // Legacy PAUSED records without reason flags must also retain the manual pause.
            f.Cycle.State = "PAUSED";
            await f.Db.SaveChangesAsync(ct);
        }
        await f.FillAsync(ct);
        if (!pauseBefore) await f.Lifecycle.SetOperatorPauseAsync(f.Cycle, true, null, ct);
        Assert.Equal(["OPERATOR", "UNPROTECTED_EXPOSURE"], f.Cycle.EntryPauseReasons);
        f.Adapter.RejectProtection = false;
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.Equal("PAUSED", f.Cycle.State);
        Assert.False(f.Cycle.RiskPaused);
        Assert.True(f.Cycle.OperatorPaused);
        Assert.Equal(0, f.Adapter.EntryPlacements);
        await f.Lifecycle.SetOperatorPauseAsync(f.Cycle, false, null, ct);
        Assert.Equal("RUNNING", f.Cycle.State);
        Assert.True(f.Adapter.EntryPlacements > 0);
    }

    [Fact]
    public async Task ManualResumeCannotBypassRiskPause()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        await f.FillAsync(ct);
        await f.Lifecycle.SetOperatorPauseAsync(f.Cycle, true, null, ct);
        await f.Lifecycle.SetOperatorPauseAsync(f.Cycle, false, null, ct);
        Assert.True(f.Cycle.RiskPaused);
        Assert.False(f.Cycle.OperatorPaused);
        Assert.Equal("PAUSED", f.Cycle.State);
        Assert.Equal(0, f.Adapter.EntryPlacements);
        await Assert.ThrowsAsync<TradingProblemException>(() => f.Lifecycle.SetOperatorPauseAsync(f.Cycle, false, null, ct));
    }

    [Fact]
    public async Task FailedEntryCancellationDoesNotStopProtectionRepairOrPermitRecovery()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        f.Adapter.FailCancellation = true;
        await f.FillAsync(ct);
        f.Adapter.RejectProtection = false;
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        var protectedOrder = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct);
        Assert.Equal("NEW", protectedOrder.Status);
        // A TP fill also tries to cancel the Entry tail; failure must not interrupt later TP work.
        await f.Lifecycle.ProcessFillsAsync("account", [new NormalizedExecutionFill(
            "partial-tp", protectedOrder.ExchangeOrderId, protectedOrder.ClientOrderId,
            "SELL", protectedOrder.Price, .1m, 0m, DateTimeOffset.UtcNow)], ct);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.Equal(.1m, (await f.Db.VirtualLots.SingleAsync(ct)).RemainingQuantity);
        Assert.True(f.Cycle.RiskPaused);
        Assert.Equal(0, f.Cycle.RiskRecoveryChecks);
        Assert.Equal(0, f.Adapter.EntryPlacements);
        f.Adapter.FailCancellation = false;
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.Equal("RUNNING", f.Cycle.State);
    }

    [Theory]
    [InlineData("UNKNOWN")]
    [InlineData("PENDING_EXCHANGE")]
    public async Task UnconfirmedProtectionCannotClearRiskPause(string status)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        await f.FillAsync(ct);
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct);
        tp.Status = status;
        f.Adapter.ProtectionStatus = status;
        f.Adapter.RejectProtection = false;
        await f.Db.SaveChangesAsync(ct);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.True(f.Cycle.RiskPaused);
        Assert.Equal(0, f.Cycle.RiskRecoveryChecks);
        Assert.Equal(0, f.Adapter.EntryPlacements);
    }

    [Theory]
    [InlineData("0", "0", true)]
    [InlineData("0", "0.01", false)]
    [InlineData("10", "10", false)]
    [InlineData("10", "9", true)]
    public async Task RecoveryUsesStrictThresholdAndAllowsZeroExposure(string threshold, string exposure, bool resumes)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct, decimal.Parse(threshold));
        await f.FillAsync(ct);
        var lot = await f.Db.VirtualLots.SingleAsync(ct);
        lot.RemainingQuantity = decimal.Parse(exposure) / lot.TakeProfitPrice;
        await f.Db.SaveChangesAsync(ct);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.Equal(!resumes, f.Cycle.RiskPaused);
        Assert.Equal(resumes ? "RUNNING" : "PAUSED", f.Cycle.State);
        Assert.Equal(resumes, f.Adapter.EntryPlacements > 0);
    }

    [Fact]
    public async Task ExposureReboundResetsRecoveryAndTakeProfitFillsStillCloseLots()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        await f.FillAsync(ct);
        f.Adapter.RejectProtection = false;
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.Equal(1, f.Cycle.RiskRecoveryChecks);
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct);
        tp.Status = "UNKNOWN";
        await f.Db.SaveChangesAsync(ct);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.Equal(0, f.Cycle.RiskRecoveryChecks);
        await f.Lifecycle.ProcessFillsAsync("account", [new NormalizedExecutionFill(
            "tp-fill", tp.ExchangeOrderId, tp.ClientOrderId, "SELL", tp.Price, .2m, 0m, DateTimeOffset.UtcNow)], ct);
        Assert.Equal("CLOSED", (await f.Db.VirtualLots.SingleAsync(ct)).Status);
        Assert.True(f.Cycle.RealisedCyclePnl > 0m);
        Assert.True(f.Cycle.RiskPaused);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.True(f.Cycle.RiskPaused);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.False(f.Cycle.RiskPaused);
    }

    [Fact]
    public async Task FailedReconciliationResetsRecoveryProgress()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        await f.FillAsync(ct);
        f.Adapter.RejectProtection = false;
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.Equal(1, f.Cycle.RiskRecoveryChecks);
        f.Adapter.FailReconciliation = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Lifecycle.ReconcileAsync(f.Cycle, ct));
        Assert.Equal(0, f.Cycle.RiskRecoveryChecks);
        f.Adapter.FailReconciliation = false;
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.True(f.Cycle.RiskPaused);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.False(f.Cycle.RiskPaused);
    }

    [Fact]
    public async Task RiskPauseDoesNotSubmitPersistedPendingEntryIntents()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        await f.FillAsync(ct);
        var pending = new OrderEntity { Id = "pending-entry", CycleId = f.Cycle.Id,
            ClientOrderId = "pending-entry-client", ExchangeOrderId = "pending", Kind = "ENTRY",
            Symbol = "SOL", Side = "SELL", Status = "PENDING_EXCHANGE", Price = 101m, Quantity = .2m };
        f.Db.Orders.Add(pending);
        await f.Db.SaveChangesAsync(ct);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.Equal("CANCELLED", pending.Status);
        Assert.True(f.Cycle.RiskPaused);
        Assert.Equal(0, f.Adapter.EntryPlacements);
    }

    [Theory]
    [InlineData("NEW", 0)]
    [InlineData("PARTIALLY_FILLED", .2)]
    [InlineData("UNKNOWN", 0)]
    [InlineData("PENDING_EXCHANGE", 0)]
    public async Task ManualPauseRetriesFailedCancellationAfterReload(string status, double filled)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        f.Entry.Status = status;
        f.Entry.FilledQuantity = (decimal)filled;
        await f.Db.SaveChangesAsync(ct);
        f.Adapter.FailCancellation = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Lifecycle.SetOperatorPauseAsync(f.Cycle, true, null, ct));
        Assert.Equal("PAUSED", f.Cycle.State);
        Assert.True(f.Cycle.OperatorPaused);
        Assert.False(f.Cycle.RiskPaused);
        Assert.Equal(status, f.Entry.Status);
        Assert.Single(f.Adapter.CancellationAttempts);

        f.Db.ChangeTracker.Clear();
        f.Cycle = await f.Db.Cycles.SingleAsync(ct);
        f.Adapter.FailCancellation = false;
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        var entry = await f.Db.Orders.SingleAsync(x => x.Id == f.Entry.Id, ct);
        Assert.Equal("CANCELLED", entry.Status);
        Assert.Equal((decimal)filled, entry.FilledQuantity);
        Assert.Equal(2, f.Adapter.CancellationAttempts.Count);
        Assert.Equal("PAUSED", f.Cycle.State);
        Assert.Equal(["OPERATOR"], f.Cycle.EntryPauseReasons);
        Assert.Equal(0, f.Adapter.EntryPlacements);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.Equal(2, f.Adapter.CancellationAttempts.Count);
        Assert.Equal("PAUSED", f.Cycle.State);
    }

    [Fact]
    public async Task ManualPauseRetriesEverySyncWhileFailuresContinue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        f.Adapter.FailCancellation = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Lifecycle.SetOperatorPauseAsync(f.Cycle, true, null, ct));
        for (var sync = 1; sync <= 2; sync++)
        {
            await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
            Assert.Equal(sync + 1, f.Adapter.CancellationAttempts.Count);
            Assert.Equal("NEW", f.Entry.Status);
            Assert.Equal("PAUSED", f.Cycle.State);
            Assert.False(f.Cycle.RiskPaused);
            Assert.Equal(0, f.Adapter.EntryPlacements);
        }
        var alert = await f.Db.RiskAlerts.SingleAsync(ct);
        Assert.Equal("OPERATOR_ENTRY_CANCEL_PENDING", alert.Code);
        Assert.Contains("人工暂停", alert.Message);
        Assert.DoesNotContain("风险暂停", alert.Message);
    }

    [Fact]
    public async Task LegacyManualPauseCancelsEntriesWithoutReasonFlags()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        f.Cycle.State = "PAUSED";
        await f.Db.SaveChangesAsync(ct);
        f.Db.ChangeTracker.Clear();
        f.Cycle = await f.Db.Cycles.SingleAsync(ct);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.Equal("CANCELLED", (await f.Db.Orders.SingleAsync(ct)).Status);
        Assert.Equal("PAUSED", f.Cycle.State);
        Assert.False(f.Cycle.OperatorPaused);
        Assert.True(f.Cycle.IsOperatorPaused);
        Assert.False(f.Cycle.RiskPaused);
        Assert.Equal(0, f.Adapter.EntryPlacements);
    }

    [Fact]
    public async Task ManualPauseRetainsSuccessfulCancellationsAndRetriesOnlyRemainingOrders()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        var second = new OrderEntity { Id = "second-entry", CycleId = f.Cycle.Id,
            ClientOrderId = "second-entry-client", ExchangeOrderId = "second-entry-venue", Kind = "ENTRY",
            Symbol = "SOL", Side = "SELL", Status = "NEW", Price = 101m, Quantity = .2m };
        f.Db.Orders.Add(second);
        await f.Db.SaveChangesAsync(ct);
        f.Adapter.CancellationFailures.Add(second.Id);
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Lifecycle.SetOperatorPauseAsync(f.Cycle, true, null, ct));
        Assert.Equal("CANCELLED", f.Entry.Status);
        Assert.Equal("NEW", second.Status);
        f.Db.ChangeTracker.Clear();
        f.Cycle = await f.Db.Cycles.SingleAsync(ct);
        f.Adapter.CancellationFailures.Clear();
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.All(await f.Db.Orders.ToListAsync(ct), order => Assert.Equal("CANCELLED", order.Status));
        Assert.Equal(1, f.Adapter.CancellationAttempts.Count(x => x == f.Entry.Id));
        Assert.Equal(2, f.Adapter.CancellationAttempts.Count(x => x == second.Id));
        Assert.Equal("PAUSED", f.Cycle.State);
    }

    [Fact]
    public async Task ManualPauseCancellationFailuresDoNotInterruptTpFillsOrRepair()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct, threshold: 100m);
        f.Adapter.FailCancellation = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Lifecycle.SetOperatorPauseAsync(f.Cycle, true, null, ct));
        f.Adapter.RejectProtection = false;
        await f.FillAsync(ct); // A late entry fill while cancellation is failing still needs protection.
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct);
        Assert.Equal("NEW", tp.Status);
        await f.Lifecycle.ProcessFillsAsync("account", [new NormalizedExecutionFill(
            "manual-partial-tp", tp.ExchangeOrderId, tp.ClientOrderId, "SELL", tp.Price, .05m, 0m, DateTimeOffset.UtcNow)], ct);
        tp.Status = "REJECTED"; // Reconciliation must repair this even when Entry-tail cancellation fails.
        await f.Db.SaveChangesAsync(ct);
        for (var sync = 0; sync < 2; sync++)
        {
            var attemptsBefore = f.Adapter.CancellationAttempts.Count;
            await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
            Assert.True(f.Adapter.CancellationAttempts.Count > attemptsBefore);
            Assert.Equal("NEW", tp.Status);
            Assert.Equal(.15m, tp.Quantity - tp.FilledQuantity);
            Assert.Equal("PAUSED", f.Cycle.State);
            Assert.False(f.Cycle.RiskPaused);
            Assert.Equal(0, f.Adapter.EntryPlacements);
        }
        Assert.False((await f.Db.VirtualLots.SingleAsync(ct)).ProtectionPending);
        Assert.True(f.Cycle.RealisedCyclePnl > 0m);
        Assert.Equal("OPERATOR_ENTRY_CANCEL_PENDING", (await f.Db.RiskAlerts.SingleAsync(ct)).Code);
    }

    [Theory]
    [InlineData("CLOSE")]
    [InlineData("EMERGENCY_FLATTEN")]
    public async Task CloseWaitsForRecoveryBeforePersistingClosing(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        await f.FillAsync(ct);
        f.Adapter.RejectProtection = false;
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.Equal(1, f.Cycle.RiskRecoveryChecks);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Adapter.BeforeReconcile = async () => { entered.SetResult(); await release.Task.WaitAsync(ct); };
        var recovery = f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        await entered.Task.WaitAsync(ct);
        await using var closerDb = f.NewContext();
        var closerAdapter = new Adapter(closerDb) { RejectProtection = false };
        using var hubServices = CreateHubServices();
        var workflow = CreateWorkflow(closerDb, closerAdapter, f.Gate, hubServices);
        var close = workflow.CommandAsync(f.Cycle.Id, command, "test", "close-race", null, true, ct);
        string stateWhileWaiting;
        try
        {
            await using var reader = f.NewContext();
            stateWhileWaiting = (await reader.Cycles.SingleAsync(ct)).State;
        }
        finally { release.TrySetResult(); }
        await recovery.WaitAsync(ct);
        await close.WaitAsync(ct);
        Assert.Equal("PAUSED", stateWhileWaiting);
        Assert.Equal(["CLOSING", "CLOSING"], closerAdapter.ReconciledStates);
        Assert.Equal(0, closerAdapter.EntryPlacements);
        var saved = await closerDb.Cycles.SingleAsync(ct);
        Assert.True(saved.IsTerminal);
        Assert.Equal("WAITING_FOR_OPERATOR", saved.State);
    }

    [Theory]
    [InlineData("CLOSE")]
    [InlineData("EMERGENCY_FLATTEN")]
    public async Task RecoveryReloadsClosingWhileFlattenIsInFlight(string command)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        await f.FillAsync(ct);
        f.Adapter.RejectProtection = false;
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var closerDb = f.NewContext();
        var closerAdapter = new Adapter(closerDb) { RejectProtection = false,
            BeforeFlatten = async () => { entered.SetResult(); await release.Task.WaitAsync(ct); } };
        using var hubServices = CreateHubServices();
        var workflow = CreateWorkflow(closerDb, closerAdapter, f.Gate, hubServices);
        var close = workflow.CommandAsync(f.Cycle.Id, command, "test", "close-first", null, true, ct);
        await entered.Task.WaitAsync(ct);
        try
        {
            await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
            Assert.Equal("CLOSING", f.Cycle.State);
            Assert.Equal(0, f.Adapter.EntryPlacements);
            Assert.Equal(1, f.Cycle.RiskRecoveryChecks);
        }
        finally { release.TrySetResult(); }
        await close.WaitAsync(ct);
        Assert.True((await closerDb.Cycles.SingleAsync(ct)).IsTerminal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseRevalidatesVersionAndTerminalStateInsideAccountGate(bool becameTerminal)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(ct);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = f.Gate.RunAsync("account", async () =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
        }, ct);
        await entered.Task.WaitAsync(ct);
        await using var closerDb = f.NewContext();
        var adapter = new Adapter(closerDb);
        using var hubServices = CreateHubServices();
        var workflow = CreateWorkflow(closerDb, adapter, f.Gate, hubServices);
        var close = workflow.CommandAsync(f.Cycle.Id, "CLOSE", "test", "close-stale",
            becameTerminal ? null : f.Cycle.StateVersion, true, ct);
        try
        {
            f.Cycle.StateVersion++;
            f.Cycle.IsTerminal = becameTerminal;
            if (becameTerminal) f.Cycle.State = "WAITING_FOR_OPERATOR";
            await f.Db.SaveChangesAsync(ct);
        }
        finally { release.TrySetResult(); }
        await holding.WaitAsync(ct);
        var error = await Assert.ThrowsAsync<TradingProblemException>(() => close);
        Assert.Equal(becameTerminal ? "INVALID_CYCLE_STATE" : "STATE_VERSION_STALE", error.Code);
        Assert.Empty(adapter.ReconciledStates);
        Assert.Equal(0, adapter.FlattenCalls);
    }

    private static ServiceProvider CreateHubServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        return services.BuildServiceProvider();
    }

    private static GridStrategyWorkflow CreateWorkflow(TradingDbContext db, Adapter adapter,
        ExecutionAccountOperationGate gate, ServiceProvider services)
    {
        var registry = new ExecutionEnvironmentRegistry([adapter]);
        return new GridStrategyWorkflow(db, new MarketState(), new PreviewStore(), registry,
            new GridOrderLifecycle(db, registry, gate), services.GetRequiredService<IHubContext<TradingHub>>());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required TradingDbContext Db { get; init; }
        public required CycleEntity Cycle { get; set; }
        public required OrderEntity Entry { get; init; }
        public required Adapter Adapter { get; init; }
        public required GridOrderLifecycle Lifecycle { get; init; }
        public required ExecutionAccountOperationGate Gate { get; init; }
        public TradingDbContext NewContext() => new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(Connection).Options);
        public Task<int> FillAsync(CancellationToken ct) => Lifecycle.ProcessFillsAsync("account",
            [new NormalizedExecutionFill("fill", Entry.ExchangeOrderId, Entry.ClientOrderId, "BUY", 99m, .2m, 0m, DateTimeOffset.UtcNow)], ct);
        public static async Task<Fixture> CreateAsync(CancellationToken ct, decimal threshold = 10m)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(ct);
            var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync(ct);
            var config = new GridConfiguration { Symbol = "SOL", CenterPrice = 100m, TickSize = .1m,
                QuantityStep = .01m, MinOrderQuantity = .01m, MinOrderNotional = 1m, BaseLotSize = .2m,
                MaxTradeLot = 1m, MaxNetLot = 10m, MaxLevelsPerSide = 3, TakeProfitPoints = 10m,
                FaultExposureThresholdUsdt = threshold, PartialFillCancelAfterMinutes = 0 };
            var cycle = new CycleEntity { Id = "cycle", StrategyId = "strategy", State = "RUNNING",
                ExecutionAccountId = "account", ExecutionEnvironmentId = "risk-test", ExitReason = "",
                FrozenConfigurationJson = JsonSerializer.Serialize(config, JsonSupport.Options),
                FrozenPlanJson = JsonSerializer.Serialize(GridMath.BuildPlan(config, TradingService.RulesFor(config)), JsonSupport.Options) };
            var entry = new OrderEntity { Id = "entry", CycleId = cycle.Id, ClientOrderId = "entry-client", ExchangeOrderId = "entry-venue",
                Kind = "ENTRY", Symbol = "SOL", Side = "BUY", Status = "NEW", GridLevel = 0,
                Price = 99m, Quantity = .4m, CreatedAt = DateTimeOffset.UtcNow };
            db.AddRange(cycle, entry);
            await db.SaveChangesAsync(ct);
            var adapter = new Adapter(db);
            var gate = new ExecutionAccountOperationGate();
            return new Fixture { Connection = connection, Db = db, Cycle = cycle, Entry = entry, Adapter = adapter, Gate = gate,
                Lifecycle = new GridOrderLifecycle(db, new ExecutionEnvironmentRegistry([adapter]), gate) };
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    private sealed class Adapter(TradingDbContext db) : IExecutionAdapter
    {
        public ExecutionEnvironmentDescriptor Environment { get; } = new("risk-test", "TEST", "TEST", "Risk fixture");
        public bool RejectProtection { get; set; } = true;
        public bool FailCancellation { get; set; }
        public HashSet<string> CancellationFailures { get; } = [];
        public List<string> CancellationAttempts { get; } = [];
        public bool FailReconciliation { get; set; }
        public Func<Task>? BeforeReconcile { get; set; }
        public Func<Task>? BeforeFlatten { get; set; }
        public List<string> ReconciledStates { get; } = [];
        public int FlattenCalls { get; private set; }
        public string ProtectionStatus { get; set; } = "NEW";
        public int EntryPlacements { get; private set; }
        public Task PlaceOrdersAsync(ExecutionSelection selection, GridConfiguration config, IEnumerable<OrderEntity> orders, CancellationToken ct)
        {
            foreach (var order in orders)
            {
                if (order.Kind == "TAKE_PROFIT" && RejectProtection)
                {
                    order.Status = "REJECTED";
                    throw new TradingProblemException(422, "PROTECTIVE_ORDER_REJECTED", "Synthetic protection rejection");
                }
                if (order.Kind == "ENTRY") EntryPlacements++;
                order.Status = order.Kind == "TAKE_PROFIT" ? ProtectionStatus : "NEW";
                order.ExchangeOrderId = "venue-" + order.Id;
            }
            return Task.CompletedTask;
        }
        public Task CancelOrdersAsync(ExecutionSelection selection, IEnumerable<OrderEntity> orders, CancellationToken ct)
        {
            foreach (var order in orders.OrderBy(x => x.Id))
            {
                CancellationAttempts.Add(order.Id);
                if (FailCancellation || CancellationFailures.Contains(order.Id))
                    throw new HttpRequestException("Synthetic cancel timeout");
                order.Status = "CANCELLED";
            }
            return Task.CompletedTask;
        }
        public async Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config, CancellationToken ct)
        {
            ReconciledStates.Add(cycle.State);
            if (BeforeReconcile is not null) await BeforeReconcile();
            if (FailReconciliation) throw new HttpRequestException("Synthetic reconciliation failure");
            var orders = await db.Orders.Where(x => x.CycleId == cycle.Id && (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED")).ToListAsync(ct);
            return new([], [], [], orders.ToDictionary(x => x.ClientOrderId, x => x.ExchangeOrderId),
                new ExecutionPosition(cycle.ActualNetQuantity, cycle.ActualNetQuantity * 100m, 0m));
        }
        public Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection selection, string symbol, CancellationToken ct) =>
            Task.FromResult(new ExecutionQuote(99m, 101m, 100m, DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection selection, string symbol, decimal? referencePrice, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection selection, string symbol, CancellationToken ct) => throw new NotSupportedException();
        public async Task<decimal> FlattenAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config, CancellationToken ct)
        {
            FlattenCalls++;
            if (BeforeFlatten is not null) await BeforeFlatten();
            var active = await db.Orders.Where(x => x.CycleId == cycle.Id &&
                (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED" || x.Status == "UNKNOWN" || x.Status == "PENDING_EXCHANGE")).ToListAsync(ct);
            await CancelOrdersAsync(selection, active, ct);
            cycle.ActualNetQuantity = 0m;
            cycle.ReconstructedNetQuantity = 0m;
            await db.SaveChangesAsync(ct);
            return 0m;
        }
    }
}
