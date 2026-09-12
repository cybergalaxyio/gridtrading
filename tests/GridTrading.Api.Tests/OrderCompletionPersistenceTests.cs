using System.Text.Json;
using System.Text.Json.Nodes;
using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Infrastructure;
using GridTrading.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Tests;

public sealed class OrderCompletionPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LegacyUpgradeBackfillsCompletionTimeOnceAndStoresComparableUtcInteger()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(Ct);
        var completedAt = new DateTimeOffset(2026, 9, 12, 20, 0, 0, TimeSpan.FromHours(8));
        foreach (var id in new[] { "full", "partial", "unknown-time" })
            db.Orders.Add(new OrderEntity
            {
                Id = id, CycleId = "old-cycle", ClientOrderId = id, ExchangeOrderId = id, Symbol = "SOLUSDC",
                Side = "SELL", Kind = "ENTRY", Status = id == "partial" ? "CANCELLED" : "FILLED",
                Quantity = .3m, FilledQuantity = id == "partial" ? .1m : .3m,
                Price = 100m, CreatedAt = completedAt.AddDays(-2), UpdatedAt = completedAt.AddDays(20)
            });
        foreach (var (id, orderId, quantity, occurredAt) in new[]
        {
            ("full-last", "full", .2m, completedAt),
            ("full-first", "full", .1m, completedAt.AddMinutes(-10)),
            ("partial-fill", "partial", .1m, completedAt)
        })
            db.Executions.Add(new ExecutionEntity
            {
                Id = id, ExchangeExecutionId = id, CycleId = "old-cycle", OrderId = orderId,
                Side = "SELL", Quantity = quantity, Price = 100m, OccurredAt = occurredAt
            });
        await db.SaveChangesAsync(Ct);
        db.ChangeTracker.Clear();
        // Recreate the pre-feature schema while preserving its order/fill history.
        await db.Database.ExecuteSqlRawAsync("DROP INDEX IX_Orders_CycleId_Kind_Side_FilledAt", Ct);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Orders DROP COLUMN FilledAt", Ct);
        await DatabaseCompatibility.EnsureExecutionSchemaAsync(db);

        var full = await db.Orders.SingleAsync(x => x.Id == "full", Ct);
        Assert.Equal(completedAt, full.FilledAt);
        Assert.Equal(TimeSpan.Zero, full.FilledAt!.Value.Offset);
        Assert.Null((await db.Orders.SingleAsync(x => x.Id == "partial", Ct)).FilledAt);
        Assert.Null((await db.Orders.SingleAsync(x => x.Id == "unknown-time", Ct)).FilledAt);
        await using var storage = connection.CreateCommand();
        storage.CommandText = "SELECT typeof(FilledAt) FROM Orders WHERE Id = 'full'";
        Assert.Equal("integer", await storage.ExecuteScalarAsync(Ct));
        var boundary = completedAt.ToUniversalTime();
        Assert.Equal(0, await db.Orders.CountAsync(x => x.FilledAt > boundary, Ct));
        Assert.Equal(1, await db.Orders.CountAsync(x => x.FilledAt > boundary.AddMilliseconds(-1), Ct));

        // Re-running startup never resets the timestamp or backfills from UpdatedAt.
        full.FilledAt = completedAt.AddSeconds(-1);
        await db.SaveChangesAsync(Ct);
        await DatabaseCompatibility.EnsureExecutionSchemaAsync(db);
        await db.Entry(full).ReloadAsync(Ct);
        Assert.Equal(completedAt.AddSeconds(-1), full.FilledAt);
    }

    [Fact]
    public void LegacyConfigurationDefaultsToDisabledWithSixtyMinutesAndThreeOrders()
    {
        var json = JsonSerializer.SerializeToNode(StrategyRequest.Default, JsonSupport.Options)!.AsObject();
        json.Remove("entryFillLimitEnabled"); json.Remove("entryFillWindowMinutes"); json.Remove("maxEntryFillsPerSide");
        var request = json.Deserialize<StrategyRequest>(JsonSupport.Options)!;
        Assert.False(request.EntryFillLimitEnabled);
        Assert.Equal(60, request.EntryFillWindowMinutes);
        Assert.Equal(3, request.MaxEntryFillsPerSide);
        var frozen = JsonSerializer.Deserialize<GridConfiguration>("""{"symbol":"SOLUSDC"}""", JsonSupport.Options)!;
        Assert.False(frozen.EntryFillLimitEnabled);
        Assert.Equal(60, frozen.EntryFillWindowMinutes);
        Assert.Equal(3, frozen.MaxEntryFillsPerSide);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(-1, 3)]
    [InlineData(60, 0)]
    [InlineData(60, -1)]
    public void InvalidLimitSettingsAreRejected(int minutes, int count)
    {
        var config = StrategyRequest.Default.ToConfiguration(100m) with
        { EntryFillWindowMinutes = minutes, MaxEntryFillsPerSide = count };
        var error = Assert.Throws<GridValidationException>(() => GridMath.BuildPlan(config,
            new InstrumentRules("SOLUSDT", .001m, .1m, .1m, 5m)));
        Assert.Equal("ENTRY_FILL_LIMIT_INVALID", error.Code);
    }

    [Fact]
    public void FractionalIntegerConfigurationIsRejectedByApiJsonContract()
    {
        var json = JsonSerializer.SerializeToNode(StrategyRequest.Default, JsonSupport.Options)!.AsObject();
        json["entryFillWindowMinutes"] = JsonValue.Create(1.5m);
        Assert.Throws<JsonException>(() => json.Deserialize<StrategyRequest>(JsonSupport.Options));
    }
}
