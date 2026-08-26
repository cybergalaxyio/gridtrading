# Crypto Weekend Adaptive Grid — Handover Notes

**Status:** Research / backtest only  
**Prepared:** 2026-08-24  
**Primary timezone:** Asia/Singapore  
**Likely venue/instrument:** Bybit BTCUSDT USDT perpetual, neutral mode (to be confirmed)

## 1. Objective

Research and implement a safer, improved grid strategy designed specifically for low-activity crypto weekends.

Core hypothesis:

> BTC, ETH and SOL trade continuously, but weekend volume and directional movement have declined as crypto price discovery has become increasingly aligned with traditional US financial-market hours. Weekends may therefore be a useful **range-regime filter** for grid/mean-reversion strategies.

Important limitation:

> Lower weekend volatility and fewer sustained trends do **not** automatically imply negative return autocorrelation or a profitable blind reversal strategy.

The desired system is therefore not a normal static grid and not “buy every fall / short every rise.” It should be a **regime-filtered, semi-dynamic, inventory-aware neutral grid with a breakout kill switch**.

## 2. Empirical findings already established

### 2.1 Dataset and definitions

- Binance spot daily candles: BTCUSDT, ETHUSDT and SOLUSDT.
- Main sample: 2024-01-01 through 2026-08-22, 965 daily observations per coin.
- Weekend: Saturday and Sunday.
- Metrics:
  - Quote-volume share.
  - Absolute open-to-close log return.
  - Intraday log high/low range.
- Primary calculation used UTC daily candles; the conclusion remained similar after aligning days to Asia/Singapore.

### 2.2 Weekend versus weekday, 2024-01-01 to 2026-08-22

If trading activity were evenly distributed across the week, weekends would account for 28.57% of total volume.

| Metric | BTC | ETH | SOL |
|---|---:|---:|---:|
| Weekend share of volume | 16.74% | 18.93% | 21.72% |
| Weekend avg daily volume / weekday | 50.4% | 58.6% | 69.6% |
| Weekend mean absolute daily return | 1.15% | 1.82% | 2.46% |
| Weekday mean absolute daily return | 2.01% | 2.76% | 3.33% |
| Weekend mean intraday range | 2.45% | 3.94% | 5.28% |
| Weekday mean intraday range | 4.20% | 5.82% | 7.04% |
| Weekend days with absolute return >= 3% | 6.18% | 17.82% | 27.27% |
| Weekday days with absolute return >= 3% | 22.32% | 32.90% | 45.94% |

One-sided Mann–Whitney tests found lower weekend volume, absolute daily movement and range for all three assets, with all nine comparisons having p-values below approximately 1.2e-8.

Asia/Singapore-aligned weekend daily-volume ratios were approximately:

- BTC: 55.9% of weekday volume.
- ETH: 61.9%.
- SOL: 70.4%.

Conclusion: the weekend effect is not an artifact of UTC day boundaries. It is strongest for BTC and weakest for SOL.

### 2.3 Historical weekend-volume trend

Weekend share of Binance USDT spot volume:

| Year | BTC | ETH | SOL |
|---|---:|---:|---:|
| 2019 | 24.10% | 24.56% | — |
| 2020 | 22.38% | 24.44% | — |
| 2021 | 23.57% | 23.38% | 21.72% |
| 2022 | 19.34% | 20.18% | 21.15% |
| 2023 | 19.64% | 20.34% | 23.87% |
| 2024 | 15.72% | 17.98% | 20.50% |
| 2025 | 17.22% | 19.30% | 22.90% |
| 2026 YTD to Aug 22 | 18.32% | 20.04% | 21.97% |

Independent Kaiko cross-exchange research found BTC weekend share falling from about 28% in 2019 to 16% in 2024, broadly matching the Binance calculation.

### 2.4 Preliminary blind-reversal test

This was a deliberately simple diagnostic, **not a production-quality backtest**:

- Sample: 2025-01-01 to 2026-08-22.
- 4-hour Binance spot candles.
- Asia/Singapore weekends.
- Signal: current absolute 4-hour return exceeds 1 rolling standard deviation, using a 180-candle lookback.
- Trade: take the opposite direction for the next 4-hour candle.
- No fees, spread, funding or execution modelling.

