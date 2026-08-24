namespace GridTrading.Domain;

public static class GridMath
{
    public static GridPlan BuildPlan(GridConfiguration config, InstrumentRules rules)
    {
        Validate(config, rules);
        var initialGap = (config.InitialGapPoints > 0m
            ? config.InitialGapPoints
            : config.GridSpacingPoints / 2m) * rules.TickSize;

        var buyPrice = RoundDown(config.CenterPrice - initialGap, rules.TickSize);
        var sellPrice = RoundUp(config.CenterPrice + initialGap, rules.TickSize);
        var levels = new List<GridLevel>(config.MaxLevelsPerSide * 2);
        decimal buyQty = 0m, sellQty = 0m, buyNotional = 0m, sellNotional = 0m;

        for (var i = 0; i < config.MaxLevelsPerSide; i++)
        {
            if (i > 0)
            {
                var spacing = (config.GridSpacingPoints + i * config.GridSpacingStepPoints) * rules.TickSize;
                buyPrice = RoundDown(buyPrice - spacing, rules.TickSize);
                sellPrice = RoundUp(sellPrice + spacing, rules.TickSize);
            }

            var quantity = PlannedQuantity(config, rules, i);
            if (quantity < rules.MinOrderQuantity)
                throw new GridValidationException("MIN_ORDER_QUANTITY", $"Level {i} quantity is below the exchange minimum.");
            if (quantity * buyPrice < rules.MinOrderNotional || quantity * sellPrice < rules.MinOrderNotional)
                throw new GridValidationException("MIN_ORDER_NOTIONAL", $"Level {i} notional is below the exchange minimum.");

            buyQty += quantity;
            sellQty += quantity;
            var buyLevelNotional = quantity * buyPrice;
            var sellLevelNotional = quantity * sellPrice;
            buyNotional += buyLevelNotional;
            sellNotional += sellLevelNotional;

            levels.Add(new GridLevel(OrderSide.Buy, i, buyPrice, config.TakeProfitPoints * rules.TickSize,
                quantity, buyLevelNotional, buyQty, buyNotional));
            levels.Add(new GridLevel(OrderSide.Sell, i, sellPrice, config.TakeProfitPoints * rules.TickSize,
                quantity, sellLevelNotional, sellQty, sellNotional));
        }

        return new GridPlan(
            config.CenterPrice,
            buyPrice,
            sellPrice,
            (config.CenterPrice - buyPrice) / config.CenterPrice * 100m,
            (sellPrice - config.CenterPrice) / config.CenterPrice * 100m,
            levels.OrderBy(x => x.LevelIndex).ThenBy(x => x.Side).ToArray());
    }

    public static decimal PlannedQuantity(GridConfiguration config, InstrumentRules rules, int levelIndex)
    {
        var growthFactor = 1m + config.LotSizeIncreasePercent / 100m;
        var theoretical = config.BaseLotSize * Pow(growthFactor, levelIndex);
        var capped = config.MaxTradeLot > 0m ? Math.Min(theoretical, config.MaxTradeLot) : theoretical;
        return RoundDown(capped, rules.QuantityStep);
    }

    public static decimal TakeProfitPrice(OrderSide entrySide, decimal entryFillPrice, decimal takeProfitPoints, decimal tickSize)
    {
        var distance = takeProfitPoints * tickSize;
        return entrySide == OrderSide.Buy
            ? RoundUp(entryFillPrice + distance, tickSize)
            : RoundDown(entryFillPrice - distance, tickSize);
    }

    public static decimal AllowedOrderQuantity(
        OrderSide side,
        decimal requestedQuantity,
        decimal currentNetPosition,
        IEnumerable<ActiveOrderReservation> activeOrders,
        decimal maxNetLot,
        InstrumentRules rules)
    {
        var orders = activeOrders.ToArray();
        var activeBuy = orders.Where(x => x.Side == OrderSide.Buy).Sum(x => x.RemainingQuantity);
        var activeSell = orders.Where(x => x.Side == OrderSide.Sell).Sum(x => x.RemainingQuantity);
        var capacity = side == OrderSide.Buy
            ? maxNetLot - currentNetPosition - activeBuy
            : maxNetLot + currentNetPosition - activeSell;
        var allowed = RoundDown(Math.Max(0m, Math.Min(requestedQuantity, capacity)), rules.QuantityStep);
        return allowed >= rules.MinOrderQuantity ? allowed : 0m;
    }

    public static BasketPnl CalculateBasketPnl(BasketPnlInput input)
    {
        var liquidation = input.RealisedCyclePnl + input.UnrealisedAtExecutablePrice
            - input.PaidFees - input.AccruedFunding - input.EstimatedFinalTakerFee - input.EstimatedExitSlippage;
        return new BasketPnl(input.RealisedCyclePnl, input.UnrealisedAtExecutablePrice, input.PaidFees,
            input.AccruedFunding, input.EstimatedFinalTakerFee, input.EstimatedExitSlippage, liquidation);
    }

    public static decimal RoundDown(decimal value, decimal step) => Math.Floor(value / step) * step;
    public static decimal RoundUp(decimal value, decimal step) => Math.Ceiling(value / step) * step;

    private static decimal Pow(decimal value, int exponent)
    {
        var result = 1m;
        for (var i = 0; i < exponent; i++) result *= value;
        return result;
    }

    private static void Validate(GridConfiguration config, InstrumentRules rules)
    {
        if (config.CenterPrice <= 0m) throw new GridValidationException("CENTER_PRICE", "Center price must be positive.");
        if (config.MaxLevelsPerSide is < 1 or > 200) throw new GridValidationException("MAX_LEVELS", "Max levels must be between 1 and 200.");
        if (config.WorkingEntriesPerSide < 1 || config.WorkingEntriesPerSide > config.MaxLevelsPerSide)
            throw new GridValidationException("WORKING_ENTRIES", "Working entries must be within the planned level count.");
        if (config.GridSpacingPoints <= 0m || config.TakeProfitPoints <= 0m)
            throw new GridValidationException("DISTANCE", "Grid spacing and take profit points must be positive.");
        if (config.BaseLotSize <= 0m || config.MaxNetLot <= 0m)
            throw new GridValidationException("QUANTITY", "Base lot and max net lot must be positive.");
        if (config.LotSizeIncreasePercent < 0m) throw new GridValidationException("LOT_GROWTH", "Lot growth cannot be negative.");
        if (rules.TickSize <= 0m || rules.QuantityStep <= 0m) throw new GridValidationException("INSTRUMENT_RULES", "Invalid exchange rules.");
    }
}

public sealed class GridValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
