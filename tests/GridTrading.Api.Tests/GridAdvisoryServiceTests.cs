using System.Net;
using System.Text;
using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Infrastructure;
using GridTrading.Api.Services;
using GridTrading.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;

namespace GridTrading.Api.Tests;

public sealed class GridAdvisoryServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadOnlyHttpEndpointReturnsTheTypedAdvisoryContract()
    {
        await using var f = await Fixture.Create();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(f.Service);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            foreach (var converter in JsonSupport.Options.Converters) options.SerializerOptions.Converters.Add(converter);
        });
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapGridAdvisoryEndpoints();
        await app.StartAsync(Ct);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var response = await client.GetAsync("/api/v1/grid-advisory?environmentId=hyperliquid-testnet&symbol=SOL&accountId=test&strategyId=strategy", Ct);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal("BUY_ONLY", json.RootElement.GetProperty("gridMode").GetString());
            Assert.Equal(4, json.RootElement.GetProperty("frames").GetArrayLength());
            Assert.Equal(JsonValueKind.Number, json.RootElement.GetProperty("frames")[0].GetProperty("indicators").GetProperty("atr").ValueKind);
            Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("scenarios")[0].GetProperty("loss").ValueKind);
            using var invalid = await client.GetAsync("/api/v1/grid-advisory?environmentId=hyperliquid-testnet", Ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        finally { await app.StopAsync(Ct); }
    }

    [Fact]
    public async Task AnalysisUsesSavedStrategyAndNeverPreflightsTradesOrWritesState()
    {
        await using var f = await Fixture.Create();
        var before = f.Db.ChangeTracker.Entries().Select(x => x.State).ToArray();
        var result = await f.Service.GetAsync("hyperliquid-testnet", "SOL", "test", "strategy", Ct);
        Assert.Equal(4, result.Frames.Count);
        Assert.All(result.Frames, x => Assert.Null(x.Error));
        Assert.Equal("strategy", result.StrategyId);
        Assert.Equal(7, result.StrategyVersion);
        Assert.True(result.StrategyMatchesSymbol);
        Assert.Equal(5, result.Checks.Count);
        Assert.Equal(8, result.Scenarios.Count);
        Assert.DoesNotContain(result.Checks, x => x.Status == "INSUFFICIENT_DATA");
        Assert.Equal(before, f.Db.ChangeTracker.Entries().Select(x => x.State).ToArray());
        Assert.Empty(await f.Db.Cycles.ToListAsync(Ct)); Assert.Empty(await f.Db.Orders.ToListAsync(Ct));
        Assert.All(f.Handler.Hosts, host => Assert.Equal("api.hyperliquid-testnet.xyz", host));
        Assert.Contains(result.Checks.Single(x => x.Id == "costs").Reasons, x => x.Contains("taker"));
    }

    [Fact]
    public async Task CandleCacheIsSharedOnlyWithinTheSameNetworkSymbolAndInterval()
    {
        await using var f = await Fixture.Create();
        await f.Service.GetAsync("hyperliquid-testnet", "SOL", "test", "strategy", Ct);
        Assert.Equal(4, f.Handler.CandleCalls);
        await f.Service.GetAsync("hyperliquid-testnet", "SOL", "test", "strategy", Ct);
        Assert.Equal(4, f.Handler.CandleCalls);
        var mismatch = await f.Service.GetAsync("hyperliquid-testnet", "BTC", "test", "strategy", Ct);
        Assert.Equal(8, f.Handler.CandleCalls);
        Assert.False(mismatch.StrategyMatchesSymbol); Assert.Equal("INSUFFICIENT_DATA", mismatch.Status);
        Assert.Contains("differs", mismatch.Notice);
        var mainnet = await f.Service.GetAsync("hyperliquid-mainnet", "SOL", "main", "strategy", Ct);
        Assert.Equal(12, f.Handler.CandleCalls);
        Assert.Equal("main", mainnet.AccountId);
        Assert.Contains("api.hyperliquid.xyz", f.Handler.Hosts);
    }

    [Fact]
    public async Task RejectsAccountFromAnotherEnvironment()
    {
        await using var f = await Fixture.Create();
        var ex = await Assert.ThrowsAsync<TradingProblemException>(() => f.Service.GetAsync("hyperliquid-mainnet", "SOL", "test", "strategy", Ct));
        Assert.Equal("EXECUTION_ACCOUNT_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task MissingDailyHistoryAndStaleQuotesAreNotFavorable()
    {
        await using var f = await Fixture.Create();
        f.Handler.FailDaily = true;
        var result = await f.Service.GetAsync("hyperliquid-testnet", "SOL", "test", "strategy", Ct);
        Assert.Equal("INSUFFICIENT_DATA", result.Status);
        Assert.NotNull(result.Frames.Single(x => x.Interval == "1d").Error);
        f.Testnet.StaleQuote = true;
        result = await f.Service.GetAsync("hyperliquid-testnet", "SOL", "test", "strategy", Ct);
        Assert.Null(result.QuoteAsOf);
        Assert.Equal("INSUFFICIENT_DATA", result.Checks.Single(x => x.Id == "costs").Status);
    }

    [Fact]
    public async Task ManualConfigurationAndMissingEquityHaveExplicitOutcomes()
    {
        await using var f = await Fixture.Create();
        var entity = await f.Db.Strategies.SingleAsync(Ct);
        var request = JsonSerializer.Deserialize<StrategyRequest>(entity.ConfigurationJson, JsonSupport.Options)!;
        entity.ConfigurationJson = JsonSerializer.Serialize(request with { CenterSuggestionMode = "MANUAL", ManualCenterPrice = -1 }, JsonSupport.Options);
        await f.Db.SaveChangesAsync(Ct);
        var invalid = await f.Service.GetAsync("hyperliquid-testnet", "SOL", "test", "strategy", Ct);
        Assert.Equal("UNFAVORABLE", invalid.Status);
        entity.ConfigurationJson = JsonSerializer.Serialize(request with { CenterSuggestionMode = "MANUAL", ManualCenterPrice = 100 }, JsonSupport.Options);
        await f.Db.SaveChangesAsync(Ct);
        f.Handler.FailEquity = true;
        var missing = await f.Service.GetAsync("hyperliquid-testnet", "SOL", "test", "strategy", Ct);
        Assert.Equal("INSUFFICIENT_DATA", missing.Status);
        Assert.Equal("INSUFFICIENT_DATA", missing.Checks.Single(x => x.Id == "exposure").Status);
        Assert.NotEmpty(missing.Checks.Single(x => x.Id == "volatility").Metrics);
    }

    [Fact]
    public async Task MarketOnlyAndPaperAnalysisDoNotRequireAccountAccess()
    {
        await using var f = await Fixture.Create();
        var market = await f.Service.GetAsync("hyperliquid-testnet", "SOL", null, null, Ct);
        Assert.Equal(4, market.Frames.Count); Assert.Equal("INSUFFICIENT_DATA", market.Status);
        var before = f.Handler.CandleCalls;
        var paper = await f.Service.GetAsync("paper-local", "SOL", null, null, Ct);
        Assert.Equal(before, f.Handler.CandleCalls);
        Assert.Contains("Paper", paper.Notice);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required TradingDbContext Db { get; init; }
        public required HttpClient Http { get; init; }
        public required MemoryCache Cache { get; init; }
        public required DataHandler Handler { get; init; }
        public required ReadOnlyAdapter Testnet { get; init; }
        public required GridAdvisoryService Service { get; init; }
        public static async Task<Fixture> Create()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(Ct);
            var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync(Ct);
            foreach (var (id, network) in new[] { ("test", "TESTNET"), ("main", "MAINNET") })
                db.HyperliquidAccounts.Add(new() { Id = id, Name = id, AccountAddress = "fake-address-" + id, AgentAddress = "fake-agent-" + id, EncryptedAgentPrivateKey = "unused", Environment = network });
            var request = StrategyRequest.Default with { Symbol = "SOL", GridMode = GridMode.BuyOnly, MaxLevelsPerSide = 2, GridSpacingPoints = 50,
                GridSpacingStepPoints = 0, TakeProfitPoints = 100, BaseLotSize = 1, MaxNetLot = 2, BasketStopLossUsdt = 1000,
                DefaultExecutionEnvironmentId = "hyperliquid-testnet", DefaultExecutionAccountId = "test" };
            db.Strategies.Add(new() { Id = "strategy", Name = "Saved grid", Symbol = "SOL", Version = 7, ConfigurationJson = JsonSerializer.Serialize(request, JsonSupport.Options),
                DefaultExecutionEnvironmentId = "hyperliquid-testnet", DefaultExecutionAccountId = "test" });
            await db.SaveChangesAsync(Ct);
            var handler = new DataHandler(); var http = new HttpClient(handler); var configuration = new ConfigurationBuilder().Build();
            var testnet = new ReadOnlyAdapter("hyperliquid-testnet", "TESTNET", "test");
            var registry = new ExecutionEnvironmentRegistry([testnet, new ReadOnlyAdapter("hyperliquid-mainnet", "MAINNET", "main"), new ReadOnlyAdapter("paper-local", "PAPER", "paper")]);
            var cache = new MemoryCache(new MemoryCacheOptions());
            return new() { Connection = connection, Db = db, Http = http, Cache = cache, Handler = handler, Testnet = testnet,
                Service = new(db, registry, new(http, configuration, db), new(http, configuration), cache) };
        }
        public async ValueTask DisposeAsync() { Cache.Dispose(); Http.Dispose(); await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    private sealed class DataHandler : HttpMessageHandler
    {
        public int CandleCalls;
        public bool FailDaily, FailEquity;
        public List<string> Hosts { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Hosts) Hosts.Add(request.RequestUri!.Host);
            Assert.Equal("/info", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var type = body.RootElement.GetProperty("type").GetString();
            object response;
            switch (type)
            {
                case "candleSnapshot":
                    Interlocked.Increment(ref CandleCalls);
                    var interval = body.RootElement.GetProperty("req").GetProperty("interval").GetString();
                    if (FailDaily && interval == "1d") return new(HttpStatusCode.ServiceUnavailable);
                    var seconds = interval switch { "15m" => 900, "1h" => 3600, "4h" => 14400, _ => 86400 };
                    var end = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / seconds * seconds;
                    response = Enumerable.Range(0, 300).Select(i => new { t = (end - (300L - i) * seconds) * 1000,
                        o = 90m + i * .03m, h = 91m + i * .03m, l = 89m + i * .03m, c = 90.1m + i * .03m, v = 100 }).ToArray();
                    break;
                case "clearinghouseState":
                    if (FailEquity) return new(HttpStatusCode.ServiceUnavailable);
                    response = new { marginSummary = new { accountValue = "10000", totalMarginUsed = "0" }, withdrawable = "10000", assetPositions = Array.Empty<object>() }; break;
                case "spotClearinghouseState": response = new { balances = Array.Empty<object>() }; break;
                case "userAbstraction": response = "disabled"; break;
                case "metaAndAssetCtxs": response = new object[] { new { universe = new[] { new { name = "SOL", szDecimals = 2 } } }, new[] { new { markPx = "100", funding = "0.00001" } } }; break;
                default: throw new InvalidOperationException("Unexpected exchange operation: " + type);
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ReadOnlyAdapter(string id, string network, string account) : IExecutionAdapter
    {
        public bool StaleQuote;
        public ExecutionEnvironmentDescriptor Environment { get; } = new(id, "HYPERLIQUID", network, id);
        public Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ExecutionAccountDescriptor>>([new(account, id, account, true)]);
        public Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection selection, string symbol, decimal? price, CancellationToken ct) =>
            Task.FromResult(new ExecutionInstrument(symbol, id, 0, 2, price ?? 100, .01m, .1m, .1m, 1, 500, .0001m, .0005m, "HYPERLIQUID_USER_FEES", DateTimeOffset.UtcNow));
        public Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection selection, string symbol, CancellationToken ct) =>
            Task.FromResult(new ExecutionQuote(99.99m, 100.01m, 100, DateTimeOffset.UtcNow.AddMinutes(StaleQuote ? -2 : 0)));
        public Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection s, string symbol, CancellationToken ct) => throw new InvalidOperationException("Advisory must not preflight a trade");
        public Task PlaceOrdersAsync(ExecutionSelection s, GridConfiguration c, IEnumerable<OrderEntity> orders, CancellationToken ct) => throw new InvalidOperationException("Advisory must not trade");
        public Task CancelOrdersAsync(ExecutionSelection s, IEnumerable<OrderEntity> orders, CancellationToken ct) => throw new InvalidOperationException("Advisory must not cancel");
        public Task<decimal> FlattenAsync(ExecutionSelection s, CycleEntity cycle, GridConfiguration c, CancellationToken ct) => throw new InvalidOperationException("Advisory must not flatten");
        public Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection s, CycleEntity cycle, GridConfiguration c, CancellationToken ct) => throw new InvalidOperationException("Advisory must not reconcile");
    }
}
