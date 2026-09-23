using System.Text.Json;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Infrastructure;
using GridTrading.Api.Services;
using GridTrading.Api.Strategies.Grid;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Tests;

public sealed partial class HyperliquidMainnetTests
{
    [Theory]
    [InlineData(10, -3, "CLOSE")]
    [InlineData(-10, 3, "EMERGENCY_FLATTEN")]
    [InlineData(10, 3, "CLOSE")]
    [InlineData(-3, 3, "CLOSE")]
    public async Task SharedAccountCloseOffsetsOnlyStrategyExecutions(decimal manual, decimal strategy, string command)
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.SharedVenue = true;
        f.Handler.Position = manual + strategy;
        f.Handler.OpenOrders = [VenueOrder("SOL", 9000), VenueOrder("BTC", 9001)];
        var workflow = await f.WorkflowAsync();
        var cycle = await SeedPositionAsync(f, strategy);
        await workflow.CommandAsync(cycle.Id, command, "shared-account", "close", null, true, Ct);
        Assert.True(cycle.IsTerminal);
        Assert.Equal(0m, cycle.ReconstructedNetQuantity);
        Assert.Equal(manual, f.Handler.Position);
        Assert.Equal(manual, cycle.ActualNetQuantity);
        var request = Assert.Single(f.Handler.Actions).GetProperty("action").GetProperty("orders")[0];
        Assert.False(request.GetProperty("r").GetBoolean());
        Assert.Equal(Math.Abs(strategy).ToString(System.Globalization.CultureInfo.InvariantCulture), request.GetProperty("s").GetString());
        Assert.Equal(strategy < 0m, request.GetProperty("b").GetBoolean());
        Assert.Equal(2, f.Handler.OpenOrders.Length);
        Assert.Equal(2, await f.Db.Executions.CountAsync(Ct));
    }

    [Fact]
    public async Task LostExitResponseReconcilesWithoutSendingAnotherExit()
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.SharedVenue = true;
        f.Handler.Position = 7m;
        var workflow = await f.WorkflowAsync();
        var cycle = await SeedPositionAsync(f, -3m);
        f.Handler.LoseNextIocResponse = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => workflow.CommandAsync(cycle.Id, "CLOSE", "test", "first", null, false, Ct));
        Assert.Equal("UNKNOWN", (await f.Db.Orders.SingleAsync(x => x.Kind == "FLATTEN", Ct)).Status);
        Assert.False(cycle.IsTerminal);
        await workflow.CommandAsync(cycle.Id, "CLOSE", "retry", "second", null, false, Ct);
        Assert.True(cycle.IsTerminal);
        Assert.Single(f.Handler.Actions);
        Assert.Equal(10m, f.Handler.Position);
    }

    [Fact]
    public async Task MissingExitExecutionsBlockRetryUntilTheyArrive()
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.SharedVenue = true;
        f.Handler.Position = 7m;
        var workflow = await f.WorkflowAsync();
        var cycle = await SeedPositionAsync(f, -3m);
        f.Handler.HideFills = true;
        var error = await Assert.ThrowsAsync<TradingProblemException>(() => workflow.CommandAsync(cycle.Id, "CLOSE", "test", "first", null, false, Ct));
        Assert.Equal("STRATEGY_LEDGER_INCOMPLETE", error.Code);
        await Assert.ThrowsAsync<TradingProblemException>(() => workflow.CommandAsync(cycle.Id, "CLOSE", "test", "second", null, false, Ct));
        Assert.Single(f.Handler.Actions);
        f.Handler.HideFills = false;
        await workflow.CommandAsync(cycle.Id, "CLOSE", "test", "third", null, false, Ct);
        Assert.True(cycle.IsTerminal);
        Assert.Single(f.Handler.Actions);
    }

    [Fact]
    public async Task PartialExitRetriesOnlyConfirmedResidual()
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.SharedVenue = true;
        f.Handler.Position = 7m;
        var workflow = await f.WorkflowAsync();
        var cycle = await SeedPositionAsync(f, -3m);
        // A two-dollar residual is tradable in this fixture.
        cycle.FrozenConfigurationJson = JsonSerializer.Serialize(ExampleConfig() with { MinOrderNotional = 1m }, JsonSupport.Options);
        await f.Db.SaveChangesAsync(Ct);
        f.Handler.LeaveResidual = true;
        await Assert.ThrowsAsync<TradingProblemException>(() => workflow.CommandAsync(cycle.Id, "CLOSE", "test", "first", null, false, Ct));
        Assert.Equal(-.02m, cycle.ReconstructedNetQuantity);
        f.Handler.LeaveResidual = false;
        await workflow.CommandAsync(cycle.Id, "CLOSE", "test", "second", null, false, Ct);
        Assert.Equal("0.02", f.Handler.Actions[1].GetProperty("action").GetProperty("orders")[0].GetProperty("s").GetString());
        Assert.Equal(10m, f.Handler.Position);
        Assert.True(cycle.IsTerminal);
    }

    [Fact]
    public async Task RestartKeepsManualQuantityOutOfLedgerAndRecoversMissedStrategyFill()
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.SharedVenue = true;
        f.Handler.Position = 10m;
        var workflow = await f.WorkflowAsync(ExampleConfig() with { GridMode = GridMode.SellOnly });
        var (_, cycle) = await workflow.StartCycleAsync("strategy", new("preview", 100m, new(true, true, "MAINNET")), "start", Ct);
        var entry = await f.Db.Orders.SingleAsync(Ct);
        f.Handler.Fill(entry.ClientOrderId, .12m, 100.5m);
        f.Handler.Fills.Add(new { oid = 8000, hash = "manual", tid = 8000, time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), side = "B", px = "100", sz = "4", fee = "5" });
        f.Handler.Position += 4m;
        cycle.State = "PAUSED"; cycle.OperatorPaused = true;
        await f.Db.SaveChangesAsync(Ct);
        f.Db.ChangeTracker.Clear();
        cycle = await f.Db.Cycles.SingleAsync(Ct);
        var restarted = new GridOrderLifecycle(f.Db, new([f.Adapter]), new());
        Assert.False(restarted.IsLedgerReady(cycle));
        await restarted.ReconcileAsync(cycle, Ct);
        Assert.Equal(-.12m, cycle.ReconstructedNetQuantity);
        Assert.Equal(13.88m, cycle.ActualNetQuantity);
        Assert.Equal(.001m, cycle.PaidFees);
        Assert.True(cycle.OperatorPaused);
        Assert.True(restarted.IsLedgerReady(cycle));
        Assert.Single(await f.Db.Executions.ToListAsync(Ct));
        Assert.Single(await f.Db.Orders.Where(x => x.Kind == "TAKE_PROFIT").ToListAsync(Ct));
        await restarted.ReconcileAsync(cycle, Ct);
        Assert.Single(await f.Db.Executions.ToListAsync(Ct));
    }

    [Fact]
    public async Task CancellationRaceIsIncludedInExitQuantity()
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.SharedVenue = true;
        f.Handler.Position = 10m;
        var workflow = await f.WorkflowAsync(ExampleConfig() with { GridMode = GridMode.SellOnly });
        var (_, cycle) = await workflow.StartCycleAsync("strategy", new("preview", 100m, new(true, true, "MAINNET")), "start", Ct);
        var entry = await f.Db.Orders.SingleAsync(Ct);
        f.Handler.BeforeCancel = () => f.Handler.Fill(entry.ClientOrderId, .12m, 100.5m);
        await workflow.CommandAsync(cycle.Id, "CLOSE", "test", "close", null, false, Ct);
        Assert.True(cycle.IsTerminal);
        Assert.Equal(10m, f.Handler.Position);
        Assert.Equal(.12m, (await f.Db.Orders.SingleAsync(x => x.Kind == "FLATTEN", Ct)).Quantity);
    }

    [Fact]
    public async Task MissingCancellationRaceFillCannotCompleteTheCycle()
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.SharedVenue = true;
        f.Handler.Position = 10m;
        var workflow = await f.WorkflowAsync(ExampleConfig() with { GridMode = GridMode.SellOnly });
        var (_, cycle) = await workflow.StartCycleAsync("strategy", new("preview", 100m, new(true, true, "MAINNET")), "start", Ct);
        var entry = await f.Db.Orders.SingleAsync(Ct);
        f.Handler.BeforeCancel = () => f.Handler.Fill(entry.ClientOrderId, .12m, 100.5m);
        f.Handler.HideFills = true;
        var error = await Assert.ThrowsAsync<TradingProblemException>(() => workflow.CommandAsync(cycle.Id, "CLOSE", "test", "close", null, false, Ct));
        Assert.Equal("STRATEGY_LEDGER_INCOMPLETE", error.Code);
        Assert.False(cycle.IsTerminal);
        await using (var otherDb = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(f.Connection).Options))
        {
            var loaded = await otherDb.Cycles.SingleAsync(Ct);
            var otherLifecycle = new GridOrderLifecycle(otherDb, new([f.Adapter]), f.Gate);
            Assert.False(otherLifecycle.IsLedgerReady(loaded));
        }
        Assert.Empty(await f.Db.Orders.Where(x => x.Kind == "FLATTEN").ToListAsync(Ct));
        f.Handler.HideFills = false;
        await workflow.CommandAsync(cycle.Id, "CLOSE", "retry", "retry", null, false, Ct);
        Assert.True(cycle.IsTerminal);
        Assert.Equal(10m, f.Handler.Position);
    }

    [Fact]
    public async Task MissingHistoryListingFallsBackToIndividualOrderStatus()
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.SharedVenue = true;
        f.Handler.SuppressHistory = true;
        var workflow = await f.WorkflowAsync(ExampleConfig() with { GridMode = GridMode.SellOnly });
        var (_, cycle) = await workflow.StartCycleAsync("strategy", new("preview", 100m, new(true, true, "MAINNET")), "start", Ct);
        var entry = await f.Db.Orders.SingleAsync(Ct);
        f.Handler.Fill(entry.ClientOrderId, .12m, 100.5m);
        await workflow.CommandAsync(cycle.Id, "CLOSE", "test", "close", null, false, Ct);
        Assert.True(cycle.IsTerminal);
        Assert.Equal(0m, f.Handler.Position);
    }

    [Fact]
    public async Task OpposingStrategyLotsWithZeroNetNeedNoExchangeExit()
    {
        await using var f = await Fixture.CreateAsync();
        f.Handler.SharedVenue = true;
        f.Handler.Position = 10m;
        var workflow = await f.WorkflowAsync();
        var cycle = await SeedPositionAsync(f, 3m);
        f.Db.Orders.Add(new OrderEntity { Id = "short", CycleId = cycle.Id, ClientOrderId = "short", ExchangeOrderId = "60",
            Symbol = "SOL", Side = "SELL", Kind = "ENTRY", Status = "FILLED", Price = 105m, Quantity = 3m, FilledQuantity = 3m, FilledAt = cycle.StartedAt });
        f.Db.Executions.Add(new ExecutionEntity { Id = "short-fill", CycleId = cycle.Id, OrderId = "short", ExecutionAccountId = "live",
            ExchangeExecutionId = "short-fill", ExchangeOrderId = "60", Side = "SELL", Price = 105m, Quantity = 3m, OccurredAt = cycle.StartedAt });
        f.Db.VirtualLots.Add(new VirtualLotEntity { Id = "short-lot", CycleId = cycle.Id, EntryOrderId = "short", Side = "SELL",
            Status = "TP_ACCUMULATING", EntryFillPrice = 105m, FilledQuantity = 3m, RemainingQuantity = 3m });
        await f.Db.SaveChangesAsync(Ct);
        await workflow.CommandAsync(cycle.Id, "CLOSE", "test", "close", null, false, Ct);
        Assert.True(cycle.IsTerminal);
        Assert.Empty(f.Handler.Actions);
        Assert.Equal(10m, f.Handler.Position);
        Assert.Equal(15m, cycle.RealisedCyclePnl);
        Assert.Equal(2, await f.Db.VirtualLots.CountAsync(Ct));
    }

    [Fact]
    public async Task OldAmendmentGenerationWithoutClientIdStillBelongsToStrategy()
    {
        await using var f = await Fixture.CreateAsync();
        var cycle = await SeedPositionAsync(f, 3m);
        var entry = await f.Db.Orders.SingleAsync(Ct);
        entry.ExchangeOrderId = "52";
        entry.ExchangeOrderIdsJson = JsonSerializer.Serialize(new[] { "50", "51", "52" });
        await f.Db.SaveChangesAsync(Ct);
        var payload = JsonSerializer.SerializeToElement(new { oid = 51, hash = "old-generation", tid = 7,
            time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), side = "B", px = "100", sz = "0.1", fee = "0.001" });
        var fill = Assert.Single(await f.Adapter.NormalizeFillsAsync("live", [payload], Ct));
        Assert.Equal(entry.ClientOrderId, fill.ClientOrderId);
        Assert.Equal("hyperliquid-mainnet", fill.ExecutionEnvironmentId);
        var lifecycle = new GridOrderLifecycle(f.Db, new([f.Adapter]), new());
        Assert.Equal(0, await lifecycle.ProcessFillsAsync("live", [fill with { ExecutionEnvironmentId = "hyperliquid-testnet" }], Ct));
        Assert.Single(await f.Db.Executions.ToListAsync(Ct));
    }

    [Fact]
    public async Task ValuationIgnoresManualPositionAndLegacyFunding()
    {
        await using var f = await Fixture.CreateAsync();
        var cycle = await SeedPositionAsync(f, -3m);
        cycle.ActualNetQuantity = 1000m;
        cycle.AccruedFunding = 500m;
        var lifecycle = new GridOrderLifecycle(f.Db, new([f.Adapter]), new());
        var value = await lifecycle.ValueAsync(cycle, Ct);
        Assert.Equal(0m, value.AccruedFunding);
        Assert.Equal(-.3m, value.RealisedCyclePnl + value.UnrealisedAtExecutablePrice);
        Assert.Equal(3m * 100.1m * ExampleConfig().TakerFeeRate, value.EstimatedFinalTakerFee);
    }

    private static async Task<CycleEntity> SeedPositionAsync(Fixture f, decimal quantity)
    {
        var cycle = await f.AddCycleAsync();
        cycle.FrozenConfigurationJson = JsonSerializer.Serialize(ExampleConfig(), JsonSupport.Options);
        cycle.ReconstructedNetQuantity = 999m; // Recovery must rebuild this stale aggregate from executions.
        var side = quantity > 0m ? "BUY" : "SELL";
        f.Db.Orders.Add(new OrderEntity { Id = "seed-entry", CycleId = cycle.Id, ClientOrderId = "seed-entry", ExchangeOrderId = "50",
            Symbol = "SOL", Side = side, Kind = "ENTRY", Status = "FILLED", Price = 100m,
            Quantity = Math.Abs(quantity), FilledQuantity = Math.Abs(quantity), CreatedAt = cycle.StartedAt, FilledAt = cycle.StartedAt });
        f.Db.Executions.Add(new ExecutionEntity { Id = "seed-fill", CycleId = cycle.Id, OrderId = "seed-entry", ExecutionAccountId = "live",
            ExchangeExecutionId = "seed-fill", ExchangeOrderId = "50", Side = side, Price = 100m, Quantity = Math.Abs(quantity), OccurredAt = cycle.StartedAt });
        f.Db.VirtualLots.Add(new VirtualLotEntity { Id = "seed-lot", CycleId = cycle.Id, EntryOrderId = "seed-entry", Side = side,
            Status = "TP_ACCUMULATING", EntryFillPrice = 100m, FilledQuantity = Math.Abs(quantity), RemainingQuantity = Math.Abs(quantity) });
        await f.Db.SaveChangesAsync(Ct);
        return cycle;
    }
}
