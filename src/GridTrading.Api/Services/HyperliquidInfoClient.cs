using System.Net.Http.Json;
using System.Text.Json;
using GridTrading.Api.Exchange;

namespace GridTrading.Api.Services;

public sealed record ExchangeInstrumentMetadata(
    string Symbol,
    string Environment,
    int AssetIndex,
    int SizeDecimals,
    decimal ReferencePrice,
    decimal TickSize,
    decimal QuantityStep,
    decimal MinOrderQuantity,
    decimal MinOrderNotional,
    int MaxActiveOrders,
    decimal MakerFeeRate,
    decimal TakerFeeRate,
    string FeeSource,
    DateTimeOffset AsOf);

public sealed class HyperliquidInfoClient(HttpClient httpClient, IConfiguration configuration)
{
    public const decimal MinimumOrderNotional = 10m;

    public async Task<object> GetPerpetualMetadata(CancellationToken ct)
    {
        var configured = configuration["Hyperliquid:InfoUrl"] ?? "https://api.hyperliquid-testnet.xyz/info";
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var endpoint) || endpoint.Host != "api.hyperliquid-testnet.xyz")
            throw new TradingProblemException(403, "TESTNET_ONLY", "The read-only client only accepts the official Hyperliquid Testnet info endpoint.");
        using var response = await httpClient.PostAsJsonAsync(endpoint, new { type = "metaAndAssetCtxs" }, ct);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2)
            throw new TradingProblemException(503, "EXCHANGE_RESPONSE_INVALID", "Hyperliquid metadata response was not recognized.");
        var contexts = root[1];
        var universe = root[0].GetProperty("universe").EnumerateArray().Select((item, index) =>
        {
            var context = contexts.ValueKind == JsonValueKind.Array && index < contexts.GetArrayLength()
                ? contexts[index] : default;
            return new
            {
                assetIndex = index, symbol = item.GetProperty("name").GetString(),
                sizeDecimals = item.GetProperty("szDecimals").GetInt32(),
                isDelisted = item.TryGetProperty("isDelisted", out var delisted) && delisted.GetBoolean(),
                markPrice = OptionalDecimal(context, "markPx"),
                previousDayPrice = OptionalDecimal(context, "prevDayPx"),
                fundingRate = OptionalDecimal(context, "funding")
            };
        }).ToArray();
        return new { exchange = "HYPERLIQUID", environment = "TESTNET", tradingEnabled = false, asOf = DateTimeOffset.UtcNow, universe };
    }

    public async Task<ExchangeInstrumentMetadata> GetPerpetualInstrument(string symbol, decimal? referencePrice, string? userAddress, CancellationToken ct)
    {
        var endpoint = Endpoint();
        using var response = await httpClient.PostAsJsonAsync(endpoint, new { type = "metaAndAssetCtxs" }, ct);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2)
            throw new TradingProblemException(503, "EXCHANGE_RESPONSE_INVALID", "Hyperliquid metadata response was not recognized.");
        var coin = ToCoin(symbol); var index = 0; JsonElement? asset = null;
        foreach (var item in root[0].GetProperty("universe").EnumerateArray())
        {
            if (string.Equals(item.GetProperty("name").GetString(), coin, StringComparison.OrdinalIgnoreCase))
            { asset = item; break; }
            index++;
        }
        if (asset is null)
            throw new TradingProblemException(404, "INSTRUMENT_NOT_FOUND", $"{coin} is not listed in Hyperliquid Testnet metadata.");
        var context = root[1][index];
        var price = referencePrice is > 0m ? referencePrice.Value
            : Decimal(context.TryGetProperty("midPx", out var mid) && mid.ValueKind != JsonValueKind.Null ? mid : context.GetProperty("markPx"));
        var sizeDecimals = asset.Value.GetProperty("szDecimals").GetInt32();
        var quantityStep = PowerOfTen(-sizeDecimals);
        var (makerFeeRate, takerFeeRate, feeSource) = await GetUserFeeRates(userAddress, ct);
        return new ExchangeInstrumentMetadata(coin, "TESTNET", index, sizeDecimals, price,
            HyperliquidWireCodec.TickSize(price, sizeDecimals), quantityStep, quantityStep, MinimumOrderNotional, 500,
            makerFeeRate, takerFeeRate, feeSource, DateTimeOffset.UtcNow);
    }

    private async Task<(decimal Maker, decimal Taker, string Source)> GetUserFeeRates(string? userAddress, CancellationToken ct)
    {
        const decimal defaultMaker = .00015m, defaultTaker = .00045m;
        if (string.IsNullOrWhiteSpace(userAddress)) return (defaultMaker, defaultTaker, "HYPERLIQUID_DEFAULT");
        try
        {
            using var response = await httpClient.PostAsJsonAsync(Endpoint(), new { type = "userFees", user = userAddress }, ct);
            if (!response.IsSuccessStatusCode) return (defaultMaker, defaultTaker, "HYPERLIQUID_DEFAULT");
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = document.RootElement;
            if (!root.TryGetProperty("userAddRate", out var add) || !root.TryGetProperty("userCrossRate", out var cross))
                return (defaultMaker, defaultTaker, "HYPERLIQUID_DEFAULT");
            return (Decimal(add), Decimal(cross), "HYPERLIQUID_USER_FEES");
        }
        catch (HttpRequestException) { return (defaultMaker, defaultTaker, "HYPERLIQUID_DEFAULT"); }
        catch (JsonException) { return (defaultMaker, defaultTaker, "HYPERLIQUID_DEFAULT"); }
    }

    private Uri Endpoint()
    {
        var configured = configuration["Hyperliquid:InfoUrl"] ?? "https://api.hyperliquid-testnet.xyz/info";
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
            endpoint.Host != "api.hyperliquid-testnet.xyz" || endpoint.AbsolutePath != "/info")
            throw new TradingProblemException(403, "TESTNET_ONLY", "The read-only client only accepts the official Hyperliquid Testnet info endpoint.");
        return endpoint;
    }

    private static decimal? OptionalDecimal(JsonElement context, string property) =>
        context.ValueKind == JsonValueKind.Object && context.TryGetProperty(property, out var value) &&
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number &&
        decimal.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;

    private static decimal Decimal(JsonElement value) => decimal.Parse(value.GetString() ?? value.ToString(), System.Globalization.CultureInfo.InvariantCulture);
    private static decimal PowerOfTen(int exponent)
    {
        var value = 1m;
        for (var i = 0; i < Math.Abs(exponent); i++) value = exponent < 0 ? value / 10m : value * 10m;
        return value;
    }
    private static string ToCoin(string symbol)
    {
        var value = symbol.Trim().ToUpperInvariant();
        return value.EndsWith("USDT") || value.EndsWith("USDC") ? value[..^4].TrimEnd('-', '_', '/') : value;
    }
}
