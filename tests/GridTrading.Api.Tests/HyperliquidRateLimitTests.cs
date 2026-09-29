using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using GridTrading.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GridTrading.Api.Tests;

public sealed class HyperliquidRateLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RateLimitIsSharedAcrossClientsAndResumesAfterRetryAfter(bool dateHeader)
    {
        var clock = new TestClock();
        var state = new HyperliquidHttpState(clock);
        var first = new StubHandler(() => new(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("rate limited"),
            Headers = { RetryAfter = dateHeader ? new RetryConditionHeaderValue(clock.GetUtcNow().AddSeconds(12))
                : new RetryConditionHeaderValue(TimeSpan.FromSeconds(12)) }
        });
        var second = new StubHandler(() => new(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var a = Client(state, first);
        using var b = Client(state, second);
        using var limited = await a.PostAsJsonAsync("https://api.hyperliquid.xyz/info", new { type = "openOrders" }, Ct);
        Assert.Equal(1, first.Calls); // The failed request is not automatically retried.
        using var blocked = await b.PostAsJsonAsync("https://api.hyperliquid.xyz/info", new { type = "clearinghouseState" }, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.Equal(0, second.Calls);
        // A signed exchange request must not be queued or replayed either.
        using var action = await b.PostAsJsonAsync("https://api.hyperliquid.xyz/exchange", new { action = "order" }, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, action.StatusCode);
        Assert.Equal(0, second.Calls);
        using var otherNetwork = await b.PostAsJsonAsync("https://api.hyperliquid-testnet.xyz/info", new { type = "openOrders" }, Ct);
        Assert.Equal(HttpStatusCode.OK, otherNetwork.StatusCode);
        clock.Advance(TimeSpan.FromSeconds(12));
        using var resumed = await b.PostAsJsonAsync("https://api.hyperliquid.xyz/info", new { type = "openOrders" }, Ct);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Equal(2, second.Calls);
    }

    [Fact]
    public async Task MissingRetryAfterUsesThirtySecondCooldownAndReturnsStructuredError()
    {
        var clock = new TestClock();
        var wire = new StubHandler(() => new(HttpStatusCode.TooManyRequests) { Content = new StringContent("not JSON") });
        using var client = Client(new(clock), wire);
        using var response = await client.PostAsJsonAsync("https://api.hyperliquid.xyz/info", new { type = "metaAndAssetCtxs" }, Ct);
        var error = Assert.Throws<TradingProblemException>(() => HyperliquidHttpHandler.EnsureSuccess(response));
        Assert.Equal(429, error.Status);
        Assert.Equal("EXCHANGE_RATE_LIMITED", error.Code);
        Assert.Contains("30 seconds", error.Message);
        clock.Advance(TimeSpan.FromSeconds(29));
        using var blocked = await client.PostAsJsonAsync("https://api.hyperliquid.xyz/info", new { type = "openOrders" }, Ct);
        Assert.Equal(1, wire.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var retried = await client.PostAsJsonAsync("https://api.hyperliquid.xyz/info", new { type = "openOrders" }, Ct);
        Assert.Equal(2, wire.Calls);
    }

    [Fact]
    public async Task OnlyStaticMetadataIsCachedAcrossClientsWithNetworkIsolationAndExpiry()
    {
        var clock = new TestClock();
        var state = new HyperliquidHttpState(clock);
        var wire = new StubHandler(() => new(HttpStatusCode.OK)
            { Content = new StringContent("{\"universe\":[{\"name\":\"SOL\",\"szDecimals\":2}]}") });
        using var a = Client(state, wire);
        var otherWire = new StubHandler(() => new(HttpStatusCode.OK) { Content = new StringContent("{}") });
        using var b = Client(state, otherWire);
        var requests = Enumerable.Range(0, 8).Select(_ => a.PostAsJsonAsync("https://api.hyperliquid.xyz/info", new { type = "meta" }, Ct));
        var responses = await Task.WhenAll(requests);
        foreach (var response in responses) { Assert.Contains("SOL", await response.Content.ReadAsStringAsync(Ct)); response.Dispose(); }
        Assert.Equal(1, wire.Calls);
        using var cached = await b.PostAsJsonAsync("https://api.hyperliquid.xyz/info", new { type = "meta" }, Ct);
        Assert.Contains("SOL", await cached.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, otherWire.Calls);
        using var testnet = await a.PostAsJsonAsync("https://api.hyperliquid-testnet.xyz/info", new { type = "meta" }, Ct);
        Assert.Equal(2, wire.Calls);
        for (var i = 0; i < 2; i++)
        {
            using var fresh = await a.PostAsJsonAsync("https://api.hyperliquid.xyz/info", new { type = "openOrders" }, Ct);
        }
        Assert.Equal(4, wire.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        using var expired = await a.PostAsJsonAsync("https://api.hyperliquid.xyz/info", new { type = "meta" }, Ct);
        Assert.Equal(5, wire.Calls);
    }

    [Fact]
    public void NonJsonExchangeErrorIsReportedWithoutTryingToParseIt()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("gateway error") };
        var error = Assert.Throws<TradingProblemException>(() => HyperliquidHttpHandler.EnsureSuccess(response, info: false));
        Assert.Equal("EXCHANGE_HTTP_ERROR", error.Code);
        Assert.Equal(503, error.Status);
    }

    private static HttpClient Client(HyperliquidHttpState state, HttpMessageHandler wire) =>
        new(new HyperliquidHttpHandler(state, NullLogger<HyperliquidHttpHandler>.Instance) { InnerHandler = wire });

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class StubHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(response());
        }
    }
}
