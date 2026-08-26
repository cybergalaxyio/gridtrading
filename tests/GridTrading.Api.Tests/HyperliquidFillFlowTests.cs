using System.Net;
using System.Text;
using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Exchange;
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
        var coordinator = new HyperliquidCycleCoordinator(db, client,
            new HyperliquidOrderOwnershipService(db), new HyperliquidAccountOperationGate());
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
        var processed = await coordinator.ProcessWebSocketFillsAsync("account_testnet", update.Fills, ct);

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

        var repeated = await coordinator.ProcessWebSocketFillsAsync("account_testnet", update.Fills, ct);

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

        var statusUpdates = await coordinator.ProcessWebSocketOrderUpdatesAsync(
            "account_testnet", orderUpdate.Updates, ct);

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
            Id = "cycle_grid", StrategyId = strategy.Id, State = "RUNNING",
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
        var coordinator = new HyperliquidCycleCoordinator(db, client,
            new HyperliquidOrderOwnershipService(db), new HyperliquidAccountOperationGate());

        await coordinator.MaintainEntryOrdersAsync(strategy, cycle, config,
            new HyperliquidBook(99.9m, 100.1m, 100m, now), ct);

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

        await coordinator.MaintainEntryOrdersAsync(strategy, cycle, config,
            new HyperliquidBook(100.2m, 100.3m, 100.25m, now), ct);

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
}
