using Microsoft.EntityFrameworkCore;

namespace GridTrading.Api.Data;

public sealed class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    public DbSet<StrategyEntity> Strategies => Set<StrategyEntity>();
    public DbSet<CycleEntity> Cycles => Set<CycleEntity>();
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<ExecutionEntity> Executions => Set<ExecutionEntity>();
    public DbSet<FundingPaymentEntity> FundingPayments => Set<FundingPaymentEntity>();
    public DbSet<VirtualLotEntity> VirtualLots => Set<VirtualLotEntity>();
    public DbSet<OperationEntity> Operations => Set<OperationEntity>();
    public DbSet<AuditEntity> AuditLogs => Set<AuditEntity>();
    public DbSet<RiskAlertEntity> RiskAlerts => Set<RiskAlertEntity>();
    public DbSet<HyperliquidAccountEntity> HyperliquidAccounts => Set<HyperliquidAccountEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StrategyEntity>().Property(x => x.DefaultExecutionAccountId).HasColumnName("ExchangeAccountId");
        modelBuilder.Entity<StrategyEntity>().HasIndex(x => x.Name);
        modelBuilder.Entity<CycleEntity>().HasIndex(x => new { x.StrategyId, x.IsTerminal });
        modelBuilder.Entity<OrderEntity>().HasIndex(x => x.ClientOrderId).IsUnique();
        modelBuilder.Entity<OrderEntity>().HasIndex(x => new { x.CycleId, x.Status });
        modelBuilder.Entity<ExecutionEntity>().HasIndex(x => x.ExchangeExecutionId).IsUnique();
        modelBuilder.Entity<FundingPaymentEntity>().HasIndex(x => x.ExchangeFundingId).IsUnique();
        modelBuilder.Entity<FundingPaymentEntity>().HasIndex(x => new { x.CycleId, x.OccurredAt });
        modelBuilder.Entity<OperationEntity>().HasIndex(x => x.IdempotencyKey).IsUnique();
        modelBuilder.Entity<RiskAlertEntity>().HasIndex(x => new { x.CycleId, x.CreatedAt });
        modelBuilder.Entity<HyperliquidAccountEntity>().HasIndex(x => x.AgentAddress).IsUnique();

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(x => x.GetProperties()).Where(x => x.ClrType == typeof(decimal)))
            property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<decimal, string>(
                value => value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
                value => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));
    }
}

public sealed class StrategyEntity
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string StrategyType { get; set; } = "GRID";
    public string DefaultExecutionEnvironmentId { get; set; } = "paper-local";
    public string DefaultExecutionAccountId { get; set; } = "acct_paper_01";
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string ExchangeAccountId
    {
        get => DefaultExecutionAccountId;
        set => DefaultExecutionAccountId = value;
    }
    public required string Symbol { get; set; }
    public int Version { get; set; } = 1;
    public required string ConfigurationJson { get; set; }
    public bool Archived { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CycleEntity
{
    public required string Id { get; set; }
    public required string StrategyId { get; set; }
    public string ExecutionEnvironmentId { get; set; } = "paper-local";
    public string ExecutionAccountId { get; set; } = "acct_paper_01";
    public required string State { get; set; }
    public long StateVersion { get; set; }
    public bool IsTerminal { get; set; }
    public bool OperatorResetRequired { get; set; }
    public bool OperatorPaused { get; set; }
    public bool RiskPaused { get; set; }
    public int RiskRecoveryChecks { get; set; }
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool IsOperatorPaused => OperatorPaused || (State == "PAUSED" && !RiskPaused);
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string[] EntryPauseReasons =>
        (IsOperatorPaused ? new[] { "OPERATOR" } : Array.Empty<string>())
        .Concat(RiskPaused ? new[] { "UNPROTECTED_EXPOSURE" } : Array.Empty<string>()).ToArray();
    public decimal FixedCenterPrice { get; set; }
    public decimal ActualNetQuantity { get; set; }
    public decimal ReconstructedNetQuantity { get; set; }
    public decimal RealisedCyclePnl { get; set; }
    public decimal PaidFees { get; set; }
    public decimal AccruedFunding { get; set; }
    public decimal MaximumAdverseExcursion { get; set; }
    public decimal MaximumDrawdown { get; set; }
    public required string FrozenConfigurationJson { get; set; }
    public required string FrozenPlanJson { get; set; }
    public required string ExitReason { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public DateTimeOffset LastReconciledAt { get; set; }
}

public sealed class OrderEntity
{
    public DateTimeOffset? LastExchangeUpdateAt { get; set; }
    public required string Id { get; set; }
    public required string CycleId { get; set; }
    public required string ClientOrderId { get; set; }
    public required string ExchangeOrderId { get; set; }
    public required string Symbol { get; set; }
    public required string Side { get; set; }
    public required string Kind { get; set; }
    public required string Status { get; set; }
    public int GridLevel { get; set; }
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal FilledQuantity { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ExecutionEntity
{
    public string ExchangeOrderId { get; set; } = "";
    public required string Id { get; set; }
    public required string ExchangeExecutionId { get; set; }
    public required string CycleId { get; set; }
    public required string OrderId { get; set; }
    public required string Side { get; set; }
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal Fee { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class FundingPaymentEntity
{
    public required string Id { get; set; }
    public required string ExchangeFundingId { get; set; }
    public required string CycleId { get; set; }
    public required string ExecutionAccountId { get; set; }
    public required string Coin { get; set; }
    public decimal UsdcDelta { get; set; }
    public decimal FundingCost { get; set; }
    public decimal PositionQuantity { get; set; }
    public decimal FundingRate { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class VirtualLotEntity
{
    public required string Id { get; set; }
    public required string CycleId { get; set; }
    public required string EntryOrderId { get; set; }
    public string? TakeProfitOrderId { get; set; }
    public required string Side { get; set; }
    public required string Status { get; set; }
    public int GridLevel { get; set; }
    public decimal EntryFillPrice { get; set; }
    public decimal FilledQuantity { get; set; }
    public decimal RemainingQuantity { get; set; }
    public decimal TakeProfitPrice { get; set; }
    public decimal EntryFee { get; set; }
    public decimal ExitFee { get; set; }
    public decimal FundingAllocation { get; set; }
    // Desired price and remaining quantity live on this lot. This flag survives a
    // committed fill followed by a failed/uncertain protective-order action.
    public bool ProtectionPending { get; set; }
}

public sealed class OperationEntity
{
    public required string Id { get; set; }
    public required string CommandId { get; set; }
    public required string ResourceId { get; set; }
    public required string Type { get; set; }
    public required string Status { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public required string ErrorCode { get; set; }
    public DateTimeOffset AcceptedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class AuditEntity
{
    public long Id { get; set; }
    public required string ResourceId { get; set; }
    public required string Action { get; set; }
    public required string Actor { get; set; }
    public required string Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class RiskAlertEntity
{
    public required string Id { get; set; }
    public string? CycleId { get; set; }
    public required string Severity { get; set; }
    public required string Code { get; set; }
    public required string Message { get; set; }
    public bool Acknowledged { get; set; }
    public string? AcknowledgementNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
