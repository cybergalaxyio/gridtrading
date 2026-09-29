namespace GridTrading.Api.Data;

public sealed class TradingControlSettingsEntity
{
    public const string SingletonId = "trading-control";
    public string Id { get; set; } = SingletonId;
    public bool RequireManualOrderConfirmation { get; set; }
    public decimal MinimumConfirmationNotional { get; set; }
}
