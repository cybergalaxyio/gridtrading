namespace GridTrading.Api.Data;

public sealed class OrderApprovalEntity
{
    public required string Id { get; set; }
    public required string OrderId { get; set; }
    public required string CycleId { get; set; }
    public required string ExecutionEnvironmentId { get; set; }
    public required string ExecutionAccountId { get; set; }
    public required string Symbol { get; set; }
    public required string Side { get; set; }
    public required string Kind { get; set; }
    public required string Action { get; set; }
    public required string TimeInForce { get; set; }
    public required string ClientOrderId { get; set; }
    public required string ExchangeOrderId { get; set; }
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal FilledQuantity { get; set; }
    public bool ReduceOnly { get; set; }
    public string Status { get; set; } = "PENDING";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
}
