export type StrategyConfig = {
  strategyType: 'GRID'; defaultExecutionEnvironmentId: string; defaultExecutionAccountId: string;
  name: string; exchangeAccountId: string; symbol: string; gridMode: 'BUY_ONLY' | 'SELL_ONLY' | 'TWO_WAY';
  centerSuggestionMode: 'CURRENT_MID' | 'VWAP_EMA' | 'MANUAL'; autoRestart: boolean;
  maxLevelsPerSide: number; workingEntriesPerSide: number; initialGapPoints: string;
  gridSpacingPoints: string; gridSpacingStepPoints: string; takeProfitPoints: string;
  baseLotSize: string; lotSizeIncreasePercent: string; maxTradeLot: string; maxNetLot: string;
  basketTakeProfitUsdt: string; basketStopLossUsdt: string; faultExposureThresholdUsdt: string; makerFeeRate: string;
  takerFeeRate: string; estimatedExitSlippagePct: string; includeFunding: boolean;
  postOnlyEntries: boolean; postOnlyTakeProfits: boolean; reconcileIntervalSeconds: number;
  marketDataStaleSeconds: number; orderCommandTimeoutSeconds: number; maxOrderFrequency: number;
  partialFillCancelAfterMinutes: number;
  tickSize?: string; quantityStep?: string; minOrderQuantity?: string; minOrderNotional?: string;
  maxActiveOrders?: number; sizeDecimals?: number | null;
}

export type Cycle = {
  cycleId: string; strategyId: string; state: string; stateVersion: number; isTerminal: boolean;
  operatorResetRequired: boolean; fixedCenterPrice: string; startedAt: string; endedAt?: string;
  executionEnvironmentId: string; executionAccountId: string;
  frozenConfiguration?: StrategyConfig | null;
}

export type Strategy = {
  strategyId: string; name: string; strategyType: 'GRID'; defaultExecutionEnvironmentId: string;
  defaultExecutionAccountId: string; exchangeAccountId: string; symbol: string; version: number;
  archived: boolean; configuration: StrategyConfig; activeCycle: Cycle | null; createdAt: string; updatedAt: string;
}

export type Candle = { time: number; open: string; high: string; low: string; close: string; volume: string }
export type Order = { id: string; cycleId: string; clientOrderId: string; exchangeOrderId: string; symbol: string;
  side: string; kind: string; status: string; gridLevel: number; price: string; quantity: string;
  filledQuantity: string; createdAt: string }
export type Alert = { id: string; cycleId?: string; severity: string; code: string; message: string; acknowledged: boolean; createdAt: string }
export type GridLevel = { side: string; levelIndex: number; entryPrice: string; takeProfitDistance: string; plannedQuantity: string;
  orderNotional: string; cumulativeQuantity: string; cumulativeNotional: string }
export type Preview = { previewId: string; executionEnvironmentId: string; executionAccountId: string; expiresAt: string; strategyVersion: number; confirmedCenterPrice: string;
  outermostBuyPrice: string; outermostSellPrice: string; coverageBelowPct: string; coverageAbovePct: string;
  maximumPlannedQuantityPerSide: string; maximumPlannedNotionalPerSide: string; startEligible: boolean; levels: GridLevel[] }

export type Snapshot = {
  cycle: Cycle & { fixedCenterPrice: string };
  market: { bid: string; ask: string; mid: string; asOf: string; isStale: boolean };
  orders: { activeEntryCount: number; activeTakeProfitCount: number; unknownCount: number };
  position: { actualNetQuantity: string; reconstructedNetQuantity: string; absoluteMaxNetLotUsagePct: string; netNotionalUsdt: string };
  basketPnl: { realisedCyclePnl: string; unrealisedAtExecutablePrice: string; paidFees: string; accruedFunding: string;
    estimatedFinalTakerFee: string; estimatedExitSlippage: string; liquidationPnl: string; takeProfitTarget: string; stopLossLimit: string };
  risk: { color: string; reasons: string[]; usedBuyLevels: number; remainingBuyLevels: number; usedSellLevels: number; remainingSellLevels: number;
    unprotectedExposureNotionalUsdt: string; faultExposureThresholdUsdt: string; faultExposureThresholdExceeded: boolean };
  health: { exchange: string; marketData: string; reconciliation: string; lastReconciledAt: string };
  allowedCommands: string[];
}

