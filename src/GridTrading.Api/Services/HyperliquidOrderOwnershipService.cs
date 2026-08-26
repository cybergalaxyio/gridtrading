using System.Text.Json;
using System.Text.Json.Nodes;
using GridTrading.Api.Data;
using GridTrading.Api.Exchange;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed record HyperliquidOrderOwner(string StrategyId, string StrategyName, string CycleId,
    string LocalOrderId, string Side, string Kind, int GridLevel);

public sealed class HyperliquidOrderOwnershipService(TradingDbContext db)
{
    public async Task<int> CountTrackedOpenOrdersAsync(string accountId, string symbol, string? cycleId,
        JsonElement exchangeOrders, CancellationToken ct)
    {
        if (exchangeOrders.ValueKind != JsonValueKind.Array) return 0;
        var lookup = await BuildLookupAsync(accountId, cycleId, symbol, ct);
        return exchangeOrders.EnumerateArray().Count(order => lookup.Match(order) is not null);
    }

    public async Task<JsonArray> AnnotateOpenOrdersAsync(string accountId, JsonElement exchangeOrders, CancellationToken ct)
    {
        var result = new JsonArray();
        if (exchangeOrders.ValueKind != JsonValueKind.Array) return result;
        var lookup = await BuildLookupAsync(accountId, null, null, ct);
        foreach (var order in exchangeOrders.EnumerateArray())
        {
            var row = JsonNode.Parse(order.GetRawText())!.AsObject();
            AddOwnership(row, lookup.Match(order));
            result.Add(row);
        }
        return result;
    }

    public async Task<JsonArray> AnnotateHistoricalOrdersAsync(string accountId, JsonElement exchangeOrders, CancellationToken ct)
    {
        var result = new JsonArray();
        if (exchangeOrders.ValueKind != JsonValueKind.Array) return result;
        var lookup = await BuildLookupAsync(accountId, null, null, ct);
        foreach (var item in exchangeOrders.EnumerateArray())
        {
            var row = JsonNode.Parse(item.GetRawText())!.AsObject();
            var order = item.TryGetProperty("order", out var nested) ? nested : item;
            AddOwnership(row, lookup.Match(order));
            result.Add(row);
        }
        return result;
    }

    private async Task<OwnershipLookup> BuildLookupAsync(string accountId, string? cycleId, string? symbol, CancellationToken ct)
    {
        var rows = await (from order in db.Orders.AsNoTracking()
            join cycle in db.Cycles.AsNoTracking() on order.CycleId equals cycle.Id
            join strategy in db.Strategies.AsNoTracking() on cycle.StrategyId equals strategy.Id
            where cycle.ExecutionAccountId == accountId && (cycleId == null || cycle.Id == cycleId)
            select new
            {
                order.Id, order.CycleId, order.ClientOrderId, order.ExchangeOrderId, order.Symbol, order.Side,
                order.Kind, order.GridLevel, StrategyId = strategy.Id, StrategyName = strategy.Name
            }).ToListAsync(ct);

        if (!string.IsNullOrWhiteSpace(symbol))
            rows = rows.Where(row => SameCoin(row.Symbol, symbol)).ToList();

        var byOid = new Dictionary<string, HyperliquidOrderOwner>(StringComparer.OrdinalIgnoreCase);
        var byCloid = new Dictionary<string, HyperliquidOrderOwner>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var owner = new HyperliquidOrderOwner(row.StrategyId, row.StrategyName, row.CycleId, row.Id,
                row.Side, row.Kind, row.GridLevel);
            if (!string.IsNullOrWhiteSpace(row.ExchangeOrderId) && row.ExchangeOrderId != "pending")
                byOid[row.ExchangeOrderId] = owner;
            byCloid[HyperliquidWireCodec.CreateCloid(row.ClientOrderId)] = owner;
        }
        return new OwnershipLookup(byOid, byCloid);
    }

    private static void AddOwnership(JsonObject row, HyperliquidOrderOwner? owner)
    {
        row["orderSource"] = owner is null ? "EXTERNAL" : "STRATEGY";
        row["strategyId"] = owner?.StrategyId;
        row["strategyName"] = owner?.StrategyName;
        row["cycleId"] = owner?.CycleId;
        row["localOrderId"] = owner?.LocalOrderId;
        row["gridLevel"] = owner?.GridLevel;
        row["orderKind"] = owner?.Kind;
        row["levelLabel"] = owner is null ? null : LevelLabel(owner);
    }

    private static string? LevelLabel(HyperliquidOrderOwner owner)
    {
        if (owner.GridLevel < 0) return null;
        var isTakeProfit = owner.Kind.Equals("TAKE_PROFIT", StringComparison.OrdinalIgnoreCase);
        var isBuy = owner.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase);
        var entrySide = isTakeProfit
            ? isBuy ? "S" : "B"
            : isBuy ? "B" : "S";
        return $"{entrySide}{owner.GridLevel}{(isTakeProfit ? "-TP" : "")}";
    }

    private static bool SameCoin(string left, string right) =>
        Coin(left).Equals(Coin(right), StringComparison.OrdinalIgnoreCase);

    private static string Coin(string symbol)
    {
        var value = symbol.Trim();
        foreach (var quote in new[] { "USDT", "USDC" })
            if (value.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
                return value[..^quote.Length].TrimEnd('-', '_', '/');
        return value;
    }

    private sealed class OwnershipLookup(
        IReadOnlyDictionary<string, HyperliquidOrderOwner> byOid,
        IReadOnlyDictionary<string, HyperliquidOrderOwner> byCloid)
    {
        public HyperliquidOrderOwner? Match(JsonElement order)
        {
            if (order.TryGetProperty("oid", out var oid) && byOid.TryGetValue(oid.ToString(), out var oidOwner))
                return oidOwner;
            if (order.TryGetProperty("cloid", out var cloid) && cloid.ValueKind == JsonValueKind.String &&
                cloid.GetString() is { Length: > 0 } value && byCloid.TryGetValue(value, out var cloidOwner))
                return cloidOwner;
            return null;
        }
    }
}
