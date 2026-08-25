using System.Collections.Concurrent;
using GridTrading.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class HyperliquidNonceManager(TradingDbContext db)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    public async Task<long> NextAsync(string accountId, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var account = await db.HyperliquidAccounts.SingleOrDefaultAsync(x => x.Id == accountId && x.Enabled, ct)
                ?? throw new TradingProblemException(404, "TESTNET_ACCOUNT_NOT_FOUND", "Hyperliquid Testnet account was not found or is disabled.");
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            account.LastNonce = Math.Max(now, checked(account.LastNonce + 1));
            account.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct); // Reserve durably before signing/sending.
            return account.LastNonce;
        }
        finally { gate.Release(); }
    }
}
