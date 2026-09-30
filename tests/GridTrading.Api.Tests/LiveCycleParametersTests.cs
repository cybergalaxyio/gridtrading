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

public sealed class LiveCycleParametersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(GridMode.TwoWay)]
    [InlineData(GridMode.BuyOnly)]
    [InlineData(GridMode.SellOnly)]
    public async Task CombinedEditsPersistPreservePricesAndLeaveExistingOrdersAlone(GridMode mode)
    {
        await using var f = await Fixture.CreateAsync(mode);
        f.Cycle.EntryGridPriceOffset = mode == GridMode.TwoWay ? 0m : 2m;
        await f.Db.SaveChangesAsync(Ct);
        var original = f.Cycle.EffectivePlan;
        var frozenConfig = f.Cycle.FrozenConfigurationJson;
        var frozenPlan = f.Cycle.FrozenPlanJson;
        var strategyJson = f.Strategy.ConfigurationJson;
        var orders = JsonSerializer.Serialize(await f.Db.Orders.OrderBy(x => x.Id).ToListAsync(Ct));
        await f.Edit(new(125m, 5, .45m));
        Assert.Equal(125m, f.Cycle.EffectiveConfiguration.TakeProfitPoints);
        Assert.Equal(.45m, f.Cycle.EffectiveConfiguration.BaseLotSize);
        Assert.Equal(5, f.Cycle.EffectiveConfiguration.MaxLevelsPerSide);
        Assert.Equal(frozenConfig, f.Cycle.FrozenConfigurationJson);
        Assert.Equal(frozenPlan, f.Cycle.FrozenPlanJson);
        Assert.Equal(strategyJson, f.Strategy.ConfigurationJson);
        Assert.Equal(orders, JsonSerializer.Serialize(await f.Db.Orders.OrderBy(x => x.Id).ToListAsync(Ct)));
        var updated = f.Cycle.EffectivePlan;
        Assert.Equal(original.CenterPrice, updated.CenterPrice);
        foreach (var old in original.Levels)
            Assert.Equal(old.EntryPrice, updated.Levels.Single(x => x.Side == old.Side && x.LevelIndex == old.LevelIndex).EntryPrice);
        foreach (var side in updated.Levels.GroupBy(x => x.Side))
        {
            Assert.Equal(new[] { .4m, .6m, 1m, 1.5m, 2.2m }, side.OrderBy(x => x.LevelIndex).Select(x => x.PlannedQuantity));
            var last = side.MaxBy(x => x.LevelIndex)!;
            Assert.Equal(side.Sum(x => x.PlannedQuantity), last.CumulativeQuantity);
            Assert.Equal(side.Sum(x => x.EntryPrice * x.PlannedQuantity), last.CumulativeNotional);
        }
        var expected = JsonSerializer.Serialize(updated, JsonSupport.Options);
        f.Db.ChangeTracker.Clear();
        f.Cycle = await f.Db.Cycles.SingleAsync(Ct);
        Assert.Equal(expected, JsonSerializer.Serialize(f.Cycle.EffectivePlan, JsonSupport.Options));
        await f.Edit(new(MaxLevelsPerSide: 6));
        Assert.Equal(updated.CenterPrice, f.Cycle.EffectivePlan.CenterPrice);
        Assert.All(updated.Levels, old => Assert.Equal(old.EntryPrice,
            f.Cycle.EffectivePlan.Levels.Single(x => x.Side == old.Side && x.LevelIndex == old.LevelIndex).EntryPrice));
    }

    [Theory]
    [InlineData(GridMode.TwoWay, false)]
    [InlineData(GridMode.BuyOnly, false)]
    [InlineData(GridMode.SellOnly, false)]
    [InlineData(GridMode.BuyOnly, true)]
    public async Task PartialAndDelayedFillsRetainEntryTpAndQuantityAcrossRepeatedEdits(GridMode mode, bool legacy)
    {
        await using var f = await Fixture.CreateAsync(mode);
        var entry = f.ActiveEntries().First();
        if (legacy) { entry.EntryTakeProfitPoints = null; await f.Db.SaveChangesAsync(Ct); }
        await f.Fill(entry, .1m);
        var oldTp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", Ct);
        var oldPrice = oldTp.Price;
        await f.Edit(new(1000m, 5, .4m));
        Assert.Equal(oldPrice, oldTp.Price);
        Assert.Equal(.2m, entry.Quantity);
        await f.Fill(entry, .1m);
        Assert.Equal(oldPrice, oldTp.Price);
        Assert.Equal(.2m, oldTp.Quantity);
        var next = f.ActiveEntries().Single(x => x.Side == entry.Side);
        Assert.Equal(.6m, next.Quantity);
        Assert.Equal(1000m, next.EntryTakeProfitPoints);
        await f.Edit(new(TakeProfitPoints: 2000m, BaseLotSize: .6m));
        // Reload simulates durable state recovery before the delayed fill arrives.
        var nextId = next.Id;
        f.Db.ChangeTracker.Clear();
        f.Cycle = await f.Db.Cycles.SingleAsync(Ct);
        next = await f.Db.Orders.SingleAsync(x => x.Id == nextId, Ct);
        await f.Fill(next, next.Quantity);
        var lot = await f.Db.VirtualLots.SingleAsync(x => x.EntryOrderId == nextId, Ct);
        Assert.Equal(GridMath.TakeProfitPrice(Enum.Parse<OrderSide>(next.Side, true), next.Price, 1000m,
            f.Cycle.EffectiveConfiguration.TickSize), lot.TakeProfitPrice);
        Assert.Equal(.6m, next.Quantity);
        var third = f.ActiveEntries().Single(x => x.Side == entry.Side);
        Assert.Equal(1.3m, third.Quantity);
        Assert.Equal(2000m, third.EntryTakeProfitPoints);
    }

    [Fact]
    public async Task DecreasedBaseSizeUsesGrowthCapAndQuantityStepForFutureOrders()
    {
        await using var f = await Fixture.CreateAsync(GridMode.BuyOnly, maxTradeLot: .5m);
        await f.Edit(new(BaseLotSize: .15m));
        Assert.Equal(new[] { .1m, .2m, .3m }, f.Cycle.EffectivePlan.Levels.Select(x => x.PlannedQuantity));
        await f.Edit(new(BaseLotSize: .45m, MaxLevelsPerSide: 5));
        Assert.Equal(new[] { .4m, .5m, .5m, .5m, .5m }, f.Cycle.EffectivePlan.Levels.Select(x => x.PlannedQuantity));
    }

    [Theory]
    [InlineData(GridMode.BuyOnly)]
    [InlineData(GridMode.SellOnly)]
    public async Task PausedEditRespectsExposureLimitAndSingleModeResetUsesNewBase(GridMode mode)
    {
        await using var f = await Fixture.CreateAsync(mode, maxNetLot: .3m);
        var entry = Assert.Single(f.ActiveEntries());
        await f.Fill(entry, entry.Quantity);
        await f.Workflow.CommandAsync(f.Cycle.Id, "PAUSE_ENTRIES", "test", "pause", f.Cycle.StateVersion, false, Ct);
        await f.Edit(new(100m, 5, .4m));
        Assert.Equal("PAUSED", f.Cycle.State);
        Assert.Empty(f.ActiveEntries());
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", Ct);
        await f.Fill(tp, tp.Quantity);
        Assert.Empty(f.ActiveEntries());
        await f.Workflow.CommandAsync(f.Cycle.Id, "RESUME_ENTRIES", "test", "resume", f.Cycle.StateVersion, false, Ct);
        var replacement = Assert.Single(f.ActiveEntries());
        Assert.Equal(0, replacement.GridLevel);
        Assert.Equal(.3m, replacement.Quantity); // New .4 base is limited by unchanged .3 exposure cap.
        Assert.Equal(100m, replacement.EntryTakeProfitPoints);
        await f.Fill(replacement, replacement.Quantity);
        Assert.Empty(f.ActiveEntries());
        Assert.Equal(.3m, Math.Abs(f.Cycle.ReconstructedNetQuantity));
    }

    [Fact]
    public async Task RetryIsIdempotentAndVersionConflictDoesNotCommit()
    {
        await using var f = await Fixture.CreateAsync(GridMode.TwoWay);
        var request = new UpdateCycleParametersRequest(100m, 5, .4m);
        var version = f.Cycle.StateVersion;
        var first = await f.Workflow.UpdateParametersAsync(f.Cycle.Id, request, "edit", version, Ct);
        var retry = await f.Workflow.UpdateParametersAsync(f.Cycle.Id, request, "edit", version, Ct);
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(version + 1, f.Cycle.StateVersion);
        Assert.Single(await f.Db.AuditLogs.Where(x => x.Action == "CYCLE_PARAMETERS_UPDATED").ToListAsync(Ct));
        var conflict = await Assert.ThrowsAsync<TradingProblemException>(() =>
            f.Workflow.UpdateParametersAsync(f.Cycle.Id, request with { BaseLotSize = .6m }, "edit", version, Ct));
        Assert.Equal("IDEMPOTENCY_KEY_CONFLICT", conflict.Code);
        var stale = await Assert.ThrowsAsync<TradingProblemException>(() =>
            f.Workflow.UpdateParametersAsync(f.Cycle.Id, request, "stale", version, Ct));
        Assert.Equal("STATE_VERSION_STALE", stale.Code);
        Assert.False(await f.Db.Operations.AnyAsync(x => x.IdempotencyKey == "stale", Ct));
    }

    [Theory]
    [InlineData(0, 5, .4)]
    [InlineData(100, 2, .4)]
    [InlineData(100, 201, .4)]
    [InlineData(100, 5, 0)]
    [InlineData(100, 5, .01)]
    [InlineData(1000000, 5, .4)]
    public async Task InvalidCombinedRequestLeavesCycleAndOrdersUntouched(int tp, int levels, double lot)
    {
        await using var f = await Fixture.CreateAsync(GridMode.TwoWay);
        var version = f.Cycle.StateVersion;
        var orders = JsonSerializer.Serialize(await f.Db.Orders.ToListAsync(Ct));
        await Assert.ThrowsAsync<TradingProblemException>(() => f.Edit(new(tp, levels, (decimal)lot)));
        await f.Db.SaveChangesAsync(Ct);
        Assert.Null(f.Cycle.LiveConfigurationJson);
        Assert.Null(f.Cycle.LivePlanJson);
        Assert.Equal(version, f.Cycle.StateVersion);
        Assert.Equal(orders, JsonSerializer.Serialize(await f.Db.Orders.ToListAsync(Ct)));
        Assert.False(await f.Db.Operations.AnyAsync(x => x.Type == "UPDATE_PARAMETERS", Ct));
    }

    [Theory]
    [InlineData("CLOSING", false)]
    [InlineData("FAULT", false)]
    [InlineData("RUNNING", true)]
    public async Task InvalidStateIsRejected(string state, bool terminal)
    {
        await using var f = await Fixture.CreateAsync(GridMode.BuyOnly);
        f.Cycle.State = state; f.Cycle.IsTerminal = terminal;
        await f.Db.SaveChangesAsync(Ct);
        var error = await Assert.ThrowsAsync<TradingProblemException>(() => f.Edit(new(BaseLotSize: .4m)));
        Assert.Equal("INVALID_CYCLE_STATE", error.Code);
    }

    [Fact]
    public async Task RequiredHeadersAndEmptyBodyAreRejected()
    {
        await using var f = await Fixture.CreateAsync(GridMode.BuyOnly);
        Assert.Equal("STATE_VERSION_REQUIRED", (await Assert.ThrowsAsync<TradingProblemException>(() =>
            f.Workflow.UpdateParametersAsync(f.Cycle.Id, new(100m), "key", null, Ct))).Code);
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", (await Assert.ThrowsAsync<TradingProblemException>(() =>
            f.Workflow.UpdateParametersAsync(f.Cycle.Id, new(100m), "", f.Cycle.StateVersion, Ct))).Code);
        Assert.Equal("PARAMETERS_REQUIRED", (await Assert.ThrowsAsync<TradingProblemException>(() => f.Edit(new()))).Code);
    }

    [Fact]
    public async Task SchemaUpgradePreservesLegacyOrdersAndFrozenSettings()
    {
        await using var f = await Fixture.CreateAsync(GridMode.BuyOnly);
        await f.Db.Database.ExecuteSqlRawAsync("ALTER TABLE Cycles DROP COLUMN LiveConfigurationJson", Ct);
        await f.Db.Database.ExecuteSqlRawAsync("ALTER TABLE Cycles DROP COLUMN LivePlanJson", Ct);
        await f.Db.Database.ExecuteSqlRawAsync("ALTER TABLE Orders DROP COLUMN EntryTakeProfitPoints", Ct);
        await DatabaseCompatibility.EnsureExecutionSchemaAsync(f.Db);
        await DatabaseCompatibility.EnsureExecutionSchemaAsync(f.Db);
        f.Db.ChangeTracker.Clear();
        f.Cycle = await f.Db.Cycles.SingleAsync(Ct);
        Assert.Null(f.Cycle.LiveConfigurationJson);
        Assert.Equal(50m, f.Cycle.EffectiveConfiguration.TakeProfitPoints);
        var entry = Assert.Single(f.ActiveEntries());
        Assert.Null(entry.EntryTakeProfitPoints);
        await f.Edit(new(TakeProfitPoints: 500m));
        await f.Fill(entry, .1m);
        var lot = await f.Db.VirtualLots.SingleAsync(Ct);
        Assert.Equal(GridMath.TakeProfitPrice(OrderSide.Buy, entry.Price, 50m, f.Cycle.EffectiveConfiguration.TickSize), lot.TakeProfitPrice);
    }

    [Fact]
    public async Task NextCycleStartsFromSavedStrategyWithoutLiveOverrides()
    {
        await using var f = await Fixture.CreateAsync(GridMode.BuyOnly);
        await f.Edit(new(500m, 5, .4m));
        await f.Workflow.CommandAsync(f.Cycle.Id, "CLOSE", "test", "close", f.Cycle.StateVersion, false, Ct);
        var preview = await f.Workflow.CreatePreviewAsync(new(f.Strategy.Id, f.Strategy.Version, 0m, null, null), Ct);
        var (_, next) = await f.Workflow.StartCycleAsync(f.Strategy.Id,
            new(preview.Id, 0m, new(true, true, "PAPER")), "next", Ct);
        Assert.Null(next.LiveConfigurationJson);
        Assert.Null(next.LivePlanJson);
        Assert.Equal(.2m, next.EffectiveConfiguration.BaseLotSize);
        Assert.Equal(50m, next.EffectiveConfiguration.TakeProfitPoints);
        Assert.Equal(3, next.EffectiveConfiguration.MaxLevelsPerSide);
    }

    [Fact]
    public async Task FillWithAPreviouslyLoadedCycleUsesCommittedLiveSettings()
    {
        await using var f = await Fixture.CreateAsync(GridMode.BuyOnly);
        var entry = Assert.Single(f.ActiveEntries());
        await using var otherDb = f.NewContext();
        await f.NewWorkflow(otherDb).UpdateParametersAsync(f.Cycle.Id, new(500m, 5, .4m),
            "other-context", f.Cycle.StateVersion, Ct);
        Assert.Null(f.Cycle.LiveConfigurationJson); // This worker loaded before the edit.
        await f.Fill(entry, entry.Quantity);
        var snapshot = JsonSerializer.SerializeToElement(await f.Workflow.SnapshotAsync(f.Cycle, Ct), JsonSupport.Options);
        Assert.Equal(1, snapshot.GetProperty("risk").GetProperty("usedBuyLevels").GetInt32());
        Assert.Equal(4, snapshot.GetProperty("risk").GetProperty("remainingBuyLevels").GetInt32());
        var next = Assert.Single(f.ActiveEntries());
        Assert.Equal(.6m, next.Quantity);
        Assert.Equal(500m, next.EntryTakeProfitPoints);
        var lot = await f.Db.VirtualLots.SingleAsync(Ct);
        Assert.Equal(GridMath.TakeProfitPrice(OrderSide.Buy, entry.Price, 50m, f.Cycle.EffectiveConfiguration.TickSize), lot.TakeProfitPrice);
    }

    [Fact]
    public async Task StaleWorkerRefreshesMovementOffsetTogetherWithLivePlan()
    {
        await using var f = await Fixture.CreateAsync(GridMode.BuyOnly);
        await using var otherDb = f.NewContext();
        var movedCycle = await otherDb.Cycles.SingleAsync(Ct);
        var oldEntry = await otherDb.Orders.SingleAsync(x => x.Kind == "ENTRY", Ct);
        oldEntry.Status = "CANCELLED";
        movedCycle.EntryGridPriceOffset = .02m;
        var replacement = GridOrderLifecycle.CreateEntry(movedCycle, oldEntry.Symbol,
            movedCycle.EffectivePlan.Levels.Single(x => x.LevelIndex == 0), oldEntry.Quantity);
        replacement.Status = "NEW";
        replacement.ExchangeOrderId = "paper-moved-entry";
        otherDb.Orders.Add(replacement);
        await otherDb.SaveChangesAsync(Ct);
        await f.NewWorkflow(otherDb).UpdateParametersAsync(f.Cycle.Id, new(500m, 5, .4m),
            "after-move", movedCycle.StateVersion, Ct);
        Assert.Equal(0m, f.Cycle.EntryGridPriceOffset);
        await f.Fill(replacement, replacement.Quantity);
        var next = Assert.Single(f.ActiveEntries());
        Assert.Equal(movedCycle.EffectivePlan.Levels.Single(x => x.LevelIndex == 1).EntryPrice, next.Price);
        Assert.Equal(.02m, f.Cycle.EntryGridPriceOffset);
        Assert.Equal(.6m, next.Quantity);
        Assert.Equal(500m, next.EntryTakeProfitPoints);
    }

    [Fact]
    public async Task ConcurrentEditsWaitForAccountGateAndOnlyOneVersionWins()
    {
        await using var f = await Fixture.CreateAsync(GridMode.BuyOnly);
        await using var db1 = f.NewContext();
        await using var db2 = f.NewContext();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = f.Gate.RunAsync(f.Cycle.ExecutionAccountId, async () =>
        { entered.SetResult(); await release.Task; }, Ct);
        await entered.Task;
        async Task<string> Edit(GridStrategyWorkflow workflow, string key, decimal size)
        {
            try
            {
                await workflow.UpdateParametersAsync(f.Cycle.Id, new(BaseLotSize: size), key, f.Cycle.StateVersion, Ct);
                return "COMPLETED";
            }
            catch (TradingProblemException ex) { return ex.Code; }
        }
        var one = Edit(f.NewWorkflow(db1), "one", .4m);
        var two = Edit(f.NewWorkflow(db2), "two", .6m);
        Assert.False(one.IsCompleted);
        Assert.False(two.IsCompleted);
        release.SetResult(); await holder;
        var outcomes = await Task.WhenAll(one, two);
        Assert.Contains("COMPLETED", outcomes);
        Assert.Contains("STATE_VERSION_STALE", outcomes);
        Assert.Single(await f.Db.Operations.Where(x => x.Type == "UPDATE_PARAMETERS").ToListAsync(Ct));
    }

    [Fact]
    public async Task ExistingOrderAwaitingApprovalKeepsItsReviewedSizeAndTp()
    {
        await using var f = await Fixture.CreateAsync(GridMode.BuyOnly);
        await f.Workflow.CommandAsync(f.Cycle.Id, "PAUSE_ENTRIES", "test", "pause", f.Cycle.StateVersion, false, Ct);
        await new TradingControlSettingsService(f.Db).SaveAsync(new(true), Ct);
        await f.Edit(new(100m, 5, .4m));
        await f.Workflow.CommandAsync(f.Cycle.Id, "RESUME_ENTRIES", "test", "resume", f.Cycle.StateVersion, false, Ct);
        var entry = Assert.Single(f.ActiveEntries());
        Assert.Equal("PENDING_EXCHANGE", entry.Status);
        var approval = await f.Db.OrderApprovals.SingleAsync(x => x.OrderId == entry.Id, Ct);
        Assert.Equal(.4m, approval.Quantity);
        await f.Edit(new(200m, 6, .6m));
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        Assert.Equal(.4m, entry.Quantity);
        Assert.Equal(100m, entry.EntryTakeProfitPoints);
        Assert.Equal("PENDING_EXCHANGE", entry.Status);
        Assert.Equal("PENDING", approval.Status);
        Assert.Single(await f.Db.OrderApprovals.ToListAsync(Ct));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly ServiceProvider services = new ServiceCollection().AddLogging().AddSignalR().Services.BuildServiceProvider();
        public TradingDbContext Db { get; private set; } = null!;
        public GridStrategyWorkflow Workflow { get; private set; } = null!;
        public GridOrderLifecycle Lifecycle { get; private set; } = null!;
        public StrategyEntity Strategy { get; private set; } = null!;
        public CycleEntity Cycle { get; set; } = null!;
        public ExecutionAccountOperationGate Gate { get; } = new();
        private readonly MarketState market = new();
        public TradingDbContext NewContext() => new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
        public GridStrategyWorkflow NewWorkflow(TradingDbContext db)
        {
            var registry = new ExecutionEnvironmentRegistry([new PaperExecutionAdapter(market, db)]);
            return new(db, market, new PreviewStore(), registry, new(db, registry, Gate),
                services.GetRequiredService<IHubContext<TradingHub>>(), Gate);
        }
        public static async Task<Fixture> CreateAsync(GridMode mode, decimal maxNetLot = 10m, decimal maxTradeLot = 0m)
        {
            var f = new Fixture();
            await f.connection.OpenAsync(Ct);
            f.Db = new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync(Ct);
            var market = f.market; market.Tick();
            var registry = new ExecutionEnvironmentRegistry([new PaperExecutionAdapter(market, f.Db)]);
            var gate = f.Gate;
            f.Lifecycle = new(f.Db, registry, gate);
            f.Workflow = new(f.Db, market, new PreviewStore(), registry, f.Lifecycle,
                f.services.GetRequiredService<IHubContext<TradingHub>>(), gate);
            f.Strategy = await f.Workflow.CreateStrategyAsync(StrategyRequest.Default with
            {
                GridMode = mode, MaxLevelsPerSide = 3, BaseLotSize = .2m, MaxTradeLot = maxTradeLot,
                MaxNetLot = maxNetLot, LotSizeIncreasePercent = 50m, TakeProfitPoints = 50m,
                InitialGapPoints = 100m, GridSpacingPoints = 100m, GridSpacingStepPoints = 10m
            }, Ct);
            var preview = await f.Workflow.CreatePreviewAsync(new(f.Strategy.Id, f.Strategy.Version, 0m, null, null), Ct);
            (_, f.Cycle) = await f.Workflow.StartCycleAsync(f.Strategy.Id,
                new(preview.Id, 0m, new(true, true, "PAPER")), "start", Ct);
            return f;
        }
        public Task<OperationEntity> Edit(UpdateCycleParametersRequest request) =>
            Workflow.UpdateParametersAsync(Cycle.Id, request, Ids.New("edit"), Cycle.StateVersion, Ct);
        public OrderEntity[] ActiveEntries() => Db.Orders.Where(x => x.Kind == "ENTRY" &&
            (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED" || x.Status == "PENDING_EXCHANGE")).ToArray();
        public Task<int> Fill(OrderEntity order, decimal quantity) => Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId,
            [new(Ids.New("fill"), order.ExchangeOrderId, order.ClientOrderId, order.Side, order.Price, quantity, 0m, DateTimeOffset.UtcNow)], Ct);
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync(); await connection.DisposeAsync(); await services.DisposeAsync();
        }
    }
}
