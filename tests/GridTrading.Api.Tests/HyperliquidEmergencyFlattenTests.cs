using System.Net;
using System.Text;
using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Exchanges.Hyperliquid;
using GridTrading.Api.Exchange;
using GridTrading.Api.Services;
using GridTrading.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GridTrading.Api.Tests;

public sealed class HyperliquidEmergencyFlattenTests
{
    [Fact]
    public async Task FlattenUsesOwnedStrategyExposureAndIgnoresExternalAccountPosition()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
        await using var db = new TradingDbContext(options);
        await db.Database.EnsureCreatedAsync(ct);

        var credentialKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["GRID_TRADING_CREDENTIAL_KEY"] = credentialKey }).Build();
        var protector = new CredentialProtector(configuration);
        const string privateKey = "0x0000000000000000000000000000000000000000000000000000000000000001";
        var now = DateTimeOffset.UtcNow;
        var config = new GridConfiguration
        {
            Symbol = "SOLUSDT", TickSize = .01m, QuantityStep = .01m, MinOrderQuantity = .01m,
            MinOrderNotional = 1m, SizeDecimals = 2, CenterPrice = 100m
        };
        var strategy = new StrategyEntity
        {
            Id = "strategy-grid", Name = "SOL Grid", Symbol = config.Symbol,
            DefaultExecutionEnvironmentId = ExecutionEnvironmentIds.HyperliquidTestnet,
            DefaultExecutionAccountId = "account-testnet", ConfigurationJson = "{}",
            CreatedAt = now, UpdatedAt = now
        };
        var cycle = new CycleEntity
        {
            Id = "cycle-grid", StrategyId = strategy.Id,
            ExecutionEnvironmentId = ExecutionEnvironmentIds.HyperliquidTestnet,
            ExecutionAccountId = "account-testnet", State = "FAULT",
            ActualNetQuantity = -9.79m, ReconstructedNetQuantity = -1.79m,
            FrozenConfigurationJson = "{}", FrozenPlanJson = "{}", ExitReason = "",
            StartedAt = now, LastReconciledAt = now
        };
        db.HyperliquidAccounts.Add(new HyperliquidAccountEntity
        {
            Id = "account-testnet", Name = "Testnet",
            AccountAddress = "0x0000000000000000000000000000000000000002",
            AgentAddress = HyperliquidL1Signer.DeriveAddress(privateKey),
            EncryptedAgentPrivateKey = protector.Protect(privateKey), Environment = "TESTNET", Enabled = true,
            CreatedAt = now, UpdatedAt = now
        });
        db.AddRange(strategy, cycle);
        await db.SaveChangesAsync(ct);

        using var exchange = new EmergencyFlattenHandler();
        using var http = new HttpClient(exchange);
        var client = new HyperliquidTradingClient(http, configuration, db, protector,
            new HyperliquidNonceManager(db), new HyperliquidL1Signer());
        var adapter = new HyperliquidExecutionAdapter(db, client, new HyperliquidInfoClient(http, configuration),
            new HyperliquidOrderOwnershipService(db));

        var residual = await adapter.FlattenAsync(
            new ExecutionSelection(ExecutionEnvironmentIds.HyperliquidTestnet, "account-testnet"), cycle, config, ct);

        Assert.Equal(0m, residual);
        Assert.Equal(-9.79m, cycle.ActualNetQuantity);
        var request = Assert.Single(exchange.ExchangeRequests);
        var submitted = request.GetProperty("action").GetProperty("orders")[0];
        Assert.True(submitted.GetProperty("b").GetBoolean());
        Assert.Equal("1.79", submitted.GetProperty("s").GetString());
        Assert.False(submitted.GetProperty("r").GetBoolean());
        Assert.Equal("Ioc", submitted.GetProperty("t").GetProperty("limit").GetProperty("tif").GetString());
        var flatten = await db.Orders.SingleAsync(x => x.Kind == "FLATTEN", ct);
        Assert.Equal("FILLED", flatten.Status);
        Assert.Equal(1.79m, flatten.FilledQuantity);
    }

    private sealed class EmergencyFlattenHandler : HttpMessageHandler
    {
        public List<JsonElement> ExchangeRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var payload = await request.Content!.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(payload);
            if (request.RequestUri!.AbsolutePath == "/info")
            {
                return document.RootElement.GetProperty("type").GetString() switch
                {
                    "openOrders" => Json("[]"),
                    "l2Book" => Json("""{ "levels": [[{ "px": "99.9" }], [{ "px": "100.1" }]] }"""),
                    "meta" => Json("""{ "universe": [{ "name": "SOL", "szDecimals": 2 }] }"""),
                    "clearinghouseState" => throw new InvalidOperationException("Flatten must not inspect the account position."),
                    var type => throw new InvalidOperationException($"Unexpected info request: {type}")
                };
            }

            ExchangeRequests.Add(document.RootElement.Clone());
            return Json("""
                {
                  "status": "ok",
                  "response": { "data": { "statuses": [{ "filled":
                    { "totalSz": "1.79", "avgPx": "100.2", "oid": 9001 } }] } }
                }
                """);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
