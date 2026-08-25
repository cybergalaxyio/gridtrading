using GridTrading.Api.Services;

namespace GridTrading.Api.Infrastructure;

public static class ExtendedEndpoints
{
    public static void MapReplayAndExchangeEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        api.MapPost("/replay-runs", (ReplayRequest request, ReplayService service, HttpResponse response) =>
        {
            var run = service.Create(request);
            response.Headers.Location = $"/api/v1/replay-runs/{run.RunId}";
            return Results.Accepted(value: Summary(run));
        });
        api.MapGet("/replay-runs", (ReplayStore store) => store.Runs.Values.OrderByDescending(x => x.CreatedAt).Select(Summary));
        api.MapGet("/replay-runs/{id}", (string id, ReplayStore store) =>
            store.Runs.TryGetValue(id, out var run) ? Results.Ok(Summary(run)) : Results.NotFound());
        api.MapPost("/replay-runs/{id}/commands/cancel", (string id, ReplayStore store) =>
            store.Runs.TryGetValue(id, out var run) ? Results.Conflict(new { code = "REPLAY_ALREADY_TERMINAL", status = run.Status }) : Results.NotFound());
        api.MapGet("/replay-runs/{id}/events", (string id, ReplayStore store) =>
            store.Runs.TryGetValue(id, out var run) ? Results.Ok(run.Events) : Results.NotFound());
        api.MapGet("/replay-runs/{id}/report", (string id, ReplayStore store) => store.Runs.TryGetValue(id, out var run)
            ? Results.Ok(new { run.RunId, run.Status, run.BarsProcessed, run.RealisedPnl, eventCount = run.Events.Count,
                conservativeIntrabarSequence = true, run.Input.StartAt, run.Input.EndAt, run.CompletedAt }) : Results.NotFound());
        api.MapGet("/hyperliquid-testnet/instruments", async (HyperliquidInfoClient client, CancellationToken ct) =>
        {
            try { return Results.Ok(await client.GetPerpetualMetadata(ct)); }
            catch (HttpRequestException) { return Results.Problem(statusCode: 503, title: "Hyperliquid Testnet metadata is unavailable."); }
        });
    }

    private static object Summary(ReplayRun run) => new
    {
        run.RunId, run.Status, run.CreatedAt, run.CompletedAt, run.BarsProcessed, run.RealisedPnl,
        eventCount = run.Events.Count, run.Input.StrategyVersionId, run.Input.MarketDataSetId,
        run.Input.IntrabarPolicy, run.Input.FillModel
    };
}
