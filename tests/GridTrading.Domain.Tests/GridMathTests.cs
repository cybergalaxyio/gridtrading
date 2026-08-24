using GridTrading.Domain;

namespace GridTrading.Domain.Tests;

public sealed class GridMathTests
{
    private static readonly InstrumentRules Rules = new("SOLUSDT", 0.001m, 0.1m, 0.1m, 5m);

    private static GridConfiguration Configuration() => new()
    {
        Symbol = "SOLUSDT", CenterPrice = 100m, MaxLevelsPerSide = 3, WorkingEntriesPerSide = 1,
        InitialGapPoints = 0m, GridSpacingPoints = 10m, GridSpacingStepPoints = 2m,
        TakeProfitPoints = 5m, BaseLotSize = 1m, LotSizeIncreasePercent = 10m,
        MaxNetLot = 10m
    };

    [Fact]
    public void Plan_UsesHalfSpacingForZeroInitialGapAndProgressiveIntervals()
    {
        var plan = GridMath.BuildPlan(Configuration(), Rules);
        Assert.Equal(99.995m, plan.Levels.Single(x => x.Side == OrderSide.Buy && x.LevelIndex == 0).EntryPrice);
        Assert.Equal(99.983m, plan.Levels.Single(x => x.Side == OrderSide.Buy && x.LevelIndex == 1).EntryPrice);
        Assert.Equal(100.031m, plan.OutermostSellPrice);
    }

    [Fact]
    public void Plan_CalculatesGeometricLevelSize()
    {
        var plan = GridMath.BuildPlan(Configuration(), Rules);
        Assert.Equal(1.2m, plan.Levels.Single(x => x.Side == OrderSide.Buy && x.LevelIndex == 2).PlannedQuantity);
    }

    [Fact]
    public void ZeroGrowthProducesConstantSize()
    {
        var config = Configuration() with { LotSizeIncreasePercent = 0m };
        Assert.All(GridMath.BuildPlan(config, Rules).Levels, x => Assert.Equal(1m, x.PlannedQuantity));
    }

    [Fact]
    public void MaxTradeLotCapsEveryLevel()
    {
        var config = Configuration() with { MaxLevelsPerSide = 8, MaxTradeLot = 1.2m };
        Assert.All(GridMath.BuildPlan(config, Rules).Levels, x => Assert.True(x.PlannedQuantity <= 1.2m));
    }

    [Theory]
    [InlineData(OrderSide.Buy, "100.004")]
    [InlineData(OrderSide.Sell, "99.996")]
    public void TakeProfitRoundsConservatively(OrderSide side, string expected)
    {
        var value = GridMath.TakeProfitPrice(side, 100m, 3.5m, 0.001m);
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [Fact]
    public void ExposureReservationReducesFinalBuyOrder()
    {
        var active = new[] { new ActiveOrderReservation(OrderSide.Buy, 1.7m), new ActiveOrderReservation(OrderSide.Sell, 5m) };
        var allowed = GridMath.AllowedOrderQuantity(OrderSide.Buy, 2m, 2m, active, 5m, Rules);
        Assert.Equal(1.3m, allowed);
    }

    [Fact]
    public void ExposureReservationReducesFinalSellOrder()
    {
        var active = new[] { new ActiveOrderReservation(OrderSide.Sell, 1.2m) };
        var allowed = GridMath.AllowedOrderQuantity(OrderSide.Sell, 3m, -2.5m, active, 5m, Rules);
        Assert.Equal(1.3m, allowed);
    }

    [Fact]
    public void QuantityBelowExchangeMinimumReturnsZero()
    {
        var rules = Rules with { MinOrderQuantity = 0.5m };
        Assert.Equal(0m, GridMath.AllowedOrderQuantity(OrderSide.Buy, 1m, 4.8m, [], 5m, rules));
    }

    [Fact]
    public void BasketPnlSubtractsEveryLiquidationCostOnce()
    {
        var pnl = GridMath.CalculateBasketPnl(new BasketPnlInput(20m, 5m, 1m, .5m, .25m, .75m));
        Assert.Equal(22.5m, pnl.LiquidationPnl);
    }

    [Fact]
    public void StateMachineRejectsIllegalResume()
    {
        Assert.False(CycleStateMachine.CanTransition(CycleState.WaitingForOperator, CycleState.Running));
        Assert.True(CycleStateMachine.CanTransition(CycleState.Paused, CycleState.Running));
    }
}
