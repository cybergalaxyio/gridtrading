using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Services;
using GridTrading.Api.Strategies.Grid;
using GridTrading.Api.Contracts;
using GridTrading.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GridTrading.Api.Tests;

public sealed partial class TelegramNotificationTests
{
    [Theory]
    [InlineData("ENTRY")]
    [InlineData("TAKE_PROFIT")]
    [InlineData("FLATTEN")]
    public async Task FullStatusNotifiesOnceAndPartialCancellationDoesNot(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenDatabaseAsync(ct);
        await using var db = Database(connection);
        var cycle = CompletionCycle();
        var order = CompletionOrder(kind);
        db.Cycles.Add(cycle);
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        var lifecycle = new GridOrderLifecycle(db, new ExecutionEnvironmentRegistry([]), new ExecutionAccountOperationGate());
        var now = DateTimeOffset.UtcNow;
        await lifecycle.ProcessOrderUpdatesAsync(cycle.ExecutionAccountId,
            [new("123", order.ClientOrderId, "PARTIALLY_FILLED", .1m, now)], ct);
        await lifecycle.ProcessOrderUpdatesAsync(cycle.ExecutionAccountId,
            [new("123", order.ClientOrderId, "CANCELLED", .1m, now.AddMilliseconds(1))], ct);
        Assert.Empty(await db.OrderPlacementNotifications.ToListAsync(ct));

        // A replacement has its own venue order ID; only full confirmation alerts.
        order.Status = "NEW";
        order.ExchangeOrderId = "124";
        await db.SaveChangesAsync(ct);
        var update = new NormalizedOrderUpdate("124", order.ClientOrderId, "FILLED", .2m, now.AddMilliseconds(2));
        await lifecycle.ProcessOrderUpdatesAsync(cycle.ExecutionAccountId, [update, update], ct);
        var notification = await db.OrderPlacementNotifications.SingleAsync(ct);
        Assert.Contains("Order Fully Filled", notification.Message);
        Assert.Contains("Type: " + kind, notification.Message);
        Assert.Contains("Filled Quantity: 0.2", notification.Message);
        Assert.Contains("Exchange order: 124", notification.Message);
        Assert.Equal(update.OccurredAt, notification.CreatedAt);

        await using var restartedDb = Database(connection);
        var restarted = new GridOrderLifecycle(restartedDb, new ExecutionEnvironmentRegistry([]), new ExecutionAccountOperationGate());
        await restarted.ProcessOrderUpdatesAsync(cycle.ExecutionAccountId, [update], ct);
        Assert.Single(await restartedDb.OrderPlacementNotifications.ToListAsync(ct));
    }

