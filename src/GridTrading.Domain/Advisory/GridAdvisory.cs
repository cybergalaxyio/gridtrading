using System.Globalization;

namespace GridTrading.Domain.Advisory;

public static class AdvisoryRules
{
    public const string Version = "grid-advisory-v1";
    public const double WeakTrend = 20, StrongTrend = 25, MinSpacingAtr = .1, MaxSpacingAtr = 1;
    public const double QuietAtr = .5, HighAtr = 2, Oversold = 30, Overbought = 70, SqueezePercentile = .2;
    public const decimal FeeCaution = .5m, FeeUnfavorable = 1m, SpreadCaution = .25m;
    public const int HistoryWindow = 96;
    public const string Favorable = "FAVORABLE", Caution = "CAUTION", Unfavorable = "UNFAVORABLE", Missing = "INSUFFICIENT_DATA";
}

public sealed record AdvisoryMetric(string Label, string Value);
public sealed record AdvisoryCheck(string Id, string Title, string Status, IReadOnlyList<string> Reasons,
    IReadOnlyList<AdvisoryMetric> Metrics, string Consideration);
public sealed record AdvisoryFrame(string Interval, long? ClosedAt, decimal? Close, IndicatorPoint? Indicators,
    double? AtrRatio, double? BandwidthPercentile20, string? Error);
public sealed record AdvisoryScenario(string Interval, int AtrMultiple, decimal Loss, decimal FilledQuantity, string Side);
public sealed record AdvisoryResult(string Status, IReadOnlyList<AdvisoryCheck> Checks, IReadOnlyList<AdvisoryScenario> Scenarios);
public sealed record AdvisoryInputs(IReadOnlyList<AdvisoryFrame> Frames, GridConfiguration? Configuration,
    GridPlan? Plan, decimal? Bid, decimal? Ask, decimal? Equity, decimal? FundingRate,
    string? UnavailableReason = null, string? InvalidConfiguration = null,
    bool TakeProfitMayTakeLiquidity = false, string? FeeSource = null);

public static class GridAdvisory
{
    public static AdvisoryFrame Frame(string interval, int seconds, IEnumerable<MarketBar> bars, DateTimeOffset now)
    {
        var closed = TechnicalIndicators.ClosedHistory(bars, seconds, now);
        var points = TechnicalIndicators.Calculate(closed);
        var last = points[^1];
        var history = points.Take(points.Count - 1).TakeLast(AdvisoryRules.HistoryWindow).ToArray();
        var atrs = history.Where(x => x.Atr.HasValue).Select(x => x.Atr!.Value).ToArray();
        var widths = history.Where(x => x.Bandwidth.HasValue).Select(x => x.Bandwidth!.Value).ToArray();
        var median = atrs.Length == AdvisoryRules.HistoryWindow ? TechnicalIndicators.Percentile(atrs, .5) : 0;
        var complete = last.Atr > 0 && last.Adx.HasValue && last.Rsi.HasValue && last.Bandwidth > 0;
        return new(interval, closed[^1].Time + seconds, closed[^1].Close, last,
            median > 0 ? last.Atr / median : null,
            widths.Length == AdvisoryRules.HistoryWindow ? TechnicalIndicators.Percentile(widths, AdvisoryRules.SqueezePercentile) : null,
            complete ? null : "Flat or undefined indicator readings; suitability cannot be confirmed.");
    }

