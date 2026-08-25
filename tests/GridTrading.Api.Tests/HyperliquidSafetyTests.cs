using GridTrading.Api.Data;
using GridTrading.Api.Exchange;
using GridTrading.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace GridTrading.Api.Tests;

public sealed class HyperliquidSafetyTests
{
    [Fact]
    public void UnifiedAccountUsesSpotUsdcForTradingEquityAndAvailableBalance()
    {
        using var abstraction = JsonDocument.Parse("\"unifiedAccount\"");
        using var perp = JsonDocument.Parse("""
            { "marginSummary": { "accountValue": "0" }, "withdrawable": "0" }
            """);
        using var spot = JsonDocument.Parse("""
            { "balances": [{ "coin": "USDC", "total": "999.120479", "hold": "0.120479" }] }
            """);

        var funds = HyperliquidAccountFunds.Resolve(abstraction.RootElement, perp.RootElement, spot.RootElement);

        Assert.Equal("unifiedAccount", funds.AccountMode);
        Assert.Equal(999.120479m, funds.TradingEquity);
        Assert.Equal(999m, funds.AvailableBalance);
        Assert.Equal(0m, funds.PerpAccountValue);
    }

    [Fact]
    public void StandardAccountKeepsUsingPerpAccountValueAndWithdrawable()
    {
        using var abstraction = JsonDocument.Parse("\"disabled\"");
        using var perp = JsonDocument.Parse("""
            { "marginSummary": { "accountValue": "500.5" }, "withdrawable": "420.25" }
            """);
        using var spot = JsonDocument.Parse("""
            { "balances": [{ "coin": "USDC", "total": "999.12", "hold": "0" }] }
            """);

        var funds = HyperliquidAccountFunds.Resolve(abstraction.RootElement, perp.RootElement, spot.RootElement);

        Assert.Equal("disabled", funds.AccountMode);
        Assert.Equal(500.5m, funds.TradingEquity);
        Assert.Equal(420.25m, funds.AvailableBalance);
    }

