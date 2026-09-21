using System.Security.Cryptography;
using System.Text;
using GridTrading.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class TelegramAlertDispatcher(
    IServiceScopeFactory scopeFactory,
    CredentialProtector protector,
    ITelegramBotClient bot,
    ILogger<TelegramAlertDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await ProcessNextAsync(stoppingToken))
                {
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Telegram alert dispatch paused after {ErrorType}.", ex.GetType().Name);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    public async Task<bool> ProcessNextAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var settings = await db.TelegramNotificationSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == TelegramNotificationSettingsEntity.SingletonId, ct);
        if (settings is null || !settings.Enabled || settings.VerifiedAt is null || settings.EnabledAt is null)
            return false;

        string token;
        try
        {
            token = protector.UnprotectTelegramBotToken(settings.EncryptedBotToken);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            logger.LogWarning("Telegram bot token could not be decrypted; notifications remain pending.");
            return false;
        }

        var enabledAt = settings.EnabledAt.Value;
        var candidates = await db.RiskAlerts.FromSqlInterpolated($"""
            SELECT alert.*
            FROM "RiskAlerts" AS alert
            WHERE julianday(alert."CreatedAt") >= julianday({enabledAt})
              AND alert."Severity" IN ('INFO', 'WARNING', 'CRITICAL')
              AND NOT EXISTS (
                  SELECT 1 FROM "TelegramAlertDeliveries" AS delivery
                  WHERE delivery."RiskAlertId" = alert."Id"
              )
            ORDER BY julianday(alert."CreatedAt"), alert."Id"
            LIMIT 1
            """).AsNoTracking().ToListAsync(ct);
        var alert = candidates.SingleOrDefault();
        if (alert is null)
            return await ProcessOrderAsync(db, settings, token, ct);

        var delivery = new TelegramAlertDeliveryEntity
        {
            RiskAlertId = alert.Id,
            AttemptedAt = DateTimeOffset.UtcNow
        };
        db.TelegramAlertDeliveries.Add(delivery);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return true;
        }

        string? error = null;
        try
        {
            await bot.SendMessageAsync(token, settings.ChatId, FormatMessage(alert), ct);
            delivery.DeliveredAt = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TelegramBotApiException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex)
        {
            error = $"Telegram delivery failed ({ex.GetType().Name}).";
        }

        delivery.Error = error;
        var currentSettings = await db.TelegramNotificationSettings
            .SingleOrDefaultAsync(x => x.Id == TelegramNotificationSettingsEntity.SingletonId, ct);
        if (currentSettings is not null && currentSettings.UpdatedAt == settings.UpdatedAt)
        {
            currentSettings.LastDeliveryAt = DateTimeOffset.UtcNow;
            currentSettings.LastDeliveryStatus = error is null ? "SUCCEEDED" : "FAILED";
            currentSettings.LastDeliveryError = error;
        }

        await db.SaveChangesAsync(ct);
        if (error is not null)
            logger.LogWarning("Telegram delivery for risk alert {AlertId} failed: {Reason}", alert.Id, error);
        return true;
    }

    private async Task<bool> ProcessOrderAsync(TradingDbContext db,
        TelegramNotificationSettingsEntity settings, string token, CancellationToken ct)
    {
        var enabledAt = settings.EnabledAt!.Value;
        var candidates = await db.OrderPlacementNotifications.FromSqlInterpolated($"""
            SELECT * FROM "OrderPlacementNotifications"
            WHERE "AttemptedAt" IS NULL AND julianday("CreatedAt") >= julianday({enabledAt})
            ORDER BY julianday("CreatedAt"), "Id"
            LIMIT 1
            """).AsNoTracking().ToListAsync(ct);
        var notification = candidates.SingleOrDefault();
        if (notification is null) return false;

        var attemptedAt = DateTimeOffset.UtcNow;
        var claimed = await db.OrderPlacementNotifications
            .Where(x => x.Id == notification.Id && x.AttemptedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.AttemptedAt, attemptedAt), ct);
        if (claimed == 0) return true;

        db.Attach(notification);
        notification.AttemptedAt = attemptedAt;
        string? error = null;
        try
        {
            await bot.SendMessageAsync(token, settings.ChatId, Truncate(notification.Message, 4096), ct);
            notification.DeliveredAt = DateTimeOffset.UtcNow;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TelegramBotApiException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex)
        {
            error = $"Telegram delivery failed ({ex.GetType().Name}).";
        }

        notification.Error = error;
        var currentSettings = await db.TelegramNotificationSettings
            .SingleOrDefaultAsync(x => x.Id == TelegramNotificationSettingsEntity.SingletonId, ct);
        if (currentSettings is not null && currentSettings.UpdatedAt == settings.UpdatedAt)
        {
            currentSettings.LastDeliveryAt = DateTimeOffset.UtcNow;
            currentSettings.LastDeliveryStatus = error is null ? "SUCCEEDED" : "FAILED";
            currentSettings.LastDeliveryError = error;
        }
        await db.SaveChangesAsync(ct);
        if (error is not null)
            logger.LogWarning("Telegram order notification {NotificationId} failed: {Reason}", notification.Id, error);
        return true;
    }

    public static string FormatMessage(RiskAlertEntity alert)
    {
        var icon = alert.Severity switch
        {
            "CRITICAL" => "🚨",
            "WARNING" => "⚠️",
            _ => "ℹ️"
        };
        var cycle = string.IsNullOrWhiteSpace(alert.CycleId) ? "" : $"\nCycle: {alert.CycleId}";
        var text = $"{icon} GridTrading Alert\nSeverity: {alert.Severity}\nCode: {alert.Code}{cycle}\n" +
            $"Time: {alert.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss 'UTC'}\n\n{alert.Message}";
        return Truncate(text, 4096);
    }

    private static string Truncate(string value, int maximumRunes)
    {
        var runes = value.EnumerateRunes().ToArray();
        if (runes.Length <= maximumRunes) return value;
        var result = new StringBuilder();
        foreach (var rune in runes.Take(maximumRunes - 1)) result.Append(rune.ToString());
        return result.Append('…').ToString();
    }
}
