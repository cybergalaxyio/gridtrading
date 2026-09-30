using GridTrading.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class TelegramOrderActionWorker(
    IServiceScopeFactory scopeFactory, CredentialProtector protector, ITelegramBotClient bot,
    ILogger<TelegramOrderActionWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim deliveryGate = new(1, 1);
    private long retryUntilTicks;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(PollAsync(stoppingToken), DispatchAsync(stoppingToken));

    private async Task DispatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? generation = null;
            try
            {
                await WaitForBackoffAsync(ct);
                await deliveryGate.WaitAsync(ct);
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>();
                    generation = (await service.SettingsAsync(ct))?.ActionsGeneration;
                    await service.DispatchNextAsync(ct);
                }
                finally { deliveryGate.Release(); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { await FailedAsync(generation, ex, ct); }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? generation = null;
            try
            {
                await WaitForBackoffAsync(ct);
                TelegramNotificationSettingsEntity? settings;
                using (var scope = scopeFactory.CreateScope())
                    settings = await scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>().SettingsAsync(ct);
                if (!TelegramNotificationSettingsService.ActionsReady(settings))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    continue;
                }
                generation = settings!.ActionsGeneration;
                var token = protector.UnprotectTelegramBotToken(settings.EncryptedBotToken);
                var updates = await bot.GetUpdatesAsync(token, settings.NextUpdateId, ct);
                foreach (var update in updates.OrderBy(x => x.Id)) await ProcessUpdateAsync(generation, token, update, ct);
                // Long polling normally waits on Telegram; prevent a tight loop on empty immediate responses.
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { await FailedAsync(generation, ex, ct); }
        }
    }

    public async Task ProcessUpdateAsync(string generation, string token, TelegramUpdate update, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<TelegramOrderActionService>();
        var settings = await service.SettingsAsync(ct);
        if (settings?.ActionsGeneration != generation || update.Id < settings.NextUpdateId) return;
        string? answer = null;
        var page = -1;
        TelegramIncomingMessage? origin = null;
        long sender = 0;
        if (update.Callback is { } callback)
        {
            if (callback.Data?.StartsWith("oa:", StringComparison.Ordinal) == true)
                answer = await service.HandleCallbackAsync(generation, callback, ct);
            else if (callback.Data?.Split(':') is ["op", var buttonGeneration, var number] &&
                buttonGeneration == generation && int.TryParse(number, out var parsed) && parsed >= 0 &&
                TelegramOrderActionService.Authorized(settings, generation, callback.Message, callback.SenderId))
            {
                page = parsed; origin = callback.Message; sender = callback.SenderId;
                answer = "Loading order reviews…";
            }
            else answer = "This button is no longer available.";
        }
        else if (update.Message is { } message &&
            TelegramOrderActionService.Authorized(settings, generation, message, message.SenderId) &&
            (message.Text?.Split(' ', '\n')[0] is "/pending" || message.Text?.Split(' ', '\n')[0] == $"/pending@{settings.BotUsername}"))
        {
            page = 0; origin = message; sender = message.SenderId;
        }
        // Commit before any acknowledgement or page delivery. Lost acknowledgements never replay decisions.
        await db.TelegramNotificationSettings.Where(x => x.ActionsGeneration == generation && x.NextUpdateId <= update.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.NextUpdateId, update.Id + 1), ct);
        if (answer is not null && update.Callback is { } query)
        {
            try { await bot.AnswerCallbackAsync(token, query.Id, answer, ct); }
            catch (TelegramBotApiException ex) when (!ex.ServiceUnavailable)
            {
                // An expired callback answer does not invalidate an already committed decision.
                logger.LogInformation("Telegram callback acknowledgement expired; the decision remains recorded.");
            }
        }
        if (page >= 0 && origin is not null)
        {
            await deliveryGate.WaitAsync(ct);
            try { await service.SendPendingPageAsync(generation, origin, sender, page, ct); }
            finally { deliveryGate.Release(); }
        }
    }

    private async Task FailedAsync(string? generation, Exception ex, CancellationToken ct)
    {
        var seconds = ex is TelegramBotApiException { RetryAfterSeconds: { } retry } ? Math.Max(1, retry) : 10;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds).UtcTicks;
        long old;
        do { old = Interlocked.Read(ref retryUntilTicks); if (old >= deadline) break; }
        while (Interlocked.CompareExchange(ref retryUntilTicks, deadline, old) != old);
        var error = TelegramOrderActionService.SafeError(ex);
        logger.LogWarning("Telegram order actions paused: {Error}", error);
        if (generation is null) return;
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<TradingDbContext>().TelegramNotificationSettings
                .Where(x => x.ActionsGeneration == generation)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LastActionError, error), ct);
        }
        catch (Exception saveError) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not persist Telegram action status ({ErrorType}).", saveError.GetType().Name);
        }
    }

    private async Task WaitForBackoffAsync(CancellationToken ct)
    {
        while (new DateTimeOffset(Interlocked.Read(ref retryUntilTicks), TimeSpan.Zero) - DateTimeOffset.UtcNow is var delay && delay > TimeSpan.Zero)
            await Task.Delay(delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay, ct);
    }
}
