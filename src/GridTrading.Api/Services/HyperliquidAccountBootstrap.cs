using GridTrading.Api.Data;
using GridTrading.Api.Exchange;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class HyperliquidAccountBootstrap(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<HyperliquidAccountBootstrap> logger) : IHostedService
{
    public const string DefaultAccountId = "hl_testnet_default";

    public async Task StartAsync(CancellationToken ct)
    {
        await ConfigureAsync(HyperliquidNetwork.Testnet, DefaultAccountId, ct);
        await ConfigureAsync(HyperliquidNetwork.Mainnet, "hl_mainnet_default", ct);
    }

    private async Task ConfigureAsync(string network, string accountId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        // Database settings are authoritative after the first legacy import.
        if (await db.HyperliquidAccounts.AnyAsync(x => x.Id == accountId, ct)) return;
        var prefix = $"GRID_TRADING_HL_{network}";
        var accountAddress = Secret($"{prefix}_ACCOUNT_ADDRESS");
        var privateKey = Secret($"{prefix}_AGENT_PRIVATE_KEY");
        if (string.IsNullOrWhiteSpace(accountAddress) && string.IsNullOrWhiteSpace(privateKey)) return;
        if (string.IsNullOrWhiteSpace(accountAddress) || string.IsNullOrWhiteSpace(privateKey))
            throw new InvalidOperationException($"Both {prefix}_ACCOUNT_ADDRESS and {prefix}_AGENT_PRIVATE_KEY are required.");
        var protector = scope.ServiceProvider.GetRequiredService<CredentialProtector>();
        if (!protector.IsConfigured) throw new InvalidOperationException("GRID_TRADING_CREDENTIAL_KEY must be a base64-encoded 32-byte key when Hyperliquid trading is configured.");
        var normalizedAccount = HyperliquidL1Signer.NormalizeAddress(accountAddress);
        if (await db.HyperliquidAccounts.AnyAsync(x => x.Environment == network && x.AccountAddress == normalizedAccount, ct)) return;
        var normalizedKey = privateKey.Trim().ToLowerInvariant(); if (!normalizedKey.StartsWith("0x")) normalizedKey = "0x" + normalizedKey;
        var agentAddress = HyperliquidL1Signer.DeriveAddress(normalizedKey);
        var vaultValue = Secret($"{prefix}_VAULT_ADDRESS");
        var vault = string.IsNullOrWhiteSpace(vaultValue) ? null : HyperliquidL1Signer.NormalizeAddress(vaultValue);
        if (network == HyperliquidNetwork.Mainnet && vault is not null)
            throw new InvalidOperationException("Mainnet uses a dedicated account; vault routing is not supported.");
        if (agentAddress == normalizedAccount)
            throw new InvalidOperationException("Use an approved API wallet, not the main account private key.");
        if (await db.HyperliquidAccounts.AnyAsync(x => x.Id != accountId && x.AgentAddress == agentAddress, ct))
            throw new InvalidOperationException("Use a separate API wallet for each configured account and network.");
        var now = DateTimeOffset.UtcNow;
        var entity = new HyperliquidAccountEntity { Id = accountId, Name = $"Hyperliquid {network}", AccountAddress = normalizedAccount,
            AgentAddress = agentAddress, EncryptedAgentPrivateKey = protector.Protect(normalizedKey), VaultAddress = vault,
            Environment = network, Enabled = true, CreatedAt = now, UpdatedAt = now };
        db.HyperliquidAccounts.Add(entity);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Hyperliquid {Network} account configured for account {AccountAddress} with agent {AgentAddress}.", network, normalizedAccount, agentAddress);
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    private string? Secret(string name) => configuration[name] ?? Environment.GetEnvironmentVariable(name);
}
