using GridTrading.Domain;

namespace GridTrading.Domain.Tests;

public sealed class ExecutionLedgerTests
{
    [Fact]
    public void PositionLedgerAppliesExecutionExactlyOnce()
    {
        var ledger = new OneWayPositionLedger();
        Assert.True(ledger.Apply("fill-1", OrderSide.Buy, 2m));
        Assert.False(ledger.Apply("fill-1", OrderSide.Buy, 2m));
        Assert.True(ledger.Apply("fill-2", OrderSide.Sell, 3m));
        Assert.Equal(-1m, ledger.NetQuantity);
    }

    [Fact]
    public void PartialEntryProtectsOnlyActuallyFilledQuantity()
    {
        var lot = new PartialFillLot(OrderSide.Buy, 3);
        Assert.Equal(.4m, lot.ApplyEntryFill(.4m));
        Assert.Equal(.6m, lot.ApplyEntryFill(.6m));
        Assert.Equal(1m, lot.ProtectedQuantity);
    }

    [Fact]
    public void PartialTakeProfitDoesNotReleaseLevel()
    {
        var lot = new PartialFillLot(OrderSide.Buy, 3);
        lot.ApplyEntryFill(1m);
        lot.ApplyTakeProfitFill(.4m);
        Assert.False(lot.IsReusable);
        lot.ApplyTakeProfitFill(.6m);
        Assert.True(lot.IsReusable);
    }

    [Theory]
    [InlineData(CycleState.Paused, false, true, false)]
    [InlineData(CycleState.Running, true, true, false)]
    [InlineData(CycleState.Running, false, false, false)]
    [InlineData(CycleState.Running, false, true, true)]
    public void MandatorySafetyBlocksUnsafeEntry(CycleState state, bool stale, bool reconciled, bool timeout) =>
        Assert.False(EntrySafetyGate.CanCreateEntry(state, stale, reconciled, timeout));

    [Fact]
    public void RunningFreshAndReconciledAllowsEntry() =>
        Assert.True(EntrySafetyGate.CanCreateEntry(CycleState.Running, false, true, false));

    [Fact]
    public void ReplayNeverAssumesFavorableSameBarSequence()
    {
        var result = ConservativeReplayPolicy.EvaluateLongEntry(new ReplayBar(100m, 102m, 98m, 101m), 99m, 101m, false);
        Assert.True(result.EntryFilled);
        Assert.False(result.TakeProfitFilled);
    }

    [Fact]
    public void ReplayAllowsTpForLotOpenBeforeBar()
    {
        var result = ConservativeReplayPolicy.EvaluateLongEntry(new ReplayBar(100m, 102m, 98m, 101m), 99m, 101m, true);
        Assert.True(result.TakeProfitFilled);
    }
}
