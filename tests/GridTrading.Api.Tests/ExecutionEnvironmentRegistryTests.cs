using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Api.Services;
using GridTrading.Domain;

namespace GridTrading.Api.Tests;

public sealed class ExecutionEnvironmentRegistryTests
{
    [Fact]
    public async Task SingleAccountIsAutoSelectedWithinEnvironment()
    {
        var registry = new ExecutionEnvironmentRegistry([
            new StubAdapter(new("paper-local", "PAPER", "LOCAL", "Paper"),
                [new("paper-1", "paper-local", "Paper Account", true)]),
            new StubAdapter(new("hyperliquid-testnet", "HYPERLIQUID", "TESTNET", "Testnet"),
                [new("hl-1", "hyperliquid-testnet", "HL Account", true)])
        ]);

        var selection = await registry.ResolveAsync("hyperliquid-testnet", null, TestContext.Current.CancellationToken);

        Assert.Equal("hyperliquid-testnet", selection.EnvironmentId);
        Assert.Equal("hl-1", selection.AccountId);
        Assert.Equal(2, registry.Environments.Count);
    }

    [Fact]
    public async Task AccountFromAnotherEnvironmentIsRejected()
    {
        var registry = new ExecutionEnvironmentRegistry([
            new StubAdapter(new("paper-local", "PAPER", "LOCAL", "Paper"),
                [new("paper-1", "paper-local", "Paper Account", true)]),
            new StubAdapter(new("hyperliquid-testnet", "HYPERLIQUID", "TESTNET", "Testnet"),
                [new("hl-1", "hyperliquid-testnet", "HL Account", true)])
        ]);

        var error = await Assert.ThrowsAsync<TradingProblemException>(() =>
            registry.ResolveAsync("paper-local", "hl-1", TestContext.Current.CancellationToken));

        Assert.Equal("EXECUTION_ACCOUNT_NOT_FOUND", error.Code);
    }

    private sealed class StubAdapter(
        ExecutionEnvironmentDescriptor environment,
        IReadOnlyList<ExecutionAccountDescriptor> accounts) : IExecutionAdapter
    {
        public ExecutionEnvironmentDescriptor Environment => environment;
        public Task<IReadOnlyList<ExecutionAccountDescriptor>> GetAccountsAsync(CancellationToken ct) => Task.FromResult(accounts);
        public Task<ExecutionInstrument> GetInstrumentAsync(ExecutionSelection selection, string symbol, decimal? referencePrice, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionQuote> GetQuoteAsync(ExecutionSelection selection, string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionQuote> PreflightStartAsync(ExecutionSelection selection, string symbol, CancellationToken ct) => throw new NotSupportedException();
        public Task PlaceOrdersAsync(ExecutionSelection selection, GridConfiguration config, IEnumerable<OrderEntity> orders, CancellationToken ct) => throw new NotSupportedException();
        public Task CancelOrdersAsync(ExecutionSelection selection, IEnumerable<OrderEntity> orders, CancellationToken ct) => throw new NotSupportedException();
        public Task<decimal> FlattenAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config, CancellationToken ct) => throw new NotSupportedException();
        public Task<ExecutionReconciliationSnapshot> ReconcileAsync(ExecutionSelection selection, CycleEntity cycle, GridConfiguration config, CancellationToken ct) => throw new NotSupportedException();
    }
}
