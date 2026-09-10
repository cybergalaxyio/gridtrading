namespace GridTrading.Api.Services;

/// <summary>Network is selected from the account or explicit market request, never inferred from a URL.</summary>
public static class HyperliquidNetwork
{
    public const string Testnet = "TESTNET";
    public const string Mainnet = "MAINNET";

    public static string Validate(string network) => network is Testnet or Mainnet ? network
        : throw new TradingProblemException(403, "HYPERLIQUID_NETWORK_INVALID", "Unsupported Hyperliquid network.");

    public static string EnvironmentId(string network) => Validate(network) == Mainnet
        ? "hyperliquid-mainnet" : "hyperliquid-testnet";

    public static Uri Endpoint(IConfiguration configuration, string network, string kind)
    {
        Validate(network);
        var (scheme, path) = kind switch
        {
            "Info" => ("https", "/info"), "Exchange" => ("https", "/exchange"),
            "WebSocket" => ("wss", "/ws"), _ => throw new ArgumentException("Unknown endpoint kind.", nameof(kind))
        };
        var host = network == Mainnet ? "api.hyperliquid.xyz" : "api.hyperliquid-testnet.xyz";
        var key = network == Mainnet ? $"Hyperliquid:Mainnet:{kind}Url" : $"Hyperliquid:{kind}Url";
        var configured = configuration[key] ?? $"{scheme}://{host}{path}";
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) || uri.Scheme != scheme ||
            uri.Host != host || uri.Port != 443 || uri.AbsolutePath != path ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new TradingProblemException(403, "HYPERLIQUID_ENDPOINT_MISMATCH",
                $"{network} {kind} must use the official {scheme}://{host}{path} endpoint.");
        return uri;
    }

}