| Asset | Signals | Direction reversed next candle | Mean gross trade | t-statistic |
|---|---:|---:|---:|---:|
| BTC | 95 | 57.9% | +1.9 bps | 0.20 |
| ETH | 113 | 50.4% | -22.6 bps | -1.47 |
| SOL | 136 | 52.2% | -10.4 bps | -0.64 |

Conclusion: a naive one-candle fade is not supported. BTC shows a mildly higher reversal hit rate, but the edge is statistically insignificant and too small to survive normal costs. Weekend should be treated as a **regime prior**, not a standalone entry signal.

## 3. Market-structure interpretation

Working causal interpretation:

- US spot BTC ETFs trade and process their core activity during US equity-market hours.
- Institutional market makers, hedging flows, macro data and risk positioning are concentrated on weekdays.
- Dollar settlement and banking infrastructure remain less active on weekends.
- The weekend-volume decline began before the 2024 ETF approvals; ETFs appear to have accelerated an existing trend rather than created it.
- CME crypto derivatives moved to near-24/7 trading in June 2026, so “CME is closed” is no longer a complete explanation. US ETFs, banking rails, staffing and macro releases still follow weekday schedules.

Strategy implication: weekend conditions have lower average activity but occasionally thinner liquidity and violent event-driven tails. The strategy must prioritize survival during the rare true breakout.

## 4. Proposed strategy: Weekend Adaptive Neutral Grid v1

### 4.1 Initial scope

Start with one instrument only:

- **BTCUSDT USDT perpetual**.
- Isolated margin.
- 1x effective leverage for the first live/paper implementation; hard maximum 2x.
- Neutral grid: bids below centre and shorts/offers above centre.
- Post-only orders for normal grid execution.
- No martingale sizing.

Do not start with SOL. Based on the measured weekend effect, preferred research order is BTC, then ETH, then SOL.

### 4.2 Trading window

Candidate session in Asia/Singapore time:

- Start: Saturday around 05:00, approximately after Friday US cash-market close.
- Mandatory shutdown and inventory reduction: Monday 06:30, before the Monday Asia open begins restoring directional liquidity.

Exact session boundaries must be treated as parameters and tested, including daylight-saving changes in the US.

### 4.3 Regime gate

The bot may create or maintain a grid only if the market appears range-bound. Candidate filters:

1. `ADX(14)` on 1-hour bars below 18–20.
2. Recent 6-hour realised volatility no more than 1.1–1.2 times its rolling weekend baseline.
3. Price within approximately one short-term ATR or volatility unit of the target centre at activation.
4. No abnormal volume expansion; candidate threshold is 1-hour volume below 1.3–1.5 times the rolling median.
5. If available, absolute 4-hour open-interest change below approximately 3% at activation.
6. Funding is not unusually one-sided relative to its recent distribution.
7. No known scheduled or breaking catalyst.

These thresholds are research ranges, not final parameters.

### 4.4 Semi-dynamic centre

Do not implement a continuously trailing grid. Use a slowly adjusting fair-value centre.

Candidate target centre:

```text
C_target = 0.70 * session_VWAP + 0.30 * EMA_24h
```

Candidate inventory-aware effective centre:

```text
C_effective = C_target - k * (net_inventory / max_inventory) * grid_spacing
```

where `k` is initially tested between 0.5 and 1.0.

Interpretation:

- Excess long inventory lowers the effective centre: sell orders move closer and new buy orders move farther away.
- Excess short inventory raises the effective centre: buy-to-cover orders move closer and new shorts move farther away.

Recentring is allowed only when all are true:

1. `abs(C_target - current_centre)` exceeds 1.5–2 grid intervals.
2. Price has remained around the proposed new centre for at least 2 hours.
3. ADX remains below the regime threshold.
4. Absolute net inventory is below 20% of the maximum allowed net inventory.
5. At least 2–4 hours have passed since the previous recenter.

**Never recenter the entire grid while inventory is heavy.** Doing so can turn the grid into a trend-chasing system while retaining a large losing position from the old centre.

For the simplest v1, the centre may remain fixed at launch and be recentered at most once during a weekend.

