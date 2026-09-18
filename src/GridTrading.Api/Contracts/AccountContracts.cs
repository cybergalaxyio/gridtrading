namespace GridTrading.Api.Contracts;

public sealed record CreateAccountRequest(string Name, string AccountAddress, string AgentPrivateKey);
public sealed record RenameAccountRequest(string Name);
public sealed record ReplaceAccountCredentialsRequest(string AgentPrivateKey);
public sealed record AccountBlocker(string StrategyId, string StrategyName, string CycleId, string Reason);
public sealed record HyperliquidAccountResponse(
    string AccountId, string Name, string Exchange, string Environment, string AccountAddress,
    string AgentAddress, string? VaultAddress, bool Enabled, bool SigningKeyStored,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int ActiveCycleCount,
    IReadOnlyList<AccountBlocker> Blockers);
