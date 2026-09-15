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

public sealed class EntryFillLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(GridMode.BuyOnly, "BUY")]
    [InlineData(GridMode.BuyOnly, "SELL")]
    [InlineData(GridMode.SellOnly, "BUY")]
    [InlineData(GridMode.SellOnly, "SELL")]
    [InlineData(GridMode.TwoWay, "BUY")]
    [InlineData(GridMode.TwoWay, "SELL")]
    public async Task StartChecksPriorCycleOrderHistoryIndependentlyForEachSide(GridMode mode, string limitedSide)
    {
        await using var f = await Fixture.CreateAsync(mode);
        await f.AddHistoryAsync(limitedSide, -59);
        await f.AddHistoryAsync(limitedSide, -30);
        await f.AddHistoryAsync(limitedSide, -1);
        await f.StartAsync();
        Assert.Equal("RUNNING", f.Cycle.State);
        Assert.Empty(await f.Db.Executions.ToListAsync(Ct)); // The check needs only order history.
        var expected = new[] { "BUY", "SELL" }.Where(side => side != limitedSide &&
            (mode == GridMode.TwoWay || (mode == GridMode.BuyOnly ? side == "BUY" : side == "SELL"))).ToArray();
        Assert.Equal(expected, f.ActiveEntries().Select(x => x.Side).Order().ToArray());
        Assert.Equal(expected.Length, f.Adapter.Placed.Count);
    }

    [Theory]
    [InlineData("BUY")]
    [InlineData("SELL")]
    public async Task WindowExpiresExactlyAndRequiresCountBelowLimit(string side)
    {
        await using var f = await Fixture.CreateAsync();
        await f.AddHistoryAsync(side, -60); // Already expired at the boundary.
        await f.AddHistoryAsync(side, -59);
        await f.AddHistoryAsync(side, -58);
        await f.AddHistoryAsync(side, -30);
        await f.AddHistoryAsync(side, -1);
        await f.StartAsync();
        Assert.False(await f.CanPlaceAsync(side));
        f.Clock.Now = f.Clock.Now.AddMinutes(1); // Three remain: still blocked.
        Assert.False(await f.CanPlaceAsync(side));
        f.Clock.Now = f.Clock.Now.AddMinutes(1); // Two remain: eligible.
        Assert.True(await f.CanPlaceAsync(side));
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        Assert.Contains(f.ActiveEntries(), x => x.Side == side);
    }

    [Theory]
    [InlineData(GridMode.BuyOnly, "BUY")]
    [InlineData(GridMode.SellOnly, "SELL")]
    public async Task ThirdFullEntryStopsReentryButTpStillWorks(GridMode mode, string side)
    {
        await using var f = await Fixture.CreateAsync(mode);
        await f.StartAsync();
        for (var index = 0; index < 3; index++)
        {
            var entry = Assert.Single(f.ActiveEntries());
            await f.Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId, [f.Fill(entry, .2m)], Ct);
            Assert.Equal(f.Clock.Now, entry.FilledAt);
        }
        Assert.Empty(f.ActiveEntries());
        var takeProfits = await f.Db.Orders.Where(x => x.CycleId == f.Cycle.Id && x.Kind == "TAKE_PROFIT").ToListAsync(Ct);
        Assert.Equal(3, takeProfits.Count);
        Assert.All(takeProfits, tp => Assert.Equal(side == "BUY" ? "SELL" : "BUY", tp.Side));
        foreach (var tp in takeProfits)
            await f.Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId, [f.Fill(tp, .2m)], Ct);
        Assert.Equal(0m, f.Cycle.ActualNetQuantity);
        Assert.Empty(f.ActiveEntries()); // TP completion does not erase Entry history.
        f.Clock.Now = f.Clock.Now.AddMinutes(60);
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        Assert.Equal(side, Assert.Single(f.ActiveEntries()).Side);
    }

    [Theory]
    [InlineData(GridMode.BuyOnly)]
    [InlineData(GridMode.SellOnly)]
    public async Task FragmentsAndDuplicateDeliveryCountOneOrderAtCompletion(GridMode mode)
    {
        await using var f = await Fixture.CreateAsync(mode, limit: 1);
        await f.StartAsync();
        var entry = Assert.Single(f.ActiveEntries());
        var first = f.Fill(entry, .1m);
        Assert.Equal(1, await f.Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId, [first], Ct));
        Assert.Null(entry.FilledAt);
        Assert.True(await f.CanPlaceAsync(entry.Side));
        f.Clock.Now = f.Clock.Now.AddMinutes(1);
        var last = f.Fill(entry, .1m);
        await f.Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId, [last], Ct);
        var completedAt = entry.FilledAt;
        Assert.Equal(f.Clock.Now, completedAt);
        f.Clock.Now = f.Clock.Now.AddMinutes(20);
        Assert.Equal(0, await f.Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId, [first, last], Ct));
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        await f.Db.Entry(entry).ReloadAsync(Ct);
        Assert.Equal(completedAt, entry.FilledAt);
        Assert.Equal(2, await f.Db.Executions.CountAsync(Ct));
        Assert.False(await f.CanPlaceAsync(entry.Side));
    }

    [Fact]
    public async Task FullOrderNotificationCountsBeforeFillsAndDuplicateStatusDoesNotMoveTime()
    {
        await using var f = await Fixture.CreateAsync(GridMode.SellOnly, limit: 1);
        await f.StartAsync();
        var entry = Assert.Single(f.ActiveEntries());
        var extra = await f.AddWorkingOrderAsync("SELL", "ENTRY", "NEW");
        var completedAt = f.Clock.Now;
        var update = new NormalizedOrderUpdate(entry.ExchangeOrderId, entry.ClientOrderId, "FILLED", .2m, completedAt);
        await f.Lifecycle.ProcessOrderUpdatesAsync(PaperExecutionAdapter.AccountId, [update], Ct);
        Assert.Equal("CANCELLED", extra.Status);
        Assert.Empty(await f.Db.Executions.ToListAsync(Ct));
        f.Clock.Now = f.Clock.Now.AddMinutes(5);
        await f.Lifecycle.ProcessOrderUpdatesAsync(PaperExecutionAdapter.AccountId,
            [update with { OccurredAt = f.Clock.Now }], Ct);
        Assert.Equal(completedAt, entry.FilledAt);
        Assert.False(await f.CanPlaceAsync("SELL"));
        // Late fragmented executions correct the completion time using exchange chronology.
        await f.Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId,
            [f.Fill(entry, .1m) with { OccurredAt = completedAt.AddSeconds(-1) },
             f.Fill(entry, .1m) with { OccurredAt = completedAt.AddSeconds(-2) }], Ct);
        Assert.Equal(completedAt.AddSeconds(-1), entry.FilledAt);
    }

    [Theory]
    [InlineData("BUY")]
    [InlineData("SELL")]
    public async Task LimitCancelsAllSameSideEntriesIncludingPartialAndUnsentButKeepsTp(string side)
    {
        await using var f = await Fixture.CreateAsync();
        await f.StartAsync();
        var first = f.ActiveEntries().Single(x => x.Side == side);
        var partial = await f.AddWorkingOrderAsync(side, "ENTRY", "PARTIALLY_FILLED", .1m);
        var pending = await f.AddWorkingOrderAsync(side, "ENTRY", "PENDING_EXCHANGE");
        var tp = await f.AddWorkingOrderAsync(side, "TAKE_PROFIT", "NEW");
        var opposite = f.ActiveEntries().Single(x => x.Side != side);
        for (var index = 0; index < 3; index++) await f.AddHistoryAsync(side, -index);
        await f.Lifecycle.MaintainEntryOrdersAsync(f.Cycle, f.Config, f.Adapter.Quote, Ct);
        Assert.Equal("CANCELLED", first.Status);
        Assert.Equal("CANCELLED", partial.Status);
        Assert.Equal("CANCELLED", pending.Status);
        Assert.DoesNotContain(pending.Id, f.Adapter.Cancelled); // Unsent intent needs no venue cancel.
        Assert.Equal("NEW", tp.Status);
        Assert.Equal("NEW", opposite.Status);
        Assert.DoesNotContain(f.ActiveEntries(), x => x.Side == side);
    }

    [Fact]
    public async Task FinalSubmissionAndReconciliationRetryCannotBypassTheLimit()
    {
        await using var f = await Fixture.CreateAsync(GridMode.SellOnly, limit: 1);
        await f.StartAsync();
        var pending = Assert.Single(f.ActiveEntries());
        pending.Status = "PENDING_EXCHANGE";
        pending.ExchangeOrderId = "pending";
        await f.Db.SaveChangesAsync(Ct);
        await f.AddHistoryAsync("SELL", -1);
        var placed = f.Adapter.Placed.Count;
        // Call the submission boundary directly, bypassing entry-maintenance checks.
        await f.Lifecycle.PlaceNewEntryOrdersAsync(f.Cycle, f.Config, [pending], Ct);
        Assert.Equal(placed, f.Adapter.Placed.Count);
        Assert.Equal("CANCELLED", pending.Status);
        var retry = await f.AddWorkingOrderAsync("SELL", "ENTRY", "PENDING_EXCHANGE");
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        Assert.Equal("CANCELLED", retry.Status);
        Assert.Equal(placed, f.Adapter.Placed.Count);
        f.Clock.Now = f.Clock.Now.AddMinutes(59);
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        Assert.Single(f.ActiveEntries());
        Assert.Equal(placed + 1, f.Adapter.Placed.Count);
    }

    [Fact]
    public async Task FailedCancellationBlocksNewEntriesAndRetriesWithoutCancellingTp()
    {
        await using var f = await Fixture.CreateAsync(GridMode.SellOnly, limit: 1);
        await f.StartAsync();
        var entry = Assert.Single(f.ActiveEntries());
        var tp = await f.AddWorkingOrderAsync("BUY", "TAKE_PROFIT", "NEW");
        await f.AddHistoryAsync("SELL", -1);
        f.Adapter.FailCancellation = true;
        Assert.False(await f.CanPlaceAsync("SELL"));
        Assert.False(await f.CanPlaceAsync("SELL"));
        Assert.Equal("NEW", entry.Status);
        Assert.Single(await f.Db.RiskAlerts.ToListAsync(Ct));
        f.Adapter.FailCancellation = false;
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        Assert.Equal("CANCELLED", entry.Status);
        Assert.Equal("NEW", tp.Status);
        Assert.Empty(f.ActiveEntries());
    }

    [Fact]
    public async Task MissingCompletionTimeBlocksOnlyAffectedSideAndReportsOnce()
    {
        await using var f = await Fixture.CreateAsync();
        var history = await f.AddHistoryAsync("SELL", -1);
        history.FilledAt = null;
        await f.Db.SaveChangesAsync(Ct);
        await f.StartAsync();
        Assert.False(await f.CanPlaceAsync("SELL"));
        Assert.False(await f.CanPlaceAsync("SELL"));
        Assert.True(await f.CanPlaceAsync("BUY"));
        Assert.Equal("BUY", Assert.Single(f.ActiveEntries()).Side);
        Assert.Equal("ENTRY_FILL_HISTORY_NOT_READY_SELL", (await f.Db.RiskAlerts.SingleAsync(Ct)).Code);
    }

    [Fact]
    public async Task ScopeUsesSameStrategyAccountEnvironmentMarketAndOnlyCompletedEntries()
    {
        await using var f = await Fixture.CreateAsync(GridMode.SellOnly, limit: 2);
        await f.AddHistoryAsync("SELL", -1, symbol: "sol/usdc");
        await f.AddHistoryAsync("SELL", -1, strategyId: "another-strategy");
        await f.AddHistoryAsync("SELL", -1, accountId: "another-account");
        await f.AddHistoryAsync("SELL", -1, environmentId: "hyperliquid-mainnet");
        await f.AddHistoryAsync("SELL", -1, symbol: "ETHUSDC");
        await f.AddHistoryAsync("BUY", -1);
        await f.AddHistoryAsync("SELL", -1, kind: "TAKE_PROFIT");
        await f.AddHistoryAsync("SELL", -1, kind: "FLATTEN");
        var partial = await f.AddHistoryAsync("SELL", -1);
        partial.FilledAt = null; partial.Status = "CANCELLED"; partial.FilledQuantity = .1m;
        await f.Db.SaveChangesAsync(Ct);
        await f.StartAsync();
        Assert.True(await f.CanPlaceAsync("SELL"));
        await f.AddHistoryAsync("SELL", -1, symbol: "SOL-USDC");
        Assert.False(await f.CanPlaceAsync("SELL"));
    }

    [Fact]
    public async Task PersistedHistoryStillBlocksAfterContextAndLifecycleRestart()
    {
        await using var f = await Fixture.CreateAsync(GridMode.SellOnly, limit: 1);
        await f.AddHistoryAsync("SELL", -1);
        await f.StartAsync();
        await using var restarted = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(f.Connection).Options);
        var cycle = await restarted.Cycles.SingleAsync(x => x.Id == f.Cycle.Id, Ct);
        var lifecycle = new GridOrderLifecycle(restarted, new ExecutionEnvironmentRegistry([f.Adapter]), new(), f.Clock);
        Assert.False(await lifecycle.CanPlaceNewEntryOrderAsync(cycle, f.Config, OrderSide.Sell, Ct));
    }

    [Fact]
    public async Task DisabledLimitDoesNotBlockEvenWithMissingHistory()
    {
        await using var f = await Fixture.CreateAsync(GridMode.SellOnly, enabled: false);
        var history = await f.AddHistoryAsync("SELL", -1);
        history.FilledAt = null;
        await f.Db.SaveChangesAsync(Ct);
        await f.StartAsync();
        Assert.Single(f.ActiveEntries());
        Assert.Empty(await f.Db.RiskAlerts.ToListAsync(Ct));
    }

    [Fact]
    public async Task SettingsSurviveCandidatePreviewSaveAndFrozenCycleAndResumeCannotBypass()
    {
        await using var f = await Fixture.CreateAsync(GridMode.SellOnly, limit: 1);
        var request = f.Request;
        var candidate = new CandidateConfiguration(request.ExchangeAccountId, request.Symbol, request.GridMode,
            request.MaxLevelsPerSide, 1, request.InitialGapPoints, request.GridSpacingPoints,
            request.GridSpacingStepPoints, request.TakeProfitPoints, request.BaseLotSize,
            request.LotSizeIncreasePercent, request.MaxTradeLot, request.MaxNetLot,
            request.DefaultExecutionEnvironmentId, request.DefaultExecutionAccountId, "MANUAL", true, 60, 1);
        var preview = await f.Workflow.CreatePreviewAsync(new(null, null, 100m, candidate, null), Ct);
        Assert.True(preview.Configuration.EntryFillLimitEnabled);
        Assert.Equal(60, preview.Configuration.EntryFillWindowMinutes);
        Assert.Equal(1, preview.Configuration.MaxEntryFillsPerSide);
        await f.StartAsync();
        Assert.True(f.Config.EntryFillLimitEnabled);
        Assert.Equal(1, f.Config.MaxEntryFillsPerSide);
        await f.Lifecycle.SetOperatorPauseAsync(f.Cycle, true, f.Cycle.StateVersion, Ct);
        await f.AddHistoryAsync("SELL", -1);
        await f.Workflow.UpdateStrategyAsync(f.Strategy.Id, request with { EntryFillLimitEnabled = false }, Ct);
        await f.Lifecycle.SetOperatorPauseAsync(f.Cycle, false, f.Cycle.StateVersion, Ct);
        Assert.Empty(f.ActiveEntries());
        Assert.True(JsonSerializer.Deserialize<GridConfiguration>(f.Cycle.FrozenConfigurationJson, JsonSupport.Options)!.EntryFillLimitEnabled);
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = new("Data Source=:memory:");
        private readonly ServiceProvider services = new ServiceCollection().AddLogging().AddSignalR().Services.BuildServiceProvider();
        public TestClock Clock { get; } = new();
        public TradingDbContext Db { get; private set; } = null!;
        public RecordingAdapter Adapter { get; private set; } = null!;
        public GridOrderLifecycle Lifecycle { get; private set; } = null!;
        public GridStrategyWorkflow Workflow { get; private set; } = null!;
        public StrategyRequest Request { get; private set; } = null!;
        public StrategyEntity Strategy { get; private set; } = null!;
        public CycleEntity Cycle { get; private set; } = null!;
        public GridConfiguration Config { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(GridMode mode = GridMode.TwoWay, int limit = 3, bool enabled = true)
        {
            var f = new Fixture();
            await f.Connection.OpenAsync(Ct);
            f.Db = new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(f.Connection).Options);
            await f.Db.Database.EnsureCreatedAsync(Ct);
            var market = new MarketState();
            f.Adapter = new(new PaperExecutionAdapter(market, f.Db), f.Clock);
            var registry = new ExecutionEnvironmentRegistry([f.Adapter]);
            var gate = new ExecutionAccountOperationGate();
            f.Lifecycle = new(f.Db, registry, gate, f.Clock);
            f.Workflow = new(f.Db, market, new PreviewStore(), registry, f.Lifecycle,
                f.services.GetRequiredService<IHubContext<TradingHub>>(), gate);
            f.Request = StrategyRequest.Default with
            {
                GridMode = mode, CenterSuggestionMode = "MANUAL", ManualCenterPrice = 100m,
                MaxLevelsPerSide = 6, BaseLotSize = .2m, MaxTradeLot = 0m, MaxNetLot = 10m,
                LotSizeIncreasePercent = 0m, InitialGapPoints = 200m,
                GridSpacingPoints = 100m, GridSpacingStepPoints = 0m, TakeProfitPoints = 50m,
                EntryFillLimitEnabled = enabled, EntryFillWindowMinutes = 60, MaxEntryFillsPerSide = limit
            };
            f.Strategy = await f.Workflow.CreateStrategyAsync(f.Request, Ct);
            return f;
        }

        public async Task StartAsync()
        {
            var preview = await Workflow.CreatePreviewAsync(new(Strategy.Id, Strategy.Version, 100m, null, null), Ct);
            (_, Cycle) = await Workflow.StartCycleAsync(Strategy.Id, new(preview.Id, 100m, new(true, true, "PAPER")), Ids.New("start"), Ct);
            Config = JsonSerializer.Deserialize<GridConfiguration>(Cycle.FrozenConfigurationJson, JsonSupport.Options)!;
        }

        public async Task<OrderEntity> AddHistoryAsync(string side, int minutesAgo,
            string? strategyId = null, string? accountId = null, string? environmentId = null,
            string symbol = "SOLUSDT", string kind = "ENTRY")
        {
            var historyCycle = new CycleEntity
            {
                Id = Ids.New("history"), StrategyId = strategyId ?? Strategy.Id,
                ExecutionEnvironmentId = environmentId ?? ExecutionEnvironmentIds.PaperLocal,
                ExecutionAccountId = accountId ?? PaperExecutionAdapter.AccountId,
                State = "WAITING_FOR_OPERATOR", IsTerminal = true, FrozenConfigurationJson = "{}", FrozenPlanJson = "{}",
                ExitReason = "TEST", StartedAt = Clock.Now.AddHours(-2), EndedAt = Clock.Now
            };
            var order = Order(side, kind, "FILLED", historyCycle.Id);
            order.Symbol = symbol; order.FilledQuantity = .2m; order.FilledAt = Clock.Now.AddMinutes(minutesAgo);
            Db.AddRange(historyCycle, order);
            await Db.SaveChangesAsync(Ct);
            return order;
        }

        public async Task<OrderEntity> AddWorkingOrderAsync(string side, string kind, string status, decimal filled = 0m)
        {
            var order = Order(side, kind, status, Cycle.Id);
            order.FilledQuantity = filled;
            Db.Orders.Add(order); await Db.SaveChangesAsync(Ct); return order;
        }

        private OrderEntity Order(string side, string kind, string status, string cycleId) => new()
        {
            Id = Ids.New("order"), CycleId = cycleId, ClientOrderId = Ids.New("client"), ExchangeOrderId = Ids.New("venue"),
            Symbol = "SOLUSDT", Side = side, Kind = kind, Status = status, GridLevel = 4, Quantity = .2m,
            Price = side == "BUY" ? 99.5m : 100.5m, CreatedAt = Clock.Now.AddHours(-2), UpdatedAt = Clock.Now
        };
        public OrderEntity[] ActiveEntries() => Db.Orders.Where(x => x.CycleId == Cycle.Id && x.Kind == "ENTRY" &&
            (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED" || x.Status == "PENDING_EXCHANGE" || x.Status == "UNKNOWN")).ToArray();
        public NormalizedExecutionFill Fill(OrderEntity order, decimal quantity) =>
            new(Ids.New("fill"), order.ExchangeOrderId, order.ClientOrderId, order.Side, order.Price, quantity, 0m, Clock.Now);
        public Task<bool> CanPlaceAsync(string side) => Lifecycle.CanPlaceNewEntryOrderAsync(Cycle, Config, Enum.Parse<OrderSide>(side, true), Ct);
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); await services.DisposeAsync(); }
    }

    private sealed class RecordingAdapter(PaperExecutionAdapter paper, TimeProvider clock) : IExecutionAdapter, IOrderAmendmentAdapter
    {
        public List<string> Placed { get; } = [];
        public List<string> Cancelled { get; } = [];
        public bool FailCancellation { get; set; }
        public ExecutionQuote Quote => new(99.99m, 100.01m, 100m, clock.GetUtcNow());
        public ExecutionEnvironmentDescriptor Environment => paper.Environment;
        public Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct) => paper.GetAccountsAsync(ct);
        public Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection selection, string symbol, decimal? referencePrice, CancellationToken ct) => paper.GetInstrumentAsync(selection, symbol, referencePrice, ct);
        public Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection selection, string symbol, CancellationToken ct) => Task.FromResult(Quote);
        public Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection selection, string symbol, CancellationToken ct) => Task.FromResult(Quote);
        public async Task PlaceOrdersAsync(ExecutionSelection selection, GridConfiguration config, IEnumerable<OrderEntity> orders, CancellationToken ct)
        {
            var batch = orders.ToArray();
            Placed.AddRange(batch.Select(x => x.Id));
            await paper.PlaceOrdersAsync(selection, config, batch, ct);
        }
        public async Task CancelOrdersAsync(ExecutionSelection selection, IEnumerable<OrderEntity> orders, CancellationToken ct)
        {
            if (FailCancellation) throw new HttpRequestException("Synthetic cancel timeout");
            var batch = orders.ToArray(); Cancelled.AddRange(batch.Select(x => x.Id));
            await paper.CancelOrdersAsync(selection, batch, ct);
        }
        public Task AmendOrderAsync(ExecutionSelection selection, GridConfiguration config, OrderEntity order, decimal price, decimal quantity, CancellationToken ct) => paper.AmendOrderAsync(selection, config, order, price, quantity, ct);
        public Task<decimal> FlattenAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config, CancellationToken ct) => paper.FlattenAsync(selection, cycle, config, ct);
        public Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config, CancellationToken ct) => paper.ReconcileAsync(selection, cycle, config, ct);
    }
}