    [Fact]
    public void CredentialEnvelopeRoundTripsWithoutPlaintext()
    {
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["GRID_TRADING_CREDENTIAL_KEY"] = key }).Build();
        var protector = new CredentialProtector(configuration);
        const string privateKey = "0x0123456789012345678901234567890123456789012345678901234567890123";

        var envelope = protector.Protect(privateKey);

        Assert.DoesNotContain(privateKey, envelope, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(privateKey, protector.Unprotect(envelope));
    }

    [Fact]
    public async Task NonceIsStrictlyIncreasingAndPersisted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var ct = TestContext.Current.CancellationToken;
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
        await using var db = new TradingDbContext(options);
        await db.Database.EnsureCreatedAsync(ct);
        db.HyperliquidAccounts.Add(new HyperliquidAccountEntity
        {
            Id = "test", Name = "test", AccountAddress = "0x0000000000000000000000000000000000000001",
            AgentAddress = "0x0000000000000000000000000000000000000002", EncryptedAgentPrivateKey = "test",
            Environment = "TESTNET", Enabled = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        var manager = new HyperliquidNonceManager(db);

        var first = await manager.NextAsync("test", ct);
        var second = await manager.NextAsync("test", ct);

        Assert.True(second > first);
        Assert.Equal(second, (await db.HyperliquidAccounts.SingleAsync(ct)).LastNonce);
    }

    [Fact]
    public async Task OrderOwnershipMatchesOidAndCloidAndIgnoresExternalOrders()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var ct = TestContext.Current.CancellationToken;
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
        await using var db = new TradingDbContext(options);
        await db.Database.EnsureCreatedAsync(ct);
        var now = DateTimeOffset.UtcNow;
        db.Strategies.Add(new StrategyEntity
        {
            Id = "strategy_grid", Name = "SOL Grid", ExchangeAccountId = "account_testnet", Symbol = "SOLUSDT",
            ConfigurationJson = "{}", CreatedAt = now, UpdatedAt = now
        });
        db.Cycles.Add(new CycleEntity
        {
            Id = "cycle_grid", StrategyId = "strategy_grid", State = "RUNNING", FrozenConfigurationJson = "{}",
            FrozenPlanJson = "{}", ExitReason = "", StartedAt = now, LastReconciledAt = now
        });
        db.Orders.AddRange(
            Order("order_by_oid", "client_oid", "101", now),
            Order("order_by_cloid", "client_cloid", "pending", now));
        await db.SaveChangesAsync(ct);
        var clientCloid = HyperliquidWireCodec.CreateCloid("client_cloid");
        using var exchangeOrders = JsonDocument.Parse($$"""
            [
              { "oid": 101, "cloid": null, "coin": "SOL" },
              { "oid": 202, "cloid": "{{clientCloid}}", "coin": "SOL" },
              { "oid": 303, "cloid": "0x00000000000000000000000000000000", "coin": "SOL" }
            ]
            """);
        var service = new HyperliquidOrderOwnershipService(db);

        var count = await service.CountTrackedOpenOrdersAsync("account_testnet", "SOL-USDC", null,
            exchangeOrders.RootElement, ct);
        var annotated = await service.AnnotateOpenOrdersAsync("account_testnet", exchangeOrders.RootElement, ct);

        Assert.Equal(2, count);
        Assert.Equal("STRATEGY", annotated[0]!["orderSource"]!.GetValue<string>());
        Assert.Equal("strategy_grid", annotated[0]!["strategyId"]!.GetValue<string>());
        Assert.Equal("strategy_grid", annotated[1]!["strategyId"]!.GetValue<string>());
        Assert.Equal("EXTERNAL", annotated[2]!["orderSource"]!.GetValue<string>());
        Assert.Null(annotated[2]!["strategyId"]);
    }

    [Fact]
    public async Task HistoricalOrderOwnershipIsAddedAtTheHistoryRowLevel()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var ct = TestContext.Current.CancellationToken;
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
        await using var db = new TradingDbContext(options);
        await db.Database.EnsureCreatedAsync(ct);
        var now = DateTimeOffset.UtcNow;
        db.Strategies.Add(new StrategyEntity
        {
            Id = "strategy_history", Name = "History Grid", ExchangeAccountId = "account_testnet", Symbol = "SOLUSDT",
            ConfigurationJson = "{}", CreatedAt = now, UpdatedAt = now
        });
        db.Cycles.Add(new CycleEntity
        {
            Id = "cycle_history", StrategyId = "strategy_history", State = "WAITING_FOR_OPERATOR", IsTerminal = true,
            FrozenConfigurationJson = "{}", FrozenPlanJson = "{}", ExitReason = "DONE", StartedAt = now,
            EndedAt = now, LastReconciledAt = now
        });
        db.Orders.Add(Order("order_history", "client_history", "701", now, "cycle_history"));
        await db.SaveChangesAsync(ct);
        using var history = JsonDocument.Parse("""
            [
              { "order": { "oid": 701, "cloid": null }, "status": "filled", "statusTimestamp": 1 },
              { "order": { "oid": 702, "cloid": null }, "status": "canceled", "statusTimestamp": 2 }
            ]
            """);
        var service = new HyperliquidOrderOwnershipService(db);

        var annotated = await service.AnnotateHistoricalOrdersAsync("account_testnet", history.RootElement, ct);

        Assert.Equal("strategy_history", annotated[0]!["strategyId"]!.GetValue<string>());
        Assert.Equal("EXTERNAL", annotated[1]!["orderSource"]!.GetValue<string>());
    }

    private static OrderEntity Order(string id, string clientOrderId, string exchangeOrderId, DateTimeOffset now,
        string cycleId = "cycle_grid") => new()
    {
        Id = id, CycleId = cycleId, ClientOrderId = clientOrderId, ExchangeOrderId = exchangeOrderId,
        Symbol = "SOLUSDT", Side = "BUY", Kind = "ENTRY", Status = "NEW", GridLevel = 0,
        Price = 100m, Quantity = 1m, CreatedAt = now, UpdatedAt = now
    };
}
