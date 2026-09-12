using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Infrastructure;
using GridTrading.Api.Services;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Tests;

public sealed partial class HyperliquidMainnetTests
{
    [Theory]
    [InlineData("SOL")]
    [InlineData("sol/usdc")]
    public async Task MainnetPreflightAllowsOtherSymbolPositionsAndOrders(string symbol)
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.OtherPosition = 2m;
        f.Handler.OpenOrders = [VenueOrder("BTC", 20)];
        var quote = await f.Adapter.PreflightStartAsync(new("hyperliquid-mainnet", "live"), symbol, Ct);
        Assert.Equal(100m, quote.Mid);
        Assert.Empty(f.Handler.Actions);
    }

    [Theory]
    [InlineData("SOL")]
    [InlineData("SOL-USDC")]
    public async Task MainnetPreflightStillBlocksCurrentSymbolPosition(string symbol)
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.Position = .12m;
        f.Handler.OtherPosition = 2m;
        var error = await Assert.ThrowsAsync<TradingProblemException>(() =>
            f.Adapter.PreflightStartAsync(new("hyperliquid-mainnet", "live"), symbol, Ct));
        Assert.Equal("TESTNET_POSITION_NOT_FLAT", error.Code);
        Assert.Empty(f.Handler.Actions);
    }

    [Fact]
    public async Task MainnetPreflightStillBlocksUntrackedCurrentSymbolOrdersAmongOtherOrders()
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.OpenOrders = [VenueOrder("BTC", 20), VenueOrder("SOL", 21)];
        var error = await Assert.ThrowsAsync<TradingProblemException>(() =>
            f.Adapter.PreflightStartAsync(new("hyperliquid-mainnet", "live"), "sol/usdc", Ct));
        Assert.Equal("MAINNET_OPEN_ORDERS_EXIST", error.Code);
        Assert.Empty(f.Handler.Actions);
    }

    [Theory]
    [InlineData("SOL", "RUNNING")]
    [InlineData("sol/usdc", "STARTING")]
    [InlineData("SOL-USDT", "CLOSING")]
    [InlineData("SOL_USDC", "FAULT")]
    [InlineData("SOLUSDC", "PAUSED")]
    public async Task SameSymbolReservationUsesFrozenMarketIncludingAliases(string frozenSymbol, string state)
    {
        await using var f = await Fixture.CreateAsync();
        var workflow = await f.WorkflowAsync();
        await AddReservedMarketAsync(f, frozenSymbol, state: state);
        // Editing the other strategy does not release the symbol its live cycle still trades.
        var otherStrategy = await f.Db.Strategies.SingleAsync(x => x.Id == "other-strategy", Ct);
        otherStrategy.Symbol = "BTC";
        await f.Db.SaveChangesAsync(Ct);
        var error = await Assert.ThrowsAsync<TradingProblemException>(() => workflow.StartCycleAsync("strategy",
            new("preview", 100m, new(true, true, "MAINNET")), "duplicate-symbol", Ct));
        Assert.Equal("MAINNET_SYMBOL_BUSY", error.Code);
        Assert.Empty(f.Handler.Actions);
        Assert.Single(await f.Db.Cycles.ToListAsync(Ct));
    }

    [Theory]
    [InlineData("BTC", "live", "hyperliquid-mainnet", false)]
    [InlineData("SOL", "another-account", "hyperliquid-mainnet", false)]
    [InlineData("SOL", "live", "hyperliquid-testnet", false)]
    [InlineData("SOL", "live", "hyperliquid-mainnet", true)]
    public async Task OnlyActiveSameAccountEnvironmentAndSymbolBlocksStart(string symbol, string account, string environment, bool terminal)
    {
        await using var f = await Fixture.CreateAsync();
        var workflow = await f.WorkflowAsync();
        await AddReservedMarketAsync(f, symbol, account, environment, terminal);
        f.Handler.OtherPosition = 2m;
        f.Handler.OpenOrders = [VenueOrder("BTC", 20)];
        var (_, cycle) = await workflow.StartCycleAsync("strategy",
            new("preview", 100m, new(true, true, "MAINNET")), "allowed-symbol", Ct);
        Assert.Equal("RUNNING", cycle.State);
        Assert.Equal(2, f.Handler.Actions.Count);
        Assert.All(f.Handler.Actions, request => Assert.Equal(0,
            request.GetProperty("action").GetProperty("orders")[0].GetProperty("a").GetInt32()));
    }

    [Fact]
    public async Task CloseCancelsAndFlattensOnlySelectedSymbolWhileOtherStrategyContinues()
    {
        await using var f = await Fixture.CreateAsync();
        var workflow = await f.WorkflowAsync();
        await AddReservedMarketAsync(f, "BTC");
        var otherOrder = AddLocalOrder(f, "btc-order", "other-cycle", "BTC", "20");
        var (_, cycle) = await workflow.StartCycleAsync("strategy",
            new("preview", 100m, new(true, true, "MAINNET")), "start-sol", Ct);
        f.Handler.Actions.Clear();
        f.Handler.Position = .12m;
        f.Handler.OtherPosition = 2m;
        f.Handler.OpenOrders = [VenueOrder("BTC", 20)];
        var remaining = await f.Adapter.FlattenAsync(new("hyperliquid-mainnet", "live"), cycle, ExampleConfig(), Ct);
        Assert.Equal(0m, remaining);
        Assert.Equal(2m, f.Handler.OtherPosition);
        Assert.Equal("NEW", otherOrder.Status);
        Assert.False((await f.Db.Cycles.SingleAsync(x => x.Id == "other-cycle", Ct)).IsTerminal);
        Assert.Equal(3, f.Handler.Actions.Count); // Two SOL cancellations and its reduce-only close.
        Assert.All(f.Handler.Actions, request =>
        {
            var action = request.GetProperty("action");
            var isCancel = action.GetProperty("type").GetString() == "cancelByCloid";
            var orders = action.GetProperty(isCancel ? "cancels" : "orders");
            Assert.All(orders.EnumerateArray(), order => Assert.Equal(0, order.GetProperty(isCancel ? "asset" : "a").GetInt32()));
        });
    }

    [Fact]
    public async Task CloseStillBlocksUntrackedOrdersOnCurrentSymbol()
    {
        await using var f = await Fixture.CreateAsync();
        var cycle = await f.AddCycleAsync();
        f.Handler.Position = .12m;
        f.Handler.OpenOrders = [VenueOrder("BTC", 20), VenueOrder("SOL", 21)];
        var error = await Assert.ThrowsAsync<TradingProblemException>(() =>
            f.Adapter.FlattenAsync(new("hyperliquid-mainnet", "live"), cycle, ExampleConfig(), Ct));
        Assert.Equal("CANCEL_INCOMPLETE", error.Code);
        Assert.Contains("1 SOLUSDC order(s)", error.Message);
        Assert.Empty(f.Handler.Actions);
        Assert.Equal(.12m, f.Handler.Position);
    }

    [Fact]
    public async Task AutomaticCloseAndRestartAllowsAnotherActiveSymbol()
    {
        await using var f = await Fixture.CreateAsync();
        var config = ExampleConfig() with { AutoRestart = true };
        var workflow = await f.WorkflowAsync(config);
        var settings = StrategyRequest.Default with
        {
            Symbol = config.Symbol, AutoRestart = true, CenterSuggestionMode = "CURRENT_MID",
            DefaultExecutionEnvironmentId = "hyperliquid-mainnet", DefaultExecutionAccountId = "live"
        };
        (await f.Db.Strategies.SingleAsync(Ct)).ConfigurationJson = JsonSerializer.Serialize(settings, JsonSupport.Options);
        await AddReservedMarketAsync(f, "BTC");
        var otherOrder = AddLocalOrder(f, "btc-order", "other-cycle", "BTC", "20");
        var (_, cycle) = await workflow.StartCycleAsync("strategy",
            new("preview", 100m, new(true, true, "MAINNET")), "start-before-restart", Ct);
        f.Handler.OtherPosition = 2m;
        f.Handler.OpenOrders = [VenueOrder("BTC", 20)];
        await workflow.CommandAsync(cycle.Id, "CLOSE", "BASKET_TAKE_PROFIT", "automatic-close", null, false, Ct, automaticClose: true);
        var pending = await f.Db.Operations.SingleAsync(x => x.Type == "AUTO_RESTART", Ct);
        await workflow.ProcessAutoRestartAsync(pending.Id, Ct);
        Assert.Equal("COMPLETED", pending.Status);
        Assert.True(cycle.IsTerminal);
        var next = await f.Db.Cycles.SingleAsync(x => x.StrategyId == "strategy" && !x.IsTerminal, Ct);
        Assert.NotEqual(cycle.Id, next.Id);
        Assert.Equal("RUNNING", next.State);
        Assert.Equal(2m, f.Handler.OtherPosition);
        Assert.Equal("NEW", otherOrder.Status);
        Assert.Equal(2, await f.Db.Cycles.CountAsync(x => !x.IsTerminal, Ct));
        Assert.Empty(await f.Db.RiskAlerts.Where(x => x.Code == "AUTO_RESTART_FAILED").ToListAsync(Ct));
    }

    private static object VenueOrder(string coin, int oid) => new { coin, oid, side = "B", sz = "0.12", cloid = (string?)null };

    private static async Task AddReservedMarketAsync(Fixture f, string symbol, string account = "live",
        string environment = "hyperliquid-mainnet", bool terminal = false, string state = "RUNNING")
    {
        var now = DateTimeOffset.UtcNow;
        f.Db.Strategies.Add(new() { Id = "other-strategy", Name = "Other strategy", Symbol = symbol,
            ConfigurationJson = "{}", CreatedAt = now, UpdatedAt = now });
        f.Db.Cycles.Add(new()
        {
            Id = "other-cycle", StrategyId = "other-strategy", ExecutionAccountId = account,
            ExecutionEnvironmentId = environment, State = state, IsTerminal = terminal,
            FrozenConfigurationJson = JsonSerializer.Serialize(ExampleConfig() with { Symbol = symbol }, JsonSupport.Options),
            FrozenPlanJson = "{}", ExitReason = "", StartedAt = now, LastReconciledAt = now
        });
        await f.Db.SaveChangesAsync(Ct);
    }

    private static OrderEntity AddLocalOrder(Fixture f, string id, string cycleId, string symbol, string oid)
    {
        var order = new OrderEntity { Id = id, CycleId = cycleId, ClientOrderId = id, ExchangeOrderId = oid,
            Symbol = symbol, Side = "BUY", Kind = "ENTRY", Status = "NEW", Quantity = .12m,
            Price = 99m, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        f.Db.Orders.Add(order);
        return order;
    }
}
