using GridTrading.Api.Contracts;
using GridTrading.Api.Execution;
using GridTrading.Domain;

namespace GridTrading.Api.Strategies.Grid.Configuration;

public sealed record PreparedGridConfiguration(GridConfiguration Configuration, GridPlan Plan);

public sealed class GridConfigurationService(ExecutionEnvironmentRegistry environments, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public static void ValidateRequest(StrategyRequest request)
    {
        if (!request.StrategyType.Equals("GRID", StringComparison.OrdinalIgnoreCase))
            throw Problem(422, "STRATEGY_TYPE_UNSUPPORTED", $"Strategy type '{request.StrategyType}' is not supported.");
        ValidateCenterMode(request.CenterSuggestionMode);
        if (request.CenterSuggestionMode == "MANUAL" && !(request.ManualCenterPrice > 0m))
            throw Problem(422, "CENTER_PRICE", "Manual mode requires a positive center price.");
        if (!request.IncludeFunding)
            throw Problem(422, "V1_FIXED_CONSTRAINT", "V1 requires includeFunding=true.");
        if (request.FaultExposureThresholdUsdt < 0m)
            throw Problem(422, "FAULT_THRESHOLD_INVALID", "Entry risk pause exposure threshold must be zero or greater.");
    }

    // Request validation runs earlier in the workflow, before account/entity checks.
    // Saving validates the plan but does not persist current exchange metadata.
    public async Task ValidateSavedStrategyAsync(StrategyRequest request, ExecutionSelection selection, CancellationToken ct)
    {
        var adapter = environments.Adapter(selection.EnvironmentId);
        var configuration = GridConfigurationMapper.FromStrategy(request);
        if (configuration.CenterSuggestionMode == "CURRENT_MID")
            configuration = WithStartupQuote(configuration, await adapter.GetQuoteAsync(selection, configuration.Symbol, ct));
        var instrument = await adapter.GetInstrumentAsync(selection, configuration.Symbol, configuration.CenterPrice, ct);
        _ = GridMath.BuildPlan(configuration, instrument.Rules);
    }

    public async Task<PreparedGridConfiguration> PreparePreviewAsync(GridConfiguration configuration,
        ExecutionSelection selection, CancellationToken ct)
    {
        var adapter = environments.Adapter(selection.EnvironmentId);
        ValidateCenterMode(configuration.CenterSuggestionMode);
        if (configuration.CenterSuggestionMode == "CURRENT_MID")
            configuration = WithStartupQuote(configuration, await adapter.GetQuoteAsync(selection, configuration.Symbol, ct));
        return await PrepareWithInstrumentAsync(configuration, selection, ct);
    }

    // Preflight remains in the workflow after operator confirmation. Manual mode
    // keeps the confirmed preview; CURRENT_MID rebuilds from that preflight quote.
    public Task<PreparedGridConfiguration> PrepareStartupAsync(PreparedGridConfiguration preview,
        ExecutionSelection selection, ExecutionQuote startupQuote, CancellationToken ct) =>
        preview.Configuration.CenterSuggestionMode == "CURRENT_MID"
            ? PrepareWithInstrumentAsync(WithStartupQuote(preview.Configuration, startupQuote), selection, ct)
            : Task.FromResult(preview);

    private async Task<PreparedGridConfiguration> PrepareWithInstrumentAsync(GridConfiguration configuration,
        ExecutionSelection selection, CancellationToken ct)
    {
        var instrument = await environments.Adapter(selection.EnvironmentId)
            .GetInstrumentAsync(selection, configuration.Symbol, configuration.CenterPrice, ct);
        configuration = configuration with
        {
            TickSize = instrument.TickSize, QuantityStep = instrument.QuantityStep,
            MinOrderQuantity = instrument.MinOrderQuantity, MinOrderNotional = instrument.MinOrderNotional,
            MaxActiveOrders = instrument.MaxActiveOrders, SizeDecimals = instrument.SizeDecimals,
            MakerFeeRate = instrument.MakerFeeRate, TakerFeeRate = instrument.TakerFeeRate
        };
        return new(configuration, GridMath.BuildPlan(configuration, instrument.Rules));
    }

    private static void ValidateCenterMode(string mode)
    {
        if (mode is not ("CURRENT_MID" or "MANUAL"))
            throw Problem(422, "CENTER_MODE_INVALID", "Choose Current Mid or Manual.");
    }

    private GridConfiguration WithStartupQuote(GridConfiguration configuration, ExecutionQuote quote)
    {
        if (quote.Bid <= 0m || quote.Ask < quote.Bid ||
            clock.GetUtcNow() - quote.AsOf > TimeSpan.FromSeconds(configuration.MarketDataStaleSeconds))
            throw Problem(422, "MARKET_DATA_STALE", "A fresh, valid bid/ask quote is required to build the grid.");
        return configuration with
        {
            CenterPrice = (quote.Bid + quote.Ask) / 2m, InitialBid = quote.Bid, InitialAsk = quote.Ask
        };
    }

    private static TradingProblemException Problem(int status, string code, string message) => new(status, code, message);
}
