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

public sealed class ModifyAcknowledgementReproTests
{
    [Theory]
    [InlineData("0.36", true)]
    [InlineData("0.42", true)]
    [InlineData("0.36", false)]
    public async Task FragmentedEntryRealAdapterPersistsOnlyWhenModifyResponseHasOrderStatuses(string quantityText, bool defaultAck)
    {
        var ct = TestContext.Current.CancellationToken;
        var desired = decimal.Parse(quantityText, System.Globalization.CultureInfo.InvariantCulture);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
        await using var db = new TradingDbContext(options);
        await db.Database.EnsureCreatedAsync(ct);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GRID_TRADING_CREDENTIAL_KEY"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray())
        }).Build();
        var protector = new CredentialProtector(configuration);
        const string privateKey = "0x0000000000000000000000000000000000000000000000000000000000000001";
        var now = DateTimeOffset.UtcNow;
        var config = new GridConfiguration { Symbol = "SOL", TickSize = .01m, QuantityStep = .01m,
            MinOrderQuantity = .01m, MinOrderNotional = 10m, SizeDecimals = 2,
            TakeProfitPoints = 200m, FaultExposureThresholdUsdt = 10m };
        db.HyperliquidAccounts.Add(new HyperliquidAccountEntity
        {
            Id = "repro-account", Name = "Synthetic fixture", AccountAddress = "0x0000000000000000000000000000000000000002",
            AgentAddress = HyperliquidL1Signer.DeriveAddress(privateKey), EncryptedAgentPrivateKey = protector.Protect(privateKey),
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
        using var handler = new ModifyHandler(defaultAck);
        using var http = new HttpClient(handler);
        var client = new HyperliquidTradingClient(http, configuration, db, protector, new HyperliquidNonceManager(db), new HyperliquidL1Signer());
        var adapter = new HyperliquidExecutionAdapter(db, client, new HyperliquidInfoClient(http, configuration), new HyperliquidOrderOwnershipService(db));
        var lifecycle = new GridOrderLifecycle(db, new ExecutionEnvironmentRegistry([adapter]), new ExecutionAccountOperationGate());
        await lifecycle.ProcessFillsAsync("repro-account", [new NormalizedExecutionFill("first", "7001", entry.ClientOrderId,
            "BUY", 102.16m, .11m, .001m, now)], ct);
        var next = new NormalizedExecutionFill("second", "7001", entry.ClientOrderId, "BUY", 102.16m, desired - .11m, .002m, now.AddSeconds(1));
        if (defaultAck)
        {
            var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => lifecycle.ProcessFillsAsync("repro-account", [next], ct));
            Assert.Contains("SendOrderAction", ex.StackTrace!);
        }
        else await lifecycle.ProcessFillsAsync("repro-account", [next], ct);
        Assert.Equal(desired, handler.ModifiedQuantity);
        // A new DbContext checks committed values, not merely tracked in-memory entities.
        await using var persisted = new TradingDbContext(options);
        var tp = await persisted.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct);
        Assert.Equal(defaultAck ? .11m : desired, tp.Quantity);
        Assert.Equal(desired, (await persisted.VirtualLots.SingleAsync(ct)).RemainingQuantity);
        Assert.Equal(2, await persisted.Executions.CountAsync(ct));
        Assert.Equal(.003m, (await persisted.Cycles.SingleAsync(ct)).PaidFees);
        // Retrying the same fill is suppressed; it does not repair the stale TP quantity.
        Assert.Equal(0, await lifecycle.ProcessFillsAsync("repro-account", [next], ct));
        Assert.Equal(defaultAck ? .11m : desired, (await persisted.Orders.AsNoTracking().SingleAsync(x => x.Kind == "TAKE_PROFIT", ct)).Quantity);
    }

    private sealed class ModifyHandler(bool defaultAck) : HttpMessageHandler
    {
        public decimal ModifiedQuantity { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var response = "";
            if (request.RequestUri!.AbsolutePath == "/info")
                response = """{"universe":[{"name":"SOL","szDecimals":2}]}""";
            else
            {
                var action = doc.RootElement.GetProperty("action");
                if (action.GetProperty("type").GetString() == "modify")
                {
                    ModifiedQuantity = decimal.Parse(action.GetProperty("order").GetProperty("s").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                    response = defaultAck ? """{"status":"ok","response":{"type":"default"}}"""
                        : """{"status":"ok","response":{"type":"order","data":{"statuses":[{"resting":{"oid":8002}}]}}}""";
                }
                else response = """{"status":"ok","response":{"type":"order","data":{"statuses":[{"resting":{"oid":8001}}]}}}""";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
