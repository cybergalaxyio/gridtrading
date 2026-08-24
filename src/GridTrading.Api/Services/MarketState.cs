namespace GridTrading.Api.Services;

public sealed class MarketState
{
    private readonly object _gate = new();
    private decimal _mid = 145.25m;
    private DateTimeOffset _asOf = DateTimeOffset.UtcNow;
    private readonly List<CandleDto> _candles = [];

    public MarketState()
    {
        var random = new Random(42);
        var price = 142m;
        var start = DateTimeOffset.UtcNow.AddMinutes(-180);
        for (var i = 0; i < 180; i++)
        {
            var open = price;
            var close = Math.Max(100m, open + (decimal)(random.NextDouble() - .48) * .9m);
            var high = Math.Max(open, close) + (decimal)random.NextDouble() * .35m;
            var low = Math.Min(open, close) - (decimal)random.NextDouble() * .35m;
            _candles.Add(new CandleDto(start.AddMinutes(i).ToUnixTimeSeconds(), open, high, low, close, 800m + random.Next(1400)));
            price = close;
        }
        _mid = price;
    }

    public MarketSnapshot Snapshot(string symbol)
    {
        lock (_gate) return new(symbol, _mid - .005m, _mid + .005m, _mid, _asOf, DateTimeOffset.UtcNow - _asOf > TimeSpan.FromSeconds(5));
    }

    public IReadOnlyList<CandleDto> Candles()
    {
        lock (_gate) return _candles.ToArray();
    }

    public CandleDto Tick()
    {
        lock (_gate)
        {
            var wave = (decimal)Math.Sin(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 9000d) * .06m;
            _mid = Math.Max(1m, _mid + wave);
            _asOf = DateTimeOffset.UtcNow;
            var last = _candles[^1];
            var updated = last with { High = Math.Max(last.High, _mid), Low = Math.Min(last.Low, _mid), Close = _mid, Volume = last.Volume + 2m };
            _candles[^1] = updated;
            return updated;
        }
    }
}

public sealed record MarketSnapshot(string Symbol, decimal Bid, decimal Ask, decimal Mid, DateTimeOffset AsOf, bool IsStale);
public sealed record CandleDto(long Time, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);
