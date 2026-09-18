using GridTrading.Api.Contracts;
using GridTrading.Api.Data;
using GridTrading.Api.Exchange;
using GridTrading.Api.Execution;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Services;

public sealed class HyperliquidAccountManagementService(
    TradingDbContext db, CredentialProtector protector, HyperliquidTradingClient client,
    ExecutionAccountOperationGate gate)
{
    public async Task<IReadOnlyList<HyperliquidAccountResponse>> ListAsync(string network, CancellationToken ct)
    {
        var accounts = await db.HyperliquidAccounts.AsNoTracking().Where(x => x.Environment == network)
            .OrderBy(x => x.Name).ToListAsync(ct);
        var result = new List<HyperliquidAccountResponse>();
        foreach (var account in accounts) result.Add(await DescribeAsync(account, ct));
        return result;
    }

    public async Task<HyperliquidAccountResponse> GetAsync(string network, string id, CancellationToken ct) =>
        await DescribeAsync(await AccountAsync(network, id, ct), ct);

    public async Task<HyperliquidAccountResponse> CreateAsync(string network, CreateAccountRequest request, CancellationToken ct)
    {
        var name = Name(request.Name);
        var address = Address(request.AccountAddress);
        var (key, agent) = Credentials(request.AgentPrivateKey, address);
        var encrypted = protector.Protect(key);
        await EnsureUniqueAsync(network, address, agent, null, ct);
        var now = DateTimeOffset.UtcNow;
        var account = new HyperliquidAccountEntity
        {
            Id = Ids.New("hl_account"), Name = name, Environment = network,
            AccountAddress = address, AgentAddress = agent, EncryptedAgentPrivateKey = encrypted,
            Enabled = false, CreatedAt = now, UpdatedAt = now
        };
        db.HyperliquidAccounts.Add(account);
        await SaveAsync(account, "ACCOUNT_CREATED", ct);
        return await DescribeAsync(account, ct);
    }

    public Task<HyperliquidAccountResponse> RenameAsync(string network, string id, RenameAccountRequest request, CancellationToken ct) =>
        gate.RunAsync(id, async () =>
        {
            var account = await AccountAsync(network, id, ct);
            account.Name = Name(request.Name);
            await SaveAsync(account, "ACCOUNT_RENAMED", ct);
            return await DescribeAsync(account, ct);
        }, ct);

    public Task<HyperliquidAccountResponse> ReplaceCredentialsAsync(string network, string id,
        ReplaceAccountCredentialsRequest request, CancellationToken ct) => gate.RunAsync(id, async () =>
    {
        var account = await AccountAsync(network, id, ct);
        await EnsureIdleAsync(id, ct);
        var (key, agent) = Credentials(request.AgentPrivateKey, account.AccountAddress);
        await EnsureUniqueAsync(network, account.AccountAddress, agent, id, ct);
        account.EncryptedAgentPrivateKey = protector.Protect(key);
        account.AgentAddress = agent;
        account.Enabled = false;
        // Never reset the persisted nonce, including when reusing a previous agent.
        await SaveAsync(account, "ACCOUNT_CREDENTIALS_REPLACED", ct);
        return await DescribeAsync(account, ct);
    }, ct);

    public Task<HyperliquidAccountResponse> EnableAsync(string network, string id, CancellationToken ct) =>
        gate.RunAsync(id, async () =>
        {
            var account = await AccountAsync(network, id, ct);
            if (account.Enabled) return await DescribeAsync(account, ct);
            VerifyStoredCredentials(account, protector);
            var check = await client.PreflightAsync(id, null, ct, allowDisabled: true);
            if (!check.AgentApproved)
                throw new TradingProblemException(412, "API_WALLET_NOT_APPROVED", "Authorize this API wallet for the account on the selected network, then test again.");
            account.Enabled = true;
            await SaveAsync(account, "ACCOUNT_ENABLED", ct);
            return await DescribeAsync(account, ct);
        }, ct);

    public Task<HyperliquidAccountResponse> DisableAsync(string network, string id, CancellationToken ct) =>
        gate.RunAsync(id, async () =>
        {
            var account = await AccountAsync(network, id, ct);
            await EnsureIdleAsync(id, ct);
            account.Enabled = false;
            await SaveAsync(account, "ACCOUNT_DISABLED", ct);
            return await DescribeAsync(account, ct);
        }, ct);

    public static void VerifyStoredCredentials(HyperliquidAccountEntity account, CredentialProtector protector)
    {
        if (!protector.IsConfigured)
            throw new TradingProblemException(503, "CREDENTIAL_KEY_NOT_CONFIGURED", "Set GRID_TRADING_CREDENTIAL_KEY to a base64-encoded 32-byte key before managing accounts.");
        try
        {
            var (_, agent) = Credentials(protector.Unprotect(account.EncryptedAgentPrivateKey), account.AccountAddress);
            if (agent != account.AgentAddress) throw new InvalidOperationException();
        }
        catch (Exception)
        {
            throw new TradingProblemException(422, "ACCOUNT_CREDENTIALS_UNREADABLE", "Stored credentials cannot be verified. Check the server encryption key or replace the API wallet key.");
        }
    }

    private async Task<HyperliquidAccountEntity> AccountAsync(string network, string id, CancellationToken ct)
    {
        var account = await db.HyperliquidAccounts.SingleOrDefaultAsync(x => x.Id == id && x.Environment == network, ct)
            ?? throw new TradingProblemException(404, "EXECUTION_ACCOUNT_NOT_FOUND", "Account was not found in the selected network.");
        // The context may have observed the account before waiting for the operation gate.
        await db.Entry(account).ReloadAsync(ct);
        return account;
    }

    private async Task<HyperliquidAccountResponse> DescribeAsync(HyperliquidAccountEntity account, CancellationToken ct)
    {
        var blockers = await BlockersAsync(account.Id, ct);
        return HyperliquidAccountStatusService.Public(account) with
        {
            ActiveCycleCount = blockers.Count(x => x.Reason == "ACTIVE_CYCLE"), Blockers = blockers
        };
    }

    private async Task<List<AccountBlocker>> BlockersAsync(string id, CancellationToken ct) =>
        await (from cycle in db.Cycles.AsNoTracking()
               join strategy in db.Strategies.AsNoTracking() on cycle.StrategyId equals strategy.Id into strategies
               from strategy in strategies.DefaultIfEmpty()
               where cycle.ExecutionAccountId == id && (!cycle.IsTerminal || db.Operations.Any(x =>
                   x.ResourceId == cycle.Id && x.Type == "AUTO_RESTART" && (x.Status == "ACCEPTED" || x.Status == "PROCESSING")))
               select new AccountBlocker(cycle.StrategyId, strategy == null ? cycle.StrategyId : strategy.Name,
                   cycle.Id, !cycle.IsTerminal ? "ACTIVE_CYCLE" : "AUTO_RESTART_PENDING")).ToListAsync(ct);

    private async Task EnsureIdleAsync(string id, CancellationToken ct)
    {
        var blockers = await BlockersAsync(id, ct);
        if (blockers.Count > 0)
            throw new TradingProblemException(409, "ACCOUNT_IN_USE",
                "Close active cycles and turn off pending automatic restarts before changing this account. Blocking strategies: " +
                string.Join(", ", blockers.Select(x => x.StrategyName).Distinct()));
    }

    private async Task EnsureUniqueAsync(string network, string address, string agent, string? id, CancellationToken ct)
    {
        if (await db.HyperliquidAccounts.AnyAsync(x => x.Id != id && x.AgentAddress == agent, ct))
            throw new TradingProblemException(409, "AGENT_ALREADY_CONFIGURED", "Use a separate API wallet for each account and network.");
        if (await db.HyperliquidAccounts.AnyAsync(x => x.Id != id && x.Environment == network && x.AccountAddress == address, ct))
            throw new TradingProblemException(409, "ACCOUNT_ALREADY_CONFIGURED", "This trading account is already configured on this network. Manage the existing account instead.");
    }

    private async Task SaveAsync(HyperliquidAccountEntity account, string action, CancellationToken ct)
    {
        account.UpdatedAt = DateTimeOffset.UtcNow;
        db.AuditLogs.Add(new AuditEntity { ResourceId = account.Id, Action = action, Actor = "LOCAL_OPERATOR",
            Detail = "Account configuration updated.", OccurredAt = account.UpdatedAt });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            throw new TradingProblemException(409, "ACCOUNT_ALREADY_CONFIGURED", "The trading account or API wallet is already configured. Refresh the account list.");
        }
    }

    private static string Name(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 100
        ? value.Trim() : throw new TradingProblemException(422, "ACCOUNT_NAME_INVALID", "Enter an account name of 1–100 characters.");

    private static string Address(string? value)
    {
        try { return HyperliquidL1Signer.NormalizeAddress(value?.Trim() ?? ""); }
        catch { throw new TradingProblemException(422, "ACCOUNT_ADDRESS_INVALID", "Enter a valid 0x account address."); }
    }

    private static (string Key, string Agent) Credentials(string? value, string address)
    {
        var key = value?.Trim().ToLowerInvariant() ?? "";
        if (key.StartsWith("0x")) key = key[2..];
        string agent;
        try
        {
            if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new FormatException();
            agent = HyperliquidL1Signer.DeriveAddress(key);
        }
        catch { throw new TradingProblemException(422, "API_WALLET_KEY_INVALID", "Enter a valid 32-byte API wallet private key."); }
        if (agent == address)
            throw new TradingProblemException(422, "MAIN_WALLET_KEY_REJECTED", "Use an approved API wallet key, not the main account private key.");
        return ("0x" + key, agent);
    }
}
