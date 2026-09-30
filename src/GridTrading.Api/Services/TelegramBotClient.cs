using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace GridTrading.Api.Services;

public sealed record TelegramBotIdentity(string Username);
public sealed record TelegramChat(long Id, string Type);
public sealed record TelegramButton(string Text, string Data);
public sealed record TelegramIncomingMessage(long ChatId, string ChatType, long SenderId, long MessageId, string? Text);
public sealed record TelegramCallback(string Id, string? Data, long SenderId, TelegramIncomingMessage? Message);
public sealed record TelegramUpdate(long Id, TelegramIncomingMessage? Message, TelegramCallback? Callback);

public interface ITelegramBotClient
{
    Task<TelegramBotIdentity> GetIdentityAsync(string botToken, CancellationToken ct);
    Task SendMessageAsync(string botToken, string chatId, string text, CancellationToken ct);
    Task<TelegramChat> GetChatAsync(string botToken, string chatId, CancellationToken ct);
    Task<bool> HasWebhookAsync(string botToken, CancellationToken ct);
    Task<long> SendButtonsAsync(string botToken, string chatId, string text, TelegramButton[][] buttons, CancellationToken ct);
    Task EditButtonsAsync(string botToken, string chatId, long messageId, string text, TelegramButton[][] buttons, CancellationToken ct);
    Task AnswerCallbackAsync(string botToken, string callbackId, string text, CancellationToken ct);
    Task<TelegramUpdate[]> GetUpdatesAsync(string botToken, long offset, CancellationToken ct);
}

public sealed class TelegramBotApiException(string message, bool serviceUnavailable = false, int? retryAfterSeconds = null, bool requestNotSent = false) : Exception(message)
{
    public bool ServiceUnavailable { get; } = serviceUnavailable;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
    public bool RequestNotSent { get; } = requestNotSent;
}

