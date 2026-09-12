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

public sealed class SingleModeWorkflowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(GridMode.BuyOnly, "BUY_ONLY", "BUY", "SELL", 1)]
    [InlineData(GridMode.SellOnly, "SELL_ONLY", "SELL", "BUY", -1)]
    public async Task ModeSurvivesSavePreviewStartFillsPauseAndReentry(
        GridMode mode, string wireMode, string entrySide, string exitSide, int sign)
    {
        await using var f = await Fixture.CreateAsync(mode);
        using var json = JsonDocument.Parse(f.Strategy.ConfigurationJson);
        Assert.Equal(wireMode, json.RootElement.GetProperty("gridMode").GetString());
        Assert.Equal(mode, f.Config.GridMode);
        Assert.Equal(3, f.Plan.Levels.Count);
        Assert.All(f.Plan.Levels, level => Assert.Equal(entrySide, level.Side.ToString().ToUpperInvariant()));
        var entry = Assert.Single(f.ActiveEntries());
        Assert.Equal(entrySide, entry.Side);
        Assert.True(sign * (f.Config.CenterPrice - entry.Price) > 0m);

        // Editing the template must not change an already running cycle's direction.
        await f.Workflow.UpdateStrategyAsync(f.Strategy.Id, f.Request with
        {
            GridMode = mode == GridMode.BuyOnly ? GridMode.SellOnly : GridMode.BuyOnly
        }, Ct);
        Assert.Equal(mode, JsonSerializer.Deserialize<GridConfiguration>(
            f.Cycle.FrozenConfigurationJson, JsonSupport.Options)!.GridMode);

        var partial = f.Fill(entry, .1m);
        Assert.Equal(1, await f.Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId, [partial], Ct));
        Assert.Equal(0, await f.Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId, [partial], Ct));
        Assert.Equal("PARTIALLY_FILLED", entry.Status);
        Assert.Same(entry, Assert.Single(f.ActiveEntries()));
        var tp = Assert.Single(f.Db.Orders.Where(x => x.Kind == "TAKE_PROFIT").ToArray());
        Assert.Equal(exitSide, tp.Side);
        Assert.Equal(GridMath.TakeProfitPrice(Enum.Parse<OrderSide>(entrySide, true), entry.Price,
            f.Config.TakeProfitPoints, f.Config.TickSize), tp.Price);
        Assert.Equal(.1m, tp.Quantity);

        await f.Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId, [f.Fill(entry, .1m)], Ct);
        Assert.Equal("FILLED", entry.Status);
        Assert.Single(f.Db.Orders.Where(x => x.Kind == "TAKE_PROFIT").ToArray());
        Assert.Equal(.2m, tp.Quantity);
        Assert.Equal(sign * .2m, f.Cycle.ActualNetQuantity);
        var next = Assert.Single(f.ActiveEntries());
        Assert.Equal(entrySide, next.Side);
        Assert.Equal(1, next.GridLevel);
        Assert.Equal(.3m, next.Quantity); // 50% geometric growth, normalized to the venue step.

        await f.CommandAsync("PAUSE_ENTRIES");
        Assert.Equal("PAUSED", f.Cycle.State);
        Assert.Empty(f.ActiveEntries());
        Assert.Equal("NEW", tp.Status);
        await f.Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId, [f.Fill(tp, .1m)], Ct);
        Assert.Equal("PARTIALLY_FILLED", tp.Status);
        Assert.Equal(sign * .1m, f.Cycle.ActualNetQuantity);
        Assert.Empty(f.ActiveEntries());
        await f.Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId, [f.Fill(tp, .1m)], Ct);
        Assert.Equal(0m, f.Cycle.ActualNetQuantity);
        Assert.Equal("CLOSED", (await f.Db.VirtualLots.SingleAsync(Ct)).Status);
        Assert.True(f.Cycle.RealisedCyclePnl > 0m);
        Assert.Empty(f.ActiveEntries());

        await f.CommandAsync("RESUME_ENTRIES");
        var reentry = Assert.Single(f.ActiveEntries());
        Assert.Equal(entrySide, reentry.Side);
        Assert.Equal(0, reentry.GridLevel);
        Assert.Equal(entry.Price, reentry.Price);
        Assert.NotEqual(entry.Id, reentry.Id);
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        Assert.Same(reentry, Assert.Single(f.ActiveEntries()));
        Assert.All(f.Db.Orders.Where(x => x.Kind == "ENTRY"), x => Assert.Equal(entrySide, x.Side));
        Assert.Empty(await f.Db.RiskAlerts.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(GridMode.BuyOnly, "BUY", 1)]
    [InlineData(GridMode.SellOnly, "SELL", -1)]
    public async Task PositionLimitBlocksNewEntryButAllowsTpAndReentry(GridMode mode, string side, int sign)
    {
        await using var f = await Fixture.CreateAsync(mode, maxNetLot: .2m);
        var entry = Assert.Single(f.ActiveEntries());
        await f.Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId, [f.Fill(entry, entry.Quantity)], Ct);
        Assert.Equal(sign * .2m, f.Cycle.ActualNetQuantity);
        Assert.Empty(f.ActiveEntries());
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", Ct);
        Assert.Equal("NEW", tp.Status);
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        Assert.Empty(f.ActiveEntries());
        await f.Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId, [f.Fill(tp, tp.Quantity)], Ct);
        Assert.Equal(0m, f.Cycle.ActualNetQuantity);
        var reentry = Assert.Single(f.ActiveEntries());
        Assert.Equal(side, reentry.Side);
        Assert.Equal(0, reentry.GridLevel);
    }

    [Theory]
    [InlineData(GridMode.BuyOnly)]
    [InlineData(GridMode.SellOnly)]
    public async Task TakeProfitBeforeEntryCompletesCancelsTailAndReenters(GridMode mode)
    {
        await using var f = await Fixture.CreateAsync(mode);
        var entry = Assert.Single(f.ActiveEntries());
        await f.Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId, [f.Fill(entry, .1m)], Ct);
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", Ct);
        await f.Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId, [f.Fill(tp, .1m)], Ct);
        Assert.Equal("CANCELLED", entry.Status);
        Assert.Equal(.1m, entry.FilledQuantity);
        Assert.Equal(0m, f.Cycle.ActualNetQuantity);
        var reentry = Assert.Single(f.ActiveEntries());
        Assert.Equal(entry.Side, reentry.Side);
        Assert.Equal(entry.GridLevel, reentry.GridLevel);
        Assert.NotEqual(entry.Id, reentry.Id);
    }

    [Theory]
    [InlineData(GridMode.BuyOnly)]
    [InlineData(GridMode.SellOnly)]
    public async Task GridBoundaryRemovesEntryAndReturningPriceRestoresSelectedSide(GridMode mode)
    {
        await using var f = await Fixture.CreateAsync(mode);
        var original = Assert.Single(f.ActiveEntries());
        var boundary = mode == GridMode.BuyOnly ? f.Plan.OutermostBuyPrice : f.Plan.OutermostSellPrice;
        await f.Lifecycle.MaintainEntryOrdersAsync(f.Cycle, f.Config,
            new ExecutionQuote(boundary - .001m, boundary + .001m, boundary, DateTimeOffset.UtcNow), Ct);
        Assert.Equal("CANCELLED", original.Status);
        Assert.Empty(f.ActiveEntries());
        await f.Lifecycle.MaintainEntryOrdersAsync(f.Cycle, f.Config, null, Ct);
        var restored = Assert.Single(f.ActiveEntries());
        Assert.Equal(original.Side, restored.Side);
        Assert.Equal(0, restored.GridLevel);
    }

    [Theory]
    [InlineData(GridMode.BuyOnly, "SELL", "CLOSE")]
    [InlineData(GridMode.SellOnly, "BUY", "CLOSE")]
    [InlineData(GridMode.BuyOnly, "SELL", "EMERGENCY_FLATTEN")]
    [InlineData(GridMode.SellOnly, "BUY", "EMERGENCY_FLATTEN")]
    public async Task CloseCancelsOrdersAndFlattensInOppositeDirection(GridMode mode, string exitSide, string command)
    {
        await using var f = await Fixture.CreateAsync(mode);
        var entry = Assert.Single(f.ActiveEntries());
        await f.Lifecycle.ProcessFillsAsync(PaperExecutionAdapter.AccountId, [f.Fill(entry, entry.Quantity)], Ct);
        await f.CommandAsync(command);
        Assert.True(f.Cycle.IsTerminal);
        Assert.Equal(0m, f.Cycle.ActualNetQuantity);
        Assert.Equal(0m, f.Cycle.ReconstructedNetQuantity);
        Assert.Empty(f.Db.Orders.Where(x => x.Status == "NEW" || x.Status == "PARTIALLY_FILLED").ToArray());
        var flatten = await f.Db.Orders.SingleAsync(x => x.Kind == "FLATTEN", Ct);
        Assert.Equal(exitSide, flatten.Side);
        Assert.Equal(entry.Quantity, flatten.FilledQuantity);
    }

    [Theory]
    [InlineData(GridMode.BuyOnly)]
    [InlineData(GridMode.SellOnly)]
    public async Task CandidatePreviewMatchesSavedStrategyPreview(GridMode mode)
    {
        await using var f = await Fixture.CreateAsync(mode);
        var request = f.Request;
        var candidate = new CandidateConfiguration(request.ExchangeAccountId, request.Symbol, mode,
            request.MaxLevelsPerSide, request.WorkingEntriesPerSide, request.InitialGapPoints,
            request.GridSpacingPoints, request.GridSpacingStepPoints, request.TakeProfitPoints,
            request.BaseLotSize, request.LotSizeIncreasePercent, request.MaxTradeLot, request.MaxNetLot,
            request.DefaultExecutionEnvironmentId, request.DefaultExecutionAccountId);
        var payload = JsonSerializer.Serialize(new PreviewRequest(null, null, f.Config.CenterPrice,
            candidate, null), JsonSupport.Options);
        var preview = await f.Workflow.CreatePreviewAsync(
            JsonSerializer.Deserialize<PreviewRequest>(payload, JsonSupport.Options)!, Ct);
        Assert.Equal(mode, preview.Configuration.GridMode);
        Assert.Equal(f.Plan.Levels.ToArray(), preview.Plan.Levels.ToArray());
        Assert.Equal(f.Plan.OutermostBuyPrice, preview.Plan.OutermostBuyPrice);
        Assert.Equal(f.Plan.OutermostSellPrice, preview.Plan.OutermostSellPrice);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddSignalR().Services.BuildServiceProvider();
        public TradingDbContext Db { get; private set; } = null!;
        public GridStrategyWorkflow Workflow { get; private set; } = null!;
        public GridOrderLifecycle Lifecycle { get; private set; } = null!;
        public StrategyRequest Request { get; private set; } = null!;
        public StrategyEntity Strategy { get; private set; } = null!;
        public CycleEntity Cycle { get; private set; } = null!;
        public GridConfiguration Config { get; private set; } = null!;
        public GridPlan Plan { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(GridMode mode, decimal maxNetLot = 2m)
        {
            var f = new Fixture();
            await f._connection.OpenAsync(Ct);
            f.Db = new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(f._connection).Options);
            await f.Db.Database.EnsureCreatedAsync(Ct);
            var market = new MarketState();
            var registry = new ExecutionEnvironmentRegistry([new PaperExecutionAdapter(market, f.Db)]);
            var gate = new ExecutionAccountOperationGate();
            f.Lifecycle = new(f.Db, registry, gate);
            f.Workflow = new(f.Db, market, new PreviewStore(), registry, f.Lifecycle,
                f._services.GetRequiredService<IHubContext<TradingHub>>(), gate);
            f.Request = StrategyRequest.Default with
            {
                GridMode = mode, MaxLevelsPerSide = 3, BaseLotSize = .2m, MaxTradeLot = 0m,
                MaxNetLot = maxNetLot, LotSizeIncreasePercent = 50m,
                InitialGapPoints = 100m, GridSpacingPoints = 100m, GridSpacingStepPoints = 0m,
                TakeProfitPoints = 50m
            };
            // Exercise the same string-enum contract used by the browser.
            var request = JsonSerializer.Deserialize<StrategyRequest>(
                JsonSerializer.Serialize(f.Request, JsonSupport.Options), JsonSupport.Options)!;
            f.Strategy = await f.Workflow.CreateStrategyAsync(request, Ct);
            market.Tick();
            var center = market.Snapshot(request.Symbol).Mid;
            var preview = await f.Workflow.CreatePreviewAsync(new(f.Strategy.Id, f.Strategy.Version,
                center, null, null), Ct);
            f.Config = preview.Configuration;
            f.Plan = preview.Plan;
            (_, f.Cycle) = await f.Workflow.StartCycleAsync(f.Strategy.Id,
                new(preview.Id, center, new(true, true, "PAPER")), "start", Ct);
            return f;
        }

        public OrderEntity[] ActiveEntries() => Db.Orders.Where(x => x.Kind == "ENTRY" &&
            (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED" || x.Status == "PENDING_EXCHANGE" || x.Status == "UNKNOWN")).ToArray();
        public NormalizedExecutionFill Fill(OrderEntity order, decimal quantity) =>
            new(Ids.New("fill"), order.ExchangeOrderId, order.ClientOrderId, order.Side, order.Price,
                quantity, 0m, DateTimeOffset.UtcNow);
        public Task<OperationEntity> CommandAsync(string command) => Workflow.CommandAsync(Cycle.Id,
            command, "single-mode regression", Ids.New("command"), Cycle.StateVersion, true, Ct);
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
            await _services.DisposeAsync();
        }
    }
}
