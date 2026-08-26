using GridTrading.Domain;

namespace GridTrading.Api.Contracts;

public sealed record StrategyRequest(
    string Name, string ExchangeAccountId, string Symbol, GridMode GridMode, string CenterSuggestionMode,
    bool AutoRestart, int MaxLevelsPerSide, int WorkingEntriesPerSide, decimal InitialGapPoints,
    decimal GridSpacingPoints, decimal GridSpacingStepPoints, decimal TakeProfitPoints, decimal BaseLotSize,
    decimal LotSizeIncreasePercent, decimal MaxTradeLot, decimal MaxNetLot, decimal BasketTakeProfitUsdt,
    decimal BasketStopLossUsdt, decimal MakerFeeRate, decimal TakerFeeRate, decimal EstimatedExitSlippagePct,
    bool IncludeFunding, bool PostOnlyEntries, bool PostOnlyTakeProfits, int ReconcileIntervalSeconds,
    int MarketDataStaleSeconds, int OrderCommandTimeoutSeconds, int MaxOrderFrequency,
    int PartialFillCancelAfterMinutes = 10)
{
    public GridConfiguration ToConfiguration(decimal centerPrice = 0m) => new()
    {
        Symbol = Symbol, GridMode = GridMode, CenterPrice = centerPrice, MaxLevelsPerSide = MaxLevelsPerSide,
        WorkingEntriesPerSide = WorkingEntriesPerSide, InitialGapPoints = InitialGapPoints,
        GridSpacingPoints = GridSpacingPoints, GridSpacingStepPoints = GridSpacingStepPoints,
        TakeProfitPoints = TakeProfitPoints, BaseLotSize = BaseLotSize,
        LotSizeIncreasePercent = LotSizeIncreasePercent, MaxTradeLot = MaxTradeLot,
        MaxNetLot = MaxNetLot, BasketTakeProfitUsdt = BasketTakeProfitUsdt,
        BasketStopLossUsdt = BasketStopLossUsdt, MakerFeeRate = MakerFeeRate,
        TakerFeeRate = TakerFeeRate, EstimatedExitSlippagePct = EstimatedExitSlippagePct,
        IncludeFunding = IncludeFunding, PostOnlyEntries = PostOnlyEntries,
        PostOnlyTakeProfits = PostOnlyTakeProfits, ReconcileIntervalSeconds = ReconcileIntervalSeconds,
        MarketDataStaleSeconds = MarketDataStaleSeconds, OrderCommandTimeoutSeconds = OrderCommandTimeoutSeconds,
        MaxOrderFrequency = MaxOrderFrequency, PartialFillCancelAfterMinutes = PartialFillCancelAfterMinutes
    };

    public static StrategyRequest Default => new(
        "Weekend SOL Grid", "acct_paper_01", "SOLUSDT", GridMode.TwoWay, "CURRENT_MID", false,
        14, 1, 0m, 250m, 10m, 180m, 0.5m, 5m, 2m, 10m, 50m, 100m,
        .0002m, .00055m, .10m, true, true, true, 10, 5, 10, 5, 10);
}

public sealed record CandidateConfiguration(
    string ExchangeAccountId, string Symbol, GridMode GridMode, int MaxLevelsPerSide, int WorkingEntriesPerSide,
    decimal InitialGapPoints, decimal GridSpacingPoints, decimal GridSpacingStepPoints,
    decimal TakeProfitPoints, decimal BaseLotSize, decimal LotSizeIncreasePercent,
    decimal MaxTradeLot, decimal MaxNetLot);

public sealed record PreviewRequest(string? StrategyId, int? StrategyVersion, decimal ConfirmedCenterPrice,
    CandidateConfiguration? CandidateConfiguration, object? ParameterOverrides);
public sealed record StartCycleRequest(string PreviewId, decimal ConfirmedCenterPrice, OperatorConfirmation OperatorConfirmation);
public sealed record OperatorConfirmation(bool ParametersReviewed, bool CenterConfirmed, string EnvironmentConfirmed);
public sealed record CommandRequest(string Reason);
public sealed record EmergencyCommandRequest(string Reason, EmergencyConfirmation Confirmation);
public sealed record EmergencyConfirmation(bool CancelAllStrategyOrders, bool FlattenActualNetPosition, bool AcknowledgedTakerExecution);
public sealed record AcknowledgementRequest(string Note);
public sealed record PreviewCacheItem(string Id, string? StrategyId, int StrategyVersion, string ExchangeAccountId,
    DateTimeOffset ExpiresAt, GridConfiguration Configuration, GridPlan Plan);
