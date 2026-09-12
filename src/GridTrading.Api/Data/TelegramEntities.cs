namespace GridTrading.Api.Data;

public sealed class TelegramNotificationSettingsEntity
{
    public const string SingletonId = "default";

    public string Id { get; set; } = SingletonId;
    public required string EncryptedBotToken { get; set; }
    public required string ChatId { get; set; }
    public bool Enabled { get; set; }
    public string? BotUsername { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? EnabledAt { get; set; }
    public DateTimeOffset? LastTestedAt { get; set; }
    public string? LastTestError { get; set; }
    public DateTimeOffset? LastDeliveryAt { get; set; }
    public string? LastDeliveryStatus { get; set; }
    public string? LastDeliveryError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class TelegramAlertDeliveryEntity
{
    public required string RiskAlertId { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? Error { get; set; }
}
