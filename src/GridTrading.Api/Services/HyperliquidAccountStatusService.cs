using GridTrading.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class HyperliquidAccountStatusService(TradingDbContext db, CredentialProtector protector, HyperliquidTradingClient client)
{
    public async Task<object> HealthAsync(string id, CancellationToken ct)
    {
        var account = await db.HyperliquidAccounts.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new TradingProblemException(404, "TESTNET_ACCOUNT_NOT_CONFIGURED", "Hyperliquid Testnet environment variables are not configured.");
        var state = await client.PreflightAsync(id, null, ct);
        return new { accountId = account.Id, account.Name, account.AccountAddress, account.AgentAddress, account.VaultAddress,
            account.Environment, account.Enabled, credentialEncryption = protector.IsConfigured ? "CONFIGURED" : "MISSING",
            state.AgentApproved, state.AgentRole, state.AccountMode, state.TradingEquity, state.AvailableBalance,
            state.PerpAccountValue, state.NetPosition, state.OpenOrderCount, state.AsOf,
            tradingReady = account.Enabled && state.AgentApproved && protector.IsConfigured &&
                state.TradingEquity > 0m && state.AvailableBalance > 0m };
    }

    public static object Public(HyperliquidAccountEntity account) => new
    {
        accountId = account.Id, account.Name, exchange = "HYPERLIQUID", account.Environment,
        account.AccountAddress, account.AgentAddress, account.VaultAddress, account.Enabled,
        signingKeyStored = !string.IsNullOrWhiteSpace(account.EncryptedAgentPrivateKey), account.CreatedAt, account.UpdatedAt
    };
}
