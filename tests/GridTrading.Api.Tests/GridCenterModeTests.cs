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

public sealed class GridCenterModeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(GridMode.TwoWay)]
    [InlineData(GridMode.BuyOnly)]
    [InlineData(GridMode.SellOnly)]
    public async Task CurrentMidRebuildsFromStartupBidAskWithoutEnteredPrice(GridMode mode)
    {
        await using var f = await Fixture.CreateAsync();
        var strategy = await f.Workflow.CreateStrategyAsync(f.Request with { GridMode = mode }, Ct);
        var preview = await f.Workflow.CreatePreviewAsync(new(strategy.Id, strategy.Version, 0m, null, null), Ct);
        Assert.Equal(100.015m, preview.Plan.CenterPrice);
        f.Adapter.Quote = new(101.11m, 101.14m, 101.125m, DateTimeOffset.UtcNow);
        var (_, cycle) = await f.Workflow.StartCycleAsync(strategy.Id,
            new(preview.Id, 0m, new(true, true, "PAPER")), "start", Ct);
        Assert.Equal(101.125m, cycle.FixedCenterPrice);
        var config = JsonSerializer.Deserialize<GridConfiguration>(cycle.FrozenConfigurationJson, JsonSupport.Options)!;
        var plan = JsonSerializer.Deserialize<GridPlan>(cycle.FrozenPlanJson, JsonSupport.Options)!;
        Assert.Equal(101.11m, config.InitialBid);
        Assert.Equal(101.14m, config.InitialAsk);
        Assert.Equal(mode, config.GridMode);
        var orders = await f.Db.Orders.ToListAsync(Ct);
        Assert.Equal(mode == GridMode.TwoWay ? 2 : 1, orders.Count);
        foreach (var order in orders)
        {
            Assert.Equal(0, order.GridLevel);
            Assert.Equal(order.Side == "BUY" ? 101.05m : 101.20m, order.Price);
            Assert.Equal(order.Price, plan.Levels.Single(x => x.Side.ToString().ToUpperInvariant() == order.Side && x.LevelIndex == 0).EntryPrice);
        }
        var frozen = cycle.FrozenPlanJson;
        f.Adapter.Quote = new(120m, 120.03m, 120.015m, DateTimeOffset.UtcNow);
        await f.Workflow.UpdateStrategyAsync(strategy.Id, f.Request with
        { CenterSuggestionMode = "MANUAL", ManualCenterPrice = 123m }, Ct);
        Assert.Equal(frozen, cycle.FrozenPlanJson);
        Assert.Equal(101.125m, cycle.FixedCenterPrice);
    }

    [Fact]
    public async Task ManualPriceSurvivesSaveReloadPreviewAndStart()
    {
        await using var f = await Fixture.CreateAsync();
        var strategy = await f.Workflow.CreateStrategyAsync(f.Request with
        { CenterSuggestionMode = "MANUAL", ManualCenterPrice = 123.455m, InitialGapPoints = 10m }, Ct);
        var saved = JsonSerializer.Deserialize<StrategyRequest>(strategy.ConfigurationJson, JsonSupport.Options)!;
        Assert.Equal(123.455m, saved.ManualCenterPrice);
        f.Db.ChangeTracker.Clear();
        // A caller's unrelated quote must never replace the saved manual center.
        var preview = await f.Workflow.CreatePreviewAsync(new(strategy.Id, strategy.Version, 999m, null, null), Ct);
        Assert.Equal(123.455m, preview.Plan.CenterPrice);
        Assert.Null(preview.Configuration.InitialBid);
        Assert.Null(preview.Configuration.InitialAsk);
        f.Adapter.Quote = new(123.44m, 123.47m, 123.455m, DateTimeOffset.UtcNow);
        var mismatch = await Assert.ThrowsAsync<TradingProblemException>(() => f.Workflow.StartCycleAsync(strategy.Id,
            new(preview.Id, 999m, new(true, true, "PAPER")), "wrong-center", Ct));
        Assert.Equal("PREVIEW_MISMATCH", mismatch.Code);
        var (_, cycle) = await f.Workflow.StartCycleAsync(strategy.Id,
            new(preview.Id, preview.Plan.CenterPrice, new(true, true, "PAPER")), "start", Ct);
        Assert.Equal(123.455m, cycle.FixedCenterPrice);
        var orders = await f.Db.Orders.ToListAsync(Ct);
        Assert.Equal(123.40m, orders.Single(x => x.Side == "BUY").Price);
        Assert.Equal(123.51m, orders.Single(x => x.Side == "SELL").Price);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ManualRequiresPositiveSavedPrice(int? center)
    {
        await using var f = await Fixture.CreateAsync();
        var error = await Assert.ThrowsAsync<TradingProblemException>(() => f.Workflow.CreateStrategyAsync(
            f.Request with { CenterSuggestionMode = "MANUAL", ManualCenterPrice = center }, Ct));
        Assert.Equal("CENTER_PRICE", error.Code);
        Assert.Empty(await f.Db.Strategies.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidOrStaleStartupQuoteDoesNotCreateCycle(bool stale)
    {
        await using var f = await Fixture.CreateAsync();
        var strategy = await f.Workflow.CreateStrategyAsync(f.Request, Ct);
        var preview = await f.Workflow.CreatePreviewAsync(new(strategy.Id, strategy.Version, 0m, null, null), Ct);
        f.Adapter.Quote = stale
            ? f.Adapter.Quote with { AsOf = DateTimeOffset.UtcNow.AddMinutes(-1) }
            : new(102m, 101m, 101.5m, DateTimeOffset.UtcNow);
        var error = await Assert.ThrowsAsync<TradingProblemException>(() => f.Workflow.StartCycleAsync(strategy.Id,
            new(preview.Id, 0m, new(true, true, "PAPER")), "start", Ct));
        Assert.Equal("MARKET_DATA_STALE", error.Code);
        Assert.Empty(await f.Db.Cycles.ToListAsync(Ct));
        Assert.Empty(await f.Db.Orders.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("CURRENT_MID", 0)]
    [InlineData("MANUAL", 123)]
    public async Task CandidatePreviewUsesSelectedMode(string mode, int center)
    {
        await using var f = await Fixture.CreateAsync();
        var r = f.Request;
        var candidate = new CandidateConfiguration(r.ExchangeAccountId, r.Symbol, r.GridMode,
            r.MaxLevelsPerSide, 1, r.InitialGapPoints, r.GridSpacingPoints, r.GridSpacingStepPoints,
            r.TakeProfitPoints, r.BaseLotSize, r.LotSizeIncreasePercent, r.MaxTradeLot, r.MaxNetLot,
            CenterSuggestionMode: mode);
        var preview = await f.Workflow.CreatePreviewAsync(new(null, null, center, candidate, null), Ct);
        Assert.Equal(mode == "MANUAL" ? 123m : 100.015m, preview.Plan.CenterPrice);
        Assert.Equal(mode == "CURRENT_MID" ? 100m : (decimal?)null, preview.Configuration.InitialBid);
    }

    [Theory]
    [InlineData("MANUAL")]
    [InlineData("CURRENT_MID")]
    public async Task SmallTickPreviewCanBeSavedAndUpdatedUsingSelectedInstrumentRules(string mode)
    {
        await using var f = await Fixture.CreateAsync();
        f.Adapter.TickSize = .00001m;
        f.Adapter.Quote = new(.99999m, 1.00001m, 1m, DateTimeOffset.UtcNow);
        var request = f.Request with
        {
            CenterSuggestionMode = mode, ManualCenterPrice = mode == "MANUAL" ? 1m : null,
            InitialGapPoints = 1000m, GridSpacingPoints = 1000m,
            BaseLotSize = 100m, MaxTradeLot = 0m, MaxNetLot = 1000m, TakeProfitPoints = 100m
        };
        var candidate = new CandidateConfiguration(request.ExchangeAccountId, request.Symbol, request.GridMode,
            request.MaxLevelsPerSide, 1, request.InitialGapPoints, request.GridSpacingPoints, 0m,
            request.TakeProfitPoints, request.BaseLotSize, 0m, 0m, request.MaxNetLot,
            CenterSuggestionMode: mode);
        var preview = await f.Workflow.CreatePreviewAsync(new(null, null, request.ManualCenterPrice ?? 0m, candidate, null), Ct);
        Assert.Equal(6, preview.Plan.Levels.Count);
        Assert.True(preview.Plan.OutermostBuyPrice > 0m);

        var strategy = await f.Workflow.CreateStrategyAsync(request, Ct);
        var savedPreview = await f.Workflow.CreatePreviewAsync(new(strategy.Id, strategy.Version, 0m, null, null), Ct);
        Assert.Equal(preview.Plan.Levels.ToArray(), savedPreview.Plan.Levels.ToArray());
        var version = strategy.Version;
        f.Adapter.Quote = new(1.09999m, 1.10001m, 1.1m, DateTimeOffset.UtcNow);
        var updated = await f.Workflow.UpdateStrategyAsync(strategy.Id, request with
        { ManualCenterPrice = mode == "MANUAL" ? 1.1m : null }, Ct);
        Assert.NotNull(updated);
        Assert.Equal(version + 1, updated.Version);
        var updatedPreview = await f.Workflow.CreatePreviewAsync(new(strategy.Id, updated.Version, 0m, null, null), Ct);
        Assert.Equal(1.1m, updatedPreview.Plan.CenterPrice);
        Assert.True(updatedPreview.Plan.OutermostBuyPrice > savedPreview.Plan.OutermostBuyPrice);
    }

    [Fact]
    public async Task SaveAndUpdateEnforceSelectedInstrumentMinimumNotional()
    {
        await using var f = await Fixture.CreateAsync();
        f.Adapter.MinOrderNotional = 200m;
        var invalid = f.Request with { CenterSuggestionMode = "MANUAL", ManualCenterPrice = 100m };
        var error = await Assert.ThrowsAsync<GridValidationException>(() => f.Workflow.CreateStrategyAsync(invalid, Ct));
        Assert.Equal("MIN_ORDER_NOTIONAL", error.Code);
        Assert.Empty(await f.Db.Strategies.ToListAsync(Ct));

        var strategy = await f.Workflow.CreateStrategyAsync(invalid with { BaseLotSize = 3m, MaxTradeLot = 0m }, Ct);
        var before = strategy.ConfigurationJson;
        var version = strategy.Version;
        error = await Assert.ThrowsAsync<GridValidationException>(() => f.Workflow.UpdateStrategyAsync(strategy.Id, invalid, Ct));
        Assert.Equal("MIN_ORDER_NOTIONAL", error.Code);
        Assert.Equal(version, strategy.Version);
        Assert.Equal(before, strategy.ConfigurationJson);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        private readonly ServiceProvider services = new ServiceCollection().AddLogging().AddSignalR().Services.BuildServiceProvider();
        public TradingDbContext Db { get; private set; } = null!;
        public GridStrategyWorkflow Workflow { get; private set; } = null!;
        public QuoteAdapter Adapter { get; private set; } = null!;
        public StrategyRequest Request => StrategyRequest.Default with
        {
            MaxLevelsPerSide = 3, InitialGapPoints = 11m, GridSpacingPoints = 20m,
            GridSpacingStepPoints = 0m, BaseLotSize = 1m, LotSizeIncreasePercent = 0m,
            MaxNetLot = 10m, TakeProfitPoints = 10m
        };
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            await f.connection.OpenAsync(Ct);
            f.Db = new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync(Ct);
            var market = new MarketState();
            f.Adapter = new(new PaperExecutionAdapter(market, f.Db));
            var registry = new ExecutionEnvironmentRegistry([f.Adapter]);
            var gate = new ExecutionAccountOperationGate();
            f.Workflow = new(f.Db, market, new PreviewStore(), registry, new(f.Db, registry, gate),
                f.services.GetRequiredService<IHubContext<TradingHub>>(), gate);
            return f;
        }
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
            await services.DisposeAsync();
        }
    }

    private sealed class QuoteAdapter(PaperExecutionAdapter inner) : IExecutionAdapter
    {
        public ExecutionQuote Quote { get; set; } = new(100m, 100.03m, 100.015m, DateTimeOffset.UtcNow);
        public decimal TickSize { get; set; } = .01m;
        public decimal MinOrderNotional { get; set; } = 5m;
        public ExecutionEnvironmentDescriptor Environment => inner.Environment;
        public Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct) => inner.GetAccountsAsync(ct);
        public async Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection s, string symbol, decimal? referencePrice, CancellationToken ct) =>
            (await inner.GetInstrumentAsync(s, symbol, referencePrice, ct)) with { TickSize = TickSize, MinOrderNotional = MinOrderNotional };
        public Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection s, string symbol, CancellationToken ct) => Task.FromResult(Quote);
        public Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection s, string symbol, CancellationToken ct) => Task.FromResult(Quote);
        public Task PlaceOrdersAsync(ExecutionSelection s, GridConfiguration c, IEnumerable<OrderEntity> orders, CancellationToken ct) => inner.PlaceOrdersAsync(s, c, orders, ct);
        public Task CancelOrdersAsync(ExecutionSelection s, IEnumerable<OrderEntity> orders, CancellationToken ct) => inner.CancelOrdersAsync(s, orders, ct);
        public Task<decimal> FlattenAsync(ExecutionSelection s, CycleEntity cycle, GridConfiguration c, CancellationToken ct) => inner.FlattenAsync(s, cycle, c, ct);
        public Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection s, CycleEntity cycle, GridConfiguration c, CancellationToken ct) => inner.ReconcileAsync(s, cycle, c, ct);
    }
}
