namespace GridTrading.Api.Data;

public sealed class TelegramNotificationSettingsEntity
{
    public const string SingletonId = "default";

    public string Id { get; set; } = SingletonId;
    public required string EncryptedBotToken { get; set; }
    public required string ChatId { get; set; }
    public bool Enabled { get; set; }
    public bool OrderActionsEnabled { get; set; }
    public string OrderActionsChatId { get; set; } = "";
    public string ActionsGeneration { get; set; } = Guid.NewGuid().ToString("N");
    public string? VerifiedPrivateChatId { get; set; }
    public long NextUpdateId { get; set; }
    public string? LastActionError { get; set; }
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

public sealed class TelegramApprovalMessageEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public required string ApprovalId { get; set; }
    public required string Generation { get; set; }
    public required string ChatId { get; set; }
    public long? MessageId { get; set; }
    public DateTimeOffset AttemptedAt { get; set; }
    public string? RenderedStatus { get; set; }
    public bool Finished { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset? RetryAt { get; set; }
}

public sealed class TelegramCallbackReceiptEntity
{
    public required string Id { get; set; }
    public required string Generation { get; set; }
    public required string Result { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}
