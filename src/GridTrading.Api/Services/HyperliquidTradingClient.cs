using System.Net.Http.Json;
using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Exchange;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed record HyperliquidPositionSnapshot(decimal Quantity, decimal PositionValue, decimal UnrealizedPnl);
public sealed record HyperliquidOrderResult(string Status, string? ExchangeOrderId, string Cloid, string? Error, decimal FilledQuantity = 0m);
public sealed record HyperliquidPreflight(bool AgentApproved, decimal NetPosition, int OpenOrderCount,
    decimal TradingEquity, decimal AvailableBalance, decimal PerpAccountValue, string AccountMode,
    string AgentRole, DateTimeOffset AsOf);
public sealed record HyperliquidBook(decimal Bid, decimal Ask, decimal Mid, DateTimeOffset AsOf);
public sealed record HyperliquidTradingFunds(string AccountMode, decimal TradingEquity, decimal AvailableBalance,
    decimal PerpAccountValue);

public static class HyperliquidAccountFunds
{
    public static HyperliquidTradingFunds Resolve(JsonElement abstraction, JsonElement perpState, JsonElement spotState)
    {
        var accountMode = AccountMode(abstraction);
        var perpAccountValue = PropertyDecimal(perpState, "marginSummary", "accountValue");
        var perpWithdrawable = PropertyDecimal(perpState, "withdrawable");
        var usesUnifiedBalance = accountMode is "unifiedAccount" or "portfolioMargin";
        if (!usesUnifiedBalance)
            return new(accountMode, perpAccountValue, Math.Max(0m, perpWithdrawable), perpAccountValue);

        var total = 0m;
        var hold = 0m;
        if (spotState.TryGetProperty("balances", out var balances) && balances.ValueKind == JsonValueKind.Array)
        {
            foreach (var balance in balances.EnumerateArray())
            {
                if (!balance.TryGetProperty("coin", out var coin) ||
                    !string.Equals(coin.GetString(), "USDC", StringComparison.OrdinalIgnoreCase)) continue;
                total = PropertyDecimal(balance, "total");
                hold = PropertyDecimal(balance, "hold");
                break;
            }
        }
        return new(accountMode, total, Math.Max(0m, total - hold), perpAccountValue);
    }

