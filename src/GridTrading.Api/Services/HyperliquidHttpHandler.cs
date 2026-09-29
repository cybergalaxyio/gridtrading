using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace GridTrading.Api.Services;

// Shared by all three HTTP clients so a rate limit stops other callers on the same network too.
public sealed class HyperliquidHttpState(TimeProvider clock)
{
    internal TimeProvider Clock { get; } = clock;
    internal ConcurrentDictionary<string, DateTimeOffset> Cooldowns { get; } = new();
    internal ConcurrentDictionary<string, MetadataEntry> Metadata { get; } = new();
    internal ConcurrentDictionary<string, SemaphoreSlim> MetadataLocks { get; } = new();
    internal sealed record MetadataEntry(byte[] Body, DateTimeOffset ExpiresAt);
}

public sealed class HyperliquidHttpHandler(HyperliquidHttpState state, ILogger<HyperliquidHttpHandler> logger)
    : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        string? requestType = null;
        if (uri.AbsolutePath.EndsWith("/info", StringComparison.Ordinal) && request.Content is not null)
        {
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            if (body.RootElement.TryGetProperty("type", out var type)) requestType = type.GetString();
        }
        // Cache only static asset definitions, never account state, prices, or order status.
        if (requestType != "meta") return await SendOnceAsync(request, requestType, ct);
        var key = uri.AbsoluteUri;
        var gate = state.MetadataLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (state.Metadata.TryGetValue(key, out var cached) && cached.ExpiresAt > state.Clock.GetUtcNow())
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(cached.Body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } }
                };
            var response = await SendOnceAsync(request, requestType, ct);
            try
            {
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                    // Do not retain malformed responses.
                    using var document = JsonDocument.Parse(bytes);
                    if (document.RootElement.TryGetProperty("universe", out var universe) && universe.ValueKind == JsonValueKind.Array)
                        state.Metadata[key] = new(bytes, state.Clock.GetUtcNow().AddMinutes(1));
                }
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        finally { gate.Release(); }
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, string? requestType, CancellationToken ct)
    {
        var key = request.RequestUri!.GetLeftPart(UriPartial.Authority);
        var now = state.Clock.GetUtcNow();
        if (state.Cooldowns.TryGetValue(key, out var until) && until > now)
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                RequestMessage = request,
                Headers = { RetryAfter = new RetryConditionHeaderValue(until - now) }
            };

        // In particular, never automatically replay a signed exchange action.
        var response = await base.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            now = state.Clock.GetUtcNow();
            var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - now)
                ?? TimeSpan.FromSeconds(30);
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            var deadline = now + delay;
            state.Cooldowns.AddOrUpdate(key, deadline, (_, previous) => previous > deadline ? previous : deadline);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
            logger.LogWarning("Hyperliquid {RequestType} at {Host} returned HTTP 429; requests paused for {Seconds} seconds.",
                requestType ?? "exchange", request.RequestUri.Host, Math.Ceiling(delay.TotalSeconds));
        }
        return response;
    }

    public static void EnsureSuccess(HttpResponseMessage response, bool info = true)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)
                ?? TimeSpan.FromSeconds(30);
            throw new TradingProblemException(429, "EXCHANGE_RATE_LIMITED",
                $"Hyperliquid rate limit reached (HTTP 429). Requests are paused; retry in {Math.Max(1, Math.Ceiling(delay.TotalSeconds))} seconds.");
        }
        throw new TradingProblemException(503, info ? "EXCHANGE_INFO_UNAVAILABLE" : "EXCHANGE_HTTP_ERROR",
            $"Hyperliquid{(info ? " Info" : "")} returned HTTP {(int)response.StatusCode}.");
    }
}
