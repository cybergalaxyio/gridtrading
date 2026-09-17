using System.Text.Json;
using GridTrading.Domain.Advisory;

namespace GridTrading.Domain.Tests;

public sealed class GridAdvisoryTests
{
    [Fact]
    public void AtrAndBandsMatchIndependentSharedReference()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "technical-indicators.json")));
        var bars = doc.RootElement.GetProperty("candles").EnumerateArray().Select(x => new MarketBar(x.GetProperty("time").GetInt64(),
            decimal.Parse(x.GetProperty("open").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(x.GetProperty("high").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(x.GetProperty("low").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(x.GetProperty("close").GetString()!, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        var result = TechnicalIndicators.Calculate(bars);
        var expected = doc.RootElement.GetProperty("expected").EnumerateArray().ToArray();
        for (var i = 0; i < result.Count; i++)
            foreach (var (key, actual) in new[] { ("atr", result[i].Atr), ("middle", result[i].Middle), ("upper", result[i].Upper), ("lower", result[i].Lower) })
                if (expected[i].GetProperty(key).ValueKind == JsonValueKind.Null) Assert.Null(actual);
                else Assert.Equal(expected[i].GetProperty(key).GetDouble(), actual!.Value, 9);
    }

    [Fact]
    public void WilderRsiAndAdxHaveKnownMonotonicAndFlatResults()
    {
        var rising = Enumerable.Range(0, 240).Select(i => new MarketBar(i * 900, 100 + i, 102 + i, 99 + i, 101 + i)).ToArray();
        var values = TechnicalIndicators.Calculate(rising);
        Assert.Null(values[13].Rsi); Assert.Null(values[26].Adx);
        Assert.Equal(100, values[^1].Rsi); Assert.Equal(100, values[^1].Adx);
        Assert.Equal(100d / 3, values[^1].PlusDi!.Value, 9); Assert.Equal(0, values[^1].MinusDi);
        var flat = TechnicalIndicators.Calculate(rising.Select(x => x with { Open = 100, High = 100, Low = 100, Close = 100 }).ToArray());
        Assert.Equal(0, flat[^1].Atr); Assert.Null(flat[^1].Adx); Assert.Null(flat[^1].Rsi);
    }

    [Theory]
    [InlineData(900)] [InlineData(3600)] [InlineData(14400)] [InlineData(86400)]
    public void HistoryExcludesOpenCandlesDeduplicatesAndChecksFreshness(int seconds)
    {
        var boundary = 20000L * seconds;
        var bars = Enumerable.Range(0, 241).Select(i => new MarketBar(boundary - (240L - i) * seconds, 100, 101, 99, 100)).ToArray();
        var now = DateTimeOffset.FromUnixTimeSeconds(boundary + 60);
        Assert.Equal(240, TechnicalIndicators.ClosedHistory(bars.Concat([bars[10]]), seconds, now).Count);
        Assert.Equal(239, TechnicalIndicators.ClosedHistory(bars.Take(239), seconds, now).Count);
        Assert.Throws<ArgumentException>(() => TechnicalIndicators.ClosedHistory(bars.Take(239), seconds, now.AddSeconds(61)));
        Assert.Throws<ArgumentException>(() => TechnicalIndicators.ClosedHistory(bars.Take(199), seconds, now));
        Assert.Throws<ArgumentException>(() => TechnicalIndicators.ClosedHistory(bars.Where((_, i) => i != 220), seconds, now));
        Assert.Throws<ArgumentException>(() => TechnicalIndicators.ClosedHistory(bars.Select((b, i) => i == 220 ? b with { Close = 200 } : b), seconds, now));
    }

    [Theory]
    [InlineData(GridMode.TwoWay, 10, 20, 25, "UNFAVORABLE")]
    [InlineData(GridMode.BuyOnly, 10, 20, 25, "UNFAVORABLE")]
    [InlineData(GridMode.SellOnly, 20, 10, 25, "UNFAVORABLE")]
    [InlineData(GridMode.BuyOnly, 20, 10, 25, "FAVORABLE")]
    [InlineData(GridMode.TwoWay, 20, 10, 20, "CAUTION")]
    [InlineData(GridMode.TwoWay, 20, 10, 19.99, "FAVORABLE")]
    [InlineData(GridMode.BuyOnly, 10, 10, 30, "CAUTION")]
    public void DailyTrendCanOverrideShortTermReadings(GridMode mode, double plus, double minus, double adx, string expected)
    {
        var input = Input(mode);
        var daily = input.Frames[^1];
        var frames = input.Frames.Take(3).Append(daily with { Indicators = daily.Indicators! with { PlusDi = plus, MinusDi = minus, Adx = adx } }).ToArray();
        Assert.Equal(expected, Check(GridAdvisory.Evaluate(input with { Frames = frames }), "trend").Status);
    }

    [Fact]
    public void ConflictingTimeframesWarnWithoutAnAdditiveScore()
    {
        var input = Input(GridMode.BuyOnly);
        var frames = input.Frames.Select((f, i) => f with { Indicators = f.Indicators! with { Adx = 22, PlusDi = i == 3 ? 5 : 15, MinusDi = 10 } }).ToArray();
        var result = GridAdvisory.Evaluate(input with { Frames = frames });
        Assert.Equal("CAUTION", result.Status);
        Assert.Contains("disagree", Check(result, "trend").Reasons[0]);
    }

    [Fact]
    public void MissingDailyDataPreservesKnownUnfavorableWarnings()
    {
        var input = Input();
        var frames = input.Frames.Select((f, i) => i == 3 ? f with { Error = "Too little history" } : f with { Indicators = f.Indicators! with { Adx = 30 } }).ToArray();
        var result = GridAdvisory.Evaluate(input with { Frames = frames });
        Assert.Equal("INSUFFICIENT_DATA", result.Status);
        Assert.Contains(Check(result, "trend").Reasons, x => x.Contains("conflicts"));
        Assert.Equal("UNFAVORABLE", GridAdvisory.Evaluate(input with { Frames = frames, InvalidConfiguration = "Invalid size" }).Status);
    }

    [Theory]
    [InlineData(0.49, "CAUTION")] [InlineData(0.5, "FAVORABLE")] [InlineData(2, "FAVORABLE")] [InlineData(2.01, "CAUTION")]
    public void VolatilityRatioBoundaries(double ratio, string expected)
    {
        var input = Input();
        var frames = input.Frames.Select((x, i) => i == 0 ? x with { AtrRatio = ratio } : x).ToArray();
        Assert.Equal(expected, Check(GridAdvisory.Evaluate(input with { Frames = frames }), "volatility").Status);
    }

    [Theory]
    [InlineData(9, "CAUTION")] [InlineData(10, "FAVORABLE")] [InlineData(100, "FAVORABLE")] [InlineData(101, "CAUTION")]
    public void SpacingBoundariesUseActualPlanDistances(int spacing, string expected)
    {
        var input = Input(); var config = input.Configuration! with { GridSpacingPoints = spacing };
        Assert.Equal(expected, Check(GridAdvisory.Evaluate(input with { Configuration = config, Plan = Plan(config) }), "volatility").Status);
    }

    [Theory]
    [InlineData(30, "CAUTION")] [InlineData(30.01, "FAVORABLE")] [InlineData(69.99, "FAVORABLE")] [InlineData(70, "CAUTION")]
    public void RsiBoundaries(double rsi, string expected)
    {
        var input = Input(); var frames = input.Frames.Select((f, i) => i == 0 ? f with { Indicators = f.Indicators! with { Rsi = rsi } } : f).ToArray();
        Assert.Equal(expected, Check(GridAdvisory.Evaluate(input with { Frames = frames }), "entry").Status);
    }

    [Fact]
    public void BandTouchAndSqueezeWarnButAreNotReversalSignals()
    {
        var input = Input(); var fast = input.Frames[0];
        foreach (var modified in new[] { fast with { Close = 102 }, fast with { BandwidthPercentile20 = .04 } })
            Assert.Equal("CAUTION", Check(GridAdvisory.Evaluate(input with { Frames = new[] { modified }.Concat(input.Frames.Skip(1)).ToArray() }), "entry").Status);
    }

    [Theory]
    [InlineData("0.002499", "FAVORABLE")] [InlineData("0.0025", "CAUTION")] [InlineData("0.005", "UNFAVORABLE")]
    public void FeesUsePerLevelEntryAndExitNotional(string rate, string expected)
    {
        var input = Input(GridMode.BuyOnly);
        var config = input.Configuration! with { MakerFeeRate = decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture) };
        var plan = new GridPlan(100, 99.5m, 100, .5m, 0, [new(OrderSide.Buy, 0, 99.5m, 1, 1, 99.5m, 1, 99.5m)]);
        Assert.Equal(expected, Check(GridAdvisory.Evaluate(input with { Configuration = config, Plan = plan }), "costs").Status);
    }

    [Fact]
    public void TakerFallbackAndFundingSignsAreVisible()
    {
        var result = GridAdvisory.Evaluate(Input() with { TakeProfitMayTakeLiquidity = true });
        var costs = Check(result, "costs");
        Assert.Contains(costs.Reasons, x => x.Contains("taker"));
        Assert.StartsWith("-", costs.Metrics.Single(x => x.Label == "Sell funding cost · 24h").Value);
        Assert.DoesNotContain("-", costs.Metrics.Single(x => x.Label == "Buy funding cost · 24h").Value);
        Assert.Equal("INSUFFICIENT_DATA", GridAdvisory.Evaluate(Input() with { FundingRate = null }).Status);
    }

    [Fact]
    public void ScenariosCapQuantityAndIncludeFeesForBothDirections()
    {
        foreach (var mode in new[] { GridMode.BuyOnly, GridMode.SellOnly })
        {
            var config = Input(mode).Configuration! with { MaxNetLot = 1.5m, LotSizeIncreasePercent = 100, MaxTradeLot = 2 };
            var scenario = GridAdvisory.Scenario(config, Plan(config), 100, "1d", 5, 2);
            Assert.Equal(1.5m, scenario.FilledQuantity);
            Assert.True(scenario.Loss > 13);
            var none = GridAdvisory.Scenario(config, Plan(config), 100, "15m", .01m, 1);
            Assert.Equal(0, none.FilledQuantity); Assert.Equal(0, none.Loss);
        }
        var input = Input();
        Assert.Equal(8, GridAdvisory.Evaluate(input).Scenarios.Count);
        Assert.Equal("CAUTION", Check(GridAdvisory.Evaluate(input with { Configuration = input.Configuration! with { BasketStopLossUsdt = 0 } }), "exposure").Status);
        Assert.Equal("INSUFFICIENT_DATA", GridAdvisory.Evaluate(input with { Equity = null }).Status);
    }

    private static AdvisoryCheck Check(AdvisoryResult result, string id) => result.Checks.Single(x => x.Id == id);
    private static GridPlan Plan(GridConfiguration config) => GridMath.BuildPlan(config, new("SOL", .01m, .1m, .1m, 1));
    private static AdvisoryInputs Input(GridMode mode = GridMode.TwoWay)
    {
        var config = new GridConfiguration { Symbol = "SOL", GridMode = mode, CenterPrice = 100, TickSize = .01m, QuantityStep = .1m,
            MinOrderQuantity = .1m, MinOrderNotional = 1, MaxLevelsPerSide = 2, GridSpacingPoints = 50, TakeProfitPoints = 100,
            BaseLotSize = 1, MaxNetLot = 2, BasketStopLossUsdt = 1000, MakerFeeRate = .0001m, TakerFeeRate = .0005m };
        var frames = new[] { "15m", "1h", "4h", "1d" }.Select(interval => new AdvisoryFrame(interval, 1800000000, 100,
            new(1800000000, 1, 10, 15, 10, 50, 100, 102, 98, .04), 1, .02, null)).ToArray();
        return new(frames, config, Plan(config), 99.99m, 100.01m, 10000, .00001m);
    }
}