### 4.5 Grid spacing and range

Use percentage/geometric spacing.

Candidate formula:

```text
round_trip_maker_cost = 2 * maker_fee_rate
spacing_pct = max(a * ATR_1h / price, b * round_trip_maker_cost)
```

Initial research ranges:

- `a`: 0.25–0.50.
- `b`: 2.5–4.0.
- BTC spacing: approximately 0.15%–0.40%; likely first test around 0.25%.
- BTC half-width: approximately 1.5%–3.0%.
- Levels per side: 6–12; likely first test at 7.

Current Bybit base fees cited during research:

- Perpetual/futures VIP 0: maker 0.020%, taker 0.055%.
- Spot VIP 0: maker/taker 0.100%.

The account’s actual fee page must be queried before final parameter selection. Dense spot grids are unlikely to retain enough edge after a roughly 0.20% maker round trip; perpetual maker costs are more compatible with a dense grid but introduce funding and liquidation risks.

### 4.6 Order sizing and inventory skew

- Constant or volatility-adjusted size per level; never double size after losses.
- Worst-case total notional if every grid level fills should not exceed approximately 0.8x allocated capital in the initial configuration.
- Maximum net directional exposure should be approximately 30%–40% of allocated capital, subject to backtest results.
- When net inventory exceeds 25% of its cap:
  - Reduce same-direction new order size by roughly 50%.
  - Increase exit/rebalancing order size by roughly 25%, without exceeding the cap.
  - Apply the inventory-based centre skew.
- Use reduce-only orders for explicit inventory exits where supported.

### 4.7 Breakout detector and kill switch

The principal risk is a rare real breakout in thin weekend liquidity.

Cancel the grid and reduce/flatten opposing inventory if a 1-hour close exits the outer band and at least one confirmation is present:

- Volume exceeds 1.5 times its rolling median.
- ADX rises above approximately 25.
- 4-hour open interest expands by more than approximately 5% in the breakout direction.
- A known macro, regulatory, exchange, security or geopolitical catalyst appears.

Additional hard controls:

- Catastrophic price stop approximately 0.5–0.7 ATR beyond the outer grid boundary.
- Maximum net inventory stop.
- Maximum weekend drawdown stop.
- Data-staleness, websocket-disconnect and order-reconciliation stop.
- After a breakout stop, do not automatically restart for at least 6 hours.
- Mandatory time exit before Monday Asia liquidity returns.

## 5. Backtest requirements

### 5.1 Data

Preferred:

- Bybit BTCUSDT perpetual trades or 1-minute bars.
- Best bid/ask snapshots if obtainable.
- Volume, funding, open interest and mark/index price.
- At least 2021 through current date, with special focus on the post-ETF 2024+ regime.

Use Binance data only as an independent robustness check, not as the sole execution dataset for a Bybit strategy.

### 5.2 Execution simulation

Avoid assuming every touched limit order fills.

At minimum:

- Maker fill requires price to trade through the limit, or use a conservative queue/fill model.
- Include actual maker/taker fees by historical/current fee tier.
- Include funding payments.
- Include spread and adverse-selection assumptions.
- Model latency and cancel/replace delay.
- Stops should use taker pricing and slippage.
- Prevent impossible same-bar fill ordering when using OHLC bars.

### 5.3 Variants to compare

1. No trading.
2. Static neutral grid.
3. Static grid plus time window only.
4. Static grid plus regime filter.
5. Semi-dynamic centre plus inventory skew.
6. Full proposed system with breakout kill switch.
7. Simple mean-reversion entry without a grid.
8. Optional trend-following fallback after a confirmed breakout, evaluated separately.

### 5.4 Validation design

- Use walk-forward or yearly rolling optimisation.
- Keep 2026 as a strict out-of-sample period if possible.
- Test multiple weekend definitions and daylight-saving-aware US market boundaries.
- Report BTC, ETH and SOL separately; do not pool them.
- Stress test fee increases, missed fills, doubled slippage and delayed stops.
- Identify dependence on a small number of exceptional weekends.

### 5.5 Required metrics

