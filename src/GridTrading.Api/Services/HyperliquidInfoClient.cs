using System.Net.Http.Json;
using System.Text.Json;

namespace GridTrading.Api.Services;

public sealed class HyperliquidInfoClient(HttpClient httpClient, IConfiguration configuration)
{
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
        var universe = root[0].GetProperty("universe").EnumerateArray().Select((item, index) => new
        {
            assetIndex = index, symbol = item.GetProperty("name").GetString(),
            sizeDecimals = item.GetProperty("szDecimals").GetInt32(), isDelisted = item.TryGetProperty("isDelisted", out var delisted) && delisted.GetBoolean()
        }).ToArray();
        return new { exchange = "HYPERLIQUID", environment = "TESTNET", tradingEnabled = false, asOf = DateTimeOffset.UtcNow, universe };
    }
}
