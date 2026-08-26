using GridTrading.Api.Data;
using GridTrading.Domain;

namespace GridTrading.Api.Execution;

public static class ExecutionEnvironmentIds
{
    public const string PaperLocal = "paper-local";
    public const string HyperliquidTestnet = "hyperliquid-testnet";
    public const string PaperAccount = "acct_paper_01";

    public static string ForAccount(string accountId) =>
        accountId == PaperAccount ? PaperLocal : HyperliquidTestnet;
}

public sealed record ExecutionEnvironmentDescriptor(string Id, string VenueType, string Network, string DisplayName);
public sealed record ExecutionAccountDescriptor(string Id, string EnvironmentId, string DisplayName, bool Enabled);
public sealed record ExecutionSelection(string EnvironmentId, string AccountId);
public sealed record ExecutionQuote(decimal Bid, decimal Ask, decimal Mid, DateTimeOffset AsOf);
public sealed record ExecutionInstrument(
    string Symbol, string EnvironmentId, int AssetIndex, int SizeDecimals, decimal ReferencePrice,
    decimal TickSize, decimal QuantityStep, decimal MinOrderQuantity, decimal MinOrderNotional,
    int MaxActiveOrders, decimal MakerFeeRate, decimal TakerFeeRate, string FeeSource, DateTimeOffset AsOf)
{
    public InstrumentRules Rules => new(Symbol, TickSize, QuantityStep, MinOrderQuantity, MinOrderNotional, MaxActiveOrders);
}
public sealed record NormalizedExecutionFill(
    string ExecutionId, string ExchangeOrderId, string? ClientOrderId, string Side,
    decimal Price, decimal Quantity, decimal Fee, DateTimeOffset OccurredAt);
public sealed record NormalizedOrderUpdate(
    string ExchangeOrderId, string? ClientOrderId, string Status, decimal FilledQuantity, DateTimeOffset OccurredAt);
public sealed record ExecutionPosition(decimal Quantity, decimal PositionValue, decimal UnrealizedPnl);
public sealed record ExecutionReconciliationSnapshot(
    IReadOnlyList<NormalizedExecutionFill> Fills,
    IReadOnlyList<NormalizedOrderUpdate> OrderUpdates,
    IReadOnlyDictionary<string, string> OpenOrdersByClientId,
    ExecutionPosition Position);

public interface IExecutionAdapter
{
    ExecutionEnvironmentDescriptor Environment { get; }
    Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct);
    Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection selection, string symbol, decimal? referencePrice, CancellationToken ct);
    Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection selection, string symbol, CancellationToken ct);
    Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection selection, string symbol, CancellationToken ct);
    Task PlaceOrdersAsync(ExecutionSelection selection, GridConfiguration config, IEnumerable<OrderEntity> orders, CancellationToken ct);
    Task CancelOrdersAsync(ExecutionSelection selection, IEnumerable<OrderEntity> orders, CancellationToken ct);
    Task<decimal> FlattenAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config, CancellationToken ct);
    Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config, CancellationToken ct);
}

public sealed class ExecutionEnvironmentRegistry(IEnumerable<IExecutionAdapter> adapters)
{
    private readonly IReadOnlyDictionary<string, IExecutionAdapter> _adapters =
        adapters.ToDictionary(x => x.Environment.Id, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ExecutionEnvironmentDescriptor> Environments =>
        _adapters.Values.Select(x => x.Environment).OrderBy(x => x.DisplayName).ToArray();

    public IExecutionAdapter Adapter(string environmentId) =>
        _adapters.TryGetValue(environmentId, out var adapter)
            ? adapter
            : throw new TradingProblemException(404, "EXECUTION_ENVIRONMENT_NOT_FOUND",
                $"Execution environment '{environmentId}' was not found.");

    public async Task<ExecutionSelection> ResolveAsync(string? environmentId, string? accountId, CancellationToken ct)
    {
        var resolvedEnvironment = string.IsNullOrWhiteSpace(environmentId)
            ? ExecutionEnvironmentIds.ForAccount(accountId ?? ExecutionEnvironmentIds.PaperAccount)
            : environmentId;
        var adapter = Adapter(resolvedEnvironment);
        var accounts = await adapter.GetAccountsAsync(ct);
        var resolvedAccount = string.IsNullOrWhiteSpace(accountId) && accounts.Count == 1 ? accounts[0].Id : accountId;
        var account = accounts.SingleOrDefault(x => x.Id == resolvedAccount && x.Enabled);
        if (account is null)
            throw new TradingProblemException(404, "EXECUTION_ACCOUNT_NOT_FOUND",
                $"Account '{resolvedAccount}' is not enabled in execution environment '{resolvedEnvironment}'.");
        return new ExecutionSelection(adapter.Environment.Id, account.Id);
    }
}
