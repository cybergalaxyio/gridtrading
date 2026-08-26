using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Data;

public static class DatabaseCompatibility
{
    public static async Task EnsureExecutionSchemaAsync(TradingDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "HyperliquidAccounts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_HyperliquidAccounts" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "AccountAddress" TEXT NOT NULL,
                "AgentAddress" TEXT NOT NULL,
                "EncryptedAgentPrivateKey" TEXT NOT NULL,
                "VaultAddress" TEXT NULL,
                "Environment" TEXT NOT NULL,
                "Enabled" INTEGER NOT NULL,
                "LastNonce" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_HyperliquidAccounts_AgentAddress"
            ON "HyperliquidAccounts" ("AgentAddress");
            """);

        var strategyTypeAdded = await AddColumnIfMissingAsync(db, "Strategies", "StrategyType", "TEXT NOT NULL DEFAULT 'GRID'");
        var strategyEnvironmentAdded = await AddColumnIfMissingAsync(db, "Strategies", "DefaultExecutionEnvironmentId", "TEXT NOT NULL DEFAULT 'paper-local'");
        var cycleEnvironmentAdded = await AddColumnIfMissingAsync(db, "Cycles", "ExecutionEnvironmentId", "TEXT NOT NULL DEFAULT 'paper-local'");
        var cycleAccountAdded = await AddColumnIfMissingAsync(db, "Cycles", "ExecutionAccountId", "TEXT NOT NULL DEFAULT 'acct_paper_01'");
        if (strategyTypeAdded || strategyEnvironmentAdded) await db.Database.ExecuteSqlRawAsync("""
            UPDATE "Strategies"
            SET "StrategyType" = 'GRID',
                "DefaultExecutionEnvironmentId" = CASE
                    WHEN "ExchangeAccountId" = 'acct_paper_01' THEN 'paper-local'
                    ELSE 'hyperliquid-testnet'
                END
            WHERE "StrategyType" = '' OR "DefaultExecutionEnvironmentId" = '' OR
                  ("DefaultExecutionEnvironmentId" = 'paper-local' AND "ExchangeAccountId" <> 'acct_paper_01');
            """);
        if (cycleEnvironmentAdded || cycleAccountAdded) await db.Database.ExecuteSqlRawAsync("""
            UPDATE "Cycles"
            SET "ExecutionAccountId" = COALESCE((
                    SELECT "ExchangeAccountId" FROM "Strategies" WHERE "Strategies"."Id" = "Cycles"."StrategyId"
                ), "ExecutionAccountId"),
                "ExecutionEnvironmentId" = COALESCE((
                    SELECT "DefaultExecutionEnvironmentId" FROM "Strategies" WHERE "Strategies"."Id" = "Cycles"."StrategyId"
                ), "ExecutionEnvironmentId");
            """);
    }

    private static async Task<bool> AddColumnIfMissingAsync(TradingDbContext db, string table, string column, string definition)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        var exists = false;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
        }
        if (exists) return false;
        command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
        await command.ExecuteNonQueryAsync();
        return true;
    }
}
