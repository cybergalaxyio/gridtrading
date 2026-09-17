using GridTrading.Api.Services;

namespace GridTrading.Api.Infrastructure;

public static class GridAdvisoryEndpoints
{
    public static void MapGridAdvisoryEndpoints(this WebApplication app) =>
        app.MapGet("/api/v1/grid-advisory", async (string environmentId, string symbol, string? accountId,
            string? strategyId, GridAdvisoryService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(environmentId, symbol, accountId, strategyId, ct)));
}
