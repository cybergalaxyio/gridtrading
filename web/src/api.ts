import type { Alert, Candle, Order, Preview, Snapshot, Strategy, StrategyConfig, HyperliquidAccount, HyperliquidHealth, HyperliquidBook, HyperliquidAccountState, HyperliquidInstruments, HyperliquidClearinghouseState, HyperliquidSpotClearinghouseState, HyperliquidOpenOrder, HyperliquidHistoricalOrder, ExchangeInstrumentRules, ExecutionEnvironment, ExecutionAccount } from './types'

const JSON_HEADERS = { 'Content-Type': 'application/json' }

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api/v1${path}`, init)
  if (response.status === 204) return null as T
  const data = await response.json().catch(() => ({}))
  if (!response.ok) throw new Error(data.detail || data.title || `请求失败 (${response.status})`)
  return data as T
}

export const api = {
  executionEnvironments: () => call<ExecutionEnvironment[]>('/execution-environments'),
  executionAccounts: (environmentId: string) => call<ExecutionAccount[]>(`/execution-environments/${encodeURIComponent(environmentId)}/accounts`),
  testnetAccounts: () => call<HyperliquidAccount[]>("/hyperliquid-testnet/accounts"),
  testnetInstruments: () => call<HyperliquidInstruments>("/hyperliquid-testnet/instruments"),
  testnetHealth: (id: string) => call<HyperliquidHealth>(`/hyperliquid-testnet/accounts/${id}/health`),
  testnetBook: (symbol: string) => call<HyperliquidBook>(`/hyperliquid-testnet/market/${symbol}`),
  testnetCandles: (symbol: string, interval = "1m", limit = 180) => call<Candle[]>(`/hyperliquid-testnet/market/${symbol}/candles?interval=${interval}&limit=${limit}`),
  testnetAccountState: (id: string, symbol: string) => call<HyperliquidAccountState>(`/hyperliquid-testnet/accounts/${id}/state?symbol=${symbol}`),
  testnetClearinghouseState: (id: string) => call<HyperliquidClearinghouseState>(`/hyperliquid-testnet/accounts/${id}/clearinghouse-state`),
  testnetSpotClearinghouseState: (id: string) => call<HyperliquidSpotClearinghouseState>(`/hyperliquid-testnet/accounts/${id}/spot-clearinghouse-state`),
  testnetOpenOrders: (id: string) => call<HyperliquidOpenOrder[]>(`/hyperliquid-testnet/accounts/${id}/open-orders`),
  testnetOrderHistory: (id: string) => call<HyperliquidHistoricalOrder[]>(`/hyperliquid-testnet/accounts/${id}/order-history`),
  instrumentRules: (accountId: string, symbol: string, referencePrice?: string) => call<ExchangeInstrumentRules>(`/exchange-accounts/${encodeURIComponent(accountId)}/instruments/${encodeURIComponent(symbol)}${referencePrice ? `?referencePrice=${encodeURIComponent(referencePrice)}` : ''}`),
  strategies: () => call<Strategy[]>('/strategies'),
  createStrategy: (body: StrategyConfig) => call<Strategy>('/strategies', { method: 'POST', headers: JSON_HEADERS, body: JSON.stringify(body) }),
  updateStrategy: (strategyId: string, body: StrategyConfig) => call<Strategy>(`/strategies/${encodeURIComponent(strategyId)}`, { method: 'PATCH', headers: JSON_HEADERS, body: JSON.stringify(body) }),
  activeCycle: (strategyId: string) => call(`/strategies/${strategyId}/active-cycle`),
  candles: () => call<Candle[]>('/market-data/acct_paper_01/SOLUSDT/candles'),
  snapshot: (cycleId: string) => call<Snapshot>(`/cycles/${cycleId}/snapshot`),
  orders: (cycleId?: string) => call<Order[]>(`/orders${cycleId ? `?cycleId=${cycleId}` : ''}`),
  alerts: () => call<Alert[]>('/risk-alerts'),
  preview: (strategyId: string, version: number, center: string, executionEnvironmentId: string, executionAccountId: string) => call<Preview>('/grid-plan-previews', {
    method: 'POST', headers: JSON_HEADERS, body: JSON.stringify({ strategyId, strategyVersion: version, confirmedCenterPrice: center, executionEnvironmentId, executionAccountId, parameterOverrides: null }),
  }),
  previewCandidate: (config: StrategyConfig, center: string) => call<Preview>('/grid-plan-previews', {
    method: 'POST', headers: JSON_HEADERS, body: JSON.stringify({
      strategyId: null, confirmedCenterPrice: center, parameterOverrides: null,
      executionEnvironmentId: config.defaultExecutionEnvironmentId, executionAccountId: config.defaultExecutionAccountId,
      candidateConfiguration: {
        exchangeAccountId: config.defaultExecutionAccountId, executionEnvironmentId: config.defaultExecutionEnvironmentId,
        executionAccountId: config.defaultExecutionAccountId, symbol: config.symbol, gridMode: config.gridMode, maxLevelsPerSide: config.maxLevelsPerSide,
        workingEntriesPerSide: config.workingEntriesPerSide, initialGapPoints: config.initialGapPoints,
        gridSpacingPoints: config.gridSpacingPoints, gridSpacingStepPoints: config.gridSpacingStepPoints,
        takeProfitPoints: config.takeProfitPoints, baseLotSize: config.baseLotSize,
        lotSizeIncreasePercent: config.lotSizeIncreasePercent, maxTradeLot: config.maxTradeLot, maxNetLot: config.maxNetLot,
      },
    }),
  }),
  start: (strategyId: string, previewId: string, center: string, environment: string) => call(`/strategies/${strategyId}/cycles`, {
    method: 'POST', headers: { ...JSON_HEADERS, 'Idempotency-Key': crypto.randomUUID() },
    body: JSON.stringify({ previewId, confirmedCenterPrice: center, operatorConfirmation: { parametersReviewed: true, centerConfirmed: true, environmentConfirmed: environment } }),
  }),
  command: (cycleId: string, route: string, version: number, emergency = false) => call(`/cycles/${cycleId}/commands/${route}`, {
    method: 'POST', headers: { ...JSON_HEADERS, 'Idempotency-Key': crypto.randomUUID(), 'If-Match': `"${version}"` },
    body: emergency ? JSON.stringify({ reason: 'Operator emergency action', confirmation: { cancelAllStrategyOrders: true, flattenActualNetPosition: true, acknowledgedTakerExecution: true } }) : JSON.stringify({ reason: 'Operator requested action' }),
  }),
  acknowledge: (alertId: string) => call(`/risk-alerts/${alertId}/acknowledgements`, { method: 'POST', headers: JSON_HEADERS, body: JSON.stringify({ note: '已由本地操作员确认' }) }),
}

export const defaultConfig: StrategyConfig = {
  strategyType: 'GRID', defaultExecutionEnvironmentId: 'paper-local', defaultExecutionAccountId: 'acct_paper_01',
  name: 'Weekend SOL Grid', exchangeAccountId: 'acct_paper_01', symbol: 'SOLUSDT', gridMode: 'TWO_WAY',
  centerSuggestionMode: 'CURRENT_MID', autoRestart: false, maxLevelsPerSide: 14, workingEntriesPerSide: 1,
  initialGapPoints: '0', gridSpacingPoints: '250', gridSpacingStepPoints: '10', takeProfitPoints: '180',
  baseLotSize: '0.5', lotSizeIncreasePercent: '5', maxTradeLot: '2', maxNetLot: '10',
  basketTakeProfitUsdt: '50', basketStopLossUsdt: '100', makerFeeRate: '0.0002', takerFeeRate: '0.00055',
  faultExposureThresholdUsdt: '10',
  estimatedExitSlippagePct: '0.10', includeFunding: true, postOnlyEntries: true, postOnlyTakeProfits: true,
  reconcileIntervalSeconds: 10, marketDataStaleSeconds: 5, orderCommandTimeoutSeconds: 10, maxOrderFrequency: 5,
  partialFillCancelAfterMinutes: 10,
}
