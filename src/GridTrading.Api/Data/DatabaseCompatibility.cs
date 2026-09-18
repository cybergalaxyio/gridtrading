using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

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
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_HyperliquidAccounts_Environment_AccountAddress"
            ON "HyperliquidAccounts" ("Environment", "AccountAddress");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "FundingPayments" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_FundingPayments" PRIMARY KEY,
                "ExchangeFundingId" TEXT NOT NULL,
                "CycleId" TEXT NOT NULL,
                "ExecutionAccountId" TEXT NOT NULL,
                "Coin" TEXT NOT NULL,
                "UsdcDelta" TEXT NOT NULL,
                "FundingCost" TEXT NOT NULL,
                "PositionQuantity" TEXT NOT NULL,
                "FundingRate" TEXT NOT NULL,
                "OccurredAt" TEXT NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_FundingPayments_ExchangeFundingId"
            ON "FundingPayments" ("ExchangeFundingId");
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS "IX_FundingPayments_CycleId_OccurredAt"
            ON "FundingPayments" ("CycleId", "OccurredAt");
            """);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TelegramNotificationSettings" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_TelegramNotificationSettings" PRIMARY KEY,
                "EncryptedBotToken" TEXT NOT NULL,
                "ChatId" TEXT NOT NULL,
                "Enabled" INTEGER NOT NULL,
                "BotUsername" TEXT NULL,
                "VerifiedAt" TEXT NULL,
                "EnabledAt" TEXT NULL,
                "LastTestedAt" TEXT NULL,
                "LastTestError" TEXT NULL,
                "LastDeliveryAt" TEXT NULL,
                "LastDeliveryStatus" TEXT NULL,
                "LastDeliveryError" TEXT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TelegramAlertDeliveries" (
                "RiskAlertId" TEXT NOT NULL CONSTRAINT "PK_TelegramAlertDeliveries" PRIMARY KEY,
                "AttemptedAt" TEXT NOT NULL,
                "DeliveredAt" TEXT NULL,
                "Error" TEXT NULL,
                CONSTRAINT "FK_TelegramAlertDeliveries_RiskAlerts_RiskAlertId"
                    FOREIGN KEY ("RiskAlertId") REFERENCES "RiskAlerts" ("Id") ON DELETE CASCADE
            );
            """);

        await AddColumnIfMissingAsync(db, "Orders", "LastExchangeUpdateAt", "TEXT NULL");
        await AddColumnIfMissingAsync(db, "VirtualLots", "ProtectionPending", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(db, "Executions", "ExchangeOrderId", "TEXT NOT NULL DEFAULT ''");

        // Legacy PAUSED cycles are treated as operator pauses by CycleEntity.
        await AddColumnIfMissingAsync(db, "Cycles", "OperatorPaused", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(db, "Cycles", "RiskPaused", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(db, "Cycles", "RiskRecoveryChecks", "INTEGER NOT NULL DEFAULT 0");
        await AddColumnIfMissingAsync(db, "Cycles", "EntryGridPriceOffset", "TEXT NOT NULL DEFAULT '0'");
        await AddColumnIfMissingAsync(db, "Cycles", "EntryGridMovePendingOrderId", "TEXT NULL");

        await EnsureOrderCompletionSchemaAsync(db);

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
        await EnsureAccountExecutionIdentityAsync(db);
    }

    private static async Task EnsureAccountExecutionIdentityAsync(TradingDbContext db)
    {
        var columns = await ColumnsAsync(db, "Executions");
        if (!new[] { "CycleId", "ExchangeExecutionId" }.All(columns.Contains)) return;
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync() : null;
        await AddColumnIfMissingAsync(db, "Executions", "ExecutionAccountId", "TEXT NOT NULL DEFAULT ''");
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "Executions" SET "ExecutionAccountId" = COALESCE(
                (SELECT "ExecutionAccountId" FROM "Cycles" WHERE "Cycles"."Id" = "Executions"."CycleId"), 'acct_paper_01')
            WHERE "ExecutionAccountId" = '';
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Executions_ExecutionAccountId_ExchangeExecutionId"
            ON "Executions" ("ExecutionAccountId", "ExchangeExecutionId");
            DROP INDEX IF EXISTS "IX_Executions_ExchangeExecutionId";
            """);
        if (transaction is not null) await transaction.CommitAsync();
    }

    private static async Task EnsureOrderCompletionSchemaAsync(TradingDbContext db)
    {
        // Commit the column and backfill together so an interrupted upgrade can retry.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync() : null;
        var added = await AddColumnIfMissingAsync(db, "Orders", "FilledAt", "INTEGER NULL");
        var orderColumns = await ColumnsAsync(db, "Orders");
        if (new[] { "CycleId", "Kind", "Side", "FilledAt" }.All(orderColumns.Contains))
            await db.Database.ExecuteSqlRawAsync("""
                CREATE INDEX IF NOT EXISTS "IX_Orders_CycleId_Kind_Side_FilledAt"
                ON "Orders" ("CycleId", "Kind", "Side", "FilledAt");
                """);
        var executionColumns = await ColumnsAsync(db, "Executions");
        if (!added || !new[] { "Id", "Kind", "Quantity" }.All(orderColumns.Contains) ||
            !new[] { "OrderId", "Quantity", "OccurredAt" }.All(executionColumns.Contains))
        {
            if (transaction is not null) await transaction.CommitAsync();
            return;
        }

        // One-time backfill during schema upgrade. Eligibility checks read Orders only.
        var orders = await db.Orders.AsNoTracking().Where(x => x.Kind == "ENTRY" && x.FilledAt == null)
            .Select(x => new { x.Id, x.Quantity }).ToListAsync();
        var executions = await (from execution in db.Executions.AsNoTracking()
                                join order in db.Orders.AsNoTracking() on execution.OrderId equals order.Id
                                where order.Kind == "ENTRY" && order.FilledAt == null
                                select new { execution.OrderId, execution.Quantity, execution.OccurredAt }).ToListAsync();
        var byOrder = executions.ToLookup(x => x.OrderId);
        foreach (var order in orders)
        {
            var filledAt = OrderCompletion.FindFilledAt(order.Quantity,
                byOrder[order.Id].Select(x => (x.Quantity, x.OccurredAt)));
            if (filledAt.HasValue)
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Orders SET FilledAt = {filledAt.Value.ToUnixTimeMilliseconds()} WHERE Id = {order.Id} AND FilledAt IS NULL");
        }
        if (transaction is not null) await transaction.CommitAsync();
    }

    private static async Task<HashSet<string>> ColumnsAsync(TradingDbContext db, string table)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        return columns;
    }

    private static async Task<bool> AddColumnIfMissingAsync(TradingDbContext db, string table, string column, string definition)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = $"PRAGMA table_info(\"{table}\")";
        var exists = false;
        var tableExists = false;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                tableExists = true;
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }
        if (!tableExists) return false;
        if (exists) return false;
        command.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
        await command.ExecuteNonQueryAsync();
        return true;
    }
}
