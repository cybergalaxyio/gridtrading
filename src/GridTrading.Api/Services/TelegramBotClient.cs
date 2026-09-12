using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace GridTrading.Api.Services;

public sealed record TelegramBotIdentity(string Username);

public interface ITelegramBotClient
{
    Task<TelegramBotIdentity> GetIdentityAsync(string botToken, CancellationToken ct);
    Task SendMessageAsync(string botToken, string chatId, string text, CancellationToken ct);
}

public sealed class TelegramBotApiException(string message, bool serviceUnavailable = false) : Exception(message)
{
    public bool ServiceUnavailable { get; } = serviceUnavailable;
}

public sealed class TelegramBotClient : ITelegramBotClient, IDisposable
{
    private readonly HttpClient _http;

    public TelegramBotClient() : this(new HttpClientHandler { AllowAutoRedirect = false })
    {
    }

    public TelegramBotClient(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) };
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
        CallWithoutResultAsync(botToken, "sendMessage", new { chat_id = chatId, text }, ct);

    private async Task CallWithoutResultAsync(
        string botToken, string method, object? payload, CancellationToken ct) =>
        _ = await CallAsync(botToken, method, payload, ct);

    private async Task<JsonElement> CallAsync(
        string botToken, string method, object? payload, CancellationToken ct)
    {
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
                throw new TelegramBotApiException(Sanitize(description, botToken) ?? "Telegram rejected the request.", unavailable);
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
