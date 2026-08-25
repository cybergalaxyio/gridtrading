using GridTrading.Api.Data;
using GridTrading.Api.Exchange;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class HyperliquidAccountBootstrap(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<HyperliquidAccountBootstrap> logger) : IHostedService
{
    public const string DefaultAccountId = "hl_testnet_default";

    public async Task StartAsync(CancellationToken ct)
    {
        var accountAddress = Secret("GRID_TRADING_HL_TESTNET_ACCOUNT_ADDRESS");
        var privateKey = Secret("GRID_TRADING_HL_TESTNET_AGENT_PRIVATE_KEY");
        if (string.IsNullOrWhiteSpace(accountAddress) && string.IsNullOrWhiteSpace(privateKey)) return;
        if (string.IsNullOrWhiteSpace(accountAddress) || string.IsNullOrWhiteSpace(privateKey))
            throw new InvalidOperationException("Both GRID_TRADING_HL_TESTNET_ACCOUNT_ADDRESS and GRID_TRADING_HL_TESTNET_AGENT_PRIVATE_KEY are required.");
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<CredentialProtector>();
        if (!protector.IsConfigured) throw new InvalidOperationException("GRID_TRADING_CREDENTIAL_KEY must be a base64-encoded 32-byte key when Testnet trading is configured.");
        var normalizedAccount = HyperliquidL1Signer.NormalizeAddress(accountAddress);
        var normalizedKey = privateKey.Trim().ToLowerInvariant(); if (!normalizedKey.StartsWith("0x")) normalizedKey = "0x" + normalizedKey;
        var agentAddress = HyperliquidL1Signer.DeriveAddress(normalizedKey);
        var vaultValue = Secret("GRID_TRADING_HL_TESTNET_VAULT_ADDRESS");
        var vault = string.IsNullOrWhiteSpace(vaultValue) ? null : HyperliquidL1Signer.NormalizeAddress(vaultValue);
        var entity = await db.HyperliquidAccounts.FindAsync([DefaultAccountId], ct);
        var now = DateTimeOffset.UtcNow;
        if (entity is null)
        {
            entity = new HyperliquidAccountEntity { Id = DefaultAccountId, Name = "Hyperliquid Testnet", AccountAddress = normalizedAccount,
                AgentAddress = agentAddress, EncryptedAgentPrivateKey = protector.Protect(normalizedKey), VaultAddress = vault,
                Environment = "TESTNET", Enabled = true, CreatedAt = now, UpdatedAt = now };
            db.HyperliquidAccounts.Add(entity);
        }
        else
        {
            entity.AccountAddress = normalizedAccount; entity.AgentAddress = agentAddress;
            entity.EncryptedAgentPrivateKey = protector.Protect(normalizedKey); entity.VaultAddress = vault;
            entity.Environment = "TESTNET"; entity.Enabled = true; entity.UpdatedAt = now;
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Hyperliquid Testnet account configured for account {AccountAddress} with agent {AgentAddress}.", normalizedAccount, agentAddress);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    private string? Secret(string name) => configuration[name] ?? Environment.GetEnvironmentVariable(name);
}
