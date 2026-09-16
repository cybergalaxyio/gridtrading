using GridTrading.Api.Contracts;
using GridTrading.Domain;

namespace GridTrading.Api.Strategies.Grid.Configuration;

public static class GridConfigurationMapper
{
    public static GridConfiguration FromStrategy(StrategyRequest request, decimal centerPrice = 0m) => new()
    {
        Symbol = request.Symbol, GridMode = request.GridMode,
        SingleModeMoveDistancePoints = request.SingleModeMoveDistancePoints,
        SingleModeMoveIntervalSeconds = request.SingleModeMoveIntervalSeconds,
        EntryFillLimitEnabled = request.EntryFillLimitEnabled, EntryFillWindowMinutes = request.EntryFillWindowMinutes,
        MaxEntryFillsPerSide = request.MaxEntryFillsPerSide,
        CenterSuggestionMode = request.CenterSuggestionMode, AutoRestart = request.AutoRestart,
        CenterPrice = request.CenterSuggestionMode == "MANUAL" ? request.ManualCenterPrice ?? 0m : centerPrice,
        MaxLevelsPerSide = request.MaxLevelsPerSide,
        WorkingEntriesPerSide = request.WorkingEntriesPerSide, InitialGapPoints = request.InitialGapPoints,
        GridSpacingPoints = request.GridSpacingPoints, GridSpacingStepPoints = request.GridSpacingStepPoints,
        TakeProfitPoints = request.TakeProfitPoints, BaseLotSize = request.BaseLotSize,
        LotSizeIncreasePercent = request.LotSizeIncreasePercent, MaxTradeLot = request.MaxTradeLot,
        MaxNetLot = request.MaxNetLot, BasketTakeProfitUsdt = request.BasketTakeProfitUsdt,
        BasketStopLossUsdt = request.BasketStopLossUsdt, MakerFeeRate = request.MakerFeeRate,
        FaultExposureThresholdUsdt = request.FaultExposureThresholdUsdt,
        TakerFeeRate = request.TakerFeeRate, EstimatedExitSlippagePct = request.EstimatedExitSlippagePct,
        IncludeFunding = request.IncludeFunding, PostOnlyEntries = request.PostOnlyEntries,
        PostOnlyTakeProfits = request.PostOnlyTakeProfits, ReconcileIntervalSeconds = request.ReconcileIntervalSeconds,
        MarketDataStaleSeconds = request.MarketDataStaleSeconds, OrderCommandTimeoutSeconds = request.OrderCommandTimeoutSeconds,
        MaxOrderFrequency = request.MaxOrderFrequency, PartialFillCancelAfterMinutes = request.PartialFillCancelAfterMinutes
    };

    // Candidates intentionally retain GridConfiguration defaults for omitted settings.
    public static GridConfiguration FromCandidate(CandidateConfiguration candidate, decimal centerPrice) => new()
    {
        Symbol = candidate.Symbol, GridMode = candidate.GridMode, CenterPrice = centerPrice,
        SingleModeMoveDistancePoints = candidate.SingleModeMoveDistancePoints,
        SingleModeMoveIntervalSeconds = candidate.SingleModeMoveIntervalSeconds,
        CenterSuggestionMode = candidate.CenterSuggestionMode,
        EntryFillLimitEnabled = candidate.EntryFillLimitEnabled,
        EntryFillWindowMinutes = candidate.EntryFillWindowMinutes,
        MaxEntryFillsPerSide = candidate.MaxEntryFillsPerSide,
        MaxLevelsPerSide = candidate.MaxLevelsPerSide, WorkingEntriesPerSide = candidate.WorkingEntriesPerSide,
        InitialGapPoints = candidate.InitialGapPoints, GridSpacingPoints = candidate.GridSpacingPoints,
        GridSpacingStepPoints = candidate.GridSpacingStepPoints, TakeProfitPoints = candidate.TakeProfitPoints,
        BaseLotSize = candidate.BaseLotSize, LotSizeIncreasePercent = candidate.LotSizeIncreasePercent,
        MaxTradeLot = candidate.MaxTradeLot, MaxNetLot = candidate.MaxNetLot
    };
}
