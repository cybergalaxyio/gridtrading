using System.Text.Json;
using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Exchanges.Paper;
using GridTrading.Api.Infrastructure;
using GridTrading.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using GridTrading.Api.Services;
using GridTrading.Api.Strategies.Grid;
using GridTrading.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Tests;

public sealed class OrderApprovalTests
{
    [Fact]
    public async Task EachEntryAndTakeProfitWaitsForItsOwnApprovalAndSurvivesRestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(ct);
        var db = fixture.Db;
        var entry = fixture.Order("entry", "ENTRY");
        var tp = fixture.Order("tp", "TAKE_PROFIT");
        db.Orders.AddRange(entry, tp);
        await db.SaveChangesAsync(ct);
        await fixture.Adapter.PlaceOrdersAsync(fixture.Selection, fixture.Config, [entry, tp], ct);
        await fixture.Adapter.PlaceOrdersAsync(fixture.Selection, fixture.Config, [entry, tp], ct);
        Assert.All(new[] { entry, tp }, x => Assert.Equal("PENDING_EXCHANGE", x.Status));
        Assert.Equal(2, await db.OrderApprovals.CountAsync(ct));
        Assert.Empty(await db.OrderPlacementNotifications.ToListAsync(ct));
        db.ChangeTracker.Clear();
        var approval = await db.OrderApprovals.SingleAsync(x => x.OrderId == "entry", ct);
        await fixture.Approvals.ApproveAsync(approval.Id, ct);
        await fixture.Approvals.ApproveAsync(approval.Id, ct);
        entry = await db.Orders.SingleAsync(x => x.Id == "entry", ct);
        tp = await db.Orders.SingleAsync(x => x.Id == "tp", ct);
        await fixture.Adapter.PlaceOrdersAsync(fixture.Selection, fixture.Config, [entry, tp], ct);
        Assert.Equal("NEW", entry.Status);
        Assert.Equal("PENDING_EXCHANGE", tp.Status);
        Assert.Single(await db.OrderPlacementNotifications.ToListAsync(ct));
        Assert.Single(await fixture.Approvals.ListAsync(ct));
    }

    [Fact]
    public async Task ChangedPriceOrQuantityInvalidatesApprovalAndDisableDoesNotReleaseHeldOrders()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(ct);
        var order = fixture.Order("entry", "ENTRY");
        fixture.Db.Add(order);
        await fixture.Db.SaveChangesAsync(ct);
        await fixture.Adapter.PlaceOrdersAsync(fixture.Selection, fixture.Config, [order], ct);
        var original = await fixture.Db.OrderApprovals.SingleAsync(ct);
        await fixture.Approvals.ApproveAsync(original.Id, ct);
        order.Price += 1;
        order.Quantity += 1;
        await fixture.Db.SaveChangesAsync(ct);
        await fixture.Adapter.PlaceOrdersAsync(fixture.Selection, fixture.Config, [order], ct);
        Assert.Equal("PENDING_EXCHANGE", order.Status);
        Assert.Equal("STALE", original.Status);
        var fresh = await fixture.Db.OrderApprovals.SingleAsync(x => x.Status == "PENDING", ct);
        Assert.Equal(order.Price, fresh.Price);
        Assert.Equal(order.Quantity, fresh.Quantity);
        await new TradingControlSettingsService(fixture.Db).SaveAsync(new(false), ct);
        await fixture.Adapter.PlaceOrdersAsync(fixture.Selection, fixture.Config, [order], ct);
        Assert.Equal("PENDING_EXCHANGE", order.Status);
        await fixture.Approvals.ApproveAsync(fresh.Id, ct);
        await fixture.Adapter.PlaceOrdersAsync(fixture.Selection, fixture.Config, [order], ct);
        Assert.Equal("NEW", order.Status);
    }

    [Fact]
    public async Task CancelledOrdersCannotBeApprovedOrSent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(ct);
        var order = fixture.Order("entry", "ENTRY");
        fixture.Db.Add(order);
        await fixture.Db.SaveChangesAsync(ct);
        await fixture.Adapter.PlaceOrdersAsync(fixture.Selection, fixture.Config, [order], ct);
        var approval = await fixture.Db.OrderApprovals.SingleAsync(ct);
        await fixture.Adapter.CancelOrdersAsync(fixture.Selection, [order], ct);
        Assert.Empty(await fixture.Approvals.ListAsync(ct));
        var error = await Assert.ThrowsAsync<TradingProblemException>(() => fixture.Approvals.ApproveAsync(approval.Id, ct));
        Assert.Equal("ORDER_APPROVAL_STALE", error.Code);
        await fixture.Adapter.PlaceOrdersAsync(fixture.Selection, fixture.Config, [order], ct);
        Assert.Empty(await fixture.Db.OrderPlacementNotifications.ToListAsync(ct));
    }

    [Fact]
    public async Task AmendmentKeepsOriginalOrderUntilExactReplacementIsApproved()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(ct);
        var order = fixture.Order("tp", "TAKE_PROFIT");
        order.Status = "NEW";
        order.ExchangeOrderId = "paper-original";
        fixture.Db.Add(order);
        await fixture.Db.SaveChangesAsync(ct);
        await fixture.Adapter.AmendOrderAsync(fixture.Selection, fixture.Config, order, 151m, 2m, ct);
        Assert.Equal(150m, order.Price);
        Assert.Equal(1m, order.Quantity);
        var approval = await fixture.Db.OrderApprovals.SingleAsync(ct);
        Assert.Equal("AMEND", approval.Action);
        await fixture.Approvals.ApproveAsync(approval.Id, ct);
        await fixture.Adapter.AmendOrderAsync(fixture.Selection, fixture.Config, order, 151m, 2m, ct);
        Assert.Equal(151m, order.Price);
        Assert.Equal(2m, order.Quantity);
        await fixture.Adapter.AmendOrderAsync(fixture.Selection, fixture.Config, order, 152m, 3m, ct);
        Assert.Equal(151m, order.Price);
        Assert.Single(await fixture.Db.OrderApprovals.Where(x => x.Status == "PENDING").ToListAsync(ct));
    }

    [Fact]
    public async Task ClosingWaitsWithoutFaultAndCompletesAfterApproval()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(ct);
        var db = fixture.Db;
        var order = fixture.Order("entry", "ENTRY");
        order.Status = "FILLED"; order.ExchangeOrderId = "paper-entry"; order.FilledQuantity = order.Quantity;
        db.Add(order);
        db.Executions.Add(new ExecutionEntity { Id = "fill", ExchangeExecutionId = "fill", CycleId = fixture.Cycle.Id,
            ExecutionAccountId = fixture.Selection.AccountId, OrderId = order.Id, ExchangeOrderId = order.ExchangeOrderId,
            Side = "BUY", Price = order.Price, Quantity = order.Quantity, OccurredAt = DateTimeOffset.UtcNow });
        db.VirtualLots.Add(new VirtualLotEntity { Id = "lot", CycleId = fixture.Cycle.Id, EntryOrderId = order.Id,
            Side = "BUY", Status = "TP_PENDING", EntryFillPrice = order.Price, FilledQuantity = 1m,
            RemainingQuantity = 1m, TakeProfitPrice = 151m });
        fixture.Cycle.ActualNetQuantity = fixture.Cycle.ReconstructedNetQuantity = 1m;
        await db.SaveChangesAsync(ct);
        var lifecycle = new GridOrderLifecycle(db, new([fixture.Adapter]), fixture.Gate);
        await using var services = new ServiceCollection().AddLogging().AddSignalR().Services.BuildServiceProvider();
        var workflow = new GridStrategyWorkflow(db, fixture.Market, new PreviewStore(), new([fixture.Adapter]), lifecycle,
            services.GetRequiredService<IHubContext<TradingHub>>(), fixture.Gate);
        var operation = await workflow.CommandAsync(fixture.Cycle.Id, "CLOSE", "Operator close", "close-key", null, false, ct);
        Assert.Equal("ACCEPTED", operation.Status);
        var duplicate = await workflow.CommandAsync(fixture.Cycle.Id, "CLOSE", "Operator close", "duplicate-key", null, false, ct);
        Assert.Equal(operation.Id, duplicate.Id);
        Assert.Equal("CLOSING", fixture.Cycle.State);
        Assert.False(fixture.Cycle.IsTerminal);
        Assert.Empty(await db.RiskAlerts.ToListAsync(ct));
        var approval = await db.OrderApprovals.SingleAsync(ct);
        Assert.Equal("FLATTEN", approval.Kind);
        Assert.Equal("SELL", approval.Side);
        Assert.Equal(1m, approval.Quantity);
        Assert.Equal("Ioc", approval.TimeInForce);
        await fixture.Approvals.ApproveAsync(approval.Id, ct);
        // Resume using the persisted operation, as the reconciliation worker does after restart.
        var completed = await workflow.CommandAsync(fixture.Cycle.Id, "CLOSE", fixture.Cycle.ExitReason, operation.IdempotencyKey, null, false, ct);
        Assert.Equal("COMPLETED", completed.Status);
        Assert.True(fixture.Cycle.IsTerminal);
        Assert.Equal(0m, fixture.Cycle.ReconstructedNetQuantity);
        Assert.Single(await db.Orders.Where(x => x.Kind == "FLATTEN").ToListAsync(ct));
    }

    [Fact]
    public async Task StrategyGeneratedTakeProfitWaitsAfterEntryFillAndSendsOnlyWhenApproved()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(ct);
        var order = fixture.Order("entry", "ENTRY");
        fixture.Db.Add(order);
        await fixture.Db.SaveChangesAsync(ct);
        await fixture.Adapter.PlaceOrdersAsync(fixture.Selection, fixture.Config, [order], ct);
        await fixture.Approvals.ApproveAsync((await fixture.Db.OrderApprovals.SingleAsync(ct)).Id, ct);
        await fixture.Adapter.PlaceOrdersAsync(fixture.Selection, fixture.Config, [order], ct);
        var lifecycle = new GridOrderLifecycle(fixture.Db, new([fixture.Adapter]), fixture.Gate);
        lifecycle.RegisterNewCycle(fixture.Cycle);
        await lifecycle.ProcessFillsAsync(fixture.Selection.AccountId,
            [new("entry-fill", order.ExchangeOrderId, order.ClientOrderId, "BUY", 150m, 1m, 0m, DateTimeOffset.UtcNow)], ct);
        var tp = await fixture.Db.Orders.SingleAsync(x => x.Kind == "TAKE_PROFIT", ct);
        Assert.Equal("PENDING_EXCHANGE", tp.Status);
        var approval = await fixture.Db.OrderApprovals.SingleAsync(x => x.OrderId == tp.Id && x.Status == "PENDING", ct);
        Assert.Equal("SELL", approval.Side);
        Assert.Equal(1m, approval.Quantity);
        await fixture.Approvals.ApproveAsync(approval.Id, ct);
        await lifecycle.ReconcileAsync(fixture.Cycle, ct);
        Assert.Equal("NEW", tp.Status);
        Assert.Equal("SUBMITTED", approval.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectBlocksRetriesAndDisablingConfirmationUntilExplicitlyConfirmed(bool amendment)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(ct);
        var order = fixture.Order("rejected", amendment ? "TAKE_PROFIT" : "ENTRY");
        if (amendment) { order.Status = "NEW"; order.ExchangeOrderId = "paper-existing"; }
        fixture.Db.Add(order);
        await fixture.Db.SaveChangesAsync(ct);
        Task Send() => amendment
            ? fixture.Adapter.AmendOrderAsync(fixture.Selection, fixture.Config, order, 151m, 2m, ct)
            : fixture.Adapter.PlaceOrdersAsync(fixture.Selection, fixture.Config, [order], ct);
        await Send();
        var approval = await fixture.Db.OrderApprovals.SingleAsync(ct);
        await fixture.Approvals.RejectAsync(approval.Id, ct);
        await fixture.Approvals.RejectAsync(approval.Id, ct);
        await new TradingControlSettingsService(fixture.Db).SaveAsync(new(false), ct);
        await Send();
        await Send();
        Assert.Equal("REJECTED", approval.Status);
        Assert.Equal(150m, order.Price);
        Assert.Equal(amendment ? "NEW" : "PENDING_EXCHANGE", order.Status);
        Assert.Single(await fixture.Db.OrderApprovals.ToListAsync(ct));
        Assert.Empty(await fixture.Db.OrderPlacementNotifications.ToListAsync(ct));
        Assert.Single(await fixture.Approvals.ListAsync(ct)); // Visible as rejected in Open Orders.
        await fixture.Approvals.ApproveAsync(approval.Id, ct);
        await Send();
        Assert.Equal("SUBMITTED", approval.Status);
        Assert.Equal("NEW", order.Status);
        if (amendment) Assert.Equal(151m, order.Price);
        var error = await Assert.ThrowsAsync<TradingProblemException>(() => fixture.Approvals.RejectAsync(approval.Id, ct));
        Assert.Equal("ORDER_APPROVAL_STALE", error.Code);
    }

    [Fact]
    public async Task RejectCanRevokeApprovalBeforeSendAndChangedOrderRequiresFreshReview()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await Fixture.Create(ct);
        var order = fixture.Order("entry", "ENTRY");
        fixture.Db.Add(order);
        await fixture.Db.SaveChangesAsync(ct);
        await fixture.Adapter.PlaceOrdersAsync(fixture.Selection, fixture.Config, [order], ct);
        var approval = await fixture.Db.OrderApprovals.SingleAsync(ct);
        await fixture.Approvals.ApproveAsync(approval.Id, ct);
        await fixture.Approvals.RejectAsync(approval.Id, ct);
        await fixture.Adapter.PlaceOrdersAsync(fixture.Selection, fixture.Config, [order], ct);
        Assert.Equal("PENDING_EXCHANGE", order.Status);
        order.Price = 149m;
        await fixture.Db.SaveChangesAsync(ct);
        await fixture.Adapter.PlaceOrdersAsync(fixture.Selection, fixture.Config, [order], ct);
        Assert.Equal("STALE", approval.Status);
        var pending = await fixture.Db.OrderApprovals.SingleAsync(x => x.Status == "PENDING", ct);
        Assert.Equal(149m, pending.Price);
        Assert.Equal("PENDING_EXCHANGE", order.Status);
    }

    private sealed class Fixture(SqliteConnection connection, TradingDbContext db) : IAsyncDisposable
    {
        public TradingDbContext Db => db;
        public ExecutionAccountOperationGate Gate { get; } = new();
        public ExecutionSelection Selection { get; } = new(ExecutionEnvironmentIds.PaperLocal, PaperExecutionAdapter.AccountId);
        public GridConfiguration Config { get; } = new() { Symbol = "SOLUSDT", CenterPrice = 150m, TickSize = .001m,
            QuantityStep = .1m, MinOrderQuantity = .1m, MinOrderNotional = 1m, MaxLevelsPerSide = 2,
            GridSpacingPoints = 100m, TakeProfitPoints = 100m, BaseLotSize = 1m, MaxNetLot = 10m };
        public MarketState Market { get; } = new();
        public PaperExecutionAdapter Adapter => new(Market, db);
        public OrderApprovalService Approvals => new(db, Gate);
        public CycleEntity Cycle { get; private set; } = null!;
        public OrderEntity Order(string id, string kind) => new() { Id = id, ClientOrderId = id, CycleId = Cycle.Id,
            ExchangeOrderId = "pending", Symbol = "SOLUSDT", Side = kind == "ENTRY" ? "BUY" : "SELL",
            Kind = kind, Status = "PENDING_EXCHANGE", Price = 150m, Quantity = 1m, CreatedAt = DateTimeOffset.UtcNow };
        public static async Task<Fixture> Create(CancellationToken ct)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(ct);
            var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync(ct);
            var fixture = new Fixture(connection, db);
            fixture.Cycle = new CycleEntity { Id = "cycle", StrategyId = "strategy", State = "RUNNING", LedgerStatus = "READY",
                FrozenConfigurationJson = JsonSerializer.Serialize(fixture.Config, JsonSupport.Options),
                FrozenPlanJson = JsonSerializer.Serialize(GridMath.BuildPlan(fixture.Config, TradingService.RulesFor(fixture.Config)), JsonSupport.Options),
                ExitReason = "", StartedAt = DateTimeOffset.UtcNow };
            db.AddRange(fixture.Cycle, new StrategyEntity { Id = "strategy", Name = "Grid", Symbol = "SOLUSDT", ConfigurationJson = "{}" });
            await db.SaveChangesAsync(ct);
            await new TradingControlSettingsService(db).SaveAsync(new(true), ct);
            return fixture;
        }
        public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
