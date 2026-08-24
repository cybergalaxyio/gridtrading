namespace GridTrading.Domain;

public sealed class OneWayPositionLedger
{
    private readonly HashSet<string> _executionIds = new(StringComparer.Ordinal);
    public decimal NetQuantity { get; private set; }

    public bool Apply(string exchangeExecutionId, OrderSide side, decimal quantity)
    {
        if (quantity <= 0m) throw new ArgumentOutOfRangeException(nameof(quantity));
        if (!_executionIds.Add(exchangeExecutionId)) return false;
        NetQuantity += side == OrderSide.Buy ? quantity : -quantity;
        return true;
    }
}

public sealed class PartialFillLot
{
    public PartialFillLot(OrderSide side, int levelIndex)
    {
        Side = side;
        LevelIndex = levelIndex;
    }

    public OrderSide Side { get; }
    public int LevelIndex { get; }
    public decimal FilledQuantity { get; private set; }
    public decimal ProtectedQuantity { get; private set; }
    public decimal RemainingQuantity { get; private set; }
    public bool IsReusable => FilledQuantity > 0m && RemainingQuantity == 0m;

    public decimal ApplyEntryFill(decimal quantity)
    {
        if (quantity <= 0m) throw new ArgumentOutOfRangeException(nameof(quantity));
        FilledQuantity += quantity;
        RemainingQuantity += quantity;
        var protectionDelta = FilledQuantity - ProtectedQuantity;
        ProtectedQuantity = FilledQuantity;
        return protectionDelta;
    }

    public void ApplyTakeProfitFill(decimal quantity)
    {
        if (quantity <= 0m || quantity > RemainingQuantity) throw new ArgumentOutOfRangeException(nameof(quantity));
        RemainingQuantity -= quantity;
    }
}

public static class EntrySafetyGate
{
    public static bool CanCreateEntry(CycleState state, bool marketDataIsStale, bool reconciliationComplete, bool commandTimedOut) =>
        state == CycleState.Running && !marketDataIsStale && reconciliationComplete && !commandTimedOut;
}

public sealed record ReplayBar(decimal Open, decimal High, decimal Low, decimal Close);
public sealed record ReplayTouchResult(bool EntryFilled, bool TakeProfitFilled);

public static class ConservativeReplayPolicy
{
    public static ReplayTouchResult EvaluateLongEntry(ReplayBar bar, decimal entryPrice, decimal takeProfitPrice, bool lotWasOpenBeforeBar)
    {
        var entryTouched = bar.Low <= entryPrice;
        var tpTouched = bar.High >= takeProfitPrice;
        // OHLC does not reveal which extreme occurred first. A new entry cannot receive a favorable same-bar TP.
        return new ReplayTouchResult(entryTouched, tpTouched && (lotWasOpenBeforeBar || !entryTouched));
    }
}