    [Fact]
    public async Task CumulativeFullFillHasItsOwnDeliveryAndCurrentAccountSnapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenDatabaseAsync(ct);
        var bot = new FakeBot();
        using var market = new SnapshotHandler();
        await using var provider = Services(connection, bot, market);
        await InScope(provider, async scope =>
        {
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            var settings = scope.ServiceProvider.GetRequiredService<TelegramNotificationSettingsService>();
            await settings.SaveAsync(new TelegramSettingsRequest(Token, "-100123"), ct);
            await settings.TestAndEnableAsync(ct);
            db.HyperliquidAccounts.Add(SnapshotAccount());
            var cycle = CompletionCycle();
            var order = CompletionOrder("FLATTEN");
            db.Cycles.Add(cycle);
            db.Orders.Add(order);
            await OrderPlacementNotifications.RecordAsync(db,
                new ExecutionSelection(cycle.ExecutionEnvironmentId, cycle.ExecutionAccountId), order, ct);
            await db.SaveChangesAsync(ct);
            var lifecycle = new GridOrderLifecycle(db, new ExecutionEnvironmentRegistry([]), new ExecutionAccountOperationGate());
            var first = new NormalizedExecutionFill("fill-1", "123", order.ClientOrderId, "BUY", 150m, .1m, 0m, DateTimeOffset.UtcNow);
            await lifecycle.ProcessFillsAsync(cycle.ExecutionAccountId, [first], ct);
            Assert.Single(await db.OrderPlacementNotifications.ToListAsync(ct));
            var second = first with { ExecutionId = "fill-2", Quantity = .1m, OccurredAt = DateTimeOffset.UtcNow };
            await lifecycle.ProcessFillsAsync(cycle.ExecutionAccountId, [second], ct);
            await lifecycle.ProcessFillsAsync(cycle.ExecutionAccountId, [first, second], ct);
            await lifecycle.ProcessOrderUpdatesAsync(cycle.ExecutionAccountId,
                [new("123", order.ClientOrderId, "FILLED", .2m, second.OccurredAt)], ct);
            Assert.Equal(2, await db.OrderPlacementNotifications.CountAsync(ct));
        });
        var dispatcher = Dispatcher(provider, bot);
        Assert.True(await dispatcher.ProcessNextAsync(ct));
        Assert.True(await dispatcher.ProcessNextAsync(ct));
        Assert.False(await Dispatcher(provider, bot).ProcessNextAsync(ct));
        var filled = Assert.Single(bot.Messages, x => x.Text.Contains("Order Fully Filled"));
        Assert.Contains("Filled Quantity: 0.2", filled.Text);
        AssertSnapshot(filled.Text, "SHORT 0.3 SOL", "-1.25", "4.5x", "80");
        Assert.Single(bot.Messages, x => x.Text.Contains("Order Placed"));
    }

    [Fact]
    public async Task ReconciliationRecoversFullFillOnceAndKeepsOriginalCompletionTime()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenDatabaseAsync(ct);
        await using var db = Database(connection);
        var cycle = CompletionCycle();
        var order = CompletionOrder("ENTRY");
        order.Status = "UNKNOWN";
        db.Cycles.Add(cycle);
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);
        var completedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var snapshot = new ExecutionReconciliationSnapshot([], [], [], new Dictionary<string, string>(),
            new ExecutionPosition(0m, 0m, 0m),
            [new("123", order.ClientOrderId, "FILLED", order.Price, order.Quantity, 0m, completedAt)]);
        var lifecycle = new GridOrderLifecycle(db, new ExecutionEnvironmentRegistry([new CompletionSnapshotAdapter(snapshot)]),
            new ExecutionAccountOperationGate());
        await lifecycle.ReconcileAsync(cycle, ct);
        await lifecycle.ReconcileAsync(cycle, ct);
        var notification = Assert.Single(await db.OrderPlacementNotifications.ToListAsync(ct),
            x => x.Message.Contains("Order Fully Filled"));
        Assert.Equal(completedAt, notification.CreatedAt);
        Assert.Contains("Filled Quantity: 0.2", notification.Message);
    }

    [Fact]
    public async Task RecoveredHistoricalFullFillsAreNotSentAfterEnablingNotifications()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenDatabaseAsync(ct);
        var bot = new FakeBot();
        await using var provider = Services(connection, bot);
        await InScope(provider, async scope =>
        {
            var settings = scope.ServiceProvider.GetRequiredService<TelegramNotificationSettingsService>();
            await settings.SaveAsync(new TelegramSettingsRequest(Token, "-100123"), ct);
            await settings.TestAndEnableAsync(ct);
            var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
            await OrderFillNotifications.RecordConfirmedAsync(db,
                new ExecutionSelection(ExecutionEnvironmentIds.HyperliquidMainnet, "snapshot-account"),
                CompletionOrder("ENTRY"), DateTimeOffset.UtcNow.AddHours(-1), ct);
            await db.SaveChangesAsync(ct);
        });
        Assert.False(await Dispatcher(provider, bot).ProcessNextAsync(ct));
        Assert.Single(bot.Messages); // Activation test only.
    }

    private static CycleEntity CompletionCycle() => new()
    {
        Id = "cycle-notifications", StrategyId = "strategy-notifications", State = "FAULT",
        ExecutionAccountId = "snapshot-account", ExecutionEnvironmentId = ExecutionEnvironmentIds.HyperliquidMainnet,
        FrozenConfigurationJson = "{\"symbol\":\"SOL\",\"tickSize\":0.01,\"quantityStep\":0.1}", FrozenPlanJson = "{}", ExitReason = ""
    };

    private static OrderEntity CompletionOrder(string kind)
    {
        var order = NotificationOrder("complete-order", kind);
        order.Status = "NEW";
        order.ExchangeOrderId = "123";
        return order;
    }

    private sealed class CompletionSnapshotAdapter(ExecutionReconciliationSnapshot snapshot) : IExecutionAdapter
    {
        public ExecutionEnvironmentDescriptor Environment => new(ExecutionEnvironmentIds.HyperliquidMainnet, "HYPERLIQUID", "MAINNET", "Fixture");
        public Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config, CancellationToken ct) => Task.FromResult(snapshot);
        public Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection selection, string symbol, decimal? referencePrice, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection selection, string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection selection, string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task PlaceOrdersAsync(ExecutionSelection selection, GridConfiguration config, IEnumerable<OrderEntity> orders, CancellationToken ct) => throw new NotSupportedException();
        public Task CancelOrdersAsync(ExecutionSelection selection, IEnumerable<OrderEntity> orders, CancellationToken ct) => throw new NotSupportedException();
        public Task<decimal> FlattenAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config, CancellationToken ct) => throw new NotSupportedException();
    }
}
