using GridTrading.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Infrastructure;

public static class HyperliquidEndpoints
{
    public static void MapHyperliquidTestnetEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1/hyperliquid-testnet");
        api.MapGet("/accounts", async (Data.TradingDbContext db, CancellationToken ct) =>
            (await db.HyperliquidAccounts.OrderBy(x => x.Name).ToListAsync(ct)).Select(HyperliquidAccountStatusService.Public));
        api.MapGet("/accounts/{id}", async (string id, Data.TradingDbContext db, CancellationToken ct) =>
            await db.HyperliquidAccounts.FindAsync([id], ct) is { } account ? Results.Ok(HyperliquidAccountStatusService.Public(account)) : Results.NotFound());
        api.MapGet("/accounts/{id}/health", async (string id, HyperliquidAccountStatusService service, CancellationToken ct) =>
            Results.Ok(await service.HealthAsync(id, ct)));
        api.MapGet("/market/{symbol}", async (string symbol, HyperliquidTradingClient client, CancellationToken ct) =>
            Results.Ok(await client.GetBookAsync(symbol, ct)));
        api.MapGet("/accounts/{id}/open-orders", async (string id, HyperliquidTradingClient client, CancellationToken ct) =>
        {
            using var orders = await client.GetOpenOrdersAsync(id, ct); return Results.Text(orders.RootElement.GetRawText(), "application/json");
        });
    }
}
