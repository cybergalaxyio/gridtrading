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

public sealed class HyperliquidAmendmentRecoveryTests
{
    [Theory]
    [InlineData("0.36", "confirmed")]
    [InlineData("0.42", "confirmed")]
    [InlineData("0.36", "default")]
    [InlineData("0.42", "default")]
    [InlineData("0.36", "timeout-after")]
    [InlineData("0.36", "timeout-before")]
    [InlineData("0.36", "timeout-partial")]
    public async Task FragmentedEntryRecoversAcrossRestartWithoutDuplicatingOrders(string quantityText, string mode)
    {
        var ct = TestContext.Current.CancellationToken;
        var desired = decimal.Parse(quantityText, System.Globalization.CultureInfo.InvariantCulture);
        await using var fixture = await Fixture.CreateAsync(desired, mode, ct);
        await fixture.FirstFillAsync(ct);
        await fixture.Lifecycle.ProcessFillsAsync("repro-account", [fixture.SecondFill], ct);
        await using (var saved = fixture.NewContext())
        {
            var tp = await saved.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct);
            Assert.Equal(mode == "confirmed" ? desired : .11m, tp.Quantity);
            Assert.Equal(mode != "confirmed", (await saved.VirtualLots.SingleAsync(ct)).ProtectionPending);
            Assert.Equal(desired, (await saved.VirtualLots.SingleAsync(ct)).RemainingQuantity);
            Assert.Equal(2, await saved.Executions.CountAsync(ct));
        }
        // The fill has been durably deduplicated; recovery must not rely on replaying it.
        Assert.Equal(0, await fixture.Lifecycle.ProcessFillsAsync("repro-account", [fixture.SecondFill], ct));
        await using var restarted = fixture.NewContext();
        var lifecycle = fixture.CreateLifecycle(restarted);
        var cycle = await restarted.Cycles.SingleAsync(ct);
        fixture.Handler.Mode = "confirmed";
        if (mode == "timeout-partial") fixture.Handler.AddTpFill(.05m);
        await lifecycle.ReconcileAsync(cycle, ct);
        await using var verified = fixture.NewContext();
        var finalTp = await verified.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct);
        var lot = await verified.VirtualLots.SingleAsync(ct);
        Assert.Equal(desired, finalTp.Quantity);
        Assert.Equal(mode == "timeout-partial" ? .05m : 0m, finalTp.FilledQuantity);
        Assert.Equal(desired - finalTp.FilledQuantity, lot.RemainingQuantity);
        Assert.False(lot.ProtectionPending);
        Assert.Equal("8002", finalTp.ExchangeOrderId);
        Assert.Equal(1, fixture.Handler.Placements);
        Assert.Equal(mode == "timeout-before" ? 2 : 1, fixture.Handler.Modifications);
        if (mode == "confirmed") Assert.Empty(await verified.RiskAlerts.ToListAsync(ct));
        else
        {
            Assert.Equal("ENTRY_RISK_PAUSED", (await verified.RiskAlerts.SingleAsync(ct)).Code);
            Assert.True(cycle.RiskPaused);
            await lifecycle.ReconcileAsync(cycle, ct);
            Assert.False(cycle.RiskPaused);
            Assert.True(cycle.OperatorPaused);
            Assert.Equal("PAUSED", cycle.State);
        }
        // A late cancel from the old generation must not cancel or roll back the amended order.
        Assert.Equal(0, await lifecycle.ProcessOrderUpdatesAsync("repro-account",
            [new NormalizedOrderUpdate("8001", finalTp.ClientOrderId, "CANCELLED", 0m, DateTimeOffset.UtcNow.AddMinutes(1))], ct));
        Assert.Equal("8002", (await verified.Orders.AsNoTracking().SingleAsync(x => x.Kind == "TAKE_PROFIT", ct)).ExchangeOrderId);
    }

    [Fact]
    public async Task FaultReconciliationIngestsFillsWithoutAnyExchangeWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(.36m, "confirmed", ct);
        await f.FirstFillAsync(ct);
        f.Cycle.State = "FAULT";
        await f.Db.SaveChangesAsync(ct);
        await f.Lifecycle.ProcessFillsAsync("repro-account", [f.SecondFill], ct);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        await using var verified = f.NewContext();
        Assert.Equal("FAULT", (await verified.Cycles.SingleAsync(ct)).State);
        Assert.True((await verified.VirtualLots.SingleAsync(ct)).ProtectionPending);
        Assert.Equal(.36m, (await verified.VirtualLots.SingleAsync(ct)).RemainingQuantity);
        Assert.Equal(.11m, (await verified.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct)).Quantity);
        Assert.Equal(1, f.Handler.Placements);
        Assert.Equal(0, f.Handler.Modifications);
        Assert.Equal(0, f.Handler.Cancellations);
        Assert.True(f.Cycle.LastReconciledAt > f.Cycle.StartedAt);
    }

    [Fact]
    public async Task RejectedAmendmentPreservesConfirmedQuantityAndSignalsUnprotectedExposure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(.36m, "rejected", ct);
        await f.FirstFillAsync(ct);
        await f.Lifecycle.ProcessFillsAsync("repro-account", [f.SecondFill], ct);
        await using var verified = f.NewContext();
        Assert.Equal(.11m, (await verified.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct)).Quantity);
        Assert.True((await verified.VirtualLots.SingleAsync(ct)).ProtectionPending);
        Assert.Equal("PAUSED", (await verified.Cycles.SingleAsync(ct)).State);
        Assert.True((await verified.Cycles.SingleAsync(ct)).RiskPaused);
        Assert.Equal("ENTRY_RISK_PAUSED", (await verified.RiskAlerts.SingleAsync(ct)).Code);
    }

    [Fact]
    public async Task RealFillsAreNotClippedByLegacyStaleOrderQuantity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(.36m, "confirmed", ct);
        await f.FirstFillAsync(ct);
        await f.Lifecycle.ProcessFillsAsync("repro-account", [f.SecondFill], ct);
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct);
        tp.Quantity = .11m; // The observed pre-fix persisted corruption.
        await f.Db.SaveChangesAsync(ct);
        var fill = new NormalizedExecutionFill("tp-final", "8002", tp.ClientOrderId, "SELL", 104.16m, .36m, .005m, DateTimeOffset.UtcNow);
        await f.Lifecycle.ProcessFillsAsync("repro-account", [fill], ct);
        await using var verified = f.NewContext();
        tp = await verified.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct);
        Assert.Equal(.36m, tp.FilledQuantity);
        Assert.Equal(.36m, tp.Quantity);
        Assert.Equal("CLOSED", (await verified.VirtualLots.SingleAsync(ct)).Status);
        Assert.Equal(.72m, (await verified.Cycles.SingleAsync(ct)).RealisedCyclePnl);
    }

    [Fact]
    public async Task ReconciliationCountsPreviousGenerationFillsWithoutRollingBackOid()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(.36m, "confirmed", ct);
        await f.FirstFillAsync(ct);
        await f.Lifecycle.ProcessFillsAsync("repro-account", [f.SecondFill], ct);
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct);
        await f.Lifecycle.ProcessFillsAsync("repro-account",
            [new NormalizedExecutionFill("old-generation-fill", "8001", tp.ClientOrderId, "SELL", 104.16m, .05m, .001m, DateTimeOffset.UtcNow)], ct);
        f.Handler.SetCurrentOriginalQuantity(.31m);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        await using var verified = f.NewContext();
        tp = await verified.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct);
        Assert.Equal("8002", tp.ExchangeOrderId);
        Assert.Equal(.36m, tp.Quantity); // .05 from old OID + .31 origSz on the new OID.
        Assert.Equal(.05m, tp.FilledQuantity);
        Assert.Equal(.31m, (await verified.VirtualLots.SingleAsync(ct)).RemainingQuantity);
        Assert.False((await verified.VirtualLots.SingleAsync(ct)).ProtectionPending);
        Assert.Equal(1, f.Handler.Modifications);
    }

    [Fact]
    public async Task HistoricalExposureScenarioReconcilesToThreeDollarsInsteadOfSixtyOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(.36m, "confirmed", ct);
        await f.FirstFillAsync(ct);
        await f.Lifecycle.ProcessFillsAsync("repro-account", [f.SecondFill], ct);
        var firstTp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct);
        firstTp.Quantity = .11m;
        var now = DateTimeOffset.UtcNow;
        var entry = new OrderEntity { Id = "entry-b5", CycleId = f.Cycle.Id, ClientOrderId = "entry-b5-client", ExchangeOrderId = "9001",
            Kind = "ENTRY", Side = "BUY", Symbol = "SOL", Status = "FILLED", GridLevel = 5, Price = 101.66m,
            Quantity = .42m, FilledQuantity = .42m, CreatedAt = now, UpdatedAt = now };
        var tp = new OrderEntity { Id = "tp-b5", CycleId = f.Cycle.Id, ClientOrderId = "tp-b5-client", ExchangeOrderId = "9002",
            Kind = "TAKE_PROFIT", Side = "SELL", Symbol = "SOL", Status = "NEW", GridLevel = 5, Price = 103.66m,
            Quantity = .11m, CreatedAt = now, UpdatedAt = now };
        var lot = new VirtualLotEntity { Id = "lot-b5", CycleId = f.Cycle.Id, EntryOrderId = entry.Id, TakeProfitOrderId = tp.Id,
            Side = "BUY", Status = "TP_PENDING", GridLevel = 5, EntryFillPrice = 101.66m, TakeProfitPrice = 103.66m,
            FilledQuantity = .42m, RemainingQuantity = .42m };
        var execution = new ExecutionEntity { Id = "entry-b5-fill", ExchangeExecutionId = "entry-b5-fill", ExchangeOrderId = "9001",
            CycleId = f.Cycle.Id, OrderId = entry.Id, Side = "BUY", Price = 101.66m, Quantity = .42m, OccurredAt = now };
        f.Db.AddRange(entry, tp, lot, execution);
        await f.Db.SaveChangesAsync(ct);
        f.Handler.ExtraOpenOrders.Add(new { oid = 9002, cloid = HyperliquidWireCodec.CreateCloid(tp.ClientOrderId),
            coin = "SOL", side = "A", limitPx = "103.66", origSz = "0.42", sz = "0.42" });
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        var dustEntry = new OrderEntity { Id = "entry-b6", CycleId = f.Cycle.Id, ClientOrderId = "entry-b6-client", ExchangeOrderId = "9003",
            Kind = "ENTRY", Side = "BUY", Symbol = "SOL", Status = "NEW", GridLevel = 6, Price = 101.16m,
            Quantity = .49m, CreatedAt = now, UpdatedAt = now };
        f.Db.Orders.Add(dustEntry);
        await f.Db.SaveChangesAsync(ct);
        await f.Lifecycle.ProcessFillsAsync("repro-account",
            [new NormalizedExecutionFill("dust-fill", "9003", dustEntry.ClientOrderId, "BUY", 101.16m, .03m, .0001m, now)], ct);
        await using var verified = f.NewContext();
        Assert.Equal("PAUSED", (await verified.Cycles.SingleAsync(ct)).State);
        Assert.Equal(.36m, (await verified.Orders.SingleAsync(x => x.Id == firstTp.Id, ct)).Quantity);
        Assert.Equal(.42m, (await verified.Orders.SingleAsync(x => x.Id == tp.Id, ct)).Quantity);
        var alert = await verified.RiskAlerts.SingleAsync(ct);
        Assert.Equal("PROTECTIVE_ORDER_BELOW_FAULT_THRESHOLD", alert.Code);
        Assert.Contains("3.09 USD", alert.Message);
        Assert.Equal(1, f.Handler.Placements);
        Assert.Equal(1, f.Handler.Modifications);
    }

    [Fact]
    public async Task IocAcknowledgementDoesNotDoubleCountItsSubsequentExecution()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(.36m, "confirmed", ct);
        var order = new OrderEntity { Id = "flatten", CycleId = f.Cycle.Id, ClientOrderId = "flatten-client", ExchangeOrderId = "9100",
            Kind = "FLATTEN", Side = "SELL", Symbol = "SOL", Status = "FILLED", GridLevel = -1, Price = 100m,
            Quantity = .03m, FilledQuantity = .03m, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        f.Db.Orders.Add(order);
        await f.Db.SaveChangesAsync(ct);
        await f.Lifecycle.ProcessFillsAsync("repro-account",
            [new NormalizedExecutionFill("flatten-execution", "9100", order.ClientOrderId, "SELL", 100m, .03m, .001m, DateTimeOffset.UtcNow)], ct);
        await using var verified = f.NewContext();
        order = await verified.Orders.SingleAsync(x => x.Id == "flatten", ct);
        Assert.Equal(.03m, order.Quantity);
        Assert.Equal(.03m, order.FilledQuantity);
        Assert.Single(await verified.Executions.ToListAsync(ct));
    }

    [Fact]
    public async Task PendingGridMoveQueriesCancelledEntryHistoryAndRewindsFillWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await Fixture.CreateAsync(.36m, "confirmed", ct);
        var entry = await f.Db.Orders.SingleAsync(ct);
        entry.Status = "CANCELLED";
        entry.CreatedAt = DateTimeOffset.UtcNow.AddHours(-1);
        f.Cycle.EntryGridMovePendingOrderId = entry.Id;
        f.Cycle.State = "FAULT"; // Read-only reconciliation exercises the actual HTTP adapter.
        f.Cycle.LastReconciledAt = DateTimeOffset.UtcNow;
        await f.Db.SaveChangesAsync(ct);
        await f.Lifecycle.ReconcileAsync(f.Cycle, ct);
        Assert.Contains(f.Handler.InfoRequests, x => x.GetProperty("type").GetString() == "historicalOrders");
        var request = Assert.Single(f.Handler.InfoRequests, x => x.GetProperty("type").GetString() == "userFillsByTime");
        Assert.Equal(entry.CreatedAt.AddSeconds(-5).ToUnixTimeMilliseconds(), request.GetProperty("startTime").GetInt64());
        Assert.Equal(0, f.Handler.Placements);
        Assert.Equal(0, f.Handler.Cancellations);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required DbContextOptions<TradingDbContext> Options { get; init; }
        public required TradingDbContext Db { get; init; }
        public required IConfiguration Configuration { get; init; }
        public required CycleEntity Cycle { get; init; }
        public required Handler Handler { get; init; }
        public required HttpClient Http { get; init; }
        public required NormalizedExecutionFill SecondFill { get; init; }
        public GridOrderLifecycle Lifecycle => CreateLifecycle(Db);
        public TradingDbContext NewContext() => new(Options);
        private readonly ExecutionAccountOperationGate _gate = new();
        public GridOrderLifecycle CreateLifecycle(TradingDbContext db)
        {
            var client = new HyperliquidTradingClient(Http, Configuration, db, new CredentialProtector(Configuration), new HyperliquidNonceManager(db), new HyperliquidL1Signer());
            var adapter = new HyperliquidExecutionAdapter(db, client, new HyperliquidInfoClient(Http, Configuration), new HyperliquidOrderOwnershipService(db));
            return new GridOrderLifecycle(db, new ExecutionEnvironmentRegistry([adapter]), _gate);
        }
        public Task<int> FirstFillAsync(CancellationToken ct) => Lifecycle.ProcessFillsAsync("repro-account",
            [new NormalizedExecutionFill("first", "7001", "repro-client", "BUY", 102.16m, .11m, .001m, Cycle.StartedAt)], ct);
        public static async Task<Fixture> CreateAsync(decimal desired, string mode, CancellationToken ct)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(ct);
            var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
            var db = new TradingDbContext(options);
            await db.Database.EnsureCreatedAsync(ct);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GRID_TRADING_CREDENTIAL_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray())
            }).Build();
            const string key = "0x0000000000000000000000000000000000000000000000000000000000000001";
            var now = DateTimeOffset.UtcNow.AddSeconds(-5);
            var config = new GridConfiguration { Symbol = "SOL", TickSize = .01m, QuantityStep = .01m,
                MinOrderQuantity = .01m, MinOrderNotional = 10m, SizeDecimals = 2,
                TakeProfitPoints = 200m, FaultExposureThresholdUsdt = 10m };
            db.HyperliquidAccounts.Add(new HyperliquidAccountEntity
            {
                Id = "repro-account", Name = "Synthetic fixture", AccountAddress = "0x0000000000000000000000000000000000000002",
                AgentAddress = HyperliquidL1Signer.DeriveAddress(key), EncryptedAgentPrivateKey = new CredentialProtector(configuration).Protect(key),
                Environment = "TESTNET", Enabled = true, CreatedAt = now, UpdatedAt = now
            });
            var cycle = new CycleEntity { Id = "repro-cycle", StrategyId = "synthetic", ExecutionAccountId = "repro-account",
                ExecutionEnvironmentId = ExecutionEnvironmentIds.HyperliquidTestnet, State = "PAUSED", StartedAt = now,
                LastReconciledAt = now, FrozenConfigurationJson = JsonSerializer.Serialize(config, JsonSupport.Options),
                FrozenPlanJson = "{}", ExitReason = "" };
            var entry = new OrderEntity { Id = "repro-entry", CycleId = cycle.Id, ClientOrderId = "repro-client",
                ExchangeOrderId = "7001", Symbol = "SOL", Side = "BUY", Kind = "ENTRY", Status = "NEW",
                GridLevel = 4, Price = 102.16m, Quantity = desired, CreatedAt = now, UpdatedAt = now };
            db.AddRange(cycle, entry);
            await db.SaveChangesAsync(ct);
            var handler = new Handler { Mode = mode, Position = desired };
            return new Fixture { Connection = connection, Options = options, Db = db, Configuration = configuration,
                Cycle = cycle, Handler = handler, Http = new HttpClient(handler),
                SecondFill = new NormalizedExecutionFill("second", "7001", entry.ClientOrderId, "BUY", 102.16m, desired - .11m, .002m, now.AddSeconds(1)) };
        }
        public async ValueTask DisposeAsync()
        {
            Http.Dispose();
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        public required string Mode { get; set; }
        public decimal Position { get; set; }
        public int Placements { get; private set; }
        public int Modifications { get; private set; }
        public int Cancellations { get; private set; }
        public List<object> ExtraOpenOrders { get; } = [];
        public List<JsonElement> InfoRequests { get; } = [];
        public void SetCurrentOriginalQuantity(decimal quantity) => _quantity = quantity;
        private string _cloid = "";
        private long _oid = 8001;
        private decimal _quantity, _filled;
        private long _fillTime;
        public void AddTpFill(decimal quantity) { _filled += quantity; Position -= quantity; _fillTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (request.RequestUri!.AbsolutePath == "/info")
            {
                InfoRequests.Add(doc.RootElement.Clone());
                var type = doc.RootElement.GetProperty("type").GetString();
                return type switch
                {
                    "meta" => Json(new { universe = new[] { new { name = "SOL", szDecimals = 2 } } }),
                    "userFunding" or "historicalOrders" => Json(Array.Empty<object>()),
                    "userFillsByTime" => Json(_filled == 0m ? [] : new object[] { new { oid = _oid, cloid = _cloid, hash = "fixture-hash", tid = 123, time = _fillTime, side = "A", px = "104.16", sz = Text(_filled), fee = "0.001" } }),
                    "frontendOpenOrders" => Json(new object[] { new { oid = _oid, cloid = _cloid, coin = "SOL", side = "A", limitPx = "104.16", origSz = Text(_quantity), sz = Text(_quantity - _filled) } }.Concat(ExtraOpenOrders).ToArray()),
                    "clearinghouseState" => Json(new { assetPositions = new[] { new { position = new { coin = "SOL", szi = Text(Position), positionValue = Text(Position * 102.16m), unrealizedPnl = "0" } } } }),
                    _ => throw new InvalidOperationException($"Unexpected info request {type}")
                };
            }
            var action = doc.RootElement.GetProperty("action");
            var actionType = action.GetProperty("type").GetString();
            if (actionType == "order")
            {
                Placements++;
                var order = action.GetProperty("orders")[0];
                _quantity = decimal.Parse(order.GetProperty("s").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                _cloid = order.GetProperty("c").GetString()!;
            }
            else if (actionType == "batchModify")
            {
                Modifications++;
                Assert.Single(action.GetProperty("modifies").EnumerateArray());
                var modification = action.GetProperty("modifies")[0];
                Assert.Equal(_cloid, modification.GetProperty("oid").GetString());
                if (Mode == "timeout-before") throw new HttpRequestException("Synthetic failure before acceptance");
                if (Mode == "rejected") return Json(new { status = "ok", response = new { type = "order", data = new { statuses = new[] { new { error = "Synthetic rejection" } } } } });
                _quantity = decimal.Parse(modification.GetProperty("order").GetProperty("s").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                _oid = 8002;
                if (Mode.StartsWith("timeout-", StringComparison.Ordinal)) throw new HttpRequestException("Synthetic lost response after acceptance");
                if (Mode == "default") return Json(new { status = "ok", response = new { type = "default" } });
            }
            else if (actionType == "cancelByCloid") Cancellations++;
            else throw new InvalidOperationException($"Unexpected exchange action {actionType}");
            return Json(new { status = "ok", response = new { type = "order", data = new { statuses = new[] { new { resting = new { oid = _oid } } } } } });
        }
        private static string Text(decimal value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