    public static AdvisoryResult Evaluate(AdvisoryInputs input)
    {
        var checks = new List<AdvisoryCheck>();
        var frames = input.Frames;
        var config = input.Configuration;
        var fast = frames.FirstOrDefault(x => x.Interval == "15m");
        var allFrames = new[] { "15m", "1h", "4h", "1d" }.All(key => frames.Any(x => x.Interval == key && x.Error is null && x.Indicators is not null));
        var reasons = new List<string>();
        var trendStatus = AdvisoryRules.Favorable;
        var directions = new HashSet<int>();
        foreach (var f in frames.Where(x => x.Error is null && x.Indicators is not null))
        {
            var trendPoint = f.Indicators!;
            if (trendPoint.Adx is null || trendPoint.PlusDi is null || trendPoint.MinusDi is null) continue;
            var direction = Math.Sign(trendPoint.PlusDi.Value - trendPoint.MinusDi.Value);
            if (trendPoint.Adx >= AdvisoryRules.WeakTrend)
            {
                if (direction != 0) directions.Add(direction);
                var incompatible = config is not null && direction != 0 && (config.GridMode == GridMode.TwoWay ||
                    config.GridMode == GridMode.BuyOnly && direction < 0 || config.GridMode == GridMode.SellOnly && direction > 0);
                if (trendPoint.Adx >= AdvisoryRules.StrongTrend && incompatible) trendStatus = AdvisoryRules.Unfavorable;
                else if ((trendPoint.Adx < AdvisoryRules.StrongTrend || direction == 0 || config is null) && trendStatus != AdvisoryRules.Unfavorable)
                    trendStatus = AdvisoryRules.Caution;
                var reason = $"{f.Interval}: ADX {N(trendPoint.Adx)} — {(direction > 0 ? "upward" : direction < 0 ? "downward" : "unclear")} trend{(incompatible && trendPoint.Adx >= AdvisoryRules.StrongTrend ? " conflicts with the grid mode" : "")}.";
                if (incompatible && trendPoint.Adx >= AdvisoryRules.StrongTrend) reasons.Insert(0, reason);
                else reasons.Add(reason);
            }
        }
        if (directions.Count > 1) { if (trendStatus != AdvisoryRules.Unfavorable) trendStatus = AdvisoryRules.Caution; reasons.Insert(0, "Timeframes disagree on trend direction."); }
        if (reasons.Count == 0) reasons.Add("Weak trend evidence across available timeframes; this alone does not establish grid suitability.");
        foreach (var interval in new[] { "15m", "1h", "4h", "1d" })
        {
            var frame = frames.FirstOrDefault(x => x.Interval == interval);
            if (frame is null || frame.Error is not null) reasons.Insert(0, $"{interval}: {frame?.Error ?? "Market history is unavailable."}");
        }
        checks.Add(new("trend", "Trend", allFrames ? trendStatus : AdvisoryRules.Missing, reasons, [], "Check the 4h and daily direction before committing to a grid that may run for days."));

        var atr = fast?.Error is null ? fast?.Indicators?.Atr : null;
        var strategyReady = config is not null && input.Plan is not null && input.Bid > 0 && input.Ask >= input.Bid;
        var unavailable = input.UnavailableReason ?? "Select a matching saved strategy and account with fresh market data.";
        if (atr > 0 && strategyReady)
        {
            var metrics = new List<AdvisoryMetric> { new("15m ATR", N(atr)), new("ATR / recent median", N(fast!.AtrRatio) + "×"),
                new("TP / ATR", N((double)(config!.TakeProfitPoints * config.TickSize) / atr.Value) + "×") };
            var ratios = new List<double>();
            foreach (var side in input.Plan!.Levels.GroupBy(x => x.Side))
            {
                var levels = side.OrderBy(x => x.LevelIndex).ToArray();
                metrics.Add(new($"{side.Key} initial distance / ATR", N((double)Math.Abs(levels[0].EntryPrice - (input.Bid!.Value + input.Ask!.Value) / 2) / atr.Value) + "×"));
                for (var i = 1; i < levels.Length; i++) ratios.Add((double)Math.Abs(levels[i].EntryPrice - levels[i - 1].EntryPrice) / atr.Value);
            }
            if (ratios.Count > 0) metrics.Add(new("Inter-level spacing / ATR", $"{N(ratios.Min())}–{N(ratios.Max())}×"));
            var caution = ratios.Any(x => x < AdvisoryRules.MinSpacingAtr || x > AdvisoryRules.MaxSpacingAtr) ||
                fast!.AtrRatio < AdvisoryRules.QuietAtr || fast.AtrRatio > AdvisoryRules.HighAtr;
            checks.Add(new("volatility", "Volatility", fast!.AtrRatio is null ? AdvisoryRules.Missing : caution ? AdvisoryRules.Caution : AdvisoryRules.Favorable,
                [caution ? "Spacing or current volatility is outside the initial reference range." : "Spacing and volatility are within the configured reference ranges."], metrics,
                "Review spacing relative to 15m movement; longer-timeframe ATR describes broader movement, not an ideal spacing target."));
        }
        else checks.Add(Missing("volatility", "Volatility", unavailable));

        if (fast is { Error: null, Indicators: { Rsi: not null, Upper: not null, Lower: not null, Bandwidth: not null } p } && fast.BandwidthPercentile20 is not null)
        {
            var outside = (double)fast.Close! >= p.Upper || (double)fast.Close! <= p.Lower;
            var stretched = p.Rsi >= AdvisoryRules.Overbought || p.Rsi <= AdvisoryRules.Oversold;
            var squeeze = p.Bandwidth <= fast.BandwidthPercentile20;
            var entryReasons = new List<string>();
            if (outside) entryReasons.Add("15m close touches or exceeds a Bollinger band.");
            if (stretched) entryReasons.Add("15m RSI indicates stretched momentum.");
            if (squeeze) entryReasons.Add("15m bandwidth is in its recent lowest 20%; watch for a breakout.");
            if (entryReasons.Count == 0) entryReasons.Add("No band, RSI, or compression warning on the last closed 15m candle.");
            checks.Add(new("entry", "Entry", outside || stretched || squeeze ? AdvisoryRules.Caution : AdvisoryRules.Favorable, entryReasons,
                [new("RSI(14)", N(p.Rsi)), new("BB lower / middle / upper", $"{N(p.Lower)} / {N(p.Middle)} / {N(p.Upper)}"), new("Bandwidth", N(p.Bandwidth * 100) + "%")],
                "Band touches and RSI extremes are not reversal predictions."));
        }
        else checks.Add(Missing("entry", "Entry", fast?.Error ?? "15m entry indicators are unavailable."));

        if (strategyReady)
        {
            var entryFee = config!.PostOnlyEntries ? config.MakerFeeRate : config.TakerFeeRate;
            var tpFee = config.PostOnlyTakeProfits && !input.TakeProfitMayTakeLiquidity ? config.MakerFeeRate : config.TakerFeeRate;
            var levels = input.Plan!.Levels;
            var feeRatio = levels.Max(l => (l.EntryPrice * entryFee + GridMath.TakeProfitPrice(l.Side, l.EntryPrice, config.TakeProfitPoints, config.TickSize) * tpFee) / l.TakeProfitDistance);
            var spreadRatio = (input.Ask!.Value - input.Bid!.Value) / levels.Min(x => x.TakeProfitDistance);
            var costReasons = new List<string> { $"Estimated fees consume up to {D(feeRatio * 100)}% of a completed level's gross TP." };
            if (input.TakeProfitMayTakeLiquidity) costReasons.Add("TP can fall back to taking liquidity; its estimate uses taker fees.");
            if (spreadRatio > AdvisoryRules.SpreadCaution) costReasons.Add("The spread exceeds 25% of TP distance (shown separately from fees).");
            if (input.FeeSource?.Contains("DEFAULT", StringComparison.Ordinal) == true) costReasons.Add("Exchange default fees are being used; account-specific fees were unavailable.");
            var costStatus = feeRatio >= AdvisoryRules.FeeUnfavorable ? AdvisoryRules.Unfavorable :
                feeRatio >= AdvisoryRules.FeeCaution || spreadRatio > AdvisoryRules.SpreadCaution || input.FeeSource?.Contains("DEFAULT", StringComparison.Ordinal) == true ? AdvisoryRules.Caution : AdvisoryRules.Favorable;
            var metrics = new List<AdvisoryMetric> { new("Highest fee / gross TP", D(feeRatio * 100) + "%"), new("Spread / TP", D(spreadRatio * 100) + "%"), new("Fee source", input.FeeSource ?? "Configured") };
            metrics.Add(new("Hourly funding", input.FundingRate.HasValue ? D(input.FundingRate.Value * 100, "0.######") + "%" : "Unavailable"));
            if (input.FundingRate is { } rate)
                foreach (var side in levels.GroupBy(x => x.Side))
                {
                    var notional = Math.Min(side.Sum(x => x.PlannedQuantity), config.MaxNetLot) * (input.Bid.Value + input.Ask.Value) / 2;
                    foreach (var hours in new[] { 4, 24, 72 })
                        metrics.Add(new($"{side.Key} funding cost · {hours}h", D(notional * rate * hours * (side.Key == OrderSide.Buy ? 1 : -1)) + " USDC"));
                }
            else costReasons.Add("Funding is unavailable; total suitability cannot be confirmed.");
            checks.Add(new("costs", "Costs", input.FundingRate is null ? AdvisoryRules.Missing : costStatus, costReasons, metrics,
                "Funding scenarios hold the current rate and capped exposure constant; positive values are costs, negative values receipts. Rates and positions change."));
        }
        else checks.Add(Missing("costs", "Costs", unavailable));

        var scenarios = new List<AdvisoryScenario>();
        if (strategyReady && input.Equity > 0)
        {
            foreach (var f in frames.Where(x => x.Error is null && x.Indicators?.Atr > 0))
                foreach (var multiple in new[] { 1, 2 })
                    scenarios.Add(Scenario(config!, input.Plan!, (input.Bid!.Value + input.Ask!.Value) / 2, f.Interval, (decimal)f.Indicators!.Atr!.Value, multiple));
            var riskReasons = new List<string>();
            if (config!.BasketStopLossUsdt <= 0) riskReasons.Add("No basket stop-loss is configured.");
            if (config.BasketStopLossUsdt > 0 && scenarios.Any(x => x.Loss >= config.BasketStopLossUsdt)) riskReasons.Add("An adverse-move scenario reaches the configured basket stop-loss.");
            if (scenarios.Any(x => x.Loss >= input.Equity.Value)) riskReasons.Add("An adverse-move scenario reaches account equity.");
            var riskStatus = riskReasons.Count > 0 ? AdvisoryRules.Caution : AdvisoryRules.Favorable;
            if (riskReasons.Count == 0) riskReasons.Add("Within configured checks; this does not bound future losses.");
            var metrics = new List<AdvisoryMetric> { new("Account equity", D(input.Equity.Value) + " USDC"), new("Basket SL", D(config.BasketStopLossUsdt) + " USDC") };
            foreach (var side in input.Plan!.Levels.GroupBy(x => x.Side))
            {
                var quantity = side.Sum(x => x.PlannedQuantity); var cap = Math.Min(quantity, config.MaxNetLot);
                var notional = cap * (input.Bid!.Value + input.Ask!.Value) / 2;
                metrics.Add(new($"{side.Key} planned / capped quantity", D(quantity, "0.########") + " / " + D(cap, "0.########")));
                metrics.Add(new($"{side.Key} capped exposure / equity", D(notional) + " USDC / " + D(notional / input.Equity.Value * 100) + "%"));
            }
            checks.Add(new("exposure", "Exposure", allFrames ? riskStatus : AdvisoryRules.Missing, riskReasons, metrics,
                "Scenarios start flat, retain crossed fills, and include entry/exit fees and exit slippage. They do not assume a stop-loss fill, include funding, or estimate liquidation. ATR is not a holding-period forecast or maximum loss."));
        }
        else checks.Add(Missing("exposure", "Exposure", input.Equity is <= 0 ? "Positive account equity is required for exposure comparisons." : unavailable));

        if (input.InvalidConfiguration is { } invalid)
            checks[1] = new("volatility", "Volatility", AdvisoryRules.Unfavorable, ["Invalid grid configuration: " + invalid], [], "Review the saved grid configuration before starting.");
        var status = input.InvalidConfiguration is not null ? AdvisoryRules.Unfavorable :
            checks.Any(x => x.Status == AdvisoryRules.Missing) || !allFrames ? AdvisoryRules.Missing :
            checks.Any(x => x.Status == AdvisoryRules.Unfavorable) ? AdvisoryRules.Unfavorable :
            checks.Any(x => x.Status == AdvisoryRules.Caution) ? AdvisoryRules.Caution : AdvisoryRules.Favorable;
        return new(status, checks, scenarios);
    }

