using GridTrading.Domain;

namespace GridTrading.Domain.Tests;

public sealed class SingleModeEntryRulesTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-13T00:00:00Z");
    private static GridConfiguration Config(GridMode mode = GridMode.SellOnly) => new()
    {
        Symbol = "SOL", GridMode = mode, CenterPrice = 100m, TickSize = .01m,
        InitialGapPoints = 20m, GridSpacingPoints = 20m, GridSpacingStepPoints = 5m,
        SingleModeMoveDistancePoints = 40m, SingleModeMoveIntervalSeconds = 45,
        MaxLevelsPerSide = 3, BaseLotSize = 1m, MaxNetLot = 10m, TakeProfitPoints = 10m
    };

    [Theory]
    [InlineData(44, "99.59", "99.60", false)]
    [InlineData(45, "99.60", "99.61", false)]
    [InlineData(45, "99.59", "99.60", true)]
    [InlineData(60, "99.00", "99.01", true)]
    [InlineData(60, "100.49", "100.50", false)]
    public void BothThresholdsMustBeMet(int elapsed, string bid, string ask, bool moves)
    {
        var target = SingleModeEntryRules.TargetPrice(Config(), 100.10m, Start,
            decimal.Parse(bid), decimal.Parse(ask), Start.AddSeconds(elapsed));
        Assert.Equal(moves, target.HasValue);
        if (moves) Assert.Equal(decimal.Parse(ask) + .10m, target);
    }

    [Fact]
    public void BuyMirrorsSellAndNeverChasesARebound()
    {
        var config = Config(GridMode.BuyOnly);
        Assert.Equal(100.30m, SingleModeEntryRules.TargetPrice(config, 99.90m, Start, 100.40m, 100.41m, Start.AddSeconds(45)));
        Assert.Null(SingleModeEntryRules.TargetPrice(config, 99.90m, Start, 99m, 99.01m, Start.AddMinutes(1)));
    }

    [Fact]
    public void LegacyDefaultsUseOwnSpacingAndThirtySecondsWithOutwardRounding()
    {
        var config = Config() with { SingleModeMoveDistancePoints = null, SingleModeMoveIntervalSeconds = 30, InitialGapPoints = 0m };
        Assert.Equal(99.90m, SingleModeEntryRules.TargetPrice(config, 100.10m, Start, 99.781m, 99.791m, Start.AddSeconds(30)));
        Assert.Null(SingleModeEntryRules.TargetPrice(config, 100.10m, Start, 99m, 99.01m, Start.AddSeconds(29)));
        Assert.Equal(100.10m, SingleModeEntryRules.TargetPrice(config with { GridMode = GridMode.BuyOnly },
            99.90m, Start, 100.209m, 100.219m, Start.AddSeconds(30)));
    }

    [Theory]
    [InlineData(GridMode.SellOnly, "100.221", "100.231", "100.34")]
    [InlineData(GridMode.BuyOnly, "100.221", "100.231", "100.12")]
    public void NewRoundUsesCurrentBookWithOutwardRounding(GridMode mode, string bid, string ask, string expected)
    {
        var config = Config(mode) with { SingleModeMoveDistancePoints = 99999m, SingleModeMoveIntervalSeconds = 99999 };
        Assert.Equal(decimal.Parse(expected), SingleModeEntryRules.InitialEntryPrice(config, decimal.Parse(bid), decimal.Parse(ask)));
        Assert.Equal(decimal.Parse(expected), SingleModeEntryRules.InitialEntryPrice(config with { InitialGapPoints = 0m }, decimal.Parse(bid), decimal.Parse(ask)));
        Assert.Null(SingleModeEntryRules.InitialEntryPrice(config, 0m, 100m));
        Assert.Null(SingleModeEntryRules.InitialEntryPrice(config, 100m, 99m));
        Assert.Null(SingleModeEntryRules.InitialEntryPrice(config with { GridMode = GridMode.TwoWay }, 100m, 101m));
    }

    [Fact]
    public void ShiftPreservesSpacingAndQuantityAndRecalculatesNotionals()
    {
        var plan = GridMath.BuildPlan(Config(), new("SOL", .01m, .01m, .01m, 1m));
        var moved = SingleModeEntryRules.ShiftPlan(plan, -2m);
        Assert.Equal(98m, moved.CenterPrice);
        for (var i = 0; i < plan.Levels.Count; i++)
        {
            Assert.Equal(plan.Levels[i].EntryPrice - 2m, moved.Levels[i].EntryPrice);
            Assert.Equal(plan.Levels[i].PlannedQuantity, moved.Levels[i].PlannedQuantity);
            Assert.Equal(moved.Levels[i].PlannedQuantity * moved.Levels[i].EntryPrice, moved.Levels[i].OrderNotional);
            Assert.Equal(moved.Levels.Take(i + 1).Sum(x => x.OrderNotional), moved.Levels[i].CumulativeNotional);
        }
        Assert.Equal(0m, moved.CoverageBelowPct);
        Assert.Equal((moved.OutermostSellPrice - moved.CenterPrice) / moved.CenterPrice * 100m, moved.CoverageAbovePct);
        Assert.Equal(100m, plan.CenterPrice);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(-1, 30)]
    [InlineData(20, 0)]
    [InlineData(20, -1)]
    public void InvalidThresholdsAreRejectedOnlyForSingleMode(int distance, int seconds)
    {
        var config = Config() with { SingleModeMoveDistancePoints = distance, SingleModeMoveIntervalSeconds = seconds };
        Assert.Equal("SINGLE_MODE_MOVE_INVALID", Assert.Throws<GridValidationException>(() => SingleModeEntryRules.Validate(config)).Code);
        SingleModeEntryRules.Validate(config with { GridMode = GridMode.TwoWay });
        Assert.Null(SingleModeEntryRules.TargetPrice(config with { GridMode = GridMode.TwoWay }, 100m, Start, 90m, 91m, Start.AddHours(1)));
    }
}
