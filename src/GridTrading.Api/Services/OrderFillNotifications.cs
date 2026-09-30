using System.Security.Cryptography;
using System.Text;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public static class OrderFillNotifications
{
    // Call only for a confirmed full fill. Share the durable order notification queue
    // so delivery includes the same account snapshot, settings, and failure handling.
    public static async Task RecordConfirmedAsync(TradingDbContext db, ExecutionSelection selection,
        OrderEntity order, DateTimeOffset completedAt, CancellationToken ct, string? exchangeOrderId = null)
    {
        var venueId = exchangeOrderId ?? order.ExchangeOrderId;
        if (order.Quantity <= 0m || string.IsNullOrWhiteSpace(venueId) || venueId == "pending" ||
            venueId.StartsWith("0x", StringComparison.Ordinal)) return;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"filled:{order.Id}:{venueId}")));
        if (db.OrderPlacementNotifications.Local.Any(x => x.Id == id) ||
            await db.OrderPlacementNotifications.AnyAsync(x => x.Id == id, ct)) return;

        var accountName = await TelegramOrderFormatting.AccountNameAsync(db, selection.AccountId, ct);
        db.OrderPlacementNotifications.Add(new OrderPlacementNotificationEntity
        {
            Id = id, ExecutionAccountId = selection.AccountId, Symbol = order.Symbol,
            // Use the actual completion time so recovered historical fills are not
            // back-sent after notifications are enabled or re-enabled.
            CreatedAt = completedAt,
            Message = FormattableString.Invariant($"""
                ✅ GridTrading Order Fully Filled
                Account: {accountName}
                Symbol: {order.Symbol}
                Side: {TelegramOrderFormatting.SideAndType(order)}
                Order Price: {order.Price:G29}
                Filled Quantity: {order.Quantity:G29}
                Order: {order.Id}
                Filled at: {completedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss 'UTC'}
                """)
        });
    }
}
