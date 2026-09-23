using GridTrading.Api.Data;
using GridTrading.Api.Execution;
using GridTrading.Domain;

namespace GridTrading.Api.Strategies.Grid;

public static class GridCycleAccounting
{
    public static decimal NetQuantity(IEnumerable<ExecutionEntity> executions) =>
        executions.Sum(x => x.Side == "BUY" ? x.Quantity : -x.Quantity);

    public static BasketPnl Value(CycleEntity cycle, IReadOnlyCollection<ExecutionEntity> executions,
        GridConfiguration config, ExecutionQuote quote)
    {
        var quantity = NetQuantity(executions);
        var cashflow = executions.Sum(x => (x.Side == "SELL" ? 1m : -1m) * x.Price * x.Quantity);
        var exitPrice = quantity >= 0m ? quote.Bid : quote.Ask;
        var notional = Math.Abs(quantity) * exitPrice;
        var gross = cashflow + quantity * exitPrice;
        var realised = cycle.IsTerminal && quantity == 0m ? cashflow : cycle.RealisedCyclePnl;
        return GridMath.CalculateBasketPnl(new(realised, gross - realised, executions.Sum(x => x.Fee),
            0m, notional * config.TakerFeeRate, notional * config.EstimatedExitSlippagePct / 100m));
    }
}
