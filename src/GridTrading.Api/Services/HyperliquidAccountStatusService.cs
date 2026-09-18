using GridTrading.Api.Data;
using GridTrading.Api.Contracts;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class HyperliquidAccountStatusService(TradingDbContext db, CredentialProtector protector, HyperliquidTradingClient client)
{
    public async Task<object> HealthAsync(string id, CancellationToken ct)
    {
        var account = await db.HyperliquidAccounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new TradingProblemException(404, "EXECUTION_ACCOUNT_NOT_CONFIGURED", "Add this account in Settings → Exchange Accounts.");
        HyperliquidAccountManagementService.VerifyStoredCredentials(account, protector);
        var state = await client.PreflightAsync(id, null, ct, allowDisabled: true);
        return new { accountId = account.Id, account.Name, account.AccountAddress, account.AgentAddress, account.VaultAddress,
            account.Environment, account.Enabled, credentialEncryption = protector.IsConfigured ? "CONFIGURED" : "MISSING",
            state.AgentApproved, state.AgentRole, state.AccountMode, state.TradingEquity, state.AvailableBalance,
            state.PerpAccountValue, state.NetPosition, state.OpenOrderCount, state.AsOf,
            tradingEnabled = account.Enabled,
            tradingReady = account.Enabled && state.AgentApproved && protector.IsConfigured &&
                state.TradingEquity > 0m && state.AvailableBalance > 0m };
    }

    public static HyperliquidAccountResponse Public(HyperliquidAccountEntity account) => new(
        account.Id, account.Name, "HYPERLIQUID", account.Environment,
        account.AccountAddress, account.AgentAddress, account.VaultAddress, account.Enabled,
        !string.IsNullOrWhiteSpace(account.EncryptedAgentPrivateKey), account.CreatedAt, account.UpdatedAt, 0, []);
}
