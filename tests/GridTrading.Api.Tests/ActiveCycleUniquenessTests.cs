using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Hubs;
using GridTrading.Api.Infrastructure;
using GridTrading.Api.Services;
using GridTrading.Api.Strategies.Grid;
using GridTrading.Domain;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridTrading.Api.Tests;

public sealed class ActiveCycleUniquenessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("paper-local")]
    [InlineData("hyperliquid-testnet")]
    [InlineData("hyperliquid-mainnet")]
    public async Task EveryNonterminalStateReservesFrozenMarketAcrossStrategies(string environment)
    {
        await using var f = await Fixture.CreateAsync(environment);
        await f.PrepareAsync("new", "SOL");
        var reserved = f.Reserved("existing", "sol/usdc");
        f.Db.Cycles.Add(reserved);
        await f.Db.SaveChangesAsync(Ct);
        foreach (var state in new[] { "STARTING", "RUNNING", "PAUSED", "CLOSING", "FAULT" })
        {
            reserved.State = state;
            await f.Db.SaveChangesAsync(Ct);
            var error = await Assert.ThrowsAsync<TradingProblemException>(() => f.StartAsync("new"));
            Assert.Equal(environment == "hyperliquid-mainnet" ? "MAINNET_SYMBOL_BUSY" : "ACCOUNT_SYMBOL_BUSY", error.Code);
            Assert.Equal(0, f.Adapter.Placed);
            Assert.Equal(0, f.Adapter.Preflights);
            Assert.Single(await f.Db.Cycles.ToListAsync(Ct));
        }
        reserved.IsTerminal = true;
        await f.Db.SaveChangesAsync(Ct);
        var (_, next) = await f.StartAsync("new");
        Assert.Equal("RUNNING", next.State);
        Assert.Single(await f.Db.Cycles.Where(x => !x.IsTerminal).ToListAsync(Ct));
    }

    [Theory]
    [InlineData("ETH", "account", "paper-local")]
    [InlineData("SOLUSDT", "other-account", "paper-local")]
    [InlineData("SOLUSDT", "account", "hyperliquid-testnet")]
    public async Task OtherSymbolsAccountsAndEnvironmentsRemainIndependent(string symbol, string account, string environment)
    {
        await using var f = await Fixture.CreateAsync("paper-local");
        await f.PrepareAsync("new", "SOLUSDT");
        var other = f.Reserved("other", symbol);
        other.ExecutionAccountId = account;
        other.ExecutionEnvironmentId = environment;
        f.Db.Cycles.Add(other);
        await f.Db.SaveChangesAsync(Ct);
        var (_, cycle) = await f.StartAsync("new");
        Assert.Equal("RUNNING", cycle.State);
        Assert.Equal(2, await f.Db.Cycles.CountAsync(x => !x.IsTerminal, Ct));
    }

    [Fact]
    public async Task LegacyDuplicatesRemainIntactAndPreventAdditionalStarts()
    {
        await using var f = await Fixture.CreateAsync("hyperliquid-testnet");
        await f.PrepareAsync("new", "SOL-USDC");
        f.Db.Cycles.AddRange(f.Reserved("first", "SOL"), f.Reserved("second", "sol_usdc"));
        await f.Db.SaveChangesAsync(Ct);
        await Assert.ThrowsAsync<TradingProblemException>(() => f.StartAsync("new"));
        Assert.Equal(2, await f.Db.Cycles.CountAsync(x => !x.IsTerminal, Ct));
        Assert.Empty(await f.Db.Orders.ToListAsync(Ct));
    }

    [Fact]
    public async Task SeparateWorkersThatBothPassPreflightCanOnlyReserveOneCycle()
    {
        await using var f = await Fixture.CreateAsync("hyperliquid-testnet");
        await f.PrepareAsync("left", "SOL");
        await f.PrepareAsync("right", "sol-usdc");
        await using var leftDb = f.NewDb();
        await using var rightDb = f.NewDb();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        async Task Barrier()
        {
            if (Interlocked.Increment(ref arrived) == 2) ready.SetResult();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
        var leftAdapter = new Adapter(f.Environment) { BeforePreflight = Barrier };
        var rightAdapter = new Adapter(f.Environment) { BeforePreflight = Barrier };
        // Distinct gates model different server processes. Only the database reservation is shared.
        var left = f.Workflow(leftDb, leftAdapter);
        var right = f.Workflow(rightDb, rightAdapter);
        async Task<Exception?> Start(GridStrategyWorkflow workflow, string id)
        {
            try { await f.StartAsync(id, workflow); return null; }
            catch (Exception error) { return error; }
        }
        var results = await Task.WhenAll(Task.Run(() => Start(left, "left"), Ct), Task.Run(() => Start(right, "right"), Ct));
        Assert.Single(results, error => error is null);
        Assert.Equal("ACCOUNT_SYMBOL_BUSY", Assert.IsType<TradingProblemException>(Assert.Single(results, error => error is not null)).Code);
        Assert.Equal(2, arrived);
        Assert.Single(await f.Db.Cycles.Where(x => !x.IsTerminal).ToListAsync(Ct));
        Assert.Equal(1, (leftAdapter.Placed > 0 ? 1 : 0) + (rightAdapter.Placed > 0 ? 1 : 0));
        Assert.All(await f.Db.Operations.ToListAsync(Ct), operation => Assert.Equal("COMPLETED", operation.Status));
    }

    [Fact]
    public async Task TwoAccountsRunSameMarketAndEmergencyOnlyClosesItsTarget()
    {
        await using var f = await Fixture.CreateAsync("hyperliquid-mainnet");
        await f.PrepareAsync("first", "SOL", "account");
        await f.PrepareAsync("second", "SOL", "other-account");
        var (_, first) = await f.StartAsync("first");
        var (_, second) = await f.StartAsync("second");
        Assert.Equal(2, await f.Db.Cycles.CountAsync(x => !x.IsTerminal, Ct));
        var secondOrders = await f.Db.Orders.AsNoTracking().Where(x => x.CycleId == second.Id).ToListAsync(Ct);
        await f.Grid.CommandAsync(first.Id, "EMERGENCY_FLATTEN", "Test", "emergency-first", first.StateVersion, true, Ct);
        Assert.True(first.IsTerminal);
        Assert.False(second.IsTerminal);
        Assert.Equal("RUNNING", second.State);
        Assert.Equal(new[] { "account" }, f.Adapter.FlattenedAccounts);
        var after = await f.Db.Orders.AsNoTracking().Where(x => x.CycleId == second.Id).ToListAsync(Ct);
        Assert.Equal(secondOrders.Select(x => (x.Id, x.Status, x.FilledQuantity)), after.Select(x => (x.Id, x.Status, x.FilledQuantity)));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"cycle-uniqueness-{Guid.NewGuid():N}.db");
        private readonly ServiceProvider services = new ServiceCollection().AddLogging().AddSignalR().Services.BuildServiceProvider();
        private readonly PreviewStore previews = new();
        public required string Environment { get; init; }
        public TradingDbContext Db { get; private set; } = null!;
        public Adapter Adapter { get; private set; } = null!;
        public GridStrategyWorkflow Grid { get; private set; } = null!;
        public static async Task<Fixture> CreateAsync(string environment)
        {
            var f = new Fixture { Environment = environment };
            f.Db = f.NewDb();
            await f.Db.Database.EnsureCreatedAsync(Ct);
            f.Adapter = new(environment);
            f.Grid = f.Workflow(f.Db, f.Adapter);
            return f;
        }
        public TradingDbContext NewDb() => new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
        public GridStrategyWorkflow Workflow(TradingDbContext db, Adapter adapter)
        {
            var registry = new ExecutionEnvironmentRegistry([adapter]);
            var gate = new ExecutionAccountOperationGate();
            return new(db, new MarketState(), previews, registry, new(db, registry, gate), services.GetRequiredService<IHubContext<TradingHub>>(), gate);
        }
        public async Task PrepareAsync(string id, string symbol, string account = "account")
        {
            var config = Config(symbol);
            var now = DateTimeOffset.UtcNow;
            Db.Strategies.Add(new() { Id = id, Name = id, Symbol = symbol, ConfigurationJson = "{}", CreatedAt = now, UpdatedAt = now });
            await Db.SaveChangesAsync(Ct);
            previews.Items[id] = new(id, id, 1, Environment, account, now.AddMinutes(5), config, GridMath.BuildPlan(config, TradingService.RulesFor(config)));
        }
        public Task<(OperationEntity Operation, CycleEntity Cycle)> StartAsync(string id, GridStrategyWorkflow? workflow = null) =>
            (workflow ?? Grid).StartCycleAsync(id, new(id, 100m, new(true, true, Environment)), $"start-{id}", Ct);
        public CycleEntity Reserved(string id, string symbol) => new()
        {
            Id = id, StrategyId = id, ExecutionAccountId = "account", ExecutionEnvironmentId = Environment,
            State = "RUNNING", FrozenConfigurationJson = JsonSerializer.Serialize(Config(symbol), JsonSupport.Options),
            FrozenPlanJson = "{}", ExitReason = "", StartedAt = DateTimeOffset.UtcNow
        };
        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await services.DisposeAsync();
            File.Delete(path);
        }
    }
    private static GridConfiguration Config(string symbol) => new()
    {
        Symbol = symbol, CenterSuggestionMode = "MANUAL", TickSize = .1m, QuantityStep = .1m,
        MinOrderQuantity = .1m, MinOrderNotional = 1m, CenterPrice = 100m,
        MaxLevelsPerSide = 2, InitialGapPoints = 1m, GridSpacingPoints = 1m,
        TakeProfitPoints = 1m, BaseLotSize = 1m, MaxTradeLot = 1m, MaxNetLot = 5m
    };
    private sealed class Adapter(string environment) : IExecutionAdapter
    {
        public List<string> FlattenedAccounts { get; } = [];
        public int Placed;
        public int Preflights;
        public Func<Task>? BeforePreflight { get; init; }
        public ExecutionEnvironmentDescriptor Environment { get; } = new(environment, "PAPER", "PAPER", environment);
        public Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ExecutionAccountDescriptor>>([new("account", environment, "Account", true), new("other-account", environment, "Other", true)]);
        public Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection s, string symbol, decimal? price, CancellationToken ct) =>
            Task.FromResult(new ExecutionInstrument(symbol, environment, 0, 1, 100m, .1m, .1m, .1m, 1m, 10, 0m, 0m, "TEST", DateTimeOffset.UtcNow));
        public Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection s, string symbol, CancellationToken ct) =>
            Task.FromResult(new ExecutionQuote(99m, 101m, 100m, DateTimeOffset.UtcNow));
        public async Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection s, string symbol, CancellationToken ct)
        {
            Interlocked.Increment(ref Preflights);
            if (BeforePreflight is not null) await BeforePreflight();
            return await GetQuoteAsync(s, symbol, ct);
        }
        public Task PlaceOrdersAsync(ExecutionSelection s, GridConfiguration config, IEnumerable<OrderEntity> orders, CancellationToken ct)
        {
            Interlocked.Add(ref Placed, orders.Count());
            return Task.CompletedTask;
        }
        public Task CancelOrdersAsync(ExecutionSelection s, IEnumerable<OrderEntity> orders, CancellationToken ct) => Task.CompletedTask;
        public Task<decimal> FlattenAsync(ExecutionSelection s, CycleEntity cycle, GridConfiguration config, CancellationToken ct)
        { FlattenedAccounts.Add(s.AccountId); return Task.FromResult(0m); }
        public Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection s, CycleEntity cycle, GridConfiguration config, CancellationToken ct) =>
            Task.FromResult(new ExecutionReconciliationSnapshot([], [], [], new Dictionary<string, string>(), new(0m, 0m, 0m)));
    }
}
