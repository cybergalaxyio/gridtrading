using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Infrastructure;
using GridTrading.Api.Services;
using GridTrading.Api.Strategies.Grid;
using GridTrading.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Tests;

public sealed class SingleModeMoveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(GridMode.SellOnly, -1)]
    [InlineData(GridMode.BuyOnly, 1)]
    public async Task MovesOnlyAfterBothThresholdsAndPersistsAcrossRestart(GridMode mode, int direction)
    {
        await using var f = await Fixture.CreateAsync(mode);
        var original = f.Entry;
        var frozen = f.Cycle.FrozenPlanJson;
        f.Adapter.SetMarket(100m + direction * .40m);
        f.Clock.Advance(44);
        await f.Maintain();
        Assert.Equal(0, f.Adapter.Cancels);
        f.Clock.Advance(1);
        await f.Maintain();
        var moved = Assert.Single(f.ActiveEntries());
        Assert.NotEqual(original.Id, moved.Id);
        Assert.Equal(original.Price + direction * .40m, moved.Price);
        Assert.Equal(direction * .40m, f.Cycle.EntryGridPriceOffset);
        Assert.Equal(frozen, f.Cycle.FrozenPlanJson);
        Assert.Null(f.Cycle.EntryGridMovePendingOrderId);
        Assert.Equal(1, await f.Db.AuditLogs.CountAsync(x => x.Action == "ENTRY_GRID_MOVED" && x.ResourceId == f.Cycle.Id, Ct));
        f.Adapter.SetMarket(100m + direction * .80m);
        await f.Maintain();
        Assert.Equal(1, f.Adapter.Cancels);
        await f.Restart();
        Assert.Equal(direction * .40m, f.Cycle.EntryGridPriceOffset);
        f.Clock.Advance(45);
        await f.Maintain();
        Assert.Equal(original.Price + direction * .80m, Assert.Single(f.ActiveEntries()).Price);
        Assert.Equal(2, f.Adapter.Cancels);
        f.Adapter.SetMarket(100m); // Rebound: leave the order to fill.
        f.Clock.Advance(60);
        await f.Maintain();
        Assert.Equal(2, f.Adapter.Cancels);
        Assert.Equal(2, await f.Db.AuditLogs.CountAsync(x => x.Action == "ENTRY_GRID_MOVED", Ct));
    }

    [Theory]
    [InlineData("distance")]
    [InlineData("stale")]
    [InlineData("unknown")]
    [InlineData("partial")]
    [InlineData("position")]
    [InlineData("reconstructed")]
    [InlineData("paused")]
    [InlineData("minimum")]
    [InlineData("limit")]
    public async Task IneligibleOrdersAreNotCancelled(string reason)
    {
        await using var f = await Fixture.CreateAsync();
        f.Clock.Advance(60);
        f.Adapter.SetMarket(reason == "minimum" ? 30m : reason == "distance" ? 99.61m : 99m);
        if (reason == "stale") f.Adapter.Stale = true;
        if (reason == "unknown") f.Entry.Status = "UNKNOWN";
        if (reason == "partial") { f.Entry.Status = "PARTIALLY_FILLED"; f.Entry.FilledQuantity = .1m; }
        if (reason == "position") f.Cycle.ActualNetQuantity = -.2m;
        if (reason == "reconstructed") f.Cycle.ReconstructedNetQuantity = -.2m;
        if (reason == "paused") { f.Cycle.State = "PAUSED"; f.Cycle.OperatorPaused = true; }
        if (reason == "limit")
        {
            f.Config = f.Config with { EntryFillLimitEnabled = true, MaxEntryFillsPerSide = 1 };
            var completed = GridOrderLifecycle.CreateEntry(f.Cycle, "SOL", f.Cycle.EffectivePlan.Levels[0], .2m);
            completed.Status = "FILLED"; completed.FilledQuantity = .2m; completed.FilledAt = f.Clock.GetUtcNow();
            f.Db.Orders.Add(completed);
        }
        await f.Db.SaveChangesAsync(Ct);
        await f.Maintain();
        Assert.Equal(0m, f.Cycle.EntryGridPriceOffset);
        Assert.Empty(await f.Db.Orders.Where(x => x.Kind == "ENTRY" && x.Id != f.Entry.Id && x.Status != "FILLED").ToListAsync(Ct));
        Assert.Equal(reason == "limit" ? 1 : 0, f.Adapter.Cancels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedCancellationRecoversWithoutDuplicateOrders(bool responseLost)
    {
        await using var f = await Fixture.CreateAsync();
        f.Clock.Advance(60); f.Adapter.SetMarket(99m);
        f.Adapter.CancelFailure = responseLost ? "after" : "before";
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Maintain());
        Assert.Equal(f.Entry.Id, f.Cycle.EntryGridMovePendingOrderId);
        Assert.Equal(0m, f.Cycle.EntryGridPriceOffset);
        Assert.Equal(1, await f.Db.Orders.CountAsync(Ct));
        await f.Restart();
        await f.Maintain();
        Assert.Null(f.Cycle.EntryGridMovePendingOrderId);
        Assert.Equal(-1m, f.Cycle.EntryGridPriceOffset);
        Assert.Single(f.ActiveEntries());
        Assert.Equal(2, await f.Db.Orders.CountAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FillDuringCancellationIsRecordedAndProtectedBeforeAnyReplacement(bool delayFill)
    {
        await using var f = await Fixture.CreateAsync();
        f.Clock.Advance(60); f.Adapter.SetMarket(99m);
        f.Adapter.FillOnCancel = .1m;
        f.Adapter.HideFills = delayFill;
        await f.Maintain();
        Assert.Equal(0m, f.Cycle.EntryGridPriceOffset);
        Assert.Empty(f.ActiveEntries());
        if (delayFill)
        {
            Assert.NotNull(f.Cycle.EntryGridMovePendingOrderId);
            await f.Restart();
            f.Adapter.HideFills = false;
            await f.Maintain();
        }
        Assert.Null(f.Cycle.EntryGridMovePendingOrderId);
        Assert.Equal(-.1m, f.Cycle.ActualNetQuantity);
        var lot = await f.Db.VirtualLots.SingleAsync(Ct);
        Assert.Equal(.1m, lot.RemainingQuantity);
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", Ct);
        Assert.Equal(.1m, tp.Quantity);
        Assert.Equal(f.Entry.Price - .10m, tp.Price);
        Assert.Equal(1, await f.Db.Orders.CountAsync(x => x.Kind == "ENTRY", Ct));
    }

    [Fact]
    public async Task MissingTerminalObservationBlocksUntilConfirmed()
    {
        await using var f = await Fixture.CreateAsync();
        f.Clock.Advance(60); f.Adapter.SetMarket(99m);
        f.Adapter.HideCancelled = true;
        await f.Maintain();
        Assert.NotNull(f.Cycle.EntryGridMovePendingOrderId);
        Assert.Equal(0m, f.Cycle.EntryGridPriceOffset);
        await f.Restart();
        await f.Maintain();
        Assert.Equal(1, await f.Db.Orders.CountAsync(Ct));
        f.Adapter.HideCancelled = false;
        await f.Maintain();
        Assert.Equal(-1m, f.Cycle.EntryGridPriceOffset);
        Assert.Single(f.ActiveEntries());
    }

    [Fact]
    public async Task MovedGridStaysFixedWhileHoldingAndResumesAfterTakeProfit()
    {
        await using var f = await Fixture.CreateAsync();
        f.Clock.Advance(60); f.Adapter.SetMarket(99m);
        await f.Maintain();
        var moved = Assert.Single(f.ActiveEntries());
        var fill = f.Adapter.Fill(moved, moved.Quantity);
        await f.Lifecycle.ProcessFillsAsync(f.Cycle.ExecutionAccountId, [fill], Ct);
        var next = Assert.Single(f.ActiveEntries());
        Assert.Equal(1, next.GridLevel);
        Assert.Equal(moved.Price + .20m, next.Price);
        Assert.Equal(.3m, next.Quantity);
        f.Clock.Advance(60); f.Adapter.SetMarket(98m);
        await f.Maintain();
        Assert.Equal(-1m, f.Cycle.EntryGridPriceOffset);
        Assert.Equal(next.Id, Assert.Single(f.ActiveEntries()).Id);
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", Ct);
        await f.Lifecycle.ProcessFillsAsync(f.Cycle.ExecutionAccountId, [f.Adapter.Fill(tp, tp.Quantity)], Ct);
        Assert.Equal(0m, f.Cycle.ActualNetQuantity);
        Assert.Equal(-2m, f.Cycle.EntryGridPriceOffset);
        var reset = Assert.Single(f.ActiveEntries());
        Assert.Equal(0, reset.GridLevel);
        Assert.Equal(.2m, reset.Quantity);
        Assert.Equal(98.10m, reset.Price);
    }

    [Theory]
    [InlineData("before-send")]
    [InlineData("after-send")]
    public async Task ReplacementIntentAndOffsetRecoverTogetherWithoutResubmission(string failure)
    {
        await using var f = await Fixture.CreateAsync();
        f.Clock.Advance(60); f.Adapter.SetMarket(99m);
        f.Adapter.PlaceFailure = failure;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Maintain());
        Assert.Equal(-1m, f.Cycle.EntryGridPriceOffset);
        Assert.Null(f.Cycle.EntryGridMovePendingOrderId);
        var replacement = Assert.Single(f.ActiveEntries());
        Assert.Equal(failure == "before-send" ? "PENDING_EXCHANGE" : "UNKNOWN", replacement.Status);
        Assert.Equal(0, await f.Db.AuditLogs.CountAsync(x => x.Action == "ENTRY_GRID_MOVED", Ct));
        await f.Restart();
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        Assert.Equal(replacement.Id, Assert.Single(f.ActiveEntries()).Id);
        Assert.Equal("NEW", Assert.Single(f.ActiveEntries()).Status);
        Assert.Equal(2, f.Adapter.Placements); // Original and one accepted replacement.
        Assert.Equal(2, await f.Db.Orders.CountAsync(Ct));
        Assert.Equal(1, await f.Db.AuditLogs.CountAsync(x => x.Action == "ENTRY_GRID_MOVED", Ct));
        Assert.Equal(-1m, f.Cycle.EntryGridPriceOffset);
    }

    [Theory]
    [InlineData(GridMode.SellOnly, -1)]
    [InlineData(GridMode.BuyOnly, 1)]
    public async Task StaleQuoteAfterCancellationBlocksUntilFreshEvenAcrossRestart(GridMode mode, int direction)
    {
        await using var f = await Fixture.CreateAsync(mode);
        f.Clock.Advance(60);
        f.Adapter.SetMarket(100m + direction);
        f.Adapter.StaleAfterCancel = true;
        await f.Maintain();
        Assert.Equal(1, f.Adapter.Cancels);
        Assert.Equal(f.Entry.Id, f.Cycle.EntryGridMovePendingOrderId);
        Assert.Empty(f.ActiveEntries());
        await f.Maintain();
        await f.Restart();
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        Assert.Empty(f.ActiveEntries());
        Assert.Equal(1, f.Adapter.Placements);
        Assert.Equal(0m, f.Cycle.EntryGridPriceOffset);
        f.Adapter.Stale = f.Adapter.StaleAfterCancel = false;
        f.Adapter.SetMarket(100m + direction * 2m);
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        Assert.Equal(f.Entry.Price + direction * 2m, Assert.Single(f.ActiveEntries()).Price);
        Assert.Equal(2, f.Adapter.Placements);
        Assert.Equal(1, f.Adapter.Cancels);
        Assert.Null(f.Cycle.EntryGridMovePendingOrderId);
    }

    [Theory]
    [InlineData(GridMode.SellOnly)]
    [InlineData(GridMode.BuyOnly)]
    [InlineData(GridMode.TwoWay)]
    public async Task EmptyGridDoesNotCreateOrdersFromStaleQuotes(GridMode mode)
    {
        await using var f = await Fixture.CreateAsync(mode);
        await f.Adapter.CancelOrdersAsync(new(f.Cycle.ExecutionEnvironmentId, f.Cycle.ExecutionAccountId), [f.Entry], Ct);
        f.Adapter.Stale = true;
        await f.Maintain();
        Assert.Empty(f.ActiveEntries());
        Assert.Equal(1, await f.Db.Orders.CountAsync(Ct));
        f.Adapter.Stale = false;
        await f.Maintain();
        Assert.NotEmpty(f.ActiveEntries());
        Assert.All(f.ActiveEntries(), order => Assert.Equal("NEW", order.Status));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("crossed")]
    [InlineData("zero")]
    public async Task PendingEntrySubmissionRequiresAValidCurrentQuote(string invalid)
    {
        await using var f = await Fixture.CreateAsync();
        var entry = GridOrderLifecycle.CreateEntry(f.Cycle, "SOL", f.Cycle.EffectivePlan.Levels[0], .2m);
        f.Db.Orders.Add(entry);
        await f.Db.SaveChangesAsync(Ct);
        f.Adapter.InvalidQuote = invalid;
        await f.Lifecycle.PlaceNewEntryOrdersAsync(f.Cycle, f.Config, [entry], Ct);
        Assert.Equal("PENDING_EXCHANGE", entry.Status);
        Assert.Equal(1, f.Adapter.Placements);
        f.Adapter.InvalidQuote = null;
        await f.Lifecycle.PlaceNewEntryOrdersAsync(f.Cycle, f.Config, [entry], Ct);
        Assert.Equal("NEW", entry.Status);
        Assert.Equal(2, f.Adapter.Placements);
    }

    [Fact]
    public async Task QuoteExpiryAtFinalSubmissionRetainsIntentAndRecoversExactlyOnce()
    {
        await using var f = await Fixture.CreateAsync();
        f.Clock.Advance(60);
        f.Adapter.SetMarket(99m);
        f.Adapter.ExpireWhenEntryPending = true;
        await f.Maintain();
        var pending = Assert.Single(f.ActiveEntries());
        Assert.Equal("PENDING_EXCHANGE", pending.Status);
        Assert.Equal(-1m, f.Cycle.EntryGridPriceOffset);
        Assert.Equal(1, f.Adapter.Placements);
        await f.Restart();
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        Assert.Equal("PENDING_EXCHANGE", Assert.Single(f.ActiveEntries()).Status);
        Assert.Equal(1, f.Adapter.Placements);
        f.Adapter.ExpireWhenEntryPending = false;
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        Assert.Equal(pending.Id, Assert.Single(f.ActiveEntries()).Id);
        Assert.Equal("NEW", Assert.Single(f.ActiveEntries()).Status);
        Assert.Equal(2, f.Adapter.Placements);
        Assert.Equal(1, await f.Db.AuditLogs.CountAsync(x => x.Action == "ENTRY_GRID_MOVED", Ct));
    }

    [Fact]
    public async Task StaleQuotesStillAllowEntryLimitCancellationAndTakeProfitProtection()
    {
        await using var f = await Fixture.CreateAsync();
        f.Adapter.Stale = true;
        var partial = f.Adapter.Fill(f.Entry, .1m);
        await f.Lifecycle.ProcessFillsAsync(f.Cycle.ExecutionAccountId, [partial], Ct);
        Assert.Equal("PARTIALLY_FILLED", f.Entry.Status);
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", Ct);
        Assert.Equal("NEW", tp.Status);
        Assert.Equal(.1m, tp.Quantity);
        // A completed entry at the configured limit must still cancel the remaining tail.
        var completed = GridOrderLifecycle.CreateEntry(f.Cycle, "SOL", f.Cycle.EffectivePlan.Levels[1], .2m);
        completed.Status = "FILLED"; completed.FilledQuantity = .2m; completed.FilledAt = f.Clock.GetUtcNow();
        f.Db.Orders.Add(completed);
        f.Config = f.Config with { EntryFillLimitEnabled = true, MaxEntryFillsPerSide = 1 };
        await f.Db.SaveChangesAsync(Ct);
        await f.Maintain();
        Assert.Equal("CANCELLED", f.Entry.Status);
        Assert.Equal("NEW", tp.Status);
        Assert.Equal(1, await f.Db.Orders.CountAsync(x => x.Kind == "TAKE_PROFIT", Ct));
    }

    [Theory]
    [InlineData(GridMode.SellOnly, -1)]
    [InlineData(GridMode.BuyOnly, 1)]
    public async Task OnlyTheLastClosedLotResetsToFirstLevelAndBaseQuantity(GridMode mode, int sign)
    {
        await using var f = await Fixture.CreateAsync(mode);
        var frozen = f.Cycle.FrozenPlanJson;
        await f.Lifecycle.ProcessFillsAsync("test", [f.Adapter.Fill(f.Entry, f.Entry.Quantity)], Ct);
        var second = Assert.Single(f.ActiveEntries());
        await f.Lifecycle.ProcessFillsAsync("test", [f.Adapter.Fill(second, second.Quantity)], Ct);
        var third = Assert.Single(f.ActiveEntries());
        Assert.Equal(2, third.GridLevel);
        var tps = await f.Db.Orders.Where(x => x.Kind == "TAKE_PROFIT").OrderBy(x => x.GridLevel).ToArrayAsync(Ct);
        f.Adapter.SetMarket(100m + sign * 2m);
        await f.Lifecycle.ProcessFillsAsync("test", [f.Adapter.Fill(tps[0], tps[0].Quantity)], Ct);
        Assert.Equal(third.Id, Assert.Single(f.ActiveEntries()).Id);
        Assert.Equal(0m, f.Cycle.EntryGridPriceOffset);
        await f.Lifecycle.ProcessFillsAsync("test", [f.Adapter.Fill(tps[1], tps[1].Quantity)], Ct);
        var reset = Assert.Single(f.ActiveEntries());
        Assert.Equal("CANCELLED", third.Status);
        Assert.Equal(0, reset.GridLevel);
        Assert.Equal(.2m, reset.Quantity);
        Assert.Equal(100m + sign * 1.9m, reset.Price);
        Assert.Equal(sign * 2m, f.Cycle.EntryGridPriceOffset);
        Assert.Equal(frozen, f.Cycle.FrozenPlanJson);
        Assert.Equal(f.Clock.GetUtcNow(), reset.CreatedAt);
        // The first round's large offset must not accumulate into the next round.
        await f.Lifecycle.ProcessFillsAsync("test", [f.Adapter.Fill(reset, reset.Quantity)], Ct);
        var latestTp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT" && x.Status == "NEW", Ct);
        f.Adapter.SetMarket(100m - sign); // New rounds can re-anchor in either direction.
        await f.Lifecycle.ProcessFillsAsync("test", [f.Adapter.Fill(latestTp, latestTp.Quantity)], Ct);
        var again = Assert.Single(f.ActiveEntries());
        Assert.Equal(0, again.GridLevel);
        Assert.Equal(.2m, again.Quantity);
        Assert.Equal(-sign, f.Cycle.EntryGridPriceOffset);
        var placements = f.Adapter.Placements;
        await f.Restart();
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        await f.Maintain();
        Assert.Equal(again.Id, Assert.Single(f.ActiveEntries()).Id);
        Assert.Equal(placements, f.Adapter.Placements);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("stale")]
    [InlineData("missing")]
    public async Task FlatResetWaitsForConfirmedCancellationAndRecoversAfterRestart(string interruption)
    {
        await using var f = await Fixture.CreateAsync();
        await f.Lifecycle.ProcessFillsAsync("test", [f.Adapter.Fill(f.Entry, f.Entry.Quantity)], Ct);
        var next = Assert.Single(f.ActiveEntries());
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", Ct);
        f.Adapter.SetMarket(101m);
        f.Adapter.StaleAfterCancel = interruption == "stale";
        f.Adapter.HideCancelled = interruption == "missing";
        if (interruption is "before" or "after") f.Adapter.CancelFailure = interruption;
        var close = f.Adapter.Fill(tp, tp.Quantity);
        if (interruption is "before" or "after")
            await Assert.ThrowsAsync<HttpRequestException>(() => f.Lifecycle.ProcessFillsAsync("test", [close], Ct));
        else await f.Lifecycle.ProcessFillsAsync("test", [close], Ct);
        Assert.Equal(next.Id, f.Cycle.EntryGridMovePendingOrderId);
        Assert.Equal(0m, f.Cycle.EntryGridPriceOffset);
        await f.Restart();
        f.Adapter.StaleAfterCancel = f.Adapter.Stale = f.Adapter.HideCancelled = false;
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        var reset = Assert.Single(f.ActiveEntries());
        Assert.Equal(0, reset.GridLevel);
        Assert.Equal(.2m, reset.Quantity);
        Assert.Equal(101.1m, reset.Price);
        Assert.Equal(1m, f.Cycle.EntryGridPriceOffset);
        var count = f.Adapter.Placements;
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        Assert.Equal(count, f.Adapter.Placements);
    }

    [Fact]
    public async Task AFillDuringResetCancellationKeepsTheHoldingGridAndProtectsTheFill()
    {
        await using var f = await Fixture.CreateAsync();
        await f.Lifecycle.ProcessFillsAsync("test", [f.Adapter.Fill(f.Entry, f.Entry.Quantity)], Ct);
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", Ct);
        f.Adapter.FillOnCancel = .1m;
        await f.Lifecycle.ProcessFillsAsync("test", [f.Adapter.Fill(tp, tp.Quantity)], Ct);
        Assert.Equal(-.1m, f.Cycle.ActualNetQuantity);
        Assert.Equal(0m, f.Cycle.EntryGridPriceOffset);
        Assert.Empty(f.ActiveEntries());
        var protection = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT" && x.Status == "NEW", Ct);
        Assert.Equal(.1m, protection.Quantity);
        Assert.Null(f.Cycle.EntryGridMovePendingOrderId);
    }

    [Fact]
    public async Task UnsentHigherLevelIsNeverSubmittedAfterClosingThePosition()
    {
        await using var f = await Fixture.CreateAsync();
        // TP submission succeeds; the following entry becomes a persisted unsent intent.
        f.Cycle.OperatorPaused = true;
        await f.Lifecycle.ProcessFillsAsync("test", [f.Adapter.Fill(f.Entry, f.Entry.Quantity)], Ct);
        f.Cycle.OperatorPaused = false;
        f.Adapter.PlaceFailure = "before-send";
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Maintain());
        var pending = Assert.Single(f.ActiveEntries());
        Assert.Equal(1, pending.GridLevel);
        Assert.Equal("PENDING_EXCHANGE", pending.Status);
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", Ct);
        // Leave the close for restart reconciliation, whose pending-entry submission runs first.
        f.Adapter.Fill(tp, tp.Quantity);
        await f.Db.SaveChangesAsync(Ct);
        await f.Restart();
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        var reset = Assert.Single(f.ActiveEntries());
        Assert.Equal(0, reset.GridLevel);
        Assert.Equal(.2m, reset.Quantity);
        Assert.Equal("CANCELLED", (await f.Db.Orders.SingleAsync(x => x.Id == pending.Id, Ct)).Status);
        Assert.Equal(0, f.Adapter.Cancels); // The unsent intent was cancelled locally.
        Assert.Equal(3, f.Adapter.Placements); // Original, its TP, and one base entry.
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("paused")]
    [InlineData("minimum")]
    [InlineData("pending-protection")]
    public async Task FlatResetStillHonorsQuotePauseMinimumAndProtectionGuards(string reason)
    {
        await using var f = await Fixture.CreateAsync();
        await f.Lifecycle.ProcessFillsAsync("test", [f.Adapter.Fill(f.Entry, f.Entry.Quantity)], Ct);
        var next = Assert.Single(f.ActiveEntries());
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", Ct);
        f.Cycle.OperatorPaused = true;
        await f.Lifecycle.ProcessFillsAsync("test", [f.Adapter.Fill(tp, tp.Quantity)], Ct);
        f.Cycle.OperatorPaused = reason == "paused";
        if (reason == "stale") f.Adapter.Stale = true;
        if (reason == "minimum") f.Adapter.SetMarket(30m);
        if (reason == "pending-protection")
        {
            var pending = GridOrderLifecycle.CreateEntry(f.Cycle, "SOL", f.Cycle.EffectivePlan.Levels[0], .2m);
            pending.Kind = "TAKE_PROFIT";
            f.Db.Orders.Add(pending);
        }
        await f.Db.SaveChangesAsync(Ct);
        var placements = f.Adapter.Placements;
        await f.Maintain();
        Assert.Equal(next.Id, Assert.Single(f.ActiveEntries()).Id);
        Assert.Equal(0, f.Adapter.Cancels);
        Assert.Equal(placements, f.Adapter.Placements);
        Assert.Equal(0m, f.Cycle.EntryGridPriceOffset);
    }

    [Theory]
    [InlineData(GridMode.BuyOnly, 1)]
    [InlineData(GridMode.SellOnly, -1)]
    public async Task FlatWithoutAnEntryReanchorsFirstLevelAfterQuotesRecover(GridMode mode, int sign)
    {
        await using var f = await Fixture.CreateAsync(mode);
        await f.Lifecycle.ProcessFillsAsync("test", [f.Adapter.Fill(f.Entry, f.Entry.Quantity)], Ct);
        var next = Assert.Single(f.ActiveEntries());
        await f.Adapter.CancelOrdersAsync(new(f.Cycle.ExecutionEnvironmentId, "test"), [next], Ct);
        var tp = await f.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", Ct);
        f.Adapter.SetMarket(100m - sign * 3m);
        f.Adapter.Stale = true;
        await f.Lifecycle.ProcessFillsAsync("test", [f.Adapter.Fill(tp, tp.Quantity)], Ct);
        Assert.Empty(f.ActiveEntries());
        await f.Restart();
        f.Adapter.Stale = false;
        await f.Lifecycle.ReconcileAsync(f.Cycle, Ct);
        var reset = Assert.Single(f.ActiveEntries());
        Assert.Equal(0, reset.GridLevel);
        Assert.Equal(.2m, reset.Quantity);
        Assert.Equal(100m - sign * 3.1m, reset.Price);
        Assert.Equal(-sign * 3m, f.Cycle.EntryGridPriceOffset);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection = new("Data Source=:memory:");
        public TradingDbContext Db = null!;
        public CycleEntity Cycle = null!;
        public OrderEntity Entry = null!;
        public GridConfiguration Config = null!;
        public GridOrderLifecycle Lifecycle = null!;
        public RecordingAdapter Adapter = null!;
        public TestClock Clock = new();
        public static async Task<Fixture> CreateAsync(GridMode mode = GridMode.SellOnly)
        {
            var f = new Fixture();
            await f.connection.OpenAsync(Ct);
            f.Db = new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(f.connection).Options);
            await f.Db.Database.EnsureCreatedAsync(Ct);
            f.Config = new GridConfiguration
            {
                Symbol = "SOL", GridMode = mode, CenterPrice = 100m, TickSize = .01m,
                QuantityStep = .01m, MinOrderQuantity = .01m, MinOrderNotional = 1m,
                InitialGapPoints = 20m, GridSpacingPoints = 20m, TakeProfitPoints = 10m,
                SingleModeMoveDistancePoints = 40m, SingleModeMoveIntervalSeconds = 45,
                BaseLotSize = .2m, LotSizeIncreasePercent = 50m, MaxLevelsPerSide = 3, MaxNetLot = 2m,
                FaultExposureThresholdUsdt = 100m
            };
            var plan = GridMath.BuildPlan(f.Config, TradingService.RulesFor(f.Config));
            f.Cycle = new CycleEntity { Id = "cycle", StrategyId = "strategy", State = "RUNNING", ExitReason = "",
                ExecutionEnvironmentId = ExecutionEnvironmentIds.HyperliquidTestnet, ExecutionAccountId = "test",
                FixedCenterPrice = 100m, StartedAt = f.Clock.GetUtcNow(), LastReconciledAt = f.Clock.GetUtcNow(),
                FrozenConfigurationJson = JsonSerializer.Serialize(f.Config, JsonSupport.Options),
                FrozenPlanJson = JsonSerializer.Serialize(plan, JsonSupport.Options) };
            f.Db.Cycles.Add(f.Cycle);
            f.Db.Strategies.Add(new StrategyEntity { Id = "strategy", Name = "Test", Symbol = "SOL", ConfigurationJson = "{}" });
            f.Entry = GridOrderLifecycle.CreateEntry(f.Cycle, "SOL", plan.Levels[0], .2m);
            f.Entry.CreatedAt = f.Clock.GetUtcNow();
            f.Db.Orders.Add(f.Entry);
            await f.Db.SaveChangesAsync(Ct);
            f.Adapter = new(f.Db, f.Clock);
            await f.Adapter.PlaceOrdersAsync(new(f.Cycle.ExecutionEnvironmentId, "test"), f.Config, [f.Entry], Ct);
            f.Lifecycle = new(f.Db, new([f.Adapter]), new(), f.Clock);
            return f;
        }
        public Task Maintain() => Lifecycle.MaintainEntryOrdersAsync(Cycle, Config, null, Ct);
        public OrderEntity[] ActiveEntries() => Db.Orders.Where(x => x.Kind == "ENTRY" &&
            (x.Status == "NEW" || x.Status == "PENDING_EXCHANGE" || x.Status == "UNKNOWN" || x.Status == "PARTIALLY_FILLED")).ToArray();
        public async Task Restart()
        {
            await Db.DisposeAsync();
            Db = new(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
            Cycle = await Db.Cycles.SingleAsync(Ct);
            Entry = await Db.Orders.SingleAsync(x => x.Id == Entry.Id, Ct);
            Adapter.Db = Db;
            Lifecycle = new(Db, new([Adapter]), new(), Clock);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }

    // Exchange state is independent of the local DbContext, including across restarts.
    private sealed class RecordingAdapter(TradingDbContext db, TestClock clock) : IExecutionAdapter, IOrderAmendmentAdapter
    {
        public TradingDbContext Db = db;
        private readonly Dictionary<string, ExecutionOrderSnapshot> orders = [];
        private readonly List<NormalizedExecutionFill> fills = [];
        private decimal market = 100m;
        public int Cancels, Placements;
        public string? PlaceFailure;
        public string? CancelFailure;
        public decimal FillOnCancel;
        public bool HideFills, HideCancelled, Stale, StaleAfterCancel, ExpireWhenEntryPending;
        public string? InvalidQuote;
        public ExecutionEnvironmentDescriptor Environment => new(ExecutionEnvironmentIds.HyperliquidTestnet, "HYPERLIQUID", "TESTNET", "Simulated");
        public void SetMarket(decimal value) => market = value;
        public Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ExecutionAccountDescriptor>>([new("test", Environment.Id, "Test", true)]);
        public Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection s, string symbol, decimal? p, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection s, string symbol, CancellationToken ct)
        {
            var stale = Stale || InvalidQuote == "stale" || (ExpireWhenEntryPending &&
                Db.Orders.Local.Any(x => x.Kind == "ENTRY" && x.Status == "PENDING_EXCHANGE"));
            return Task.FromResult(new ExecutionQuote(InvalidQuote == "zero" ? 0m : market,
                InvalidQuote == "crossed" ? market - 1m : market, market,
                clock.GetUtcNow().AddSeconds(stale ? -10 : InvalidQuote == "future" ? 10 : 0)));
        }
        public Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection s, string symbol, CancellationToken ct) => GetQuoteAsync(s, symbol, ct);
        public async Task PlaceOrdersAsync(ExecutionSelection s, GridConfiguration c, IEnumerable<OrderEntity> entries, CancellationToken ct)
        {
            var failure = PlaceFailure; PlaceFailure = null;
            if (failure == "before-send") throw new HttpRequestException("Interrupted before send");
            foreach (var order in entries)
            {
                Placements++;
                order.Status = "NEW"; order.ExchangeOrderId = "venue-" + order.Id;
                orders[order.ClientOrderId] = new(order.ExchangeOrderId, order.ClientOrderId, "NEW", order.Price, order.Quantity, order.Quantity);
                if (failure == "after-send")
                {
                    order.Status = "UNKNOWN";
                    await Db.SaveChangesAsync(ct);
                    throw new HttpRequestException("Placement acknowledgement lost");
                }
            }
            await Db.SaveChangesAsync(ct);
        }
        public NormalizedExecutionFill Fill(OrderEntity order, decimal quantity)
        {
            var observed = orders[order.ClientOrderId];
            var remaining = observed.RemainingQuantity - quantity;
            orders[order.ClientOrderId] = observed with { RemainingQuantity = remaining, Status = remaining == 0m ? "FILLED" : "PARTIALLY_FILLED" };
            var fill = new NormalizedExecutionFill("fill-" + fills.Count, order.ExchangeOrderId, order.ClientOrderId,
                order.Side, order.Price, quantity, 0m, clock.GetUtcNow());
            fills.Add(fill);
            return fill;
        }
        public async Task CancelOrdersAsync(ExecutionSelection s, IEnumerable<OrderEntity> entries, CancellationToken ct)
        {
            Cancels++;
            var failure = CancelFailure; CancelFailure = null;
            if (failure == "before") throw new HttpRequestException("Cancellation unavailable");
            foreach (var entry in entries)
            {
                if (FillOnCancel > 0m) { Fill(entry, FillOnCancel); FillOnCancel = 0m; }
                orders[entry.ClientOrderId] = orders[entry.ClientOrderId] with { Status = "CANCELLED" };
                if (failure == "after") throw new HttpRequestException("Cancellation response lost");
                entry.Status = "CANCELLED";
                if (StaleAfterCancel) Stale = true;
            }
            await Db.SaveChangesAsync(ct);
        }
        public async Task AmendOrderAsync(ExecutionSelection s, GridConfiguration c, OrderEntity order, decimal price, decimal quantity, CancellationToken ct)
        {
            order.Price = price; order.Quantity = quantity;
            orders[order.ClientOrderId] = orders[order.ClientOrderId] with { Price = price, OriginalQuantity = quantity, RemainingQuantity = quantity - order.FilledQuantity };
            await Db.SaveChangesAsync(ct);
        }
        public Task<decimal> FlattenAsync(ExecutionSelection s, CycleEntity cycle, GridConfiguration c, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection s, CycleEntity cycle, GridConfiguration c, CancellationToken ct)
        {
            var open = orders.Values.Where(x => x.Status is "NEW" or "PARTIALLY_FILLED").ToDictionary(x => x.ClientOrderId, x => x.ExchangeOrderId);
            var position = fills.Sum(x => x.Side == "BUY" ? x.Quantity : -x.Quantity);
            return Task.FromResult(new ExecutionReconciliationSnapshot(HideFills ? [] : fills.ToArray(), [], [], open,
                new(position, position * market, 0m), orders.Values.Where(x => !HideCancelled || x.Status != "CANCELLED").ToArray()));
        }
    }
}
