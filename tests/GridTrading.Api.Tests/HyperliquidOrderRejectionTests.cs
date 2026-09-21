using System.Net;
using System.Text;
using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Exchange;
using GridTrading.Api.Exchanges.Hyperliquid;
using GridTrading.Api.Infrastructure;
using GridTrading.Api.Services;
using GridTrading.Api.Strategies.Grid;
using GridTrading.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GridTrading.Api.Tests;

public sealed class HyperliquidOrderRejectionTests
{
    [Fact]
    public async Task EntryRejectionIsRecoverableWhileTakeProfitRejectionRemainsProtective()
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
            MinOrderNotional = 1m, SizeDecimals = 1, PostOnlyEntries = true, PostOnlyTakeProfits = true
        };
        var cycle = new CycleEntity
        {
            Id = "cycle_grid", StrategyId = "strategy_grid",
            ExecutionEnvironmentId = ExecutionEnvironmentIds.HyperliquidTestnet,
            ExecutionAccountId = "account_testnet", State = "RUNNING",
            FrozenConfigurationJson = "{}", FrozenPlanJson = "{}", ExitReason = "",
            StartedAt = now, LastReconciledAt = now
        };
        db.HyperliquidAccounts.Add(new HyperliquidAccountEntity
        {
            Id = "account_testnet", Name = "Testnet fixture",
            AccountAddress = "0x0000000000000000000000000000000000000002",
            AgentAddress = HyperliquidL1Signer.DeriveAddress(privateKey),
            EncryptedAgentPrivateKey = protector.Protect(privateKey),
            Environment = "TESTNET", Enabled = true, CreatedAt = now, UpdatedAt = now
        });
        db.Cycles.Add(cycle);
        await db.SaveChangesAsync(ct);

        using var exchange = new ScriptedOrderHandler();
        using var http = new HttpClient(exchange);
        var client = new HyperliquidTradingClient(http, configuration, db, protector,
            new HyperliquidNonceManager(db), new HyperliquidL1Signer());
        var adapter = new HyperliquidExecutionAdapter(db, client, new HyperliquidInfoClient(http, configuration),
            new HyperliquidOrderOwnershipService(db));
        var selection = new ExecutionSelection(ExecutionEnvironmentIds.HyperliquidTestnet, "account_testnet");

        var rejectedEntry = Order("entry_rejected", "entry-rejected", "ENTRY", "BUY", 7);
        db.Orders.Add(rejectedEntry);
        await db.SaveChangesAsync(ct);
        await adapter.PlaceOrdersAsync(selection, config, [rejectedEntry], ct);

        Assert.Equal("REJECTED", rejectedEntry.Status);
        Assert.Empty(await db.OrderPlacementNotifications.ToListAsync(ct));
        Assert.Equal("RUNNING", cycle.State);
        var warning = await db.RiskAlerts.SingleAsync(ct);
        Assert.Equal("ENTRY_ORDER_REJECTED", warning.Code);
        Assert.Equal("WARNING", warning.Severity);
        Assert.Contains("retried on the next Sync", warning.Message);
        Assert.Contains("Post only", warning.Message);

        var retryEntry = Order("entry_retry", "entry-retry", "ENTRY", "BUY", 7);
        db.Orders.Add(retryEntry);
        await db.SaveChangesAsync(ct);
        await adapter.PlaceOrdersAsync(selection, config, [retryEntry], ct);

        Assert.Equal("NEW", retryEntry.Status);
        Assert.Equal("9001", retryEntry.ExchangeOrderId);
        Assert.Contains("Exchange order: 9001", (await db.OrderPlacementNotifications.SingleAsync(ct)).Message);
        Assert.Equal("RUNNING", cycle.State);

        var takeProfit = Order("tp_rejected", "tp-rejected", "TAKE_PROFIT", "SELL", 7);
        db.Orders.Add(takeProfit);
        await db.SaveChangesAsync(ct);
        var problem = await Assert.ThrowsAsync<TradingProblemException>(
            () => adapter.PlaceOrdersAsync(selection, config, [takeProfit], ct));

        Assert.Equal("PROTECTIVE_ORDER_REJECTED", problem.Code);
        Assert.Equal("REJECTED", takeProfit.Status);
        Assert.Single(await db.OrderPlacementNotifications.ToListAsync(ct));
        Assert.Equal(["Alo", "Alo", "Alo", "Gtc"], exchange.TimeInForce);
    }

    [Fact]
    public async Task ProtectiveRejectionCancelsEntriesFromTheRejectionPath()
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
            Symbol = "SOLUSDT", TickSize = .1m, QuantityStep = .1m,
            MinOrderQuantity = .1m, MinOrderNotional = 1m, TakeProfitPoints = 1m
        };
        var cycle = new CycleEntity
        {
            Id = "cycle_grid", StrategyId = "strategy_grid",
            ExecutionEnvironmentId = ExecutionEnvironmentIds.HyperliquidTestnet,
            ExecutionAccountId = "account_testnet", State = "RUNNING",
            FrozenConfigurationJson = JsonSerializer.Serialize(config, JsonSupport.Options),
            FrozenPlanJson = "{}", ExitReason = "", StartedAt = now, LastReconciledAt = now
        };
        var filledEntry = Order("entry_filled", "entry-filled", "ENTRY", "BUY", 7);
        filledEntry.ExchangeOrderId = "7001";
        filledEntry.Status = "NEW";
        var workingEntry = Order("entry_working", "entry-working", "ENTRY", "SELL", 6);
        workingEntry.ExchangeOrderId = "7002";
        workingEntry.Status = "NEW";
        db.AddRange(cycle, filledEntry, workingEntry);
        await db.SaveChangesAsync(ct);

        var adapter = new ProtectiveRejectingAdapter();
        var lifecycle = new GridOrderLifecycle(db, new ExecutionEnvironmentRegistry([adapter]),
            new ExecutionAccountOperationGate());

        var processed = await lifecycle.ProcessFillsAsync("account_testnet",
            [new NormalizedExecutionFill("fill-1", "7001", "entry-filled", "BUY", 99.9m, .2m, 0m, now)], ct);

        Assert.Equal(1, processed);
        Assert.Equal("PAUSED", cycle.State);
        Assert.True(cycle.RiskPaused);
        Assert.Equal(1, cycle.StateVersion);
        Assert.Equal(["entry_working"], adapter.CancelledOrderIds);
        Assert.Equal("CANCELLED", workingEntry.Status);
        Assert.Equal("REJECTED", (await db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct)).Status);
        var alert = await db.RiskAlerts.SingleAsync(ct);
        Assert.Equal("ENTRY_RISK_PAUSED", alert.Code);
        Assert.Equal("CRITICAL", alert.Severity);
        Assert.Contains(cycle.Id, alert.Message);
        Assert.Contains("Test protective rejection", alert.Message);
        Assert.Contains("风险暂停开仓", alert.Message);
        Assert.Contains("20.00 USD", alert.Message);
        Assert.Contains("10.00 USD", alert.Message);
    }

    [Fact]
    public async Task ProtectiveRejectionBelowUsdThresholdWarnsWithoutFaultOrEntryCancellation()
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
            Symbol = "SOLUSDT", TickSize = .1m, QuantityStep = .1m,
            MinOrderQuantity = .1m, MinOrderNotional = 1m, TakeProfitPoints = 1m,
            FaultExposureThresholdUsdt = 50m
        };
        var cycle = new CycleEntity
        {
            Id = "cycle_grid", StrategyId = "strategy_grid",
            ExecutionEnvironmentId = ExecutionEnvironmentIds.HyperliquidTestnet,
            ExecutionAccountId = "account_testnet", State = "PAUSED", StateVersion = 4,
            FrozenConfigurationJson = JsonSerializer.Serialize(config, JsonSupport.Options),
            FrozenPlanJson = "{}", ExitReason = "", StartedAt = now, LastReconciledAt = now
        };
        var filledEntry = Order("entry_filled", "entry-filled", "ENTRY", "BUY", 7);
        filledEntry.ExchangeOrderId = "7001";
        filledEntry.Status = "NEW";
        var workingEntry = Order("entry_working", "entry-working", "ENTRY", "SELL", 6);
        workingEntry.ExchangeOrderId = "7002";
        workingEntry.Status = "NEW";
        db.AddRange(cycle, filledEntry, workingEntry);
        await db.SaveChangesAsync(ct);

        var adapter = new ProtectiveRejectingAdapter();
        var lifecycle = new GridOrderLifecycle(db, new ExecutionEnvironmentRegistry([adapter]),
            new ExecutionAccountOperationGate());

        var processed = await lifecycle.ProcessFillsAsync("account_testnet",
            [new NormalizedExecutionFill("fill-1", "7001", "entry-filled", "BUY", 99.9m, .2m, 0m, now)], ct);

        Assert.Equal(1, processed);
        Assert.Equal("PAUSED", cycle.State);
        Assert.Equal(4, cycle.StateVersion);
        Assert.Empty(adapter.CancelledOrderIds);
        Assert.Equal("NEW", workingEntry.Status);
        var alert = await db.RiskAlerts.SingleAsync(ct);
        Assert.Equal("PROTECTIVE_ORDER_BELOW_FAULT_THRESHOLD", alert.Code);
        Assert.Equal("WARNING", alert.Severity);
        Assert.Contains("20.00 USD", alert.Message);
        Assert.Contains("50.00 USD", alert.Message);
        Assert.Contains("不因本次影响停止", alert.Message);
    }

    [Fact]
    public async Task ReconciliationProtectiveRejectionUsesTheSameExplicitCleanup()
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
            Symbol = "SOLUSDT", TickSize = .1m, QuantityStep = .1m,
            MinOrderQuantity = .1m, MinOrderNotional = 1m
        };
        var cycle = new CycleEntity
        {
            Id = "cycle_grid", StrategyId = "strategy_grid",
            ExecutionEnvironmentId = ExecutionEnvironmentIds.HyperliquidTestnet,
            ExecutionAccountId = "account_testnet", State = "PAUSED",
            FrozenConfigurationJson = JsonSerializer.Serialize(config, JsonSupport.Options),
            FrozenPlanJson = "{}", ExitReason = "", StartedAt = now, LastReconciledAt = now
        };
        var pendingTp = Order("tp_pending", "tp-pending", "TAKE_PROFIT", "SELL", 7);
        var workingEntry = Order("entry_working", "entry-working", "ENTRY", "SELL", 6);
        workingEntry.ExchangeOrderId = "7002";
        workingEntry.Status = "NEW";
        var filledEntry = Order("entry_filled", "entry-filled", "ENTRY", "BUY", 7);
        filledEntry.Status = "FILLED";
        filledEntry.FilledQuantity = filledEntry.Quantity;
        var lot = new VirtualLotEntity
        {
            Id = "lot", CycleId = cycle.Id, EntryOrderId = filledEntry.Id, TakeProfitOrderId = pendingTp.Id,
            Side = "BUY", Status = "TP_PENDING", GridLevel = 7, EntryFillPrice = 99m,
            TakeProfitPrice = pendingTp.Price, FilledQuantity = .2m, RemainingQuantity = .2m
        };
        db.AddRange(cycle, pendingTp, workingEntry, filledEntry, lot);
        await db.SaveChangesAsync(ct);

        var adapter = new ProtectiveRejectingAdapter();
        var lifecycle = new GridOrderLifecycle(db, new ExecutionEnvironmentRegistry([adapter]),
            new ExecutionAccountOperationGate());

        await lifecycle.ReconcileAsync(cycle, ct);

        Assert.Equal("PAUSED", cycle.State);
        Assert.True(cycle.RiskPaused);
        Assert.Equal(["entry_working"], adapter.CancelledOrderIds);
        Assert.Equal("CANCELLED", workingEntry.Status);
        Assert.Equal("REJECTED", pendingTp.Status);
        Assert.Equal(1, await db.RiskAlerts.CountAsync(x => x.Code == "ENTRY_RISK_PAUSED", ct));
    }

    private static OrderEntity Order(string id, string clientOrderId, string kind, string side, int level) => new()
    {
        Id = id, CycleId = "cycle_grid", ClientOrderId = clientOrderId, ExchangeOrderId = "pending",
        Symbol = "SOLUSDT", Side = side, Kind = kind, Status = "PENDING_EXCHANGE",
        GridLevel = level, Price = 99.9m, Quantity = .2m,
        CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
    };

    private sealed class ProtectiveRejectingAdapter : IExecutionAdapter
    {
        public ExecutionEnvironmentDescriptor Environment { get; } = new(
            ExecutionEnvironmentIds.HyperliquidTestnet, "HYPERLIQUID", "TESTNET", "Test adapter");
        public List<string> CancelledOrderIds { get; } = [];

        public Task PlaceOrdersAsync(ExecutionSelection selection, GridConfiguration config,
            IEnumerable<OrderEntity> orders, CancellationToken ct)
        {
            var tp = Assert.Single(orders);
            Assert.Equal("TAKE_PROFIT", tp.Kind);
            tp.Status = "REJECTED";
            return Task.FromException(new TradingProblemException(
                422, "PROTECTIVE_ORDER_REJECTED", "Test protective rejection."));
        }

        public Task CancelOrdersAsync(ExecutionSelection selection, IEnumerable<OrderEntity> orders, CancellationToken ct)
        {
            foreach (var order in orders)
            {
                CancelledOrderIds.Add(order.Id);
                order.Status = "CANCELLED";
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection selection, string symbol,
            decimal? referencePrice, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection selection, string symbol, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection selection, string symbol, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<decimal> FlattenAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection selection, CycleEntity cycle,
            GridConfiguration config, CancellationToken ct) => Task.FromResult(new ExecutionReconciliationSnapshot(
                [], [], [], new Dictionary<string, string>(), new ExecutionPosition(0m, 0m, 0m)));
    }

    private sealed class ScriptedOrderHandler : HttpMessageHandler
    {
        private int _exchangeRequest;
        public List<string> TimeInForce { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var payload = await request.Content!.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(payload);
            if (request.RequestUri!.AbsolutePath == "/info")
                return Json("""{ "universe": [{ "name": "SOL", "szDecimals": 1 }] }""");

            var limit = document.RootElement.GetProperty("action").GetProperty("orders")[0]
                .GetProperty("t").GetProperty("limit");
            TimeInForce.Add(limit.GetProperty("tif").GetString()!);
            _exchangeRequest++;
            return _exchangeRequest switch
            {
                1 or 3 => Json("""
                    { "status": "ok", "response": { "data": { "statuses":
                      [{ "error": "Post only order would have immediately matched" }] } } }
                    """),
                2 => Json("""
                    { "status": "ok", "response": { "data": { "statuses":
                      [{ "resting": { "oid": 9001 } }] } } }
                    """),
                4 => Json("""
                    { "status": "ok", "response": { "data": { "statuses":
                      [{ "error": "Order must have minimum value" }] } } }
                    """),
                _ => throw new InvalidOperationException("Unexpected exchange request.")
            };
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
