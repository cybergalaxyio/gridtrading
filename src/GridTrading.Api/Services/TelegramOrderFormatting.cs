using GridTrading.Api.Data;
using GridTrading.Api.Execution;

namespace GridTrading.Api.Services;

internal static class TelegramOrderFormatting
{
    public static async Task<string> AccountNameAsync(TradingDbContext db, string accountId, CancellationToken ct)
    {
        if (accountId == ExecutionEnvironmentIds.PaperAccount) return "Weekend Paper";
        var account = await db.HyperliquidAccounts.FindAsync(new object[] { accountId }, ct);
        return string.IsNullOrWhiteSpace(account?.Name) ? accountId : account.Name;
    }

    public static string SideAndType(OrderEntity order)
    {
        var side = order.Side;
        if (order.GridLevel >= 0)
        {
            var isTakeProfit = order.Kind.Equals("TAKE_PROFIT", StringComparison.OrdinalIgnoreCase);
            var isBuy = order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase);
            var entrySide = isTakeProfit ? (isBuy ? "S" : "B") : (isBuy ? "B" : "S");
            side = isTakeProfit
                ? FormattableString.Invariant($"{order.Side} ({entrySide}{order.GridLevel}-TP)")
                : FormattableString.Invariant($"{order.Side}({entrySide}{order.GridLevel})");
        }
        var type = order.Kind.ToUpperInvariant() switch
        {
            "ENTRY" => "Entry",
            "TAKE_PROFIT" => "TP",
            "FLATTEN" => "Close",
            _ => order.Kind
        };
        return $"{side} · {type}";
    }
}
