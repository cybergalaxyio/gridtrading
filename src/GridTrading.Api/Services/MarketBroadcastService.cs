using GridTrading.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace GridTrading.Api.Services;

public sealed class MarketBroadcastService(MarketState market, IHubContext<TradingHub> hub) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var candle = market.Tick();
            var snapshot = market.Snapshot("SOLUSDT");
            await hub.Clients.All.SendAsync("MarketSnapshotUpdated", EventEnvelope("MarketSnapshotUpdated", "Market", "SOLUSDT", snapshot), stoppingToken);
            await hub.Clients.All.SendAsync("CandleUpdated", EventEnvelope("CandleUpdated", "Market", "SOLUSDT", candle), stoppingToken);
        }
    }

    private static object EventEnvelope(string type, string aggregateType, string aggregateId, object payload) => new
    {
        eventId = Ids.New("evt"), eventType = type, occurredAt = DateTimeOffset.UtcNow,
        aggregateType, aggregateId, aggregateVersion = 1, sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        correlationId = Ids.New("corr"), payload
    };
}
