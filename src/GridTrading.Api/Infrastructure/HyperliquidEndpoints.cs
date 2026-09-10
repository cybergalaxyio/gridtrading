using GridTrading.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Infrastructure;

public static class HyperliquidEndpoints
{
    public static void MapHyperliquidTestnetEndpoints(this WebApplication app)
    {
        MapNetwork(app, HyperliquidNetwork.Testnet);
        MapNetwork(app, HyperliquidNetwork.Mainnet);
    }

    private static void MapNetwork(WebApplication app, string network)
    {
        var api = app.MapGroup($"/api/v1/{HyperliquidNetwork.EnvironmentId(network)}");
        api.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.Request.RouteValues.TryGetValue("id", out var id))
            {
                var db = context.HttpContext.RequestServices.GetRequiredService<Data.TradingDbContext>();
                if (!await db.HyperliquidAccounts.AnyAsync(x => x.Id == (string?)id && x.Environment == network,
                    context.HttpContext.RequestAborted))
                    return Results.NotFound();
            }
            return await next(context);
        });
        api.MapGet("/instruments", async (HyperliquidInfoClient client, CancellationToken ct) =>
            Results.Ok(await client.GetPerpetualMetadata(ct, network)));
        api.MapGet("/accounts", async (Data.TradingDbContext db, CancellationToken ct) =>
            (await db.HyperliquidAccounts.Where(x => x.Environment == network).OrderBy(x => x.Name).ToListAsync(ct)).Select(HyperliquidAccountStatusService.Public));
        api.MapGet("/accounts/{id}", async (string id, Data.TradingDbContext db, CancellationToken ct) =>
            await db.HyperliquidAccounts.FindAsync([id], ct) is { } account ? Results.Ok(HyperliquidAccountStatusService.Public(account)) : Results.NotFound());
        api.MapGet("/accounts/{id}/health", async (string id, HyperliquidAccountStatusService service, CancellationToken ct) =>
            Results.Ok(await service.HealthAsync(id, ct)));
        api.MapGet("/market/{symbol}", async (string symbol, HyperliquidTradingClient client, CancellationToken ct) =>
            Results.Ok(await client.GetBookAsync(symbol, ct, network)));
        api.MapGet("/market/{symbol}/candles", async (string symbol, string? interval, int? limit, HyperliquidMarketDataClient market, CancellationToken ct) =>
            Results.Ok(await market.GetCandlesAsync(symbol, interval ?? "1m", limit ?? 180, ct, network)));
        api.MapGet("/accounts/{id}/state", async (string id, string? symbol, HyperliquidMarketDataClient market, CancellationToken ct) =>
            Results.Ok(await market.GetAccountStateAsync(id, symbol ?? "SOLUSDT", ct)));
        api.MapGet("/accounts/{id}/open-orders", async (string id, HyperliquidTradingClient client,
            HyperliquidOrderOwnershipService ownership, CancellationToken ct) =>
        {
            using var orders = await client.GetFrontendOpenOrdersAsync(id, ct);
            return Results.Json(await ownership.AnnotateOpenOrdersAsync(id, orders.RootElement, ct));
        });
        api.MapGet("/accounts/{id}/clearinghouse-state", async (string id, HyperliquidTradingClient client, CancellationToken ct) =>
        {
            using var state = await client.GetClearinghouseStateAsync(id, ct); return Results.Text(state.RootElement.GetRawText(), "application/json");
        });
        api.MapGet("/accounts/{id}/spot-clearinghouse-state", async (string id, HyperliquidTradingClient client, CancellationToken ct) =>
        {
            using var state = await client.GetSpotClearinghouseStateAsync(id, ct); return Results.Text(state.RootElement.GetRawText(), "application/json");
        });
        api.MapGet("/accounts/{id}/order-history", async (string id, HyperliquidTradingClient client,
            HyperliquidOrderOwnershipService ownership, CancellationToken ct) =>
        {
            using var orders = await client.GetHistoricalOrdersAsync(id, ct);
            return Results.Json(await ownership.AnnotateHistoricalOrdersAsync(id, orders.RootElement, ct));
        });
    }
}
