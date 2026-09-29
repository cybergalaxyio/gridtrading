using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Services;
using GridTrading.Api.Strategies.Grid;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Tests;

public sealed class TradingControlSettingsTests
{
    [Fact]
    public async Task SettingDefaultsOffAndPersistsAcrossContexts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
        await using (var db = new TradingDbContext(options))
        {
            await db.Database.EnsureCreatedAsync(ct);
            var service = new TradingControlSettingsService(db);
            Assert.False((await service.GetAsync(ct)).RequireManualOrderConfirmation);
            Assert.Equal(0m, (await service.GetAsync(ct)).MinimumConfirmationNotional);
            await service.SaveAsync(new(true, 123.456789m), ct);
        }
        await using (var db = new TradingDbContext(options))
        {
            var service = new TradingControlSettingsService(db);
            Assert.True((await service.GetAsync(ct)).RequireManualOrderConfirmation);
            Assert.Equal(123.456789m, (await service.GetAsync(ct)).MinimumConfirmationNotional);
            await service.SaveAsync(new(false), ct);
            Assert.Single(await db.TradingControlSettings.ToListAsync(ct));
        }
    }

    [Fact]
    public async Task ExistingDatabaseUpgradeCreatesSettingsAndPreservesSavedValue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(ct);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE TradingControlSettings; DROP TABLE OrderApprovals", ct);
        await DatabaseCompatibility.EnsureExecutionSchemaAsync(db);
        var service = new TradingControlSettingsService(db);
        Assert.False((await service.GetAsync(ct)).RequireManualOrderConfirmation);
        await service.SaveAsync(new(true), ct);
        await DatabaseCompatibility.EnsureExecutionSchemaAsync(db);
        Assert.True((await service.GetAsync(ct)).RequireManualOrderConfirmation);
        Assert.Empty(await db.OrderApprovals.ToListAsync(ct));
    }

    [Fact]
    public async Task UpgradeAddsZeroThresholdToExistingEnabledSettingAndRejectsNegativeAmount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using var db = new TradingDbContext(new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(ct);
        await db.Database.ExecuteSqlRawAsync("""
            DROP TABLE TradingControlSettings;
            CREATE TABLE TradingControlSettings (Id TEXT PRIMARY KEY, RequireManualOrderConfirmation INTEGER NOT NULL);
            INSERT INTO TradingControlSettings VALUES ('trading-control', 1);
            """, ct);
        await DatabaseCompatibility.EnsureExecutionSchemaAsync(db);
        var service = new TradingControlSettingsService(db);
        Assert.Equal(new TradingControlSettings(true, 0m), await service.GetAsync(ct));
        await service.SaveAsync(new(true, 100.25m), ct);
        var error = await Assert.ThrowsAsync<TradingProblemException>(() => service.SaveAsync(new(false, -1m), ct));
        Assert.Equal(422, error.Status);
        await DatabaseCompatibility.EnsureExecutionSchemaAsync(db);
        Assert.Equal(new TradingControlSettings(true, 100.25m), await service.GetAsync(ct));
    }

}
