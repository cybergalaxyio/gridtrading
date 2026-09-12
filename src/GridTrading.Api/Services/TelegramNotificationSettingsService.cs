using System.Security.Cryptography;
using System.Text.RegularExpressions;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed partial class TelegramNotificationSettingsService(
    TradingDbContext db,
    CredentialProtector protector,
    ITelegramBotClient bot)
{
    public async Task<TelegramSettingsDto> GetAsync(CancellationToken ct) =>
        ToDto(await db.TelegramNotificationSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == TelegramNotificationSettingsEntity.SingletonId, ct));

    public async Task<TelegramSettingsDto> SaveAsync(TelegramSettingsRequest request, CancellationToken ct)
    {
        EnsureCredentialKey();
        var chatId = NormalizeChatId(request.ChatId);
        var token = string.IsNullOrWhiteSpace(request.BotToken) ? null : request.BotToken.Trim();
        if (token is not null) ValidateToken(token);

        var item = await db.TelegramNotificationSettings
            .SingleOrDefaultAsync(x => x.Id == TelegramNotificationSettingsEntity.SingletonId, ct);
        if (item is null && token is null)
            throw Problem("TELEGRAM_BOT_TOKEN_REQUIRED", "Enter a Telegram bot token before saving the first configuration.");

        var changed = item is null || !string.Equals(item.ChatId, chatId, StringComparison.Ordinal) || token is not null;
        if (item is null)
        {
            item = new TelegramNotificationSettingsEntity
            {
                EncryptedBotToken = protector.ProtectTelegramBotToken(token!),
                ChatId = chatId,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.TelegramNotificationSettings.Add(item);
        }
        else
        {
            item.ChatId = chatId;
            if (token is not null) item.EncryptedBotToken = protector.ProtectTelegramBotToken(token);
            item.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (changed)
        {
            item.Enabled = false;
            item.BotUsername = null;
            item.VerifiedAt = null;
            item.EnabledAt = null;
            item.LastTestedAt = null;
            item.LastTestError = null;
        }

        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    public async Task<TelegramSettingsDto> TestAndEnableAsync(CancellationToken ct)
    {
        EnsureCredentialKey();
        var item = await db.TelegramNotificationSettings
            .SingleOrDefaultAsync(x => x.Id == TelegramNotificationSettingsEntity.SingletonId, ct)
            ?? throw Problem("TELEGRAM_NOT_CONFIGURED", "Save a Telegram bot token and chat ID before testing.");

        string token;
        try
        {
            token = protector.UnprotectTelegramBotToken(item.EncryptedBotToken);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            item.Enabled = false;
            item.EnabledAt = null;
            item.VerifiedAt = null;
            item.LastTestedAt = DateTimeOffset.UtcNow;
            item.LastTestError = "The stored Telegram bot token cannot be decrypted with the current credential key.";
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            throw Problem("TELEGRAM_TOKEN_UNREADABLE", item.LastTestError);
        }

        var activationWatermark = DateTimeOffset.UtcNow;
        try
        {
            var identity = await bot.GetIdentityAsync(token, ct);
            await bot.SendMessageAsync(token, item.ChatId,
                $"✅ GridTrading Telegram notifications enabled.\nBot: @{identity.Username}\nTime: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss 'UTC'}", ct);
            var now = DateTimeOffset.UtcNow;
            item.BotUsername = identity.Username;
            item.VerifiedAt = now;
            item.LastTestedAt = now;
            item.LastTestError = null;
            if (!item.Enabled || item.EnabledAt is null) item.EnabledAt = activationWatermark;
            item.Enabled = true;
            item.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            return ToDto(item);
        }
        catch (TelegramBotApiException ex)
        {
            item.Enabled = false;
            item.EnabledAt = null;
            item.VerifiedAt = null;
            item.LastTestedAt = DateTimeOffset.UtcNow;
            item.LastTestError = ex.Message;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            throw new TradingProblemException(ex.ServiceUnavailable ? 503 : 422,
                "TELEGRAM_VERIFICATION_FAILED", ex.Message);
        }
    }

    public async Task<TelegramSettingsDto> DisableAsync(CancellationToken ct)
    {
        var item = await db.TelegramNotificationSettings
            .SingleOrDefaultAsync(x => x.Id == TelegramNotificationSettingsEntity.SingletonId, ct);
        if (item is null) return ToDto(null);
        item.Enabled = false;
        item.EnabledAt = null;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    public async Task DeleteAsync(CancellationToken ct)
    {
        var item = await db.TelegramNotificationSettings
            .SingleOrDefaultAsync(x => x.Id == TelegramNotificationSettingsEntity.SingletonId, ct);
        if (item is null) return;
        db.TelegramNotificationSettings.Remove(item);
        await db.SaveChangesAsync(ct);
    }

    private void EnsureCredentialKey()
    {
        if (!protector.IsConfigured)
            throw new TradingProblemException(503, "CREDENTIAL_KEY_NOT_CONFIGURED",
                "Set GRID_TRADING_CREDENTIAL_KEY to a base64-encoded 32-byte key before storing Telegram credentials.");
    }

    private static string NormalizeChatId(string value)
    {
        var result = value?.Trim() ?? "";
        if (result.Length is < 1 or > 256 || result.Any(char.IsControl))
            throw Problem("TELEGRAM_CHAT_ID_INVALID",
                "Enter a numeric Telegram chat ID or an @channel username.");
        return result;
    }

    private static void ValidateToken(string token)
    {
        if (token.Length > 256 || !BotTokenPattern().IsMatch(token))
            throw Problem("TELEGRAM_BOT_TOKEN_INVALID", "The Telegram bot token format is invalid.");
    }

    private static TelegramSettingsDto ToDto(TelegramNotificationSettingsEntity? item) => new(
        Configured: item is not null && !string.IsNullOrWhiteSpace(item.EncryptedBotToken),
        Enabled: item?.Enabled == true,
        TokenStored: item is not null && !string.IsNullOrWhiteSpace(item.EncryptedBotToken),
        ChatId: item?.ChatId ?? "",
        BotUsername: item?.BotUsername,
        VerifiedAt: item?.VerifiedAt,
        EnabledAt: item?.EnabledAt,
        LastTestedAt: item?.LastTestedAt,
        LastTestError: item?.LastTestError,
        LastDeliveryAt: item?.LastDeliveryAt,
        LastDeliveryStatus: item?.LastDeliveryStatus,
        LastDeliveryError: item?.LastDeliveryError);

    private static TradingProblemException Problem(string code, string message) => new(422, code, message);

    [GeneratedRegex(@"^[0-9]+:[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex BotTokenPattern();
}