    public static AdvisoryScenario Scenario(GridConfiguration config, GridPlan plan, decimal mid, string interval, decimal atr, int multiple)
    {
        var scenarios = new List<AdvisoryScenario>();
        foreach (var side in plan.Levels.GroupBy(x => x.Side))
        {
            var target = Math.Max(config.TickSize, mid + (side.Key == OrderSide.Buy ? -1 : 1) * atr * multiple);
            decimal filled = 0, pnl = 0, fees = 0;
            foreach (var level in side.OrderBy(x => x.LevelIndex))
            {
                if (side.Key == OrderSide.Buy ? target > level.EntryPrice : target < level.EntryPrice) continue;
                var quantity = GridMath.RoundDown(Math.Min(level.PlannedQuantity, Math.Max(0, config.MaxNetLot - filled)), config.QuantityStep);
                filled += quantity;
                pnl += quantity * (side.Key == OrderSide.Buy ? target - level.EntryPrice : level.EntryPrice - target);
                fees += quantity * (level.EntryPrice * (config.PostOnlyEntries ? config.MakerFeeRate : config.TakerFeeRate) +
                    target * (config.TakerFeeRate + config.EstimatedExitSlippagePct / 100));
            }
            scenarios.Add(new(interval, multiple, Math.Max(0, fees - pnl), filled, side.Key == OrderSide.Buy ? "BUY" : "SELL"));
        }
        return scenarios.OrderByDescending(x => x.Loss).First();
    }

    private static AdvisoryCheck Missing(string id, string title, string reason) => new(id, title, AdvisoryRules.Missing, [reason], [], "Refresh data or select a matching saved strategy and account.");
    private static string N(double? value) => value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "—";
    private static string D(decimal value, string format = "0.####") => value.ToString(format, CultureInfo.InvariantCulture);
}
