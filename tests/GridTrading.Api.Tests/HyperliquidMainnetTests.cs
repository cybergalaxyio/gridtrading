using System.Net;
using System.Text;
using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Exchange;
using GridTrading.Api.Execution;
using GridTrading.Api.Exchanges.Hyperliquid;
using GridTrading.Api.Hubs;
using GridTrading.Api.Infrastructure;
using GridTrading.Api.Services;
using GridTrading.Api.Strategies.Grid;
using GridTrading.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;

namespace GridTrading.Api.Tests;

public sealed partial class HyperliquidMainnetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("TESTNET", "Info", "https://api.hyperliquid-testnet.xyz/info")]
    [InlineData("MAINNET", "Info", "https://api.hyperliquid.xyz/info")]
    [InlineData("MAINNET", "Exchange", "https://api.hyperliquid.xyz/exchange")]
    [InlineData("MAINNET", "WebSocket", "wss://api.hyperliquid.xyz/ws")]
    public void OfficialEndpointsAreSelectedByNetwork(string network, string kind, string expected) =>
        Assert.Equal(expected, HyperliquidNetwork.Endpoint(new ConfigurationBuilder().Build(), network, kind).AbsoluteUri);

    [Theory]
    [InlineData("https://api.hyperliquid-testnet.xyz/exchange")]
    [InlineData("http://api.hyperliquid.xyz/exchange")]
    [InlineData("https://api.hyperliquid.xyz:8443/exchange")]
    [InlineData("https://api.hyperliquid.xyz/exchange?redirect=other")]
    [InlineData("https://api.hyperliquid.xyz/exchange#fragment")]
    [InlineData("https://user@api.hyperliquid.xyz/exchange")]
    [InlineData("https://api.hyperliquid.xyz.example.com/exchange")]
    public void MainnetRejectsWrongOrAmbiguousEndpoints(string url)
    {
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Hyperliquid:Mainnet:ExchangeUrl"] = url }).Build();
        Assert.Equal("HYPERLIQUID_ENDPOINT_MISMATCH", Assert.Throws<TradingProblemException>(() =>
            HyperliquidNetwork.Endpoint(settings, "MAINNET", "Exchange")).Code);
    }

    [Theory]
    [InlineData("SOL")]
    [InlineData("SOLUSDT")]
    [InlineData("SOLUSDC")]
    [InlineData("SOL-USDC")]
    [InlineData("sol/usdc")]
    public void SolAliasesResolveToPerpetualCoin(string symbol) => Assert.Equal("SOL", HyperliquidTradingClient.ToCoin(symbol));

    [Fact]
    public void MarketSubscriptionsCannotMixNetworks()
    {
        var registry = new HyperliquidMarketSubscriptionRegistry();
        registry.Subscribe("live", "SOL-USDC", "MAINNET");
        registry.Subscribe("test", "BTC", "TESTNET");
        Assert.Equal(["SOL"], registry.ActiveSymbols("MAINNET"));
        Assert.Equal(["BTC"], registry.ActiveSymbols("TESTNET"));
        Assert.NotEqual(HyperliquidMarketGroups.Group("SOL", "MAINNET"), HyperliquidMarketGroups.Group("SOL", "TESTNET"));
        registry.Unsubscribe("live", "SOLUSDC", "MAINNET");
        Assert.Empty(registry.ActiveSymbols("MAINNET"));
        Assert.Single(registry.ActiveSymbols("TESTNET"));
    }

    [Fact]
    public async Task MainnetOrderActionsUseOfficialExchangeEndpoint()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Client.PlaceLimitAsync("live", "SOLUSDC", true, 100m, 1m, true, "entry", Ct);
        await f.Client.ModifyLimitAsync("live", "SOL", true, 100m, 1m, true, "entry", Ct);
        await f.Client.CancelByCloidAsync("live", "SOL", "entry", Ct);
        await f.Client.PlaceLimitAsync("live", "SOL", false, 99m, 1m, false, "exit", Ct, immediateOrCancel: true, reduceOnly: true);
        Assert.Equal(4, f.Handler.Actions.Count);
        Assert.All(f.Handler.Hosts, host => Assert.Equal("api.hyperliquid.xyz", host));
    }

    [Fact]
    public async Task BothAccountsRouteIndependentlyIncludingMetadataAndFees()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Client.PlaceLimitAsync("live", "SOL-USDC", true, 100m, .12m, true, "entry-main", Ct);
        var main = Assert.Single(f.Handler.Actions);
        var nonce = main.GetProperty("nonce").GetInt64();
        var order = new HyperliquidLimitOrder(0, true, "100", "0.12", false, "Alo", HyperliquidWireCodec.CreateCloid("entry-main"));
        var expected = new HyperliquidL1Signer().Sign(HyperliquidWireCodec.PackOrderAction([order]), Fixture.Key, nonce, true);
        Assert.Equal(expected.R, main.GetProperty("signature").GetProperty("r").GetString());
        Assert.All(f.Handler.Hosts, host => Assert.Equal("api.hyperliquid.xyz", host));
        f.Handler.Hosts.Clear();
        await f.Client.PlaceLimitAsync("test", "SOL", true, 100m, .12m, true, "entry-test", Ct);
        Assert.All(f.Handler.Hosts, host => Assert.Equal("api.hyperliquid-testnet.xyz", host));
        f.Handler.Hosts.Clear();
        var instrument = await f.Info.GetPerpetualInstrument("SOL-USDC", null, Fixture.Address, Ct, "MAINNET");
        Assert.Equal("MAINNET", instrument.Environment);
        Assert.Equal(.0001m, instrument.MakerFeeRate);
        Assert.All(f.Handler.Hosts, host => Assert.Equal("api.hyperliquid.xyz", host));
        var registry = new ExecutionEnvironmentRegistry([f.Adapter, new HyperliquidExecutionAdapter(f.Db, f.Client, f.Info, new(f.Db))]);
        Assert.Equal("hyperliquid-mainnet", (await registry.ResolveAsync(null, "live", Ct)).EnvironmentId);
        Assert.Equal("EXECUTION_ACCOUNT_NOT_FOUND", (await Assert.ThrowsAsync<TradingProblemException>(() => registry.ResolveAsync("hyperliquid-testnet", "live", Ct))).Code);
    }

    [Theory]
    [InlineData("unapproved", "API_WALLET_NOT_APPROVED")]
    [InlineData("unfunded", "TESTNET_ACCOUNT_UNFUNDED")]
    [InlineData("orders", "MAINNET_OPEN_ORDERS_EXIST")]
    public async Task MainnetPreflightFailsClosed(string condition, string code)
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.Condition = condition;
        Assert.Equal(code, (await Assert.ThrowsAsync<TradingProblemException>(() => f.Adapter.PreflightStartAsync(new("hyperliquid-mainnet", "live"), "SOL", Ct))).Code);
        Assert.Empty(f.Handler.Actions);
    }

    [Fact]
    public async Task PreflightDoesNotRequirePresetLeverage()
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.Condition = "leverage";
        var quote = await f.Adapter.PreflightStartAsync(new("hyperliquid-mainnet", "live"), "SOL-USDC", Ct);
        Assert.Equal(100m, quote.Mid);
        Assert.Empty(f.Handler.Actions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmergencyExitUsesActualPositionAndConfirmsResidual(bool residual)
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.Position = -.12m;
        f.Handler.LeaveResidual = residual;
        var cycle = await f.AddCycleAsync();
        cycle.ReconstructedNetQuantity = -9m; // stale local attribution must never size the live close
        var left = await f.Adapter.FlattenAsync(new("hyperliquid-mainnet", "live"), cycle, ExampleConfig(), Ct);
        Assert.Equal(residual ? -.02m : 0m, left);
        var action = Assert.Single(f.Handler.Actions).GetProperty("action").GetProperty("orders")[0];
        Assert.Equal("0.12", action.GetProperty("s").GetString());
        Assert.True(action.GetProperty("r").GetBoolean());
        Assert.Equal("Ioc", action.GetProperty("t").GetProperty("limit").GetProperty("tif").GetString());
    }

    [Theory]
    [InlineData("MAINNET", 0)]
    [InlineData("HYPERLIQUID", 500)]
    public async Task OperatorConfigurationRunsWithoutTrialCapsOrExtraLiveConfirmation(string confirmation, int stop)
    {
        await using var f = await Fixture.CreateAsync();
        var config = ExampleConfig() with
        {
            Symbol = "ETHUSDC", MaxLevelsPerSide = 10, BaseLotSize = 1m, MaxTradeLot = 5m,
            MaxNetLot = 20m, LotSizeIncreasePercent = 25m, BasketStopLossUsdt = stop
        };
        var workflow = await f.WorkflowAsync(config);
        var result = await workflow.StartCycleAsync("strategy", new("preview", 100m, new(true, true, confirmation)), "start-custom", Ct);
        Assert.Equal("RUNNING", result.Cycle.State);
        var frozen = JsonSerializer.Deserialize<GridConfiguration>(result.Cycle.FrozenConfigurationJson, JsonSupport.Options)!;
        Assert.Equal(config, frozen);
        Assert.Equal(2, f.Handler.Actions.Count);
        Assert.All(f.Handler.Actions, request =>
        {
            var order = request.GetProperty("action").GetProperty("orders")[0];
            Assert.Equal("1", order.GetProperty("s").GetString());
            Assert.Equal(1, order.GetProperty("a").GetInt32());
        });
    }

    [Fact]
    public async Task UncertainStartupRemainsNonterminalForReconciliationAndClose()
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.Condition = "timeout";
        var workflow = await f.WorkflowAsync();
        await Assert.ThrowsAsync<HttpRequestException>(() => workflow.StartCycleAsync("strategy", new("preview", 100m, new(true, true, "MAINNET")), "start", Ct));
        var cycle = await f.Db.Cycles.SingleAsync(Ct);
        Assert.Equal("FAULT", cycle.State);
        Assert.False(cycle.IsTerminal);
        Assert.Null(cycle.EndedAt);
    }

    [Fact]
    public async Task ExplicitMainnetStartSendsTwoSmallEntriesAndReservesSymbol()
    {
        await using var f = await Fixture.CreateAsync();
        var workflow = await f.WorkflowAsync();
        var result = await workflow.StartCycleAsync("strategy", new("preview", 100m, new(true, true, "MAINNET")), "start-ok", Ct);
        Assert.Equal("RUNNING", result.Cycle.State);
        Assert.Equal(2, f.Handler.Actions.Count);
        Assert.All(f.Handler.Actions, request => Assert.False(request.GetProperty("action").GetProperty("orders")[0].GetProperty("r").GetBoolean()));
        Assert.All(f.Handler.Hosts, host => Assert.Equal("api.hyperliquid.xyz", host));
    }

    [Fact]
    public async Task MainnetAccountBalanceUsesStandardPerpEquityAndCorrectNetwork()
    {
        await using var f = await Fixture.CreateAsync();
        var market = new HyperliquidMarketDataClient(f.Http, f.Settings, f.Db);
        var state = await market.GetAccountStateAsync("live", "SOL-USDC", Ct);
        Assert.Equal(100m, state.TradingEquity);
        Assert.Equal(100m, state.AvailableBalance);
        Assert.Equal("disabled", state.AccountMode);
        Assert.All(f.Handler.Hosts, host => Assert.Equal("api.hyperliquid.xyz", host));
    }

    [Fact]
    public async Task StaleQuotePreventsMainnetStartWithoutSendingOrders()
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.Condition = "stale-book";
        Assert.Equal("MARKET_DATA_STALE", (await Assert.ThrowsAsync<TradingProblemException>(() => f.Adapter.PreflightStartAsync(new("hyperliquid-mainnet", "live"), "SOL", Ct))).Code);
        Assert.Empty(f.Handler.Actions);
    }

    [Fact]
    public async Task NonceReservationIgnoresStaleTrackedAccountAcrossScopes()
    {
        await using var f = await Fixture.CreateAsync();
        var account = await f.Db.HyperliquidAccounts.SingleAsync(x => x.Id == "live", Ct);
        account.LastNonce = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        await f.Db.SaveChangesAsync(Ct);
        await using var second = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(f.Connection).Options);
        _ = await second.HyperliquidAccounts.SingleAsync(x => x.Id == "live", Ct);
        var firstNonce = await new HyperliquidNonceManager(f.Db).NextAsync("live", Ct);
        var secondNonce = await new HyperliquidNonceManager(second).NextAsync("live", Ct);
        Assert.Equal(firstNonce + 1, secondNonce);
    }

    [Theory]
    [InlineData(GridMode.BuyOnly, true, true)]
    [InlineData(GridMode.BuyOnly, true, false)]
    [InlineData(GridMode.SellOnly, false, true)]
    [InlineData(GridMode.SellOnly, false, false)]
    public async Task SingleModeStartsOnlySelectedEntryAndSendsOppositeTakeProfit(GridMode mode, bool isBuy, bool postOnlyTp)
    {
        await using var f = await Fixture.CreateAsync();
        var config = ExampleConfig() with { GridMode = mode, MaxNetLot = .12m, PostOnlyTakeProfits = postOnlyTp };
        var workflow = await f.WorkflowAsync(config);
        var (_, cycle) = await workflow.StartCycleAsync("strategy",
            new("preview", 100m, new(true, true, "MAINNET")), "single-start", Ct);
        var entry = await f.Db.Orders.SingleAsync(Ct);
        Assert.Equal(isBuy ? "BUY" : "SELL", entry.Side);
        var submitted = Assert.Single(f.Handler.Actions).GetProperty("action").GetProperty("orders");
        Assert.Equal(1, submitted.GetArrayLength());
        Assert.Equal(isBuy, submitted[0].GetProperty("b").GetBoolean());
        Assert.Equal("Alo", submitted[0].GetProperty("t").GetProperty("limit").GetProperty("tif").GetString());

        var lifecycle = new GridOrderLifecycle(f.Db, new ExecutionEnvironmentRegistry([f.Adapter]),
            new ExecutionAccountOperationGate());
        await lifecycle.ProcessFillsAsync("live", [new NormalizedExecutionFill("single-fill",
            entry.ExchangeOrderId, entry.ClientOrderId, entry.Side, entry.Price, entry.Quantity, 0m,
            DateTimeOffset.UtcNow)], Ct);

        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", Ct);
        Assert.Equal(isBuy ? "SELL" : "BUY", tp.Side);
        Assert.Equal(entry.Quantity, tp.Quantity);
        Assert.Equal(isBuy ? entry.Price + .4m : entry.Price - .4m, tp.Price);
        Assert.Equal("NEW", tp.Status);
        Assert.Equal("RUNNING", cycle.State);
        Assert.Equal(2, f.Handler.Actions.Count);
        var protection = f.Handler.Actions[1].GetProperty("action").GetProperty("orders");
        Assert.Equal(1, protection.GetArrayLength());
        Assert.Equal(!isBuy, protection[0].GetProperty("b").GetBoolean());
        Assert.Equal(postOnlyTp ? "Alo" : "Gtc", protection[0].GetProperty("t").GetProperty("limit").GetProperty("tif").GetString());
        Assert.Single(await f.Db.Orders.Where(x => x.Kind == "ENTRY").ToListAsync(Ct));
        Assert.All(f.Handler.Hosts, host => Assert.Equal("api.hyperliquid.xyz", host));
    }

    private static GridConfiguration ExampleConfig() => new()
    {
        Symbol = "SOLUSDC", CenterPrice = 100m, TickSize = .01m, QuantityStep = .01m, MinOrderQuantity = .01m,
        MinOrderNotional = 10m, SizeDecimals = 2, BaseLotSize = .12m, MaxNetLot = .24m, MaxTradeLot = .12m,
        MaxLevelsPerSide = 2, WorkingEntriesPerSide = 1, InitialGapPoints = 50m, GridSpacingPoints = 50m,
        TakeProfitPoints = 40m, BasketStopLossUsdt = 100m
    };

    private sealed class Fixture : IAsyncDisposable
    {
        public const string Key = "0x0123456789012345678901234567890123456789012345678901234567890123";
        public const string TestKey = "0x0000000000000000000000000000000000000000000000000000000000000001";
        public const string Address = "0x0000000000000000000000000000000000000002";
        public required SqliteConnection Connection { get; init; }
        public required TradingDbContext Db { get; init; }
        public required IConfiguration Settings { get; init; }
        public Handler Handler { get; } = new();
        private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddSignalR().Services.BuildServiceProvider();
        public required HttpClient Http { get; set; }
        public HyperliquidTradingClient Client => new(Http, Settings, Db, new(Settings), new(Db), new());
        public HyperliquidInfoClient Info => new(Http, Settings);
        public HyperliquidExecutionAdapter Adapter => new(Db, Client, Info, new(Db), "MAINNET");
        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync(Ct);
            var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync(Ct);
            var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GRID_TRADING_CREDENTIAL_KEY"] = Convert.ToBase64String(Enumerable.Range(1,32).Select(x => (byte)x).ToArray())
            }).Build();
            var f = new Fixture { Connection = connection, Db = db, Settings = settings, Http = null! };
            f.Http = new HttpClient(f.Handler);
            foreach (var (id, network) in new[] { ("live", "MAINNET"), ("test", "TESTNET") })
                db.HyperliquidAccounts.Add(new() { Id = id, Name = id, AccountAddress = Address, AgentAddress = HyperliquidL1Signer.DeriveAddress(id == "live" ? Key : TestKey), EncryptedAgentPrivateKey = new CredentialProtector(settings).Protect(id == "live" ? Key : TestKey), Environment = network, Enabled = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
            return f;
        }
        public async Task<GridStrategyWorkflow> WorkflowAsync(GridConfiguration? selected = null)
        {
            var config = selected ?? ExampleConfig();
            Db.Strategies.Add(new() { Id = "strategy", Name = "Operator configuration", Symbol = config.Symbol, DefaultExecutionEnvironmentId = "hyperliquid-mainnet", DefaultExecutionAccountId = "live", ConfigurationJson = "{}", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await Db.SaveChangesAsync(Ct);
            var previews = new PreviewStore();
            previews.Items["preview"] = new("preview", "strategy", 1, "hyperliquid-mainnet", "live", DateTimeOffset.UtcNow.AddMinutes(5), config, GridMath.BuildPlan(config, TradingService.RulesFor(config)));
            var registry = new ExecutionEnvironmentRegistry([Adapter]);
            var gate = new ExecutionAccountOperationGate();
            return new(Db, new(), previews, registry, new(Db, registry, gate), _services.GetRequiredService<IHubContext<TradingHub>>(), gate);
        }
        public async Task<CycleEntity> AddCycleAsync()
        {
            var cycle = new CycleEntity { Id = "cycle", StrategyId = "strategy", ExecutionEnvironmentId = "hyperliquid-mainnet", ExecutionAccountId = "live", State = "CLOSING", FrozenConfigurationJson = "{}", FrozenPlanJson = "{}", ExitReason = "", StartedAt = DateTimeOffset.UtcNow, LastReconciledAt = DateTimeOffset.UtcNow };
            Db.Cycles.Add(cycle); await Db.SaveChangesAsync(Ct); return cycle;
        }
        public async ValueTask DisposeAsync() { Http.Dispose(); await _services.DisposeAsync(); await Db.DisposeAsync(); await Connection.DisposeAsync(); }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];
        public List<JsonElement> Actions { get; } = [];
        public string Condition { get; set; } = "";
        public decimal Position { get; set; }
        public bool LeaveResidual { get; set; }
        public object[] OpenOrders { get; set; } = [];
        public decimal OtherPosition { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Hosts.Add(request.RequestUri!.Host);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = payload.RootElement;
            if (request.RequestUri.AbsolutePath == "/info")
                return Json(root.GetProperty("type").GetString() switch
                {
                    "meta" => """{"universe":[{"name":"SOL","szDecimals":2},{"name":"ETH","szDecimals":2}]}""",
                    "metaAndAssetCtxs" => """[{"universe":[{"name":"SOL","szDecimals":2}]},[{"markPx":"100","midPx":"100"}]]""",
                    "userFees" => """{"userAddRate":"0.0001","userCrossRate":"0.0004"}""",
                    "l2Book" => $$"""{"time":{{DateTimeOffset.UtcNow.AddSeconds(Condition == "stale-book" ? -30 : 0).ToUnixTimeMilliseconds()}},"levels":[[{"px":"99.9"}],[{"px":"100.1"}]]}""",
                    "userRole" => Condition == "unapproved" ? """{"role":"missing"}""" : JsonSerializer.Serialize(new { role = "agent", data = new { user = Fixture.Address } }),
                    "clearinghouseState" => JsonSerializer.Serialize(new { marginSummary = new { accountValue = Condition == "unfunded" ? "0" : "100" }, withdrawable = "100", assetPositions = new[] { new { position = new { coin = "SOL", szi = Position.ToString(System.Globalization.CultureInfo.InvariantCulture), positionValue = "12", unrealizedPnl = "0" } }, new { position = new { coin = "BTC", szi = (Condition == "other-position" ? 1m : OtherPosition).ToString(System.Globalization.CultureInfo.InvariantCulture), positionValue = "12", unrealizedPnl = "0" } } } }),
                    "spotClearinghouseState" => """{"balances":[]}""",
                    "userAbstraction" => "\"disabled\"",
                    "openOrders" or "frontendOpenOrders" => Condition == "orders" ? """[{"coin":"SOL","oid":5,"side":"B","sz":"0.12","cloid":null}]""" : JsonSerializer.Serialize(OpenOrders),
                    "activeAssetData" => JsonSerializer.Serialize(new { leverage = new { type = "cross", value = Condition == "leverage" ? 10 : 1 } }),
                    var type => throw new InvalidOperationException(type)
                });
            Actions.Add(root.Clone());
            if (Condition == "timeout") throw new HttpRequestException("Synthetic uncertain exchange response");
            var action = root.GetProperty("action");
            if (action.GetProperty("type").GetString() == "cancelByCloid")
                return Json("""{"status":"ok","response":{"type":"cancel","data":{"statuses":["success"]}}}""");
            if (action.GetProperty("type").GetString() == "order" && action.GetProperty("orders")[0].GetProperty("r").GetBoolean())
            {
                Position = LeaveResidual ? -.02m : 0m;
                return Json("""{"status":"ok","response":{"type":"order","data":{"statuses":[{"filled":{"oid":99,"totalSz":"0.12"}}]}}}""");
            }
            return Json("""{"status":"ok","response":{"type":"order","data":{"statuses":[{"resting":{"oid":10}}]}}}""");
        }
        private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}
