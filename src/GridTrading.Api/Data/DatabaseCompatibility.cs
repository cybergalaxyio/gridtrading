using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Data;

public static class DatabaseCompatibility
{
    public static async Task EnsureTestnetSchemaAsync(TradingDbContext db)
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
    }
}
