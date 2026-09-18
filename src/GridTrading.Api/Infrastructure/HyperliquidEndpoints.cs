using GridTrading.Api.Services;
using GridTrading.Api.Contracts;
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
            if ((context.HttpContext.Request.Method is "POST" or "PATCH" or "PUT" or "DELETE") &&
                !LocalAccountRequestPolicy.IsAllowed(context.HttpContext))
                return Results.Problem(statusCode: 403, title: "Trusted local portal required",
                    detail: "Account changes require a local portal origin and an application/json request.");
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
        api.MapGet("/accounts", (HyperliquidAccountManagementService service, CancellationToken ct) => service.ListAsync(network, ct));
        api.MapGet("/accounts/{id}", (string id, HyperliquidAccountManagementService service, CancellationToken ct) => service.GetAsync(network, id, ct));
        api.MapPost("/accounts", async (CreateAccountRequest request, HyperliquidAccountManagementService service, CancellationToken ct) =>
        {
            var account = await service.CreateAsync(network, request, ct);
            return Results.Created($"/api/v1/{HyperliquidNetwork.EnvironmentId(network)}/accounts/{account.AccountId}", account);
        });
        api.MapPatch("/accounts/{id}", (string id, RenameAccountRequest request, HyperliquidAccountManagementService service, CancellationToken ct) =>
            service.RenameAsync(network, id, request, ct));
        api.MapPut("/accounts/{id}/credentials", (string id, ReplaceAccountCredentialsRequest request, HyperliquidAccountManagementService service, CancellationToken ct) =>
            service.ReplaceCredentialsAsync(network, id, request, ct));
        api.MapPost("/accounts/{id}/test-and-enable", (string id, HyperliquidAccountManagementService service, CancellationToken ct) =>
            service.EnableAsync(network, id, ct));
        api.MapPost("/accounts/{id}/disable", (string id, HyperliquidAccountManagementService service, CancellationToken ct) =>
            service.DisableAsync(network, id, ct));
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
