using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Infrastructure;
using GridTrading.Api.Services;
using GridTrading.Api.Strategies.Grid.Configuration;
using GridTrading.Domain;

namespace GridTrading.Api.Tests;

public sealed class GridConfigurationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly ExecutionSelection Selection = new(ExecutionEnvironmentIds.PaperLocal, "test");
    private static StrategyRequest Request => StrategyRequest.Default with
    {
        Symbol = "SOL", CenterSuggestionMode = "MANUAL", ManualCenterPrice = 123m,
        MaxLevelsPerSide = 2, InitialGapPoints = 20m, GridSpacingPoints = 20m,
        GridSpacingStepPoints = 0m, TakeProfitPoints = 10m, BaseLotSize = 1m,
        MaxTradeLot = 0m, LotSizeIncreasePercent = 0m, MaxNetLot = 10m,
        MakerFeeRate = .7m, TakerFeeRate = .8m
    };

    [Fact]
    public void SavedAndCandidateMappingsKeepTheirOwnCentersAndOmittedDefaults()
    {
        var request = Request with
        {
            AutoRestart = true, IncludeFunding = false, PostOnlyEntries = false,
            PartialFillCancelAfterMinutes = 7, FaultExposureThresholdUsdt = 25m,
            GridMode = GridMode.SellOnly, SingleModeMoveDistancePoints = 75m,
            SingleModeMoveIntervalSeconds = 90, EntryFillLimitEnabled = true,
            EntryFillWindowMinutes = 15, MaxEntryFillsPerSide = 4
        };
        var candidate = new CandidateConfiguration(request.ExchangeAccountId, request.Symbol, request.GridMode,
            request.MaxLevelsPerSide, request.WorkingEntriesPerSide, request.InitialGapPoints,
            request.GridSpacingPoints, request.GridSpacingStepPoints, request.TakeProfitPoints,
            request.BaseLotSize, request.LotSizeIncreasePercent, request.MaxTradeLot, request.MaxNetLot,
            CenterSuggestionMode: "MANUAL", EntryFillLimitEnabled: true, EntryFillWindowMinutes: 15,
            MaxEntryFillsPerSide: 4, SingleModeMoveDistancePoints: 75m, SingleModeMoveIntervalSeconds: 90);

        var saved = GridConfigurationMapper.FromStrategy(request, 999m);
        var draft = GridConfigurationMapper.FromCandidate(candidate, 999m);

        Assert.Equal(123m, saved.CenterPrice);
        Assert.Equal(999m, draft.CenterPrice);
        Assert.Equal(.7m, saved.MakerFeeRate);
        Assert.Equal(.0002m, draft.MakerFeeRate);
        Assert.True(saved.AutoRestart);
        Assert.False(draft.AutoRestart);
        Assert.False(saved.IncludeFunding);
        Assert.True(draft.IncludeFunding);
        Assert.False(saved.PostOnlyEntries);
        Assert.True(draft.PostOnlyEntries);
        Assert.Equal(7, saved.PartialFillCancelAfterMinutes);
        Assert.Equal(10, draft.PartialFillCancelAfterMinutes);
        Assert.Equal(25m, saved.FaultExposureThresholdUsdt);
        Assert.Equal(10m, draft.FaultExposureThresholdUsdt);
        foreach (var configuration in new[] { saved, draft })
        {
            Assert.Equal(GridMode.SellOnly, configuration.GridMode);
            Assert.Equal(75m, configuration.SingleModeMoveDistancePoints);
            Assert.Equal(90, configuration.SingleModeMoveIntervalSeconds);
            Assert.True(configuration.EntryFillLimitEnabled);
            Assert.Equal(15, configuration.EntryFillWindowMinutes);
            Assert.Equal(4, configuration.MaxEntryFillsPerSide);
        }
    }

    [Theory]
    [InlineData(GridMode.TwoWay)]
    [InlineData(GridMode.BuyOnly)]
    [InlineData(GridMode.SellOnly)]
    public async Task PreviewAndCurrentMidStartupUseMatchingInstrumentSettingsAndPlans(GridMode mode)
    {
        var adapter = new RecordingAdapter();
        var service = new GridConfigurationService(new([adapter]), new FixedClock());
        var request = Request with { GridMode = mode, CenterSuggestionMode = "CURRENT_MID" };
        var input = GridConfigurationMapper.FromStrategy(request);
        var preview = await service.PreparePreviewAsync(input, Selection, Ct);

        Assert.Equal(new[] { "quote", "instrument" }, adapter.Calls);
        Assert.Equal(100.01m, preview.Configuration.CenterPrice);
        Assert.Equal(100m, preview.Configuration.InitialBid);
        Assert.Equal(100.02m, preview.Configuration.InitialAsk);
        Assert.Equal(adapter.Instrument.Rules, GridInstrumentRules.FromConfiguration(preview.Configuration));
        Assert.Equal(.001m, preview.Configuration.MakerFeeRate);
        Assert.Equal(.002m, preview.Configuration.TakerFeeRate);
        Assert.Equal(2, preview.Configuration.SizeDecimals);
        Assert.Equal(preview.Configuration.CenterPrice, preview.Plan.CenterPrice);
        Assert.Equal(0m, input.TickSize); // Preparation does not mutate the request configuration.
        Assert.Equal(.7m, input.MakerFeeRate);

        adapter.Calls.Clear();
        adapter.Instrument = adapter.Instrument with
        {
            TickSize = .1m, QuantityStep = .5m, MinOrderQuantity = .5m,
            MinOrderNotional = 10m, MaxActiveOrders = 100, SizeDecimals = 1,
            MakerFeeRate = .01m, TakerFeeRate = .02m
        };
        // The supplied preflight quote, not another quote request, anchors startup.
        adapter.Quote = new(1000m, 1001m, 1000.5m, Now);
        var startup = await service.PrepareStartupAsync(preview, Selection, new(101m, 101.2m, 101.1m, Now), Ct);

        Assert.Equal(new[] { "instrument" }, adapter.Calls);
        Assert.Equal(101.1m, adapter.LastReferencePrice);
        Assert.Equal(adapter.Instrument.Rules, GridInstrumentRules.FromConfiguration(startup.Configuration));
        Assert.Equal(.01m, startup.Configuration.MakerFeeRate);
        Assert.Equal(.02m, startup.Configuration.TakerFeeRate);
        Assert.Equal(1, startup.Configuration.SizeDecimals);
        Assert.Equal(101.1m, startup.Plan.CenterPrice);
        Assert.Equal(startup.Plan.Levels.ToArray(), GridMath.BuildPlan(startup.Configuration, adapter.Instrument.Rules).Levels.ToArray());
        Assert.All(startup.Plan.Levels.Where(x => x.LevelIndex == 0), level =>
            Assert.Equal(level.Side == OrderSide.Buy ? 100m : 102.2m, level.EntryPrice));
        Assert.Equal(.01m, preview.Configuration.TickSize);
        Assert.Equal(100.01m, preview.Plan.CenterPrice);
    }

    [Fact]
    public async Task ManualStartupKeepsConfirmedPreviewWithoutFetchingMetadata()
    {
        var adapter = new RecordingAdapter();
        var service = new GridConfigurationService(new([adapter]), new FixedClock());
        var preview = await service.PreparePreviewAsync(GridConfigurationMapper.FromStrategy(Request), Selection, Ct);
        Assert.Equal(new[] { "instrument" }, adapter.Calls);
        Assert.Equal(123m, preview.Plan.CenterPrice);
        Assert.Null(preview.Configuration.InitialBid);
        Assert.Null(preview.Configuration.InitialAsk);
        adapter.Calls.Clear();
        adapter.Instrument = adapter.Instrument with { TickSize = 100m, MinOrderNotional = 999999m };

        var startup = await service.PrepareStartupAsync(preview, Selection, new(900m, 901m, 900.5m, Now), Ct);

        Assert.Same(preview, startup);
        Assert.Empty(adapter.Calls);
    }

    [Theory]
    [InlineData("MANUAL")]
    [InlineData("CURRENT_MID")]
    public async Task SavedStrategyValidationUsesInstrumentRulesWithoutChangingSavedSettings(string mode)
    {
        var adapter = new RecordingAdapter();
        var service = new GridConfigurationService(new([adapter]), new FixedClock());
        var request = Request with { CenterSuggestionMode = mode };
        var before = GridConfigurationCodec.WriteStrategy(request);
        GridConfigurationService.ValidateRequest(request);
        await service.ValidateSavedStrategyAsync(request, Selection, Ct);
        Assert.Equal(mode == "MANUAL" ? new[] { "instrument" } : new[] { "quote", "instrument" }, adapter.Calls);
        Assert.Equal(before, GridConfigurationCodec.WriteStrategy(request));
        adapter.Instrument = adapter.Instrument with { MinOrderNotional = 1000m };

        var error = await Assert.ThrowsAsync<GridValidationException>(() => service.ValidateSavedStrategyAsync(request, Selection, Ct));

        Assert.Equal("MIN_ORDER_NOTIONAL", error.Code);
        Assert.Equal(before, GridConfigurationCodec.WriteStrategy(request));
    }

    [Fact]
    public async Task InvalidPreviewModeAndStaleStartupKeepValidationBeforeMetadataFetch()
    {
        var adapter = new RecordingAdapter();
        var service = new GridConfigurationService(new([adapter]), new FixedClock());
        var invalid = GridConfigurationMapper.FromStrategy(Request with { CenterSuggestionMode = "UNKNOWN" });
        var error = await Assert.ThrowsAsync<TradingProblemException>(() => service.PreparePreviewAsync(invalid, Selection, Ct));
        Assert.Equal("CENTER_MODE_INVALID", error.Code);
        Assert.Empty(adapter.Calls);
        var preview = await service.PreparePreviewAsync(
            GridConfigurationMapper.FromStrategy(Request with { CenterSuggestionMode = "CURRENT_MID" }), Selection, Ct);
        adapter.Calls.Clear();

        error = await Assert.ThrowsAsync<TradingProblemException>(() => service.PrepareStartupAsync(
            preview, Selection, adapter.Quote with { AsOf = Now.AddMinutes(-1) }, Ct));

        Assert.Equal("MARKET_DATA_STALE", error.Code);
        Assert.Empty(adapter.Calls);
    }

    [Fact]
    public void CodecPreservesExistingJsonAndFrozenValuesWithoutNormalizingThem()
    {
        var request = Request with { GridMode = GridMode.SellOnly, SingleModeMoveDistancePoints = 75m };
        var configuration = GridConfigurationMapper.FromStrategy(request) with
        {
            TickSize = .00001m, QuantityStep = .01m, MinOrderQuantity = .01m,
            MinOrderNotional = 7m, MaxActiveOrders = 100, SizeDecimals = 5,
            InitialBid = 122.12345m, InitialAsk = 122.12346m
        };
        var savedJson = GridConfigurationCodec.WriteStrategy(request);
        var frozenJson = GridConfigurationCodec.WriteFrozen(configuration);

        Assert.Equal(JsonSerializer.Serialize(request, JsonSupport.Options), savedJson);
        Assert.Equal(JsonSerializer.Serialize(configuration, JsonSupport.Options), frozenJson);
        Assert.Equal(request, GridConfigurationCodec.ReadStrategy(savedJson));
        Assert.Equal(configuration, GridConfigurationCodec.ReadFrozen(frozenJson));
        var legacy = GridConfigurationCodec.ReadFrozen("""
            {"symbol":"SOL","gridMode":"SELL_ONLY","gridSpacingPoints":"75"}
            """);
        Assert.Equal(0m, legacy.TickSize);
        Assert.Null(legacy.SingleModeMoveDistancePoints);
        Assert.Equal(30, legacy.SingleModeMoveIntervalSeconds);
        Assert.Equal(10, legacy.PartialFillCancelAfterMinutes);
        Assert.Equal(10m, legacy.FaultExposureThresholdUsdt);
        Assert.Equal(.001m, GridInstrumentRules.FromConfiguration(legacy).TickSize);
        Assert.Equal(0m, GridConfigurationCodec.ReadFrozen(GridConfigurationCodec.WriteFrozen(legacy)).TickSize);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingAdapter : IExecutionAdapter
    {
        public List<string> Calls { get; } = [];
        public decimal? LastReferencePrice;
        public ExecutionQuote Quote = new(100m, 100.02m, 100.01m, Now);
        public ExecutionInstrument Instrument = new("SOL", ExecutionEnvironmentIds.PaperLocal,
            0, 2, 100m, .01m, .1m, .1m, 5m, 200, .001m, .002m, "test", Now);
        public ExecutionEnvironmentDescriptor Environment => new(ExecutionEnvironmentIds.PaperLocal, "PAPER", "LOCAL", "Test");
        public Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection selection, string symbol, decimal? referencePrice, CancellationToken ct)
        {
            Calls.Add("instrument");
            LastReferencePrice = referencePrice;
            return Task.FromResult(Instrument);
        }
        public Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection selection, string symbol, CancellationToken ct)
        {
            Calls.Add("quote");
            return Task.FromResult(Quote);
        }
        public Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection selection, string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task PlaceOrdersAsync(ExecutionSelection selection, GridConfiguration config, IEnumerable<OrderEntity> orders, CancellationToken ct) => throw new NotSupportedException();
        public Task CancelOrdersAsync(ExecutionSelection selection, IEnumerable<OrderEntity> orders, CancellationToken ct) => throw new NotSupportedException();
        public Task<decimal> FlattenAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config, CancellationToken ct) => throw new NotSupportedException();
    }
}
