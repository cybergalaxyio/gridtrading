using System.Collections.Concurrent;
using GridTrading.Domain;

namespace GridTrading.Api.Services;

public sealed record ReplayRequest(string StrategyVersionId, string MarketDataSetId, DateTimeOffset StartAt,
    DateTimeOffset EndAt, string IntrabarPolicy, string FillModel);
public sealed record ReplayEvent(long Sequence, string Type, DateTimeOffset OccurredAt, object Payload);
public sealed record ReplayRun(string RunId, string Status, ReplayRequest Input, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, int BarsProcessed, decimal RealisedPnl, IReadOnlyList<ReplayEvent> Events);

public sealed class ReplayStore
{
    public ConcurrentDictionary<string, ReplayRun> Runs { get; } = new();
}

public sealed class ReplayService(MarketState market, ReplayStore store)
{
    public ReplayRun Create(ReplayRequest request)
    {
        if (!request.IntrabarPolicy.Equals("CONSERVATIVE_NO_FAVORABLE_SEQUENCE", StringComparison.Ordinal))
            throw new TradingProblemException(422, "UNSAFE_INTRABAR_POLICY", "V1 only allows conservative no-favorable-sequence replay.");
        if (request.EndAt <= request.StartAt) throw new TradingProblemException(422, "INVALID_REPLAY_RANGE", "Replay end must be after start.");
        var events = new List<ReplayEvent>();
        var candles = market.Candles();
        var sequence = 0L;
        var entry = candles.Count > 0 ? candles[0].Open - .1m : 145m;
        var tp = entry + .08m;
        var lotOpen = false;
        var pnl = 0m;
        foreach (var candle in candles)
        {
            var bar = new ReplayBar(candle.Open,
                candle.High,
                candle.Low,
                candle.Close);
            var result = ConservativeReplayPolicy.EvaluateLongEntry(bar, entry, tp, lotOpen);
            if (!lotOpen && result.EntryFilled)
            {
                lotOpen = true; events.Add(new ReplayEvent(++sequence, "ENTRY_FILLED", DateTimeOffset.FromUnixTimeSeconds(candle.Time), new { side = "BUY", price = entry, quantity = 1m }));
            }
            if (lotOpen && result.TakeProfitFilled)
            {
                lotOpen = false; pnl += tp - entry;
                events.Add(new ReplayEvent(++sequence, "TAKE_PROFIT_FILLED", DateTimeOffset.FromUnixTimeSeconds(candle.Time), new { side = "SELL", price = tp, realisedPnl = tp - entry }));
            }
        }
        var now = DateTimeOffset.UtcNow;
        var run = new ReplayRun(Ids.New("replay"), "COMPLETED", request, now, now, candles.Count, pnl, events);
        store.Runs[run.RunId] = run;
        return run;
    }
}