export type ExecutionEnvironment = { id: string; venueType: 'PAPER' | 'HYPERLIQUID'; network: string; displayName: string }
export type ExecutionAccount = { id: string; environmentId: string; displayName: string; enabled: boolean }

export type HyperliquidAccount = { accountId: string; name: string; exchange: "HYPERLIQUID"; environment: "TESTNET"; accountAddress: string; agentAddress: string; enabled: boolean; signingKeyStored: boolean }
export type HyperliquidHealth = HyperliquidAccount & { agentApproved: boolean; agentRole: string; accountMode: string; tradingEquity: string; availableBalance: string; perpAccountValue: string; netPosition: string; openOrderCount: number; tradingReady: boolean; asOf: string }
export type HyperliquidBook = { bid: string; ask: string; mid: string; asOf: string }
export type HyperliquidMidPriceTick = { symbol: string; mid: string; asOf: string }
export type HyperliquidAccountState = { accountId: string; symbol: string; accountValue: string; withdrawable: string; totalMarginUsed: string; netPosition: string; unrealizedPnl: string; entryPrice?: string | null; asOf: string }
export type HyperliquidInstrument = { assetIndex: number; symbol: string; sizeDecimals: number; isDelisted: boolean }
export type HyperliquidInstruments = { exchange: "HYPERLIQUID"; environment: "TESTNET"; tradingEnabled: boolean; asOf: string; universe: HyperliquidInstrument[] }
export type ExchangeInstrumentRules = {
  symbol: string; environment: 'PAPER' | 'TESTNET'; assetIndex: number; sizeDecimals: number;
  referencePrice: string; tickSize: string; quantityStep: string; minOrderQuantity: string;
  minOrderNotional: string; maxActiveOrders: number; makerFeeRate: string; takerFeeRate: string;
  feeSource: string; asOf: string;
}
export type HyperliquidMarginSummary = { accountValue: string; totalNtlPos: string; totalRawUsd: string; totalMarginUsed: string }
export type HyperliquidPosition = {
  coin: string; szi: string; entryPx?: string | null; positionValue: string; unrealizedPnl: string; returnOnEquity: string;
  liquidationPx?: string | null; marginUsed: string; maxLeverage: number; leverage: { type: string; value: number; rawUsd?: string };
  cumFunding?: { allTime: string; sinceChange: string; sinceOpen: string };
}
export type HyperliquidClearinghouseState = {
  marginSummary: HyperliquidMarginSummary; crossMarginSummary: HyperliquidMarginSummary;
  crossMaintenanceMarginUsed: string; withdrawable: string; assetPositions: { type: string; position: HyperliquidPosition }[];
}
export type HyperliquidSpotBalance = { coin: string; token: number; hold: string; total: string; entryNtl: string }
export type HyperliquidSpotClearinghouseState = { balances: HyperliquidSpotBalance[] }
export type HyperliquidOrderAttribution = {
  orderSource: 'STRATEGY' | 'EXTERNAL'; strategyId?: string | null; strategyName?: string | null;
  cycleId?: string | null; localOrderId?: string | null;
  gridLevel?: number | null; orderKind?: string | null; levelLabel?: string | null;
}
export type HyperliquidOpenOrder = HyperliquidOrderAttribution & {
  coin: string; side: 'A' | 'B'; limitPx: string; sz: string; origSz: string; oid: number; timestamp: number;
  orderType: string; reduceOnly: boolean; isTrigger: boolean; isPositionTpsl: boolean; triggerPx: string; triggerCondition: string; cloid?: string | null;
}
export type HyperliquidHistoricalOrder = HyperliquidOrderAttribution & {
  order: {
    coin: string; side: 'A' | 'B'; limitPx: string; sz: string; origSz: string; oid: number; timestamp: number;
    orderType?: string; reduceOnly?: boolean; isTrigger?: boolean; triggerPx?: string; cloid?: string | null;
  };
  status: string; statusTimestamp: number;
}
