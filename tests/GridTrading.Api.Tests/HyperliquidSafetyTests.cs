using GridTrading.Api.Data;
using GridTrading.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace GridTrading.Api.Tests;

public sealed class HyperliquidSafetyTests
{
    [Fact]
    public void CredentialEnvelopeRoundTripsWithoutPlaintext()
    {
        var key = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["GRID_TRADING_CREDENTIAL_KEY"] = key }).Build();
        var protector = new CredentialProtector(configuration);
        const string privateKey = "0x0123456789012345678901234567890123456789012345678901234567890123";

        var envelope = protector.Protect(privateKey);

        Assert.DoesNotContain(privateKey, envelope, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(privateKey, protector.Unprotect(envelope));
    }

    [Fact]
    public async Task NonceIsStrictlyIncreasingAndPersisted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        var ct = TestContext.Current.CancellationToken;
        await connection.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseSqlite(connection).Options;
        await using var db = new TradingDbContext(options);
        await db.Database.EnsureCreatedAsync(ct);
        db.HyperliquidAccounts.Add(new HyperliquidAccountEntity
        {
            Id = "test", Name = "test", AccountAddress = "0x0000000000000000000000000000000000000001",
            AgentAddress = "0x0000000000000000000000000000000000000002", EncryptedAgentPrivateKey = "test",
            Environment = "TESTNET", Enabled = true, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
        var manager = new HyperliquidNonceManager(db);

        var first = await manager.NextAsync("test", ct);
        var second = await manager.NextAsync("test", ct);

        Assert.True(second > first);
        Assert.Equal(second, (await db.HyperliquidAccounts.SingleAsync(ct)).LastNonce);
    }
}
