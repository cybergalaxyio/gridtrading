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

    [Theory]
    [InlineData(GridMode.BuyOnly, OrderSide.Buy)]
    [InlineData(GridMode.SellOnly, OrderSide.Sell)]
    public void OneSidedModeBuildsOnlyTheSelectedSide(GridMode mode, OrderSide expectedSide)
    {
        var plan = GridMath.BuildPlan(Configuration() with { GridMode = mode }, Rules);

        Assert.Equal(3, plan.Levels.Count);
        Assert.All(plan.Levels, level => Assert.Equal(expectedSide, level.Side));
        if (mode == GridMode.BuyOnly)
        {
            Assert.True(plan.CoverageBelowPct > 0m); Assert.Equal(0m, plan.CoverageAbovePct);
        }
        else
        {
            Assert.Equal(0m, plan.CoverageBelowPct); Assert.True(plan.CoverageAbovePct > 0m);
        }
    }

    [Fact]
    public void MissingGridModeDefaultsToTwoWay()
    {
        var plan = GridMath.BuildPlan(Configuration(), Rules);
        Assert.Equal(6, plan.Levels.Count);
        Assert.Contains(plan.Levels, level => level.Side == OrderSide.Buy);
        Assert.Contains(plan.Levels, level => level.Side == OrderSide.Sell);
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
    public void PointDistanceUsesTheLoadedTickSize()
    {
        var value = GridMath.TakeProfitPrice(OrderSide.Buy, 100m, 180m, 0.01m);
        Assert.Equal(101.8m, value);
    }

    [Fact]
    public void PlanRejectsEntryWhoseTakeProfitFallsBelowVenueNotionalMinimum()
    {
        var config = Configuration() with
        {
            GridMode = GridMode.SellOnly,
            CenterPrice = 100m,
            MaxLevelsPerSide = 1,
            InitialGapPoints = 1m,
            TakeProfitPoints = 2000m,
            BaseLotSize = .1m
        };
        var rules = Rules with { MinOrderNotional = 10m };

        var error = Assert.Throws<GridValidationException>(() => GridMath.BuildPlan(config, rules));

        Assert.Equal("MIN_TAKE_PROFIT_NOTIONAL", error.Code);
    }

    [Fact]
    public void WorkingEntriesStraddleCurrentPriceAndSkipOccupiedLevels()
    {
        var plan = GridMath.BuildPlan(Configuration(), Rules);

        var buy = GridMath.SelectWorkingEntryLevel(plan, OrderSide.Buy, 99.99m, []);
        var nextBuy = GridMath.SelectWorkingEntryLevel(plan, OrderSide.Buy, 99.99m, [buy!.LevelIndex]);
        var sell = GridMath.SelectWorkingEntryLevel(plan, OrderSide.Sell, 99.99m, []);

        Assert.Equal(1, buy.LevelIndex);
        Assert.Equal(2, nextBuy!.LevelIndex);
        Assert.Equal(0, sell!.LevelIndex);
    }

    [Fact]
    public void WorkingEntryStopsAtGridBoundary()
    {
        var plan = GridMath.BuildPlan(Configuration(), Rules);

        Assert.Null(GridMath.SelectWorkingEntryLevel(plan, OrderSide.Buy, plan.OutermostBuyPrice, []));
        Assert.Null(GridMath.SelectWorkingEntryLevel(plan, OrderSide.Sell, plan.OutermostSellPrice, []));
    }

    [Fact]
    public void GeometricLotSizeIsNormalizedDownToQuantityStep()
    {
        var config = Configuration() with { BaseLotSize = .5m, LotSizeIncreasePercent = 16.2m };
        var rules = Rules with { QuantityStep = .01m };

        var quantity = GridMath.PlannedQuantity(config, rules, 12);

        Assert.Equal(0m, quantity % rules.QuantityStep);
        Assert.True(quantity <= .5m * (decimal)Math.Pow(1.162d, 12));
    }

    [Fact]
    public void InvalidDeepGridReportsNonPositivePriceInsteadOfMinimumNotional()
    {
        var config = Configuration() with
        {
            CenterPrice = 103.355m, MaxLevelsPerSide = 14, GridSpacingPoints = 250m,
            GridSpacingStepPoints = 100m, BaseLotSize = .5m, LotSizeIncreasePercent = 16.2m
        };
        var rules = Rules with { TickSize = .01m, QuantityStep = .01m };

        var error = Assert.Throws<GridValidationException>(() => GridMath.BuildPlan(config, rules));

        Assert.Equal("GRID_PRICE_NON_POSITIVE", error.Code);
        Assert.Contains("Reduce the level count", error.Message);
    }

    [Theory]
    [InlineData("-100", "0", false)]
    [InlineData("-100", "100", true)]
    [InlineData("-99.99", "100", false)]
    public void ZeroBasketStopLossIsDisabled(string pnl, string limit, bool expected) =>
        Assert.Equal(expected, GridMath.BasketStopLossTriggered(decimal.Parse(pnl), decimal.Parse(limit)));

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(11, true)]
    public void PartialFillCancellationStartsAtFirstFill(int elapsedMinutes, bool expected)
    {
        var firstFill = new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, GridMath.PartialFillCancellationDue(firstFill, firstFill.AddMinutes(elapsedMinutes), 10));
        Assert.False(GridMath.PartialFillCancellationDue(firstFill, firstFill.AddDays(1), 0));
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
