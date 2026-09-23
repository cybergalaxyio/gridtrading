namespace GridTrading.Api.Data;

// Durable order-event snapshots (placements and full fills). Later updates must not change the message.
public sealed class OrderPlacementNotificationEntity
{
    public required string Id { get; set; }
    public string? ExecutionAccountId { get; set; }
    public string? Symbol { get; set; }
    public required string Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AttemptedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? Error { get; set; }
}