- Net P&L after all costs.
- P&L per weekend and per fill.
- Hit rate and average win/loss.
- Profit factor.
- Weekend-level Sharpe/Sortino, with limitations noted.
- Maximum drawdown and maximum single-weekend loss.
- Turnover, maker/taker mix and fill count.
- Average and maximum inventory held.
- Time to flatten inventory.
- Tail loss during the worst 1%, 5% and event weekends.
- Percentage of total P&L contributed by the five best weekends.

Do not accept the strategy merely because gross grid P&L is positive. The acceptance decision should be based on stable out-of-sample net P&L and controlled tail inventory risk.

## 6. Implementation requirements

The user is comfortable with C#/.NET. Either C# or Python may be used for research, but production should favour a language/runtime with reliable websocket handling and testability.

Core modules:

1. Market-data collector.
2. Indicator/regime engine.
3. Grid planner.
4. Inventory and risk manager.
5. Order manager with idempotent client-order IDs.
6. Exchange reconciliation loop.
7. Backtest execution simulator.
8. Paper-trading adapter.
9. Structured event and decision logging.
10. Monitoring/alerting for disconnects, inventory, stop events and manual kill.

Safety requirements:

- Start in offline backtest mode.
- Then paper/testnet mode.
- Do not connect real API credentials or place live orders without explicit approval.
- API keys should have trading-only permission and no withdrawal permission.
- Maintain a manual global cancel/flatten command.

## 7. Open decisions

Confirm before final implementation:

1. Exchange: Bybit, Binance or another venue?
2. Instrument: spot or USDT perpetual?
3. Account fee/VIP tier?
4. Allocated research and eventual live capital?
5. Maximum acceptable single-weekend drawdown?
6. Whether simultaneous long and short positions are allowed/desired?
7. C#/.NET or Python for the research codebase?
8. Custom API bot or exchange-provided grid bot?

Current working recommendation:

> Custom Bybit BTCUSDT USDT-perpetual bot, isolated margin, 1x effective leverage, post-only normal execution, 0.25% initial spacing, seven levels per side, semi-dynamic centre, no martingale, strict breakout stop, and weekend-only operation.

## 8. Suggested first task for Codex

```text
Read this handover in full. Do not implement live trading yet.

First:
1. Critique the weekend adaptive-grid hypothesis and identify hidden assumptions.
2. Design a reproducible research/backtest plan using Bybit BTCUSDT perpetual data.
3. Specify the data schema, fill model, fee/funding treatment, walk-forward split and parameter-search ranges.
4. Implement only the offline data downloader and event-driven backtest skeleton.
5. Add tests preventing look-ahead bias, impossible OHLC fills and inventory-cap violations.
6. Produce a baseline comparison: static grid versus time-filtered grid versus regime-filtered semi-dynamic grid.

Do not use real API keys, connect a funded account or place any live orders.
```

## 9. Sources

- Binance public historical market data: https://github.com/binance/binance-public-data
- Binance API: https://www.binance.com/en/binance-api
- Kaiko, BTC ETFs' Impact on Spot Market Structure: https://www.kaiko.com/resources/btc-etfs-impact-on-spot-market-structure
- Kaiko, weekend BTC-USD trading after Silvergate/Signature disruptions: https://www.kaiko.com/resources/tusd-depegs-amidst-heavy-borrowing
- SEC statement on approval of spot Bitcoin ETPs, 2024-01-10: https://www.sec.gov/newsroom/speeches-statements/gensler-statement-spot-bitcoin-011023
- CME announcement of 24/7 cryptocurrency futures and options trading, 2026-06-01: https://www.cmegroup.com/media-room/press-releases/2026/6/01/cme_group_announceslaunchof247cryptocurrencyfuturesandoptionstra.html
- Bybit trading fee structure: https://www.bybit.com/en/help-center/article/Trading-Fee-Structure
- Bybit futures grid bot introduction: https://www.bybit.com/id-ID/help-center/article/Introduction-to-Futures-Grid-Bot-on-Bybit
- Intraday return predictability in cryptocurrency markets: https://www.sciencedirect.com/science/article/abs/pii/S1062940822000833
- Bitcoin intraday trend seasonality / Monday Asia open effect: https://concretumgroup.com/seasonality-in-bitcoin-intraday-trend-trading/

