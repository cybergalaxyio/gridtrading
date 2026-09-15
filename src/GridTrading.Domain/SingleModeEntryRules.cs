namespace GridTrading.Domain;

public static class SingleModeEntryRules
{
    public static void Validate(GridConfiguration config)
    {
        if (config.GridMode == GridMode.TwoWay) return;
        if ((config.SingleModeMoveDistancePoints ?? config.GridSpacingPoints) <= 0m ||
            config.SingleModeMoveIntervalSeconds <= 0)
            throw new GridValidationException("SINGLE_MODE_MOVE_INVALID",
                "Single-mode move distance must be positive and the waiting time must be a positive integer in seconds.");
    }

    public static decimal? TargetPrice(GridConfiguration config, decimal currentOrderPrice,
        DateTimeOffset createdAt, decimal bid, decimal ask, DateTimeOffset now)
    {
        if (config.GridMode == GridMode.TwoWay || config.TickSize <= 0m || bid <= 0m || ask < bid) return null;
        Validate(config);
        if (now - createdAt < TimeSpan.FromSeconds(config.SingleModeMoveIntervalSeconds)) return null;
        var target = InitialEntryPrice(config, bid, ask);
        if (target is null) return null;
        var improvement = config.GridMode == GridMode.SellOnly ? currentOrderPrice - target.Value : target.Value - currentOrderPrice;
        var threshold = (config.SingleModeMoveDistancePoints ?? config.GridSpacingPoints) * config.TickSize;
        return improvement >= Math.Max(config.TickSize, threshold) ? target : null;
    }

    // A new flat round starts at level zero around the current book. Unlike
    // trailing an existing order, this has no directional or waiting threshold.
    public static decimal? InitialEntryPrice(GridConfiguration config, decimal bid, decimal ask)
    {
        if (config.GridMode == GridMode.TwoWay || config.TickSize <= 0m || bid <= 0m || ask < bid) return null;
        var gap = (config.InitialGapPoints > 0m ? config.InitialGapPoints : config.GridSpacingPoints)
            / 2m * config.TickSize;
        var price = config.GridMode == GridMode.SellOnly
            ? GridMath.RoundUp(ask + gap, config.TickSize)
            : GridMath.RoundDown(bid - gap, config.TickSize);
        return price > 0m ? price : null;
    }

    public static GridPlan ShiftPlan(GridPlan plan, decimal offset)
    {
        if (offset == 0m) return plan;
        var center = plan.CenterPrice + offset;
        var buy = plan.OutermostBuyPrice + offset;
        var sell = plan.OutermostSellPrice + offset;
        var levels = plan.Levels.Select(x => x with
        {
            EntryPrice = x.EntryPrice + offset,
            OrderNotional = (x.EntryPrice + offset) * x.PlannedQuantity,
            CumulativeNotional = x.CumulativeNotional + offset * x.CumulativeQuantity
        }).ToArray();
        return plan with
        {
            CenterPrice = center, OutermostBuyPrice = buy, OutermostSellPrice = sell,
            CoverageBelowPct = center > 0m ? (center - buy) / center * 100m : 0m,
            CoverageAbovePct = center > 0m ? (sell - center) / center * 100m : 0m,
            Levels = levels
        };
    }
}
