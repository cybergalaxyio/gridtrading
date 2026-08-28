using System.Net;
using System.Text;
using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Exchanges.Hyperliquid;
using GridTrading.Api.Exchange;
using GridTrading.Api.Strategies.Grid;
using GridTrading.Api.Infrastructure;
using GridTrading.Api.Services;
using GridTrading.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GridTrading.Api.Tests;

public sealed class HyperliquidFillFlowTests
{
    [Fact]
    public void WebSocketAndRestFundingPayloadsNormalizeToTheSameId()
    {
        using var websocket = JsonDocument.Parse("""
            [{ "time": 1787695200000, "coin": "SOL", "usdc": "-0.125",
               "szi": "0.45", "fundingRate": "0.0001" }]
            """);
        using var rest = JsonDocument.Parse("""
            [{ "time": 1787695200000, "hash": "0xfunding",
               "delta": { "type": "funding", "coin": "SOL", "usdc": "-0.125",
                          "szi": "0.45", "fundingRate": "0.0001" } }]
            """);

        var websocketPayment = Assert.Single(HyperliquidExecutionAdapter.NormalizeFundingPayments(
            "account_testnet", websocket.RootElement.EnumerateArray().ToArray()));
        var restPayment = Assert.Single(HyperliquidExecutionAdapter.NormalizeFundingPayments(
            "account_testnet", rest.RootElement.EnumerateArray().ToArray()));

        Assert.Equal(websocketPayment, restPayment);
        Assert.Equal("hl-funding:account_testnet:SOL:1787695200000", websocketPayment.FundingId);
        Assert.Equal(-.125m, websocketPayment.UsdcDelta);
    }

    [Fact]
    public async Task FundingPaymentsAreAppliedOnceToTheMatchingCycle()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
        await using var db = new TradingDbContext(options);
        await db.Database.EnsureCreatedAsync(ct);
        var settledAt = DateTimeOffset.FromUnixTimeMilliseconds(1787695200000);
        var config = new GridConfiguration { Symbol = "SOL-USDC", IncludeFunding = true };
        var cycle = new CycleEntity
        {
            Id = "cycle_funding", StrategyId = "strategy_funding",
            ExecutionEnvironmentId = ExecutionEnvironmentIds.HyperliquidTestnet,
            ExecutionAccountId = "account_testnet", State = "PAUSED",
            FrozenConfigurationJson = JsonSerializer.Serialize(config, JsonSupport.Options),
            FrozenPlanJson = "{}", ExitReason = "", StartedAt = settledAt.AddHours(-1),
            LastReconciledAt = settledAt.AddMinutes(-1)
        };
        db.Cycles.Add(cycle);
        await db.SaveChangesAsync(ct);
        var lifecycle = new GridOrderLifecycle(db, new ExecutionEnvironmentRegistry([]),
            new ExecutionAccountOperationGate());
        var payments = new[]
        {
            new NormalizedFundingPayment("funding-paid", "SOL", -.125m, .45m, .0001m, settledAt),
            new NormalizedFundingPayment("funding-received", "SOL", .025m, .45m, -.00002m, settledAt.AddMinutes(1)),
            new NormalizedFundingPayment("funding-other-coin", "BTC", -1m, .01m, .0001m, settledAt)
        };

        var processed = await lifecycle.ProcessFundingPaymentsAsync("account_testnet", payments, ct);
        var repeated = await lifecycle.ProcessFundingPaymentsAsync("account_testnet", payments, ct);

