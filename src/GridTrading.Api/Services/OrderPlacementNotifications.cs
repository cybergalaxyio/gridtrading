using System.Security.Cryptography;
using System.Text;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public static class OrderPlacementNotifications
{
    // Call only after venue confirmation (including recovery). The caller saves this
    // snapshot in the same transaction as the order, without contacting Telegram.
    public static async Task RecordAsync(TradingDbContext db, ExecutionSelection selection,
        OrderEntity order, CancellationToken ct, string? exchangeOrderId = null, decimal? quantity = null)
    {
        var venueId = exchangeOrderId ?? order.ExchangeOrderId;
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{order.Id}:{venueId}")));
        if (db.OrderPlacementNotifications.Local.Any(x => x.Id == id) ||
            await db.OrderPlacementNotifications.AnyAsync(x => x.Id == id, ct)) return;

        var now = DateTimeOffset.UtcNow;
        var message = FormattableString.Invariant($"""
            ✅ GridTrading Order Placed
            Environment: {selection.EnvironmentId}
            Account: {selection.AccountId}
            Symbol: {order.Symbol}
            Side: {order.Side}
            Type: {order.Kind}
            Price: {order.Price:G29}
            Quantity: {quantity ?? order.Quantity:G29}
            Order: {order.Id}
            Exchange order: {venueId}
            Cycle: {order.CycleId}
            Time: {now.UtcDateTime:yyyy-MM-dd HH:mm:ss 'UTC'}
            """);
        db.OrderPlacementNotifications.Add(new OrderPlacementNotificationEntity
        {
            Id = id, Message = message, CreatedAt = now
        });
    }
}
