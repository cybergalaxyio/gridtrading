using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using GridTrading.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed record HyperliquidAccountState(
    string AccountId,
    string Symbol,
    decimal AccountValue,
    decimal Withdrawable,
    decimal TotalMarginUsed,
    decimal NetPosition,
    decimal UnrealizedPnl,
    decimal? EntryPrice,
    DateTimeOffset AsOf, string AccountMode, decimal TradingEquity, decimal AvailableBalance);

public sealed class HyperliquidMarketDataClient(HttpClient http, IConfiguration configuration, TradingDbContext db)
{
    private static readonly IReadOnlyDictionary<string, TimeSpan> Intervals = new Dictionary<string, TimeSpan>
    {
        ["1m"] = TimeSpan.FromMinutes(1), ["3m"] = TimeSpan.FromMinutes(3), ["5m"] = TimeSpan.FromMinutes(5),
        ["15m"] = TimeSpan.FromMinutes(15), ["30m"] = TimeSpan.FromMinutes(30), ["1h"] = TimeSpan.FromHours(1),
        ["2h"] = TimeSpan.FromHours(2), ["4h"] = TimeSpan.FromHours(4), ["8h"] = TimeSpan.FromHours(8),
        ["12h"] = TimeSpan.FromHours(12), ["1d"] = TimeSpan.FromDays(1), ["3d"] = TimeSpan.FromDays(3),
        ["1w"] = TimeSpan.FromDays(7)
    };

    public async Task<IReadOnlyList<CandleDto>> GetCandlesAsync(string symbol, string interval, int limit, CancellationToken ct, string network = HyperliquidNetwork.Testnet)
    {
        if (!Intervals.TryGetValue(interval, out var duration))
            throw new TradingProblemException(422, "INVALID_CANDLE_INTERVAL", "Unsupported Hyperliquid candle interval.");
        limit = Math.Clamp(limit, 10, 1000);
        var end = DateTimeOffset.UtcNow;
        var start = end - duration * (limit + 2);
        using var document = await PostInfo(new
        {
            type = "candleSnapshot",
            req = new { coin = ToCoin(symbol), interval, startTime = start.ToUnixTimeMilliseconds(), endTime = end.ToUnixTimeMilliseconds() }
        }, ct, network);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        return document.RootElement.EnumerateArray().TakeLast(limit).Select(item => new CandleDto(
            item.GetProperty("t").GetInt64() / 1000,
            Decimal(item.GetProperty("o")), Decimal(item.GetProperty("h")), Decimal(item.GetProperty("l")),
            Decimal(item.GetProperty("c")), Decimal(item.GetProperty("v")))).ToArray();
    }

    public async Task<HyperliquidAccountState> GetAccountStateAsync(string accountId, string symbol, CancellationToken ct)
    {
        var account = await db.HyperliquidAccounts.SingleOrDefaultAsync(x => x.Id == accountId && x.Enabled, ct)
            ?? throw new TradingProblemException(404, "EXECUTION_ACCOUNT_NOT_FOUND", "Hyperliquid account was not found or disabled.");
        using var document = await PostInfo(new { type = "clearinghouseState", user = account.AccountAddress }, ct, account.Environment);
        using var spot = await PostInfo(new { type = "spotClearinghouseState", user = account.AccountAddress }, ct, account.Environment);
        using var abstraction = await PostInfo(new { type = "userAbstraction", user = account.AccountAddress }, ct, account.Environment);
        var funds = HyperliquidAccountFunds.Resolve(abstraction.RootElement, document.RootElement, spot.RootElement);
        var root = document.RootElement;
        var summary = root.GetProperty("marginSummary");
        var accountValue = Decimal(summary.GetProperty("accountValue"));
        var marginUsed = summary.TryGetProperty("totalMarginUsed", out var margin) ? Decimal(margin) : 0m;
        var withdrawable = root.TryGetProperty("withdrawable", out var available) ? Decimal(available) : 0m;
        var net = 0m; var unrealized = 0m; decimal? entryPrice = null;
        if (root.TryGetProperty("assetPositions", out var positions))
        {
            foreach (var item in positions.EnumerateArray())
            {
                var position = item.GetProperty("position");
                if (!string.Equals(position.GetProperty("coin").GetString(), ToCoin(symbol), StringComparison.OrdinalIgnoreCase)) continue;
                net = Decimal(position.GetProperty("szi"));
                unrealized = position.TryGetProperty("unrealizedPnl", out var pnl) ? Decimal(pnl) : 0m;
                if (position.TryGetProperty("entryPx", out var price) && price.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                    entryPrice = Decimal(price);
                break;
            }
        }
        return new HyperliquidAccountState(account.Id, symbol.ToUpperInvariant(), accountValue, withdrawable, marginUsed,
            net, unrealized, entryPrice, DateTimeOffset.UtcNow, funds.AccountMode, funds.TradingEquity, funds.AvailableBalance);
    }

    private async Task<JsonDocument> PostInfo(object request, CancellationToken ct, string network)
    {
        using var response = await http.PostAsJsonAsync(HyperliquidNetwork.Endpoint(configuration, network, "Info"), request, ct);
        if (!response.IsSuccessStatusCode)
            throw new TradingProblemException(503, "EXCHANGE_INFO_UNAVAILABLE", $"Hyperliquid Info returned HTTP {(int)response.StatusCode}.");
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private static decimal Decimal(JsonElement value) => decimal.Parse(value.GetString() ?? value.ToString(), CultureInfo.InvariantCulture);
    private static string ToCoin(string symbol) => HyperliquidTradingClient.ToCoin(symbol);
}