    private static string AccountMode(JsonElement abstraction)
    {
        if (abstraction.ValueKind == JsonValueKind.String)
            return abstraction.GetString() ?? "disabled";
        if (abstraction.ValueKind == JsonValueKind.Object &&
            abstraction.TryGetProperty("abstraction", out var value) && value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "disabled";
        return "disabled";
    }

    private static decimal PropertyDecimal(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) ? Decimal(value) : 0m;

    private static decimal PropertyDecimal(JsonElement root, string parent, string property) =>
        root.TryGetProperty(parent, out var nested) ? PropertyDecimal(nested, property) : 0m;

    private static decimal Decimal(JsonElement value) =>
        decimal.TryParse(value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString(),
            System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed : 0m;
}

public sealed class HyperliquidTradingClient(HttpClient http, IConfiguration configuration, TradingDbContext db,
    CredentialProtector protector, HyperliquidNonceManager nonces, HyperliquidL1Signer signer)
{
    private Uri InfoEndpoint => LockedEndpoint("Hyperliquid:InfoUrl", "https://api.hyperliquid-testnet.xyz/info", "/info");
    private Uri ExchangeEndpoint => LockedEndpoint("Hyperliquid:ExchangeUrl", "https://api.hyperliquid-testnet.xyz/exchange", "/exchange");

    public async Task<HyperliquidPreflight> PreflightAsync(string accountId, string? symbol, CancellationToken ct)
    {
        var account = await Account(accountId, ct);
        using var role = await PostInfo(new { type = "userRole", user = account.AgentAddress }, ct);
        var roleName = role.RootElement.TryGetProperty("role", out var roleValue) ? roleValue.GetString() ?? "missing" : "missing";
        var approved = roleName.Equals("agent", StringComparison.OrdinalIgnoreCase) &&
            role.RootElement.TryGetProperty("data", out var roleData) && roleData.TryGetProperty("user", out var approvedUser) &&
            string.Equals(approvedUser.GetString(), account.AccountAddress, StringComparison.OrdinalIgnoreCase);
        using var state = await PostInfo(new { type = "clearinghouseState", user = account.AccountAddress }, ct);
        using var spotState = await PostInfo(new { type = "spotClearinghouseState", user = account.AccountAddress }, ct);
        using var abstraction = await PostInfo(new { type = "userAbstraction", user = account.AccountAddress }, ct);
        using var orders = await PostInfo(new { type = "openOrders", user = account.AccountAddress }, ct);
        var net = 0m;
        if (state.RootElement.TryGetProperty("assetPositions", out var positions))
        {
            foreach (var item in positions.EnumerateArray())
            {
                var position = item.GetProperty("position");
                if (symbol is not null && !position.GetProperty("coin").GetString()!.Equals(ToCoin(symbol), StringComparison.OrdinalIgnoreCase)) continue;
                net += ParseDecimal(position.GetProperty("szi"));
            }
        }
        var funds = HyperliquidAccountFunds.Resolve(abstraction.RootElement, state.RootElement, spotState.RootElement);
        var count = orders.RootElement.ValueKind == JsonValueKind.Array
            ? orders.RootElement.EnumerateArray().Count(x => symbol is null || x.GetProperty("coin").GetString()!.Equals(ToCoin(symbol), StringComparison.OrdinalIgnoreCase)) : 0;
        return new HyperliquidPreflight(approved, net, count, funds.TradingEquity, funds.AvailableBalance,
            funds.PerpAccountValue, funds.AccountMode, roleName, DateTimeOffset.UtcNow);
    }

    public async Task<HyperliquidOrderResult> PlaceLimitAsync(string accountId, string symbol, bool isBuy, decimal price,
        decimal size, bool postOnly, string stableClientOrderId, CancellationToken ct, bool immediateOrCancel = false)
    {
        var account = await Account(accountId, ct);
        var (asset, sizeDecimals) = await ResolveAsset(symbol, ct);
        var cloid = HyperliquidWireCodec.CreateCloid(stableClientOrderId);
        var order = new HyperliquidLimitOrder(asset, isBuy, HyperliquidWireCodec.PriceToWire(price, sizeDecimals),
            HyperliquidWireCodec.SizeToWire(size, sizeDecimals), false, immediateOrCancel ? "Ioc" : postOnly ? "Alo" : "Gtc", cloid);
        var actionBytes = HyperliquidWireCodec.PackOrderAction([order]);
        var action = new Dictionary<string, object>
        {
            ["type"] = "order",
            ["orders"] = new[] { new Dictionary<string, object> { ["a"] = order.Asset, ["b"] = order.IsBuy, ["p"] = order.Price,
                ["s"] = order.Size, ["r"] = order.ReduceOnly, ["t"] = new Dictionary<string, object> { ["limit"] = new Dictionary<string, object> { ["tif"] = order.Tif } }, ["c"] = order.Cloid } },
            ["grouping"] = "na"
        };
        return await SendOrderAction(account, action, actionBytes, cloid, ct);
    }

    public async Task<HyperliquidOrderResult> ModifyLimitAsync(string accountId, string symbol, bool isBuy, decimal price,
        decimal size, bool postOnly, string stableClientOrderId, CancellationToken ct)
    {
        var account = await Account(accountId, ct);
        var (asset, sizeDecimals) = await ResolveAsset(symbol, ct);
        var cloid = HyperliquidWireCodec.CreateCloid(stableClientOrderId);
        var order = new HyperliquidLimitOrder(asset, isBuy, HyperliquidWireCodec.PriceToWire(price, sizeDecimals),
            HyperliquidWireCodec.SizeToWire(size, sizeDecimals), false, postOnly ? "Alo" : "Gtc", cloid);
        var actionBytes = HyperliquidWireCodec.PackModifyAction(cloid, order);
        var action = new Dictionary<string, object>
        {
            ["type"] = "modify",
            ["oid"] = cloid,
            ["order"] = new Dictionary<string, object> { ["a"] = order.Asset, ["b"] = order.IsBuy,
                ["p"] = order.Price, ["s"] = order.Size, ["r"] = order.ReduceOnly,
                ["t"] = new Dictionary<string, object> { ["limit"] = new Dictionary<string, object> { ["tif"] = order.Tif } },
                ["c"] = order.Cloid }
        };
        return await SendOrderAction(account, action, actionBytes, cloid, ct);
    }

    public async Task CancelByCloidAsync(string accountId, string symbol, string stableClientOrderId, CancellationToken ct)
    {
        var account = await Account(accountId, ct); var (asset, _) = await ResolveAsset(symbol, ct);
        var cloid = HyperliquidWireCodec.CreateCloid(stableClientOrderId);
        var actionBytes = HyperliquidWireCodec.PackCancelByCloidAction(asset, cloid);
        var action = new Dictionary<string, object> { ["type"] = "cancelByCloid",
            ["cancels"] = new[] { new Dictionary<string, object> { ["asset"] = asset, ["cloid"] = cloid } } };
        var result = await Send(account, action, actionBytes, ct);
        EnsureOk(result.RootElement, "cancel");
    }

    public async Task<JsonDocument> GetOpenOrdersAsync(string accountId, CancellationToken ct)
    {
        var account = await Account(accountId, ct);
        return await PostInfo(new { type = "openOrders", user = account.AccountAddress }, ct);
    }

    public async Task<JsonDocument> GetClearinghouseStateAsync(string accountId, CancellationToken ct)
    {
        var account = await Account(accountId, ct);
        return await PostInfo(new { type = "clearinghouseState", user = account.AccountAddress }, ct);
    }

    public async Task<JsonDocument> GetSpotClearinghouseStateAsync(string accountId, CancellationToken ct)
    {
        var account = await Account(accountId, ct);
        return await PostInfo(new { type = "spotClearinghouseState", user = account.AccountAddress }, ct);
    }

    public async Task<JsonDocument> GetFrontendOpenOrdersAsync(string accountId, CancellationToken ct)
    {
        var account = await Account(accountId, ct);
        return await PostInfo(new { type = "frontendOpenOrders", user = account.AccountAddress }, ct);
    }

    public async Task<JsonDocument> GetHistoricalOrdersAsync(string accountId, CancellationToken ct)
    {
        var account = await Account(accountId, ct);
        return await PostInfo(new { type = "historicalOrders", user = account.AccountAddress }, ct);
    }

    public async Task<JsonDocument> GetUserFillsAsync(string accountId, long startTime, CancellationToken ct)
    {
        var account = await Account(accountId, ct);
        return await PostInfo(new { type = "userFillsByTime", user = account.AccountAddress, startTime, aggregateByTime = false }, ct);
    }

    public async Task<JsonDocument> GetUserFundingAsync(string accountId, long startTime, CancellationToken ct)
    {
        var account = await Account(accountId, ct);
        return await PostInfo(new { type = "userFunding", user = account.AccountAddress, startTime }, ct);
    }

    public async Task<int> GetOpenOrderCountAsync(string accountId, string symbol, CancellationToken ct)
    {
        using var orders = await GetOpenOrdersAsync(accountId, ct);
        return orders.RootElement.ValueKind == JsonValueKind.Array
            ? orders.RootElement.EnumerateArray().Count(x => x.GetProperty("coin").GetString()!.Equals(ToCoin(symbol), StringComparison.OrdinalIgnoreCase))
            : 0;
    }

    public async Task<decimal> GetPositionAsync(string accountId, string symbol, CancellationToken ct) =>
        (await GetPositionSnapshotAsync(accountId, symbol, ct)).Quantity;

    public async Task<HyperliquidPositionSnapshot> GetPositionSnapshotAsync(string accountId, string symbol, CancellationToken ct)
    {
        var account = await Account(accountId, ct);
        using var state = await PostInfo(new { type = "clearinghouseState", user = account.AccountAddress }, ct);
        if (!state.RootElement.TryGetProperty("assetPositions", out var positions)) return new(0m, 0m, 0m);
        foreach (var item in positions.EnumerateArray())
        {
            var position = item.GetProperty("position");
            if (position.GetProperty("coin").GetString()!.Equals(ToCoin(symbol), StringComparison.OrdinalIgnoreCase))
                return new(ParseDecimal(position.GetProperty("szi")),
                    ParseDecimal(position.GetProperty("positionValue")), ParseDecimal(position.GetProperty("unrealizedPnl")));
        }
        return new(0m, 0m, 0m);
    }

    public async Task<HyperliquidBook> GetBookAsync(string symbol, CancellationToken ct)
    {
        using var book = await PostInfo(new { type = "l2Book", coin = ToCoin(symbol), nSigFigs = 5 }, ct);
        var levels = book.RootElement.GetProperty("levels");
        var bid = ParseDecimal(levels[0][0].GetProperty("px"));
        var ask = ParseDecimal(levels[1][0].GetProperty("px"));
        return new HyperliquidBook(bid, ask, (bid + ask) / 2m, DateTimeOffset.UtcNow);
    }

    private async Task<HyperliquidOrderResult> SendOrderAction(HyperliquidAccountEntity account, object action, byte[] bytes, string cloid, CancellationToken ct)
    {
        using var result = await Send(account, action, bytes, ct);
        EnsureOk(result.RootElement, "order");
        var statuses = result.RootElement.GetProperty("response").GetProperty("data").GetProperty("statuses");
        return ParseOrderStatus(statuses[0], cloid);
    }

    public static HyperliquidOrderResult ParseOrderStatus(JsonElement status, string cloid)
    {
        if (status.ValueKind == JsonValueKind.String)
        {
            var value = status.GetString();
            if (value is "waitingForFill" or "waitingForTrigger")
                return new HyperliquidOrderResult("WAITING", null, cloid, null);
            return new HyperliquidOrderResult("UNKNOWN", null, cloid, value);
        }
        if (status.ValueKind != JsonValueKind.Object)
            return new HyperliquidOrderResult("UNKNOWN", null, cloid, status.ToString());
        if (status.TryGetProperty("error", out var error)) return new HyperliquidOrderResult("REJECTED", null, cloid, error.GetString());
        if (status.TryGetProperty("resting", out var resting)) return new HyperliquidOrderResult("RESTING", resting.GetProperty("oid").ToString(), cloid, null);
        if (status.TryGetProperty("filled", out var filled))
        {
            var quantity = filled.TryGetProperty("totalSz", out var totalSize) ? ParseDecimal(totalSize) : 0m;
            return new HyperliquidOrderResult("FILLED", filled.TryGetProperty("oid", out var oid) ? oid.ToString() : null, cloid, null, quantity);
        }
        return new HyperliquidOrderResult("UNKNOWN", null, cloid, status.ToString());
    }

    private async Task<JsonDocument> Send(HyperliquidAccountEntity account, object action, byte[] actionBytes, CancellationToken ct)
    {
        if (!account.Environment.Equals("TESTNET", StringComparison.Ordinal)) throw new TradingProblemException(403, "TESTNET_ONLY", "Only Hyperliquid Testnet is supported.");
        var nonce = await nonces.NextAsync(account.Id, ct);
        var signature = signer.SignTestnet(actionBytes, protector.Unprotect(account.EncryptedAgentPrivateKey), nonce, account.VaultAddress);
        using var response = await http.PostAsJsonAsync(ExchangeEndpoint, new { action, nonce, signature = new { r = signature.R, s = signature.S, v = signature.V }, vaultAddress = account.VaultAddress }, ct);
        var stream = await response.Content.ReadAsStreamAsync(ct);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!response.IsSuccessStatusCode) { document.Dispose(); throw new TradingProblemException(503, "EXCHANGE_HTTP_ERROR", $"Hyperliquid returned HTTP {(int)response.StatusCode}."); }
        return document;
    }

    private async Task<JsonDocument> PostInfo(object request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(InfoEndpoint, request, ct);
        if (!response.IsSuccessStatusCode) throw new TradingProblemException(503, "EXCHANGE_INFO_UNAVAILABLE", $"Hyperliquid Info returned HTTP {(int)response.StatusCode}.");
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private async Task<(int Asset, int SizeDecimals)> ResolveAsset(string symbol, CancellationToken ct)
    {
        using var meta = await PostInfo(new { type = "meta" }, ct);
        var coin = ToCoin(symbol); var index = 0;
        foreach (var item in meta.RootElement.GetProperty("universe").EnumerateArray())
        {
            if (item.GetProperty("name").GetString()!.Equals(coin, StringComparison.OrdinalIgnoreCase))
                return (index, item.GetProperty("szDecimals").GetInt32());
            index++;
        }
        throw new TradingProblemException(422, "INSTRUMENT_NOT_FOUND", $"{coin} is not listed in Hyperliquid Testnet metadata.");
    }

    private async Task<HyperliquidAccountEntity> Account(string id, CancellationToken ct) =>
        await db.HyperliquidAccounts.SingleOrDefaultAsync(x => x.Id == id && x.Enabled, ct)
        ?? throw new TradingProblemException(404, "TESTNET_ACCOUNT_NOT_FOUND", "Hyperliquid Testnet account was not found or disabled.");

    private Uri LockedEndpoint(string key, string fallback, string requiredPath)
    {
        var configured = configuration[key] ?? fallback;
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "api.hyperliquid-testnet.xyz" || uri.AbsolutePath != requiredPath)
            throw new TradingProblemException(403, "TESTNET_ONLY", "Hyperliquid endpoints are locked to the official HTTPS Testnet API.");
        return uri;
    }

    private static void EnsureOk(JsonElement root, string operation)
    {
        if (!root.TryGetProperty("status", out var status) || status.GetString() != "ok")
            throw new TradingProblemException(503, "EXCHANGE_ACTION_REJECTED", $"Hyperliquid rejected the {operation} action: {root}.");
    }
    private static decimal ParseDecimal(JsonElement value) => decimal.Parse(value.GetString() ?? value.ToString(), System.Globalization.CultureInfo.InvariantCulture);
    private static string ToCoin(string symbol) => symbol.EndsWith("USDT", StringComparison.OrdinalIgnoreCase) ? symbol[..^4] : symbol;
}
