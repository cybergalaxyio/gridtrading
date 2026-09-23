using System.Net;
using System.Text;
using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridTrading.Api.Tests;

public sealed partial class TelegramNotificationTests
{
    [Fact]
    public async Task OrderAndRiskAlertsFetchFreshMetricsForTheirOwnAccountAndSymbol()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenDatabaseAsync(ct);
        var bot = new FakeBot();
        using var market = new SnapshotHandler();
        await using var provider = Services(connection, bot, market);
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            db.HyperliquidAccounts.Add(SnapshotAccount());
            db.Cycles.Add(new CycleEntity
            {
                Id = "snapshot-cycle", StrategyId = "strategy", ExecutionAccountId = "snapshot-account",
                ExecutionEnvironmentId = ExecutionEnvironmentIds.HyperliquidMainnet, State = "RUNNING",
                FrozenConfigurationJson = "{\"symbol\":\"SOL-USDC\"}", FrozenPlanJson = "{}", ExitReason = ""
            });
            await db.SaveChangesAsync(ct);
            var settings = scope.ServiceProvider.GetRequiredService<TelegramNotificationSettingsService>();
            await settings.SaveAsync(new TelegramSettingsRequest(Token, "-100123"), ct);
            await settings.TestAndEnableAsync(ct);
            var alert = Alert("snapshot-risk", "WARNING", DateTimeOffset.UtcNow);
            alert.CycleId = "snapshot-cycle";
            alert.Message = string.Concat(Enumerable.Repeat("😀", 5000));
            db.RiskAlerts.Add(alert);
            var order = NotificationOrder("snapshot-order", "ENTRY");
            order.ExchangeOrderId = "123";
            await OrderPlacementNotifications.RecordAsync(db,
                new ExecutionSelection(ExecutionEnvironmentIds.HyperliquidMainnet, "snapshot-account"), order, ct);
            await db.SaveChangesAsync(ct);
        });

        var dispatcher = Dispatcher(provider, bot);
        Assert.True(await dispatcher.ProcessNextAsync(ct));
        AssertSnapshot(bot.Messages[^1].Text, "SHORT 0.3 SOL", "-1.25", "4.5x", "80");
        Assert.True(bot.Messages[^1].Text.EnumerateRunes().Count() <= 4096);
        // The next alert reflects account state at send time, not order creation time.
        market.Position = "0.4";
        market.UnrealizedPnl = "2.75";
        market.TotalNotional = "600";
        market.Hold = "30";
        Assert.True(await dispatcher.ProcessNextAsync(ct));
        AssertSnapshot(bot.Messages[^1].Text, "LONG 0.4 SOL", "2.75", "6x", "70");
        Assert.Contains("GridTrading Order Placed", bot.Messages[^1].Text);
        Assert.False(await dispatcher.ProcessNextAsync(ct));
        Assert.Equal(6, market.Requests.Count);
        Assert.All(market.Requests, request =>
        {
            Assert.Equal("api.hyperliquid.xyz", request.Uri.Host);
            Assert.Contains(SnapshotAccount().AccountAddress, request.Body);
        });
    }

    [Theory]
    [InlineData("unifiedAccount", "100", "20", "0", "0x", "80")]
    [InlineData("unifiedAccount", "0", "0", "10", "Unavailable", "0")]
    [InlineData("disabled", "100", "20", "450", "1.5x", "250")]
    public async Task SnapshotHandlesFlatAccountsZeroEquityAndStandardCollateral(
        string mode, string total, string hold, string notional, string leverage, string balance)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenDatabaseAsync(ct);
        using var market = new SnapshotHandler
        {
            Mode = mode, Total = total, Hold = hold, TotalNotional = notional, Position = "0", UnrealizedPnl = "0"
        };
        await using var provider = Services(connection, new FakeBot(), market);
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            db.HyperliquidAccounts.Add(SnapshotAccount());
            await db.SaveChangesAsync(ct);
            // Legacy queued messages have no structured account or symbol columns.
            var message = await scope.ServiceProvider.GetRequiredService<TelegramAccountSnapshotService>()
                .ForOrderAsync(new OrderPlacementNotificationEntity
                {
                    Id = "legacy", Message = "Account: snapshot-account\nSymbol: SOLUSDT"
                }, ct);
            AssertSnapshot(message, "FLAT 0 SOL", "0", leverage, balance);
        });
    }

    [Fact]
    public async Task SnapshotFailureStillSendsAlertWithAllFourFields()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenDatabaseAsync(ct);
        var bot = new FakeBot();
        using var market = new ThrowingHandler(new HttpRequestException("private upstream details"));
        await using var provider = Services(connection, bot, market);
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            db.HyperliquidAccounts.Add(SnapshotAccount());
            await db.SaveChangesAsync(ct);
            var settings = scope.ServiceProvider.GetRequiredService<TelegramNotificationSettingsService>();
            await settings.SaveAsync(new TelegramSettingsRequest(Token, "-100123"), ct);
            await settings.TestAndEnableAsync(ct);
            db.OrderPlacementNotifications.Add(new OrderPlacementNotificationEntity
            {
                Id = "unavailable", Message = "Order placed", CreatedAt = DateTimeOffset.UtcNow,
                ExecutionAccountId = "snapshot-account", Symbol = "SOL"
            });
            await db.SaveChangesAsync(ct);
        });
        Assert.True(await Dispatcher(provider, bot).ProcessNextAsync(ct));
        var message = bot.Messages[^1].Text;
        Assert.Contains("Order placed", message);
        AssertUnavailableSnapshot(message);
        Assert.DoesNotContain("private upstream details", message);
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            Assert.NotNull((await db.OrderPlacementNotifications.SingleAsync(ct)).DeliveredAt);
            Assert.Equal("SUCCEEDED", (await db.TelegramNotificationSettings.SingleAsync(ct)).LastDeliveryStatus);
        });
    }

    [Fact]
    public async Task UnscopedRiskAlertNeverUsesAnArbitraryAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenDatabaseAsync(ct);
        using var market = new SnapshotHandler();
        await using var provider = Services(connection, new FakeBot(), market);
        await InScope(provider, async scope =>
        {
            var service = scope.ServiceProvider.GetRequiredService<TelegramAccountSnapshotService>();
            AssertUnavailableSnapshot(await service.ForCycleAsync(null, ct));
            AssertUnavailableSnapshot(await service.ForCycleAsync("missing", ct));
        });
        Assert.Empty(market.Requests);
    }

    private static void AssertSnapshot(string message, string position, string pnl, string leverage, string balance)
    {
        Assert.Contains("Current Account Position (SOL): " + position, message);
        Assert.Contains("Unrealized PNL (SOL): " + pnl + " USDC", message);
        Assert.Contains("Unified Account Leverage: " + leverage, message);
        Assert.Contains("Account Available Balance: " + balance + " USDC", message);
        Assert.Contains("As of:", message);
    }

    private static void AssertUnavailableSnapshot(string message)
    {
        Assert.Contains("Current Account Position: Unavailable", message);
        Assert.Contains("Unrealized PNL: Unavailable", message);
        Assert.Contains("Unified Account Leverage: Unavailable", message);
        Assert.Contains("Account Available Balance: Unavailable", message);
    }

    private static HyperliquidAccountEntity SnapshotAccount() => new()
    {
        Id = "snapshot-account", Name = "Snapshot account", Environment = "MAINNET", Enabled = true,
        AccountAddress = "0x0000000000000000000000000000000000000123",
        AgentAddress = "0x0000000000000000000000000000000000000456", EncryptedAgentPrivateKey = "unused"
    };

    private sealed class SnapshotHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];
        public string Mode { get; set; } = "unifiedAccount";
        public string Position { get; set; } = "-0.3";
        public string UnrealizedPnl { get; set; } = "-1.25";
        public string Total { get; set; } = "100";
        public string Hold { get; set; } = "20";
        public string TotalNotional { get; set; } = "450";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add(new CapturedRequest(request.RequestUri!, body));
            using var document = JsonDocument.Parse(body);
            var response = document.RootElement.GetProperty("type").GetString() switch
            {
                "userAbstraction" => JsonSerializer.Serialize(Mode),
                "spotClearinghouseState" => JsonSerializer.Serialize(new
                {
                    balances = new[] { new { coin = "USDC", total = Total, hold = Hold } }
                }),
                "clearinghouseState" => JsonSerializer.Serialize(new
                {
                    marginSummary = new { accountValue = "300", totalMarginUsed = "10", totalNtlPos = TotalNotional },
                    withdrawable = "250",
                    assetPositions = new[]
                    {
                        new { position = new { coin = "ETH", szi = "9", unrealizedPnl = "99", entryPx = "2500" } },
                        new { position = new { coin = "SOL", szi = Position, unrealizedPnl = UnrealizedPnl, entryPx = "111" } }
                    }
                }),
                _ => throw new InvalidOperationException("Unexpected snapshot request")
            };
            return Json(HttpStatusCode.OK, response);
        }
    }
}