        Assert.Equal(2, processed);
        Assert.Equal(0, repeated);
        Assert.Equal(.1m, cycle.AccruedFunding);
        var stored = (await db.FundingPayments.ToListAsync(ct)).OrderBy(x => x.OccurredAt).ToList();
        Assert.Equal(2, stored.Count);
        Assert.Equal(.125m, stored[0].FundingCost);
        Assert.Equal(-.025m, stored[1].FundingCost);
    }
    [Fact]
    public async Task EntryFillCreatesTakeProfitAndReplacementEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
        await using var db = new TradingDbContext(options);
        await db.Database.EnsureCreatedAsync(ct);

        var credentialKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GRID_TRADING_CREDENTIAL_KEY"] = credentialKey
        }).Build();
        var protector = new CredentialProtector(configuration);
        const string privateKey = "0x0000000000000000000000000000000000000000000000000000000000000001";
        var now = DateTimeOffset.UtcNow;
        var config = new GridConfiguration
        {
            Symbol = "SOLUSDT",
            TickSize = .1m,
            QuantityStep = .1m,
            MinOrderQuantity = .1m,
            MinOrderNotional = 1m,
            SizeDecimals = 1,
            CenterPrice = 100m,
            MaxLevelsPerSide = 2,
            InitialGapPoints = 1m,
            GridSpacingPoints = 1m,
            TakeProfitPoints = 2m,
            BaseLotSize = .2m,
            MaxNetLot = 5m,
            PostOnlyEntries = true,
            PostOnlyTakeProfits = true
        };
        var plan = GridMath.BuildPlan(config, TradingService.RulesFor(config));

        db.HyperliquidAccounts.Add(new HyperliquidAccountEntity
        {
            Id = "account_testnet",
            Name = "Testnet fixture",
            AccountAddress = "0x0000000000000000000000000000000000000002",
            AgentAddress = HyperliquidL1Signer.DeriveAddress(privateKey),
            EncryptedAgentPrivateKey = protector.Protect(privateKey),
            Environment = "TESTNET",
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Strategies.Add(new StrategyEntity
        {
            Id = "strategy_grid",
            Name = "SOL Grid",
            ExchangeAccountId = "account_testnet",
            Symbol = config.Symbol,
            ConfigurationJson = JsonSerializer.Serialize(config, JsonSupport.Options),
            CreatedAt = now,
            UpdatedAt = now
        });
        db.Cycles.Add(new CycleEntity
        {
            Id = "cycle_grid",
            StrategyId = "strategy_grid",
            ExecutionEnvironmentId = ExecutionEnvironmentIds.HyperliquidTestnet,
            ExecutionAccountId = "account_testnet",
            State = "RUNNING",
            FrozenConfigurationJson = JsonSerializer.Serialize(config, JsonSupport.Options),
            FrozenPlanJson = JsonSerializer.Serialize(plan, JsonSupport.Options),
            ExitReason = "",
            StartedAt = now,
            LastReconciledAt = now
        });
        db.Orders.AddRange(
            EntryOrder("entry_buy_0", "entry-buy-0", "7001", "BUY", 0, 99.9m, now),
            EntryOrder("entry_sell_0", "entry-sell-0", "7002", "SELL", 0, 100.1m, now));
        await db.SaveChangesAsync(ct);

        using var exchange = new FakeHyperliquidHandler();
        using var http = new HttpClient(exchange);
        var client = new HyperliquidTradingClient(http, configuration, db, protector,
            new HyperliquidNonceManager(db), new HyperliquidL1Signer());
        var adapter = new HyperliquidExecutionAdapter(db, client, new HyperliquidInfoClient(http, configuration),
            new HyperliquidOrderOwnershipService(db));
        var lifecycle = new GridOrderLifecycle(db, new ExecutionEnvironmentRegistry([adapter]),
            new ExecutionAccountOperationGate());
        using var message = JsonDocument.Parse("""
            {
              "channel": "userFills",
              "data": {
                "user": "0x0000000000000000000000000000000000000002",
                "isSnapshot": false,
                "fills": [{
                  "coin": "SOL", "px": "99.75", "sz": "0.2", "side": "B",
                  "time": 1787695200000, "hash": "0xfeed", "oid": 7001,
                  "fee": "0.001", "tid": 42
                }]
              }
            }
            """);

        Assert.True(HyperliquidWebSocketProtocol.TryReadUserFills(message.RootElement, out var update));
        Assert.NotNull(update);
        var normalizedFills = await adapter.NormalizeFillsAsync("account_testnet", update.Fills, ct);
        var processed = await lifecycle.ProcessFillsAsync("account_testnet", normalizedFills, ct);

        Assert.Equal(1, processed);
        var filledEntry = await db.Orders.SingleAsync(x => x.Id == "entry_buy_0", ct);
        Assert.Equal("FILLED", filledEntry.Status);
        Assert.Equal(.2m, filledEntry.FilledQuantity);

        var execution = await db.Executions.SingleAsync(ct);
        Assert.Equal("entry_buy_0", execution.OrderId);
        Assert.Equal("BUY", execution.Side);
        Assert.Equal(99.75m, execution.Price);
        Assert.Equal(.2m, execution.Quantity);
        Assert.Equal(.001m, execution.Fee);

        var lot = await db.VirtualLots.SingleAsync(ct);
        Assert.Equal("TP_PENDING", lot.Status);
        Assert.Equal("entry_buy_0", lot.EntryOrderId);
        Assert.Equal(0, lot.GridLevel);
        Assert.Equal(99.75m, lot.EntryFillPrice);
        Assert.Equal(.2m, lot.FilledQuantity);
        Assert.Equal(.2m, lot.RemainingQuantity);
        Assert.Equal(100m, lot.TakeProfitPrice);

        var takeProfit = await db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct);
        Assert.Equal(lot.TakeProfitOrderId, takeProfit.Id);
        Assert.Equal("SELL", takeProfit.Side);
        Assert.Equal("NEW", takeProfit.Status);
        Assert.Equal("8001", takeProfit.ExchangeOrderId);
        Assert.Equal(0, takeProfit.GridLevel);
        Assert.Equal(100m, takeProfit.Price);
        Assert.Equal(.2m, takeProfit.Quantity);

        var replacement = await db.Orders.SingleAsync(x => x.Kind == "ENTRY" && x.Side == "BUY" && x.Id != "entry_buy_0", ct);
        Assert.Equal("NEW", replacement.Status);
        Assert.Equal("8002", replacement.ExchangeOrderId);
        Assert.Equal(1, replacement.GridLevel);
        Assert.Equal(99.8m, replacement.Price);
        Assert.Equal(.2m, replacement.Quantity);

        var originalSell = await db.Orders.SingleAsync(x => x.Id == "entry_sell_0", ct);
        Assert.Equal("NEW", originalSell.Status);
        Assert.Equal("7002", originalSell.ExchangeOrderId);
        Assert.Equal(4, await db.Orders.CountAsync(ct));

        Assert.Equal(2, exchange.ExchangeRequests.Count);
        AssertOrderRequest(exchange.ExchangeRequests[0], takeProfit, isBuy: false, "100", "0.2", "Alo");
        AssertOrderRequest(exchange.ExchangeRequests[1], replacement, isBuy: true, "99.8", "0.2", "Alo");

        normalizedFills = await adapter.NormalizeFillsAsync("account_testnet", update.Fills, ct);
        var repeated = await lifecycle.ProcessFillsAsync("account_testnet", normalizedFills, ct);

        Assert.Equal(0, repeated);
        Assert.Equal(1, await db.Executions.CountAsync(ct));
        Assert.Equal(1, await db.VirtualLots.CountAsync(ct));
        Assert.Equal(4, await db.Orders.CountAsync(ct));
        Assert.Equal(2, exchange.ExchangeRequests.Count);

        using var orderMessage = JsonDocument.Parse("""
            {
              "channel": "orderUpdates",
              "data": [{
                "order": {
                  "coin": "SOL", "side": "A", "limitPx": "100.1", "sz": "0.2",
                  "oid": 7002, "timestamp": 1787695200000, "origSz": "0.2", "cloid": null
                },
                "status": "marginCanceled",
                "statusTimestamp": 1787695260000
              }]
            }
            """);
        Assert.True(HyperliquidWebSocketProtocol.TryReadOrderUpdates(orderMessage.RootElement, out var orderUpdate));
        Assert.NotNull(orderUpdate);

        var normalizedUpdates = await adapter.NormalizeOrderUpdatesAsync("account_testnet", orderUpdate.Updates, ct);
        var statusUpdates = await lifecycle.ProcessOrderUpdatesAsync("account_testnet", normalizedUpdates, ct);

        Assert.Equal(1, statusUpdates);
        Assert.Equal("CANCELLED", originalSell.Status);
        var replacementSell = await db.Orders.SingleAsync(x =>
            x.Kind == "ENTRY" && x.Side == "SELL" && x.Id != "entry_sell_0", ct);
        Assert.Equal("NEW", replacementSell.Status);
        Assert.Equal("8003", replacementSell.ExchangeOrderId);
        Assert.Equal(0, replacementSell.GridLevel);
        Assert.Equal(100.1m, replacementSell.Price);
        Assert.Equal(5, await db.Orders.CountAsync(ct));
        Assert.Equal(3, exchange.ExchangeRequests.Count);
    }

    [Fact]
    public async Task SplitEntryFillAmendsOneTakeProfitAndStillCreatesNextLevelEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
        await using var db = new TradingDbContext(options);
        await db.Database.EnsureCreatedAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var config = new GridConfiguration
        {
            Symbol = "SOL", TickSize = .01m, QuantityStep = .01m,
            MinOrderQuantity = .01m, MinOrderNotional = 5m, SizeDecimals = 2,
            CenterPrice = 101.935m, MaxLevelsPerSide = 3,
            GridSpacingPoints = 50m, TakeProfitPoints = 200m,
            BaseLotSize = .2m, LotSizeIncreasePercent = 16.2m,
            MaxNetLot = 10m, PostOnlyEntries = true, PostOnlyTakeProfits = true
        };
        var plan = GridMath.BuildPlan(config, TradingService.RulesFor(config));
        var cycle = new CycleEntity
        {
            Id = "cycle_grid", StrategyId = "strategy_grid",
            ExecutionEnvironmentId = ExecutionEnvironmentIds.HyperliquidTestnet,
            ExecutionAccountId = "account_testnet", State = "RUNNING",
            FrozenConfigurationJson = JsonSerializer.Serialize(config, JsonSupport.Options),
            FrozenPlanJson = JsonSerializer.Serialize(plan, JsonSupport.Options),
            ExitReason = "", StartedAt = now, LastReconciledAt = now
        };
        var buyZero = EntryOrder("entry_buy_0", "entry-buy-0", "7001", "BUY", 0, 101.68m, now);
        var sellZero = EntryOrder("entry_sell_0", "entry-sell-0", "7002", "SELL", 0, 102.19m, now);
        db.AddRange(cycle, buyZero, sellZero);
        await db.SaveChangesAsync(ct);

        var adapter = new RecordingAmendmentAdapter(102.2m);
        var lifecycle = new GridOrderLifecycle(db, new ExecutionEnvironmentRegistry([adapter]),
            new ExecutionAccountOperationGate());

        var first = await lifecycle.ProcessFillsAsync("account_testnet",
            [new NormalizedExecutionFill("fill-s0-1", "7002", "entry-sell-0",
                "SELL", 102.19m, .17m, .002605m, now)], ct);
        var second = await lifecycle.ProcessFillsAsync("account_testnet",
            [new NormalizedExecutionFill("fill-s0-2", "7002", "entry-sell-0",
                "SELL", 102.19m, .03m, .000459m, now.AddSeconds(29))], ct);

        Assert.Equal(1, first);
        Assert.Equal(1, second);
        Assert.Equal("RUNNING", cycle.State);
        Assert.Equal("FILLED", sellZero.Status);
        Assert.Equal(.2m, sellZero.FilledQuantity);

        var takeProfit = await db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct);
        Assert.Equal("BUY", takeProfit.Side);
        Assert.Equal(100.19m, takeProfit.Price);
        Assert.Equal(.2m, takeProfit.Quantity);
        Assert.Equal("NEW", takeProfit.Status);

        var lot = await db.VirtualLots.SingleAsync(ct);
        Assert.Equal(takeProfit.Id, lot.TakeProfitOrderId);
        Assert.Equal(.2m, lot.FilledQuantity);
        Assert.Equal(.2m, lot.RemainingQuantity);
        Assert.Equal(.003064m, lot.EntryFee);

        var amendment = Assert.Single(adapter.Amendments);
        Assert.Equal(takeProfit.Id, amendment.OrderId);
        Assert.Equal(100.19m, amendment.Price);
        Assert.Equal(.2m, amendment.Quantity);

        await db.SaveChangesAsync(ct);
        var openEntries = await db.Orders.Where(x => x.Kind == "ENTRY" &&
            (x.Status == "NEW" || x.Status == "PARTIALLY_FILLED")).OrderBy(x => x.Side).ToListAsync(ct);
        Assert.True(openEntries.Count == 2,
            string.Join("; ", (await db.Orders.ToListAsync(ct)).Select(x => $"{x.Side}/{x.Kind}/{x.Status}/L{x.GridLevel}/{x.Quantity}")));
        Assert.Contains(openEntries, x => x.Id == buyZero.Id && x.GridLevel == 0);
        Assert.Contains(openEntries, x => x.Side == "SELL" && x.GridLevel == 1 && x.Quantity == .23m);
        Assert.Empty(await db.RiskAlerts.ToListAsync(ct));
    }

    [Fact]
    public async Task EntryMaintenanceMovesUnfilledOrderButLeavesPartialFillInPlace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
        await using var db = new TradingDbContext(options);
        await db.Database.EnsureCreatedAsync(ct);

        var credentialKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GRID_TRADING_CREDENTIAL_KEY"] = credentialKey
        }).Build();
        var protector = new CredentialProtector(configuration);
        const string privateKey = "0x0000000000000000000000000000000000000000000000000000000000000001";
        var now = DateTimeOffset.UtcNow;
        var config = new GridConfiguration
        {
            Symbol = "SOLUSDT", TickSize = .1m, QuantityStep = .1m, MinOrderQuantity = .1m,
            MinOrderNotional = 1m, SizeDecimals = 1, CenterPrice = 100m, MaxLevelsPerSide = 4,
            InitialGapPoints = 1m, GridSpacingPoints = 1m, TakeProfitPoints = 2m,
            BaseLotSize = .2m, MaxNetLot = 5m, PostOnlyEntries = true
        };
        var plan = GridMath.BuildPlan(config, TradingService.RulesFor(config));

        db.HyperliquidAccounts.Add(new HyperliquidAccountEntity
        {
            Id = "account_testnet", Name = "Testnet fixture",
            AccountAddress = "0x0000000000000000000000000000000000000002",
            AgentAddress = HyperliquidL1Signer.DeriveAddress(privateKey),
            EncryptedAgentPrivateKey = protector.Protect(privateKey),
            Environment = "TESTNET", Enabled = true, CreatedAt = now, UpdatedAt = now
        });
        var strategy = new StrategyEntity
        {
            Id = "strategy_grid", Name = "SOL Grid", ExchangeAccountId = "account_testnet",
            Symbol = config.Symbol, ConfigurationJson = JsonSerializer.Serialize(config, JsonSupport.Options),
            CreatedAt = now, UpdatedAt = now
        };
        var cycle = new CycleEntity
        {
            Id = "cycle_grid", StrategyId = strategy.Id,
            ExecutionEnvironmentId = ExecutionEnvironmentIds.HyperliquidTestnet,
            ExecutionAccountId = "account_testnet", State = "RUNNING",
            FrozenConfigurationJson = JsonSerializer.Serialize(config, JsonSupport.Options),
            FrozenPlanJson = JsonSerializer.Serialize(plan, JsonSupport.Options),
            ExitReason = "", StartedAt = now, LastReconciledAt = now
        };
        var buyZero = EntryOrder("entry_buy_0", "entry-buy-0", "7001", "BUY", 0, 99.9m, now);
        var staleSell = EntryOrder("entry_sell_3", "entry-sell-3", "7002", "SELL", 3, 100.4m, now);
        db.AddRange(strategy, cycle, buyZero, staleSell);
        await db.SaveChangesAsync(ct);

        using var exchange = new FakeHyperliquidHandler();
        using var http = new HttpClient(exchange);
        var client = new HyperliquidTradingClient(http, configuration, db, protector,
            new HyperliquidNonceManager(db), new HyperliquidL1Signer());
        var adapter = new HyperliquidExecutionAdapter(db, client, new HyperliquidInfoClient(http, configuration),
            new HyperliquidOrderOwnershipService(db));
        var lifecycle = new GridOrderLifecycle(db, new ExecutionEnvironmentRegistry([adapter]),
            new ExecutionAccountOperationGate());

        await lifecycle.MaintainEntryOrdersAsync(cycle, config,
            new ExecutionQuote(99.9m, 100.1m, 100m, now), ct);

        Assert.Equal("CANCELLED", staleSell.Status);
        Assert.Equal("NEW", buyZero.Status);
        var replacement = await db.Orders.SingleAsync(x =>
            x.Kind == "ENTRY" && x.Side == "SELL" && x.Id != staleSell.Id, ct);
        Assert.Equal(0, replacement.GridLevel);
        Assert.Equal(100.1m, replacement.Price);
        Assert.Equal(.2m, replacement.Quantity);
        Assert.Equal("NEW", replacement.Status);

        Assert.Equal(2, exchange.ExchangeRequests.Count);
        Assert.Equal("cancelByCloid", exchange.ExchangeRequests[0].GetProperty("action").GetProperty("type").GetString());
        AssertOrderRequest(exchange.ExchangeRequests[1], replacement, isBuy: false, "100.1", "0.2", "Alo");

        replacement.Status = "PARTIALLY_FILLED";
        replacement.FilledQuantity = .1m;
        await db.SaveChangesAsync(ct);

        await lifecycle.MaintainEntryOrdersAsync(cycle, config,
            new ExecutionQuote(100.2m, 100.3m, 100.25m, now), ct);

        Assert.Equal("PARTIALLY_FILLED", replacement.Status);
        Assert.Equal(2, exchange.ExchangeRequests.Count);
    }

    private static OrderEntity EntryOrder(string id, string clientOrderId, string exchangeOrderId, string side,
        int level, decimal price, DateTimeOffset now) => new()
    {
        Id = id,
        CycleId = "cycle_grid",
        ClientOrderId = clientOrderId,
        ExchangeOrderId = exchangeOrderId,
        Symbol = "SOLUSDT",
        Side = side,
        Kind = "ENTRY",
        Status = "NEW",
        GridLevel = level,
        Price = price,
        Quantity = .2m,
        CreatedAt = now,
        UpdatedAt = now
    };

    private static void AssertOrderRequest(JsonElement request, OrderEntity order, bool isBuy,
        string price, string size, string tif)
    {
        var action = request.GetProperty("action");
        Assert.Equal("order", action.GetProperty("type").GetString());
        var submitted = action.GetProperty("orders")[0];
        Assert.Equal(isBuy, submitted.GetProperty("b").GetBoolean());
        Assert.Equal(price, submitted.GetProperty("p").GetString());
        Assert.Equal(size, submitted.GetProperty("s").GetString());
        Assert.False(submitted.GetProperty("r").GetBoolean());
        Assert.Equal(tif, submitted.GetProperty("t").GetProperty("limit").GetProperty("tif").GetString());
        Assert.Equal(HyperliquidWireCodec.CreateCloid(order.ClientOrderId), submitted.GetProperty("c").GetString());
    }

    private sealed class FakeHyperliquidHandler : HttpMessageHandler
    {
        private int _nextOrderId = 8001;

        public List<JsonElement> ExchangeRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var payload = await request.Content!.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(payload);
            if (request.RequestUri!.AbsolutePath == "/info")
            {
                var type = document.RootElement.GetProperty("type").GetString();
                return type switch
                {
                    "meta" => Json("""{ "universe": [{ "name": "SOL", "szDecimals": 1 }] }"""),
                    "l2Book" => Json("""{ "levels": [[{ "px": "99.9" }], [{ "px": "100.1" }]] }"""),
                    _ => throw new InvalidOperationException($"Unexpected info request: {payload}")
                };
            }

            if (request.RequestUri.AbsolutePath != "/exchange")
                throw new InvalidOperationException($"Unexpected request URI: {request.RequestUri}");

            ExchangeRequests.Add(document.RootElement.Clone());
            var orderId = _nextOrderId++;
            return Json($$"""
                {
                  "status": "ok",
                  "response": { "data": { "statuses": [{ "resting": { "oid": {{orderId}} } }] } }
                }
                """);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class RecordingAmendmentAdapter(decimal mid) : IExecutionAdapter, IOrderAmendmentAdapter
    {
        public ExecutionEnvironmentDescriptor Environment { get; } = new(
            ExecutionEnvironmentIds.HyperliquidTestnet, "HYPERLIQUID", "TESTNET", "Test adapter");
        public List<(string OrderId, decimal Price, decimal Quantity)> Amendments { get; } = [];

        public Task PlaceOrdersAsync(ExecutionSelection selection, GridConfiguration config,
            IEnumerable<OrderEntity> orders, CancellationToken ct)
        {
            foreach (var order in orders)
            {
                order.ExchangeOrderId = Ids.New("exchange");
                order.Status = "NEW";
            }
            return Task.CompletedTask;
        }

        public Task AmendOrderAsync(ExecutionSelection selection, GridConfiguration config, OrderEntity order,
            decimal price, decimal quantity, CancellationToken ct)
        {
            Amendments.Add((order.Id, price, quantity));
            order.Price = price;
            order.Quantity = quantity;
            order.Status = "NEW";
            return Task.CompletedTask;
        }

        public Task CancelOrdersAsync(ExecutionSelection selection, IEnumerable<OrderEntity> orders, CancellationToken ct)
        {
            foreach (var order in orders) order.Status = "CANCELLED";
            return Task.CompletedTask;
        }

        public Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection selection, string symbol, CancellationToken ct) =>
            Task.FromResult(new ExecutionQuote(mid - .01m, mid + .01m, mid, DateTimeOffset.UtcNow));
        public Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection selection, string symbol,
            decimal? referencePrice, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection selection, string symbol, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<decimal> FlattenAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection selection, CycleEntity cycle,
            GridConfiguration config, CancellationToken ct) => throw new NotSupportedException();
    }
}