public sealed class TelegramBotClient : ITelegramBotClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, DateTimeOffset> cooldowns = new();
    private sealed class ChatLane
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public DateTimeOffset NextSend { get; set; }
    }
    private readonly ConcurrentDictionary<(string Token, string Chat), ChatLane> chatLanes = new();

    public TelegramBotClient() : this(new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    public TelegramBotClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<TelegramBotIdentity> GetIdentityAsync(string botToken, CancellationToken ct)
    {
        var result = await CallAsync(botToken, "getMe", null, ct);
        var username = result.TryGetProperty("username", out var value) ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(username))
            throw new TelegramBotApiException("Telegram returned an invalid bot identity.");
        return new TelegramBotIdentity(username);
    }

    public Task SendMessageAsync(string botToken, string chatId, string text, CancellationToken ct) =>
        CallChatAsync(botToken, chatId, "sendMessage", new { chat_id = chatId, text }, ct);

    public async Task<TelegramChat> GetChatAsync(string botToken, string chatId, CancellationToken ct)
    {
        var result = await CallAsync(botToken, "getChat", new { chat_id = chatId }, ct);
        return new(result.GetProperty("id").GetInt64(), result.GetProperty("type").GetString() ?? "");
    }

    public async Task<bool> HasWebhookAsync(string botToken, CancellationToken ct)
    {
        var result = await CallAsync(botToken, "getWebhookInfo", null, ct);
        return result.TryGetProperty("url", out var url) && !string.IsNullOrEmpty(url.GetString());
    }

    private static object Markup(TelegramButton[][] buttons) => new
    {
        inline_keyboard = buttons.Select(row => row.Select(button => new { text = button.Text, callback_data = button.Data }).ToArray()).ToArray()
    };

    public async Task<long> SendButtonsAsync(string botToken, string chatId, string text, TelegramButton[][] buttons, CancellationToken ct)
    {
        var result = await CallChatAsync(botToken, chatId, "sendMessage", new { chat_id = chatId, text, reply_markup = Markup(buttons) }, ct);
        return result.GetProperty("message_id").GetInt64();
    }

    public Task EditButtonsAsync(string botToken, string chatId, long messageId, string text, TelegramButton[][] buttons, CancellationToken ct) =>
        CallChatAsync(botToken, chatId, "editMessageText", new { chat_id = chatId, message_id = messageId, text, reply_markup = Markup(buttons) }, ct);

    public Task AnswerCallbackAsync(string botToken, string callbackId, string text, CancellationToken ct) =>
        CallWithoutResultAsync(botToken, "answerCallbackQuery", new { callback_query_id = callbackId, text }, ct);

    public async Task<TelegramUpdate[]> GetUpdatesAsync(string botToken, long offset, CancellationToken ct)
    {
        var result = await CallAsync(botToken, "getUpdates", new { offset, timeout = 20, allowed_updates = new[] { "message", "callback_query" } }, ct);
        return result.EnumerateArray().Select(update =>
        {
            TelegramIncomingMessage? message = update.TryGetProperty("message", out var msg) ? ReadMessage(msg) : null;
            TelegramCallback? callback = null;
            if (update.TryGetProperty("callback_query", out var query))
                callback = new(query.GetProperty("id").GetString()!, query.TryGetProperty("data", out var data) ? data.GetString() : null,
                    query.GetProperty("from").GetProperty("id").GetInt64(),
                    query.TryGetProperty("message", out var origin) ? ReadMessage(origin) : null);
            return new TelegramUpdate(update.GetProperty("update_id").GetInt64(), message, callback);
        }).ToArray();
    }

    private static TelegramIncomingMessage ReadMessage(JsonElement message) => new(
        message.GetProperty("chat").GetProperty("id").GetInt64(),
        message.GetProperty("chat").GetProperty("type").GetString() ?? "",
        message.TryGetProperty("from", out var from) ? from.GetProperty("id").GetInt64() : 0,
        message.GetProperty("message_id").GetInt64(), message.TryGetProperty("text", out var text) ? text.GetString() : null);

    private async Task<JsonElement> CallChatAsync(string token, string chatId, string method, object payload, CancellationToken ct)
    {
        var lane = chatLanes.GetOrAdd((token, chatId), _ => new());
        await lane.Gate.WaitAsync(ct);
        try
        {
            var delay = lane.NextSend - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            while (true)
            {
                try { return await CallAsync(token, method, payload, ct); }
                catch (TelegramBotApiException ex) when (ex.RequestNotSent)
                {
                    // Only wait for locally blocked requests. Never replay an attempted send.
                    await Task.Delay(TimeSpan.FromSeconds(ex.RetryAfterSeconds ?? 1), ct);
                }
            }
        }
        finally { lane.NextSend = DateTimeOffset.UtcNow.AddMilliseconds(1100); lane.Gate.Release(); }
    }

    private async Task CallWithoutResultAsync(
        string botToken, string method, object? payload, CancellationToken ct) =>
        _ = await CallAsync(botToken, method, payload, ct);

    private async Task<JsonElement> CallAsync(
        string botToken, string method, object? payload, CancellationToken ct)
    {
        if (cooldowns.TryGetValue(botToken, out var until) && until > DateTimeOffset.UtcNow)
        {
            var remaining = Math.Max(1, (int)Math.Ceiling((until - DateTimeOffset.UtcNow).TotalSeconds));
            throw new TelegramBotApiException($"Telegram is rate limited. Retry in {remaining} seconds.", true, remaining, requestNotSent: true);
        }
        using var request = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post,
            new Uri($"https://api.telegram.org/bot{botToken}/{method}"));
        if (payload is not null) request.Content = JsonContent.Create(payload);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TelegramBotApiException("Telegram request timed out.", serviceUnavailable: true);
        }
        catch (HttpRequestException)
        {
            throw new TelegramBotApiException("Telegram is unavailable.", serviceUnavailable: true);
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                throw new TelegramBotApiException("Telegram returned an unreadable response.",
                    serviceUnavailable: (int)response.StatusCode >= 500);
            }

            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(body);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new TelegramBotApiException("Telegram returned an invalid response.",
                    serviceUnavailable: (int)response.StatusCode >= 500);
            }

            var ok = root.TryGetProperty("ok", out var okValue) && okValue.ValueKind == JsonValueKind.True;
            if (!response.IsSuccessStatusCode || !ok)
            {
                var description = root.TryGetProperty("description", out var descriptionValue)
                    ? descriptionValue.GetString()
                    : null;
                var unavailable = response.StatusCode == HttpStatusCode.TooManyRequests ||
                    (int)response.StatusCode >= 500;
                var retryAfter = root.TryGetProperty("parameters", out var parameters) && parameters.TryGetProperty("retry_after", out var retry)
                    && retry.TryGetInt32(out var seconds) ? (int?)Math.Max(1, seconds) : null;
                if (response.StatusCode == HttpStatusCode.TooManyRequests || retryAfter is not null)
                {
                    retryAfter ??= 30;
                    var deadline = DateTimeOffset.UtcNow.AddSeconds(retryAfter.Value);
                    cooldowns.AddOrUpdate(botToken, deadline, (_, previous) => previous > deadline ? previous : deadline);
                }
                throw new TelegramBotApiException(Sanitize(description, botToken) ?? "Telegram rejected the request.", unavailable, retryAfter);
            }

            return root.TryGetProperty("result", out var result) ? result.Clone() : default;
        }
    }

    public void Dispose() => _http.Dispose();

    private static string? Sanitize(string? value, string botToken)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var singleLine = string.Join(' ', value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim()
            .Replace(botToken, "[redacted]", StringComparison.Ordinal);
        return singleLine.Length <= 240 ? singleLine : singleLine[..239] + "…";
    }
}
