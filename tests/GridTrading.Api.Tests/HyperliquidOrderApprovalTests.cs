using System.Net;
using System.Text;
using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Exchange;
using GridTrading.Api.Exchanges.Hyperliquid;
using GridTrading.Api.Services;
using GridTrading.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GridTrading.Api.Tests;

public sealed class HyperliquidOrderApprovalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LivePlacementAndAmendmentRequireSeparateApprovalForGtcFallback(bool amendment)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(ct);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["GRID_TRADING_CREDENTIAL_KEY"] = Convert.ToBase64String(new byte[32]) }).Build();
        var protector = new CredentialProtector(configuration);
        const string key = "0x0000000000000000000000000000000000000000000000000000000000000001";
        db.HyperliquidAccounts.Add(new HyperliquidAccountEntity { Id = "account", Name = "Mainnet mock",
            Environment = "MAINNET", Enabled = true, AccountAddress = "0x0000000000000000000000000000000000000002",
            AgentAddress = HyperliquidL1Signer.DeriveAddress(key), EncryptedAgentPrivateKey = protector.Protect(key) });
        var cycle = new CycleEntity { Id = "cycle", StrategyId = "strategy", State = "RUNNING",
            ExecutionEnvironmentId = ExecutionEnvironmentIds.HyperliquidMainnet, ExecutionAccountId = "account",
            FrozenConfigurationJson = "{}", FrozenPlanJson = "{}", ExitReason = "" };
        var order = new OrderEntity { Id = "tp", CycleId = cycle.Id, ClientOrderId = "tp", ExchangeOrderId = amendment ? "8001" : "pending",
            Symbol = "SOL", Side = "SELL", Kind = "TAKE_PROFIT", Status = amendment ? "NEW" : "PENDING_EXCHANGE", Price = 151.12345m, Quantity = 1.239m };
        db.AddRange(cycle, order);
        await db.SaveChangesAsync(ct);
        await new TradingControlSettingsService(db).SaveAsync(new(true), ct);
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        var client = new HyperliquidTradingClient(http, configuration, db, protector, new(db), new());
        var adapter = new HyperliquidExecutionAdapter(db, client, new(http, configuration), new(db), HyperliquidNetwork.Mainnet);
        var selection = new ExecutionSelection(ExecutionEnvironmentIds.HyperliquidMainnet, "account");
        var config = new GridConfiguration { Symbol = "SOL", PostOnlyTakeProfits = true };
        Task Send() => amendment ? adapter.AmendOrderAsync(selection, config, order, 152.12345m, 2.239m, ct)
            : adapter.PlaceOrdersAsync(selection, config, [order], ct);
        await Send();
        Assert.Empty(handler.Sends);
        var first = await db.OrderApprovals.SingleAsync(ct);
        Assert.Equal(amendment ? 152.12m : 151.12m, first.Price);
        Assert.Equal(amendment ? 2.23m : 1.23m, first.Quantity);
        Assert.Equal("Alo", first.TimeInForce);
        var service = new OrderApprovalService(db, new());
        await service.RejectAsync(first.Id, ct);
        await new TradingControlSettingsService(db).SaveAsync(new(false), ct);
        await Send();
        Assert.Empty(handler.Sends);
        Assert.Equal("REJECTED", first.Status);
        await new TradingControlSettingsService(db).SaveAsync(new(true), ct);
        await service.ApproveAsync(first.Id, ct);
        await Send();
        Assert.Single(handler.Sends); // ALO was rejected; fallback has NOT been sent.
        Assert.Equal(amendment ? "NEW" : "PENDING_EXCHANGE", order.Status);
        var fallback = await db.OrderApprovals.SingleAsync(x => x.Status == "PENDING", ct);
        Assert.Equal("Gtc", fallback.TimeInForce);
        await service.ApproveAsync(fallback.Id, ct);
        await Send();
        Assert.Equal(2, handler.Sends.Count);
        var sent = handler.Sends[1];
        var wire = amendment ? sent.GetProperty("modifies")[0].GetProperty("order") : sent.GetProperty("orders")[0];
        Assert.Equal("Gtc", wire.GetProperty("t").GetProperty("limit").GetProperty("tif").GetString());
        Assert.Equal(first.Price.ToString(System.Globalization.CultureInfo.InvariantCulture), wire.GetProperty("p").GetString());
        Assert.Equal("NEW", order.Status);
        Assert.Equal(2, await db.OrderApprovals.CountAsync(x => x.Status == "SUBMITTED", ct));
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<JsonElement> Sends { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (request.RequestUri!.AbsolutePath == "/info")
                return Json("""{"universe":[{"name":"SOL","szDecimals":2}]}""");
            Sends.Add(document.RootElement.GetProperty("action").Clone());
            return Json(Sends.Count == 1
                ? """{"status":"ok","response":{"data":{"statuses":[{"error":"Post only would cross"}]}}}"""
                : """{"status":"ok","response":{"data":{"statuses":[{"resting":{"oid":9001}}]}}}""");
        }
        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
