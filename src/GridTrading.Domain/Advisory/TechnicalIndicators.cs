namespace GridTrading.Domain.Advisory;

public sealed record MarketBar(long Time, decimal Open, decimal High, decimal Low, decimal Close);
public sealed record IndicatorPoint(long Time, double? Atr, double? Adx, double? PlusDi, double? MinusDi,
    double? Rsi, double? Middle, double? Upper, double? Lower, double? Bandwidth);

public static class TechnicalIndicators
{
    public const int Period = 14;
    public const int BandPeriod = 20;

    // ATR is seeded with the first 14 true ranges (first TR = high-low).
    // RSI/DI use 14 price changes; ADX is seeded with the first 14 DX readings.
    public static IReadOnlyList<IndicatorPoint> Calculate(IReadOnlyList<MarketBar> bars)
    {
        var result = new List<IndicatorPoint>();
        double trSeed = 0, atr = 0, gain = 0, loss = 0, plus = 0, minus = 0, directionalTr = 0;
        double dxSum = 0, adx = 0;
        var dxCount = 0;
        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            var tr = (double)(b.High - b.Low);
            if (i > 0) tr = Math.Max(tr, Math.Max(Math.Abs((double)(b.High - bars[i - 1].Close)), Math.Abs((double)(b.Low - bars[i - 1].Close))));
            if (i < Period) { trSeed += tr; if (i == Period - 1) atr = trSeed / Period; }
            else atr = (atr * (Period - 1) + tr) / Period;

            double? pdi = null, mdi = null, rsi = null, adxValue = null;
            if (i > 0)
            {
                var change = (double)(b.Close - bars[i - 1].Close);
                var up = (double)(b.High - bars[i - 1].High);
                var down = (double)(bars[i - 1].Low - b.Low);
                var p = up > down && up > 0 ? up : 0;
                var m = down > up && down > 0 ? down : 0;
                if (i <= Period)
                {
                    gain += Math.Max(change, 0); loss += Math.Max(-change, 0);
                    plus += p; minus += m; directionalTr += tr;
                    if (i == Period) { gain /= Period; loss /= Period; plus /= Period; minus /= Period; directionalTr /= Period; }
                }
                else
                {
                    gain = (gain * (Period - 1) + Math.Max(change, 0)) / Period;
                    loss = (loss * (Period - 1) + Math.Max(-change, 0)) / Period;
                    plus = (plus * (Period - 1) + p) / Period; minus = (minus * (Period - 1) + m) / Period;
                    directionalTr = (directionalTr * (Period - 1) + tr) / Period;
                }
                if (i >= Period)
                {
                    rsi = gain + loss == 0 ? null : loss == 0 ? 100 : 100 - 100 / (1 + gain / loss);
                    if (directionalTr > 0) { pdi = 100 * plus / directionalTr; mdi = 100 * minus / directionalTr; }
                    if (pdi + mdi > 0)
                    {
                        var dx = 100 * Math.Abs(pdi!.Value - mdi!.Value) / (pdi.Value + mdi.Value);
                        dxCount++;
                        if (dxCount <= Period) { dxSum += dx; if (dxCount == Period) adx = dxSum / Period; }
                        else adx = (adx * (Period - 1) + dx) / Period;
                        if (dxCount >= Period) adxValue = adx;
                    }
                    else { dxCount = 0; dxSum = 0; adx = 0; }
                }
            }
            double? middle = null, upper = null, lower = null, bandwidth = null;
            if (i >= BandPeriod - 1)
            {
                var closes = bars.Skip(i - BandPeriod + 1).Take(BandPeriod).Select(x => (double)x.Close).ToArray();
                middle = closes.Average();
                var sigma = Math.Sqrt(closes.Average(x => Math.Pow(x - middle.Value, 2)));
                upper = middle + 2 * sigma; lower = middle - 2 * sigma;
                if (middle > 0) bandwidth = (upper - lower) / middle;
            }
            result.Add(new(b.Time, i >= Period - 1 ? atr : null, adxValue, pdi, mdi, rsi, middle, upper, lower, bandwidth));
        }
        return result;
    }

    public static IReadOnlyList<MarketBar> ClosedHistory(IEnumerable<MarketBar> source, int seconds, DateTimeOffset now)
    {
        var unix = now.ToUnixTimeSeconds();
        var bars = source.Where(x => x.Time + seconds <= unix).GroupBy(x => x.Time).Select(x => x.Last()).OrderBy(x => x.Time).ToArray();
        if (bars.Length < 200) throw new ArgumentException("At least 200 closed candles are required.");
        // Only tolerate the immediately preceding close for two minutes after an interval boundary.
        var expected = unix / seconds * seconds - seconds;
        if (bars[^1].Time < expected && !(unix % seconds <= 120 && bars[^1].Time == expected - seconds))
            throw new ArgumentException("Latest closed candle is not yet available.");
        foreach (var b in bars)
            if (b.Time % seconds != 0 || b.Open <= 0 || b.Close <= 0 || b.Low <= 0 || b.High < Math.Max(b.Open, b.Close) || b.Low > Math.Min(b.Open, b.Close))
                throw new ArgumentException("Candle history contains invalid prices or timestamps.");
        for (var i = 1; i < bars.Length; i++)
            if (bars[i].Time - bars[i - 1].Time != seconds) throw new ArgumentException("Candle history contains a gap.");
        return bars;
    }

    public static double Percentile(IEnumerable<double> values, double fraction)
    {
        var sorted = values.Order().ToArray();
        var at = (sorted.Length - 1) * fraction;
        var lo = (int)Math.Floor(at); var hi = (int)Math.Ceiling(at);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (at - lo);
    }
}
