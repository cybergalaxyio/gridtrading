using System.Globalization;
using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Exchange;
using GridTrading.Api.Services;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Exchanges.Hyperliquid;

public sealed class HyperliquidExecutionAdapter(
    TradingDbContext db,
    HyperliquidTradingClient client,
    HyperliquidInfoClient instruments,
    HyperliquidOrderOwnershipService ownership) : IExecutionAdapter
{
    public ExecutionEnvironmentDescriptor Environment { get; } =
        new(ExecutionEnvironmentIds.HyperliquidTestnet, "HYPERLIQUID", "TESTNET", "Hyperliquid Testnet");

    public async Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct) =>
        await db.HyperliquidAccounts.AsNoTracking()
            .Where(x => x.Enabled && x.Environment == "TESTNET")
            .Select(x => new ExecutionAccountDescriptor(x.Id, ExecutionEnvironmentIds.HyperliquidTestnet, x.Name, x.Enabled))
            .ToListAsync(ct);

    public async Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection selection, string symbol, decimal? referencePrice, CancellationToken ct)
    {
        var account = await AccountAsync(selection, ct);
        var metadata = await instruments.GetPerpetualInstrument(symbol, referencePrice, account.AccountAddress, ct);
        return new ExecutionInstrument(metadata.Symbol, Environment.Id, metadata.AssetIndex, metadata.SizeDecimals,
            metadata.ReferencePrice, metadata.TickSize, metadata.QuantityStep, metadata.MinOrderQuantity,
            metadata.MinOrderNotional, metadata.MaxActiveOrders, metadata.MakerFeeRate, metadata.TakerFeeRate,
            metadata.FeeSource, metadata.AsOf);
    }

    public async Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection selection, string symbol, CancellationToken ct)
    {
        await AccountAsync(selection, ct);
        var book = await client.GetBookAsync(symbol, ct);
        return new ExecutionQuote(book.Bid, book.Ask, book.Mid, book.AsOf);
    }

    public async Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection selection, string symbol, CancellationToken ct)
    {
        await AccountAsync(selection, ct);
        var check = await client.PreflightAsync(selection.AccountId, symbol, ct);
        if (!check.AgentApproved)
            throw new TradingProblemException(412, "API_WALLET_NOT_APPROVED", "The configured API Wallet is not approved as an agent on Hyperliquid Testnet.");
        if (check.TradingEquity <= 0m)
            throw new TradingProblemException(412, "TESTNET_ACCOUNT_UNFUNDED", $"The Hyperliquid Testnet {check.AccountMode} account has no Trading Equity.");
        if (check.AvailableBalance <= 0m)
            throw new TradingProblemException(412, "TESTNET_ACCOUNT_NO_AVAILABLE_BALANCE", "The Hyperliquid Testnet account has no available balance.");
        if (check.NetPosition != 0m)
            throw new TradingProblemException(409, "TESTNET_POSITION_NOT_FLAT", $"Start is blocked because the actual {symbol} position is {check.NetPosition}.");
        if (check.OpenOrderCount != 0)
        {
            using var openOrders = await client.GetOpenOrdersAsync(selection.AccountId, ct);
            var tracked = await ownership.CountTrackedOpenOrdersAsync(selection.AccountId, symbol, null, openOrders.RootElement, ct);
            if (tracked != 0)
                throw new TradingProblemException(409, "TESTNET_OPEN_ORDERS_EXIST", $"Start is blocked because {tracked} tracked strategy order(s) remain open.");
        }
        return await GetQuoteAsync(selection, symbol, ct);
    }

    public async Task PlaceOrdersAsync(ExecutionSelection selection, GridConfiguration config, IEnumerable<OrderEntity> orders, CancellationToken ct)
    {
        await AccountAsync(selection, ct);
        foreach (var order in orders.Where(x => x.Status == "PENDING_EXCHANGE").ToArray())
        {
            var result = await client.PlaceLimitAsync(selection.AccountId, order.Symbol, order.Side == "BUY", order.Price,
                order.Quantity - order.FilledQuantity, order.Kind == "ENTRY" ? config.PostOnlyEntries : config.PostOnlyTakeProfits,
                order.ClientOrderId, ct);
            if (result.Status == "REJECTED" && order.Kind == "TAKE_PROFIT" && config.PostOnlyTakeProfits)
                result = await client.PlaceLimitAsync(selection.AccountId, order.Symbol, order.Side == "BUY", order.Price,
                    order.Quantity - order.FilledQuantity, false, order.ClientOrderId, ct);
            if (result.Status == "REJECTED")
            {
                var rejectedAt = DateTimeOffset.UtcNow;
                var venueError = result.Error ?? "Hyperliquid rejected an order.";
                order.Status = "REJECTED";
                order.UpdatedAt = rejectedAt;
                if (order.Kind == "ENTRY")
                {
                    var warningCutoff = rejectedAt.AddMinutes(-1);
                    var priorWarnings = await db.RiskAlerts.Where(x => x.CycleId == order.CycleId &&
                        x.Code == "ENTRY_ORDER_REJECTED").Select(x => x.CreatedAt).ToListAsync(ct);
                    var recentWarningExists = priorWarnings.Any(x => x >= warningCutoff);
                    if (!recentWarningExists)
                        db.RiskAlerts.Add(new RiskAlertEntity
                        {
                            Id = Ids.New("alert"), CycleId = order.CycleId, Severity = "WARNING",
                            Code = "ENTRY_ORDER_REJECTED",
                            Message = $"Entry {(order.Side == "BUY" ? "B" : "S")}{order.GridLevel} was rejected and will be retried on the next Sync. Venue: {venueError}",
                            CreatedAt = rejectedAt
                        });
                    await db.SaveChangesAsync(ct);
                    continue;
                }
                await db.SaveChangesAsync(ct);
                throw new TradingProblemException(422, "PROTECTIVE_ORDER_REJECTED", venueError);
            }
            order.ExchangeOrderId = result.ExchangeOrderId ?? result.Cloid;
            order.Status = result.Status switch
            {
                "WAITING" => "PENDING_EXCHANGE",
                "UNKNOWN" => "UNKNOWN",
                "FILLED" => "PARTIALLY_FILLED",
                _ => "NEW"
            };
            order.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task CancelOrdersAsync(ExecutionSelection selection, IEnumerable<OrderEntity> orders, CancellationToken ct)
    {
        await AccountAsync(selection, ct);
        foreach (var order in orders.Where(IsActive).ToArray())
        {
            await client.CancelByCloidAsync(selection.AccountId, order.Symbol, order.ClientOrderId, ct);
            order.Status = "CANCELLED";
            order.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<decimal> FlattenAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config, CancellationToken ct)
    {
        await AccountAsync(selection, ct);
        var active = await db.Orders.Where(x => x.CycleId == cycle.Id && x.Kind != "FLATTEN" &&
            (x.Status == "PENDING_EXCHANGE" || x.Status == "NEW" || x.Status == "PARTIALLY_FILLED" ||
             x.Status == "UNKNOWN")).ToListAsync(ct);
        await CancelOrdersAsync(selection, active, ct);
        using var openOrders = await client.GetOpenOrdersAsync(selection.AccountId, ct);
        var remainingOrders = await ownership.CountTrackedOpenOrdersAsync(selection.AccountId, config.Symbol, cycle.Id,
            openOrders.RootElement, ct);
        if (remainingOrders != 0)
            throw new TradingProblemException(503, "CANCEL_INCOMPLETE",
                $"Close is blocked because {remainingOrders} tracked strategy order(s) remain open.");

        // Hyperliquid accounts are netted and may contain exposure owned outside this Cycle.
        // Flatten only the position reconstructed from fills belonging to this strategy.
        var strategyPosition = cycle.ReconstructedNetQuantity;
        if (strategyPosition == 0m) return 0m;

        var quote = await GetQuoteAsync(selection, config.Symbol, ct);
        var isBuy = strategyPosition < 0m;
        var price = isBuy ? quote.Ask * 1.02m : quote.Bid * .98m;
        var order = new OrderEntity
        {
            Id = Ids.New("order"), CycleId = cycle.Id, ClientOrderId = Ids.New("flatten"), ExchangeOrderId = "pending",
            Symbol = config.Symbol, Side = isBuy ? "BUY" : "SELL", Kind = "FLATTEN", Status = "PENDING_EXCHANGE",
            GridLevel = -1, Price = price, Quantity = Math.Abs(strategyPosition),
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);

        // Deliberately not Reduce-only: an external position on the same netted account
        // must not prevent closing this strategy's independently attributed exposure.
        var result = await client.PlaceLimitAsync(selection.AccountId, config.Symbol, isBuy, price, order.Quantity,
            false, order.ClientOrderId, ct, immediateOrCancel: true);
        order.ExchangeOrderId = result.ExchangeOrderId ?? result.Cloid;
        if (result.Status == "REJECTED")
        {
            order.Status = "REJECTED";
            order.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            throw new TradingProblemException(422, "FLATTEN_ORDER_REJECTED",
                result.Error ?? "Hyperliquid rejected the strategy flatten order.");
        }

        var filled = Math.Min(order.Quantity, result.FilledQuantity);
        if (result.Status == "FILLED" && filled == 0m) filled = order.Quantity;
        order.FilledQuantity = filled;
        order.Status = filled >= order.Quantity ? "FILLED" : filled > 0m ? "PARTIALLY_FILLED" : "CANCELLED";
        order.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return isBuy ? strategyPosition + filled : strategyPosition - filled;
    }

    public async Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection selection, CycleEntity cycle,
        GridConfiguration config, CancellationToken ct)
    {
        await AccountAsync(selection, ct);
        using var fillsDocument = await client.GetUserFillsAsync(selection.AccountId,
            Math.Max(0, cycle.LastReconciledAt.AddSeconds(-5).ToUnixTimeMilliseconds()), ct);
        var fills = fillsDocument.RootElement.ValueKind == JsonValueKind.Array
            ? await NormalizeFillsAsync(selection.AccountId, fillsDocument.RootElement.EnumerateArray().ToArray(), ct)
            : [];

        using var fundingDocument = await client.GetUserFundingAsync(selection.AccountId,
            Math.Max(0, cycle.LastReconciledAt.AddSeconds(-5).ToUnixTimeMilliseconds()), ct);
        var fundingPayments = fundingDocument.RootElement.ValueKind == JsonValueKind.Array
            ? NormalizeFundingPayments(selection.AccountId, fundingDocument.RootElement.EnumerateArray().ToArray())
            : [];

        using var openDocument = await client.GetOpenOrdersAsync(selection.AccountId, ct);
        var openByClientId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (openDocument.RootElement.ValueKind == JsonValueKind.Array)
        {
            var local = await db.Orders.Where(x => x.CycleId == cycle.Id).ToListAsync(ct);
            foreach (var row in openDocument.RootElement.EnumerateArray())
            {
                var oid = ReadString(row, "oid");
                var cloid = ReadString(row, "cloid");
                var order = local.FirstOrDefault(x => x.ExchangeOrderId == oid ||
                    (!string.IsNullOrWhiteSpace(cloid) && HyperliquidWireCodec.CreateCloid(x.ClientOrderId) == cloid));
                if (order is not null) openByClientId[order.ClientOrderId] = oid;
            }
        }

        var position = await client.GetPositionSnapshotAsync(selection.AccountId, config.Symbol, ct);
        return new ExecutionReconciliationSnapshot(fills, fundingPayments, [], openByClientId,
            new ExecutionPosition(position.Quantity, position.PositionValue, position.UnrealizedPnl));
    }

    public async Task<IReadOnlyList<NormalizedExecutionFill>> NormalizeFillsAsync(
        string accountId, IReadOnlyList<JsonElement> values, CancellationToken ct)
    {
        var candidates = await OwnedOrdersAsync(accountId, ct);
        var result = new List<NormalizedExecutionFill>();
        foreach (var fill in values)
        {
            var oid = ReadString(fill, "oid");
            var cloid = ReadString(fill, "cloid");
            var local = candidates.FirstOrDefault(x => x.ExchangeOrderId == oid ||
                (!string.IsNullOrWhiteSpace(cloid) && HyperliquidWireCodec.CreateCloid(x.ClientOrderId) == cloid));
            var time = ReadTime(fill);
            var id = $"hl:{ReadString(fill, "hash")}:{oid}:{time}:{ReadString(fill, "tid")}";
            result.Add(new NormalizedExecutionFill(id, oid, local?.ClientOrderId,
                ReadString(fill, "side") == "B" ? "BUY" : "SELL", ReadDecimal(fill, "px"),
                ReadDecimal(fill, "sz"), Math.Abs(ReadDecimal(fill, "fee")), DateTimeOffset.FromUnixTimeMilliseconds(time)));
        }
        return result;
    }

    public static IReadOnlyList<NormalizedFundingPayment> NormalizeFundingPayments(
        string accountId, IReadOnlyList<JsonElement> values)
    {
        var result = new List<NormalizedFundingPayment>();
        foreach (var value in values)
        {
            var details = value.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                ? delta : value;
            if (details.TryGetProperty("type", out var type) &&
                !string.Equals(type.GetString(), "funding", StringComparison.OrdinalIgnoreCase)) continue;
            var coin = ReadString(details, "coin");
            if (string.IsNullOrWhiteSpace(coin)) continue;
            var time = value.TryGetProperty("time", out var rootTime) && rootTime.TryGetInt64(out var rootMilliseconds)
                ? rootMilliseconds
                : details.TryGetProperty("time", out var detailTime) && detailTime.TryGetInt64(out var detailMilliseconds)
                    ? detailMilliseconds : 0L;
            if (time <= 0) continue;
            var id = $"hl-funding:{accountId}:{coin.ToUpperInvariant()}:{time}";
            result.Add(new NormalizedFundingPayment(id, coin, ReadDecimal(details, "usdc"),
                ReadDecimal(details, "szi"), ReadDecimal(details, "fundingRate"),
                DateTimeOffset.FromUnixTimeMilliseconds(time)));
        }
        return result;
    }

    public async Task<IReadOnlyList<NormalizedOrderUpdate>> NormalizeOrderUpdatesAsync(
        string accountId, IReadOnlyList<JsonElement> values, CancellationToken ct)
    {
        var candidates = await OwnedOrdersAsync(accountId, ct);
        var result = new List<NormalizedOrderUpdate>();
        foreach (var update in values)
        {
            if (!update.TryGetProperty("order", out var details)) continue;
            var oid = ReadString(details, "oid");
            var cloid = ReadString(details, "cloid");
            var local = candidates.FirstOrDefault(x => x.ExchangeOrderId == oid ||
                (!string.IsNullOrWhiteSpace(cloid) && HyperliquidWireCodec.CreateCloid(x.ClientOrderId) == cloid));
            var filled = Math.Max(0m, ReadDecimal(details, "origSz") - ReadDecimal(details, "sz"));
            var status = MapStatus(ReadString(update, "status"), local?.FilledQuantity ?? filled);
            var occurredAt = update.TryGetProperty("statusTimestamp", out var timestamp) && timestamp.TryGetInt64(out var milliseconds)
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : DateTimeOffset.UtcNow;
            result.Add(new NormalizedOrderUpdate(oid, local?.ClientOrderId, status, filled, occurredAt));
        }
        return result;
    }

    private async Task<List<OrderEntity>> OwnedOrdersAsync(string accountId, CancellationToken ct) =>
        await (from order in db.Orders
               join cycle in db.Cycles on order.CycleId equals cycle.Id
               where cycle.ExecutionAccountId == accountId && !cycle.IsTerminal
               select order).ToListAsync(ct);

    private async Task<HyperliquidAccountEntity> AccountAsync(ExecutionSelection selection, CancellationToken ct)
    {
        if (selection.EnvironmentId != Environment.Id)
            throw new TradingProblemException(422, "EXECUTION_SELECTION_MISMATCH", "The Hyperliquid adapter received a mismatched environment.");
        return await db.HyperliquidAccounts.SingleOrDefaultAsync(x =>
                x.Id == selection.AccountId && x.Enabled && x.Environment == "TESTNET", ct)
            ?? throw new TradingProblemException(404, "EXECUTION_ACCOUNT_NOT_FOUND", "Hyperliquid Testnet account was not found or disabled.");
    }

    private static string MapStatus(string exchangeStatus, decimal filledQuantity)
    {
        var status = exchangeStatus.Trim().ToLowerInvariant();
        if (status == "open") return filledQuantity > 0m ? "PARTIALLY_FILLED" : "NEW";
        if (status == "filled") return "FILLED";
        if (status.Contains("rejected", StringComparison.Ordinal)) return "REJECTED";
        if (status == "canceled" || status.EndsWith("canceled", StringComparison.Ordinal) || status == "scheduledcancel")
            return "CANCELLED";
        return "UNKNOWN";
    }

    private static bool IsActive(OrderEntity order) =>
        order.Status is "PENDING_EXCHANGE" or "NEW" or "PARTIALLY_FILLED" or "UNKNOWN";
    private static long ReadTime(JsonElement value) => value.GetProperty("time").GetInt64();
    private static string ReadString(JsonElement value, string name) => value.TryGetProperty(name, out var item) ? item.ToString() : "";
    private static decimal ReadDecimal(JsonElement value, string name) => value.TryGetProperty(name, out var item)
        ? decimal.Parse(item.ValueKind == JsonValueKind.String ? item.GetString()! : item.ToString(), CultureInfo.InvariantCulture) : 0m;
}
