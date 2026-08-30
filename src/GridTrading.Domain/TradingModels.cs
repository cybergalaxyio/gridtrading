namespace GridTrading.Domain;

public enum OrderSide { Buy, Sell }
public enum GridMode { TwoWay, BuyOnly, SellOnly }
public enum CycleState { WaitingForOperator, Starting, Running, Paused, Closing, Fault }
public enum OrderKind { Entry, TakeProfit, Flatten }
public enum OrderStatus { New, PartiallyFilled, Filled, Cancelled, Rejected, Unknown }
public enum RiskColor { Green, Yellow, Orange, Red }

public sealed record InstrumentRules(
    string Symbol,
    decimal TickSize,
    decimal QuantityStep,
    decimal MinOrderQuantity,
    decimal MinOrderNotional,
    int MaxActiveOrders = 500);

public sealed record GridConfiguration
{
    public required string Symbol { get; init; }
    public GridMode GridMode { get; init; } = GridMode.TwoWay;
    public decimal TickSize { get; init; }
    public decimal QuantityStep { get; init; }
    public decimal MinOrderQuantity { get; init; }
    public decimal MinOrderNotional { get; init; }
    public int MaxActiveOrders { get; init; } = 500;
    public int? SizeDecimals { get; init; }
    public decimal CenterPrice { get; init; }
    public int MaxLevelsPerSide { get; init; } = 10;
    public int WorkingEntriesPerSide { get; init; } = 1;
    public decimal InitialGapPoints { get; init; }
    public decimal GridSpacingPoints { get; init; } = 25m;
    public decimal GridSpacingStepPoints { get; init; }
    public decimal TakeProfitPoints { get; init; } = 20m;
    public decimal BaseLotSize { get; init; } = 0.01m;
    public decimal LotSizeIncreasePercent { get; init; }
    public decimal MaxTradeLot { get; init; }
    public decimal MaxNetLot { get; init; } = 1m;
    public decimal BasketTakeProfitUsdt { get; init; } = 50m;
    public decimal BasketStopLossUsdt { get; init; } = 100m;
    public decimal MakerFeeRate { get; init; } = 0.0002m;
    public decimal TakerFeeRate { get; init; } = 0.00055m;
    public decimal FaultExposureThresholdUsdt { get; init; } = 10m;
    public decimal EstimatedExitSlippagePct { get; init; } = 0.10m;
    public bool IncludeFunding { get; init; } = true;
    public bool PostOnlyEntries { get; init; } = true;
    public bool PostOnlyTakeProfits { get; init; } = true;
    public int ReconcileIntervalSeconds { get; init; } = 10;
    public int MarketDataStaleSeconds { get; init; } = 5;
    public int OrderCommandTimeoutSeconds { get; init; } = 10;
    public int MaxOrderFrequency { get; init; } = 5;
    public int PartialFillCancelAfterMinutes { get; init; } = 10;
}

public sealed record GridLevel(
    OrderSide Side,
    int LevelIndex,
    decimal EntryPrice,
    decimal TakeProfitDistance,
    decimal PlannedQuantity,
    decimal OrderNotional,
    decimal CumulativeQuantity,
    decimal CumulativeNotional);

public sealed record GridPlan(
    decimal CenterPrice,
    decimal OutermostBuyPrice,
    decimal OutermostSellPrice,
    decimal CoverageBelowPct,
    decimal CoverageAbovePct,
    IReadOnlyList<GridLevel> Levels);

public sealed record ActiveOrderReservation(OrderSide Side, decimal RemainingQuantity);

public sealed record BasketPnlInput(
    decimal RealisedCyclePnl,
    decimal UnrealisedAtExecutablePrice,
    decimal PaidFees,
    decimal AccruedFunding,
    decimal EstimatedFinalTakerFee,
    decimal EstimatedExitSlippage);

public sealed record BasketPnl(
    decimal RealisedCyclePnl,
    decimal UnrealisedAtExecutablePrice,
    decimal PaidFees,
    decimal AccruedFunding,
    decimal EstimatedFinalTakerFee,
    decimal EstimatedExitSlippage,
    decimal LiquidationPnl);

public sealed record VirtualLot(
    string LotId,
    string CycleId,
    OrderSide Side,
    int GridLevel,
    decimal EntryFillPrice,
    decimal FilledQuantity,
    decimal RemainingQuantity,
    decimal TakeProfitPrice,
    decimal EntryFee,
    decimal ExitFee,
    decimal FundingAllocation,
    string Status);
