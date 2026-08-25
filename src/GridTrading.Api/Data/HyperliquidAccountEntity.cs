namespace GridTrading.Api.Data;

public sealed class HyperliquidAccountEntity
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string AccountAddress { get; set; }
    public required string AgentAddress { get; set; }
    public required string EncryptedAgentPrivateKey { get; set; }
    public string? VaultAddress { get; set; }
    public required string Environment { get; set; } = "TESTNET";
    public bool Enabled { get; set; } = true;
    public long LastNonce { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
