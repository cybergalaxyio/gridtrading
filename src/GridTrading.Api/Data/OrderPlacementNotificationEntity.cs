namespace GridTrading.Api.Data;

// A durable snapshot: later fills, cancellations, or replacements must not change the message.
public sealed class OrderPlacementNotificationEntity
{
    public required string Id { get; set; }
    public required string Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AttemptedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? Error { get; set; }
}
