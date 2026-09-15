using GridTrading.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Tests;

public sealed class DatabaseCompatibilityTests
{
    [Fact]
    public async Task LegacyStrategiesAndCyclesAreBackfilledOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(ct);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE "VirtualLots" ("Id" TEXT PRIMARY KEY);
                CREATE TABLE "Executions" ("Id" TEXT PRIMARY KEY);
                CREATE TABLE "Orders" ("Id" TEXT PRIMARY KEY);
                INSERT INTO "VirtualLots" VALUES ('pending-lot');
                CREATE TABLE "Strategies" ("Id" TEXT PRIMARY KEY, "ExchangeAccountId" TEXT NOT NULL);
                CREATE TABLE "Cycles" ("Id" TEXT PRIMARY KEY, "StrategyId" TEXT NOT NULL);
                INSERT INTO "Strategies" VALUES ('paper-strategy', 'acct_paper_01');
                INSERT INTO "Strategies" VALUES ('hl-strategy', 'hl_testnet_default');
                INSERT INTO "Cycles" VALUES ('paper-cycle', 'paper-strategy');
                INSERT INTO "Cycles" VALUES ('hl-cycle', 'hl-strategy');
                """;
            await command.ExecuteNonQueryAsync(ct);
        }
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
        await using var db = new TradingDbContext(options);

        await DatabaseCompatibility.EnsureExecutionSchemaAsync(db);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE type = 'table' AND name IN ('TelegramNotificationSettings', 'TelegramAlertDeliveries');
                """;
            Assert.Equal(2L, await command.ExecuteScalarAsync(ct));
        }


        await using (var offsetCommand = connection.CreateCommand())
        {
            offsetCommand.CommandText = "SELECT EntryGridPriceOffset FROM Cycles WHERE Id = 'paper-cycle'";
            Assert.Equal("0", await offsetCommand.ExecuteScalarAsync(ct));
            offsetCommand.CommandText = "UPDATE Cycles SET EntryGridPriceOffset = '-2.5', EntryGridMovePendingOrderId = 'moving' WHERE Id = 'hl-cycle'";
            await offsetCommand.ExecuteNonQueryAsync(ct);
        }
        Assert.Equal(("GRID", "paper-local"), await StrategyBindingAsync(connection, "paper-strategy", ct));
        Assert.Equal(("GRID", "hyperliquid-testnet"), await StrategyBindingAsync(connection, "hl-strategy", ct));
        Assert.Equal(("paper-local", "acct_paper_01"), await CycleBindingAsync(connection, "paper-cycle", ct));
        Assert.Equal(("hyperliquid-testnet", "hl_testnet_default"), await CycleBindingAsync(connection, "hl-cycle", ct));

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE "Cycles" SET "OperatorPaused" = 1, "RiskPaused" = 1, "RiskRecoveryChecks" = 1 WHERE "Id" = 'hl-cycle';
                UPDATE "VirtualLots" SET "ProtectionPending" = 1 WHERE "Id" = 'pending-lot';
                UPDATE "Cycles" SET "ExecutionEnvironmentId" = 'frozen-env',
                    "ExecutionAccountId" = 'frozen-account' WHERE "Id" = 'hl-cycle';
                """;
            await command.ExecuteNonQueryAsync(ct);
        }
        await DatabaseCompatibility.EnsureExecutionSchemaAsync(db);

        Assert.Equal(("frozen-env", "frozen-account"), await CycleBindingAsync(connection, "hl-cycle", ct));
        await using var pendingCommand = connection.CreateCommand();
        pendingCommand.CommandText = "SELECT ProtectionPending FROM VirtualLots WHERE Id = 'pending-lot'";
        Assert.Equal(1L, await pendingCommand.ExecuteScalarAsync(ct));
        pendingCommand.CommandText = "SELECT OperatorPaused + RiskPaused + RiskRecoveryChecks FROM Cycles WHERE Id = 'hl-cycle'";
        Assert.Equal(3L, await pendingCommand.ExecuteScalarAsync(ct));
        pendingCommand.CommandText = "SELECT EntryGridPriceOffset || ':' || EntryGridMovePendingOrderId FROM Cycles WHERE Id = 'hl-cycle'";
        Assert.Equal("-2.5:moving", await pendingCommand.ExecuteScalarAsync(ct));
    }

    private static async Task<(string Type, string Environment)> StrategyBindingAsync(
        SqliteConnection connection, string id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT StrategyType, DefaultExecutionEnvironmentId FROM Strategies WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<(string Environment, string Account)> CycleBindingAsync(
        SqliteConnection connection, string id, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ExecutionEnvironmentId, ExecutionAccountId FROM Cycles WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetString(0), reader.GetString(1));
    }
}
