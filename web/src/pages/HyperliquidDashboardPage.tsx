import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { api } from '../api'
import { TradingChart } from '../components/TradingChart'
import { Icon } from '../components/Icon'
import { Modal } from '../components/Modal'
import { StrategyParameters } from '../components/StrategyParameters'
import type { Candle, ExecutionAccount, ExecutionEnvironment, ExchangeInstrumentRules, HyperliquidAccountState, HyperliquidClearinghouseState, HyperliquidHistoricalOrder, HyperliquidMidPriceTick, HyperliquidOpenOrder, HyperliquidOrderAttribution, HyperliquidPosition, HyperliquidSpotClearinghouseState, Order, Snapshot, Strategy } from '../types'

const TIMEFRAMES = ['1m', '5m', '15m', '1h', '4h', '1d'] as const
type Timeframe = typeof TIMEFRAMES[number]
type AccountPanelTab = 'balances' | 'positions' | 'orders' | 'history' | 'events' | 'alerts'

export function DashboardPage({ strategies, loadedStrategyId, reload, notify, reportError, onExecutionEnvironmentChange }: {
  strategies: Strategy[]; loadedStrategyId?: string | null; reload: () => Promise<void>; notify: (message: string) => void; reportError: (message: string) => void
  onExecutionEnvironmentChange: (environmentId: string) => void
}) {
  const strategy = strategies.find(x => x.strategyId === loadedStrategyId)
    ?? strategies.find(x => x.activeCycle)
    ?? strategies.find(x => x.defaultExecutionEnvironmentId === 'hyperliquid-testnet')
    ?? strategies[0]
  const cycle = strategy?.activeCycle
  const [runEnvironments, setRunEnvironments] = useState<ExecutionEnvironment[]>([])
  const [runEnvironmentId, setRunEnvironmentId] = useState('')
  const [runAccounts, setRunAccounts] = useState<ExecutionAccount[]>([])
  const [runAccountId, setRunAccountId] = useState('')
  const isTestnet = !!strategy && (cycle?.executionEnvironmentId ?? (runEnvironmentId || strategy.defaultExecutionEnvironmentId)) === 'hyperliquid-testnet'
  const selectedExecutionAccountId = cycle?.executionAccountId ?? (runAccountId || strategy?.defaultExecutionAccountId || '')
  const [candles, setCandles] = useState<Candle[]>([])
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null)
  const [orders, setOrders] = useState<Order[]>([])
  const [testnetMid, setTestnetMid] = useState<string | null>(null)
  const [accountState, setAccountState] = useState<HyperliquidAccountState | null>(null)
  const [clearinghouseState, setClearinghouseState] = useState<HyperliquidClearinghouseState | null>(null)
  const [spotClearinghouseState, setSpotClearinghouseState] = useState<HyperliquidSpotClearinghouseState | null>(null)
  const [exchangeOpenOrders, setExchangeOpenOrders] = useState<HyperliquidOpenOrder[] | null>(null)
  const [exchangeOrderHistory, setExchangeOrderHistory] = useState<HyperliquidHistoricalOrder[] | null>(null)
  const [historyRefreshing, setHistoryRefreshing] = useState(false)
  const [busy, setBusy] = useState(false)
  const [parametersOpen, setParametersOpen] = useState(false)
  const [accountPanelTab, setAccountPanelTab] = useState<AccountPanelTab>('balances')
  const [marketLoading, setMarketLoading] = useState(false)
  const [instruments, setInstruments] = useState<string[]>([])
  const [instrumentRules, setInstrumentRules] = useState<ExchangeInstrumentRules | null>(null)
  const [, setMarketStreamConnected] = useState(false)
  const [symbol, setSymbol] = useState(() => localStorage.getItem('grid.dashboardSymbol') ?? '')
  const [timeframe, setTimeframe] = useState<Timeframe>(() => {
    const stored = localStorage.getItem('grid.dashboardTimeframe')
    return TIMEFRAMES.includes(stored as Timeframe) ? stored as Timeframe : '1m'
  })
  const refreshSequence = useRef(0)
  const historyRefreshSequence = useRef(0)
  const lastMarketTickAt = useRef(0)
  const marketSymbol = symbol || strategy?.symbol || (isTestnet ? 'SOL' : 'SOLUSDT')
  const strategyMatchesMarket = sameCoin(strategy?.symbol, marketSymbol)

  useEffect(() => {
    void api.executionEnvironments().then(setRunEnvironments).catch(() => setRunEnvironments([]))
  }, [])
  useEffect(() => {
    if (!strategy) { onExecutionEnvironmentChange(''); return }
    const environmentId = cycle?.executionEnvironmentId ?? strategy.defaultExecutionEnvironmentId
    setRunEnvironmentId(environmentId)
    setRunAccountId(cycle?.executionAccountId ?? strategy.defaultExecutionAccountId)
    onExecutionEnvironmentChange(environmentId)
  }, [strategy?.strategyId, cycle?.cycleId, onExecutionEnvironmentChange])
  useEffect(() => {
    if (!runEnvironmentId) { setRunAccounts([]); setRunAccountId(''); return }
    let active = true
    void api.executionAccounts(runEnvironmentId).then(items => {
      if (!active) return
      setRunAccounts(items)
      setRunAccountId(current => {
        const selected = cycle?.executionAccountId ?? current
        return items.some(x => x.id === selected) ? selected : items[0]?.id ?? ''
      })
    }).catch(() => { if (active) { setRunAccounts([]); setRunAccountId(cycle?.executionAccountId ?? '') } })
    return () => { active = false }
  }, [runEnvironmentId, cycle?.executionAccountId])

  const refreshOrderHistory = useCallback(async (showLoading = true) => {
    if (!isTestnet || !strategy) return
    const sequence = ++historyRefreshSequence.current
    if (showLoading) setHistoryRefreshing(true)
    try {
      const history = await api.testnetOrderHistory(selectedExecutionAccountId)
      if (sequence === historyRefreshSequence.current) setExchangeOrderHistory(history)
    } catch (e) {
      if (sequence === historyRefreshSequence.current)
        reportError(e instanceof Error ? e.message : 'Hyperliquid 历史订单加载失败')
    } finally {
      if (showLoading && sequence === historyRefreshSequence.current) setHistoryRefreshing(false)
    }
  }, [isTestnet, strategy ? selectedExecutionAccountId : undefined, reportError])

  useEffect(() => {
    if (strategy?.symbol) setSymbol(strategy.symbol)
  }, [strategy?.strategyId])

  useEffect(() => {
    localStorage.setItem('grid.dashboardSymbol', marketSymbol)
  }, [marketSymbol])

  useEffect(() => {
    localStorage.setItem('grid.dashboardTimeframe', timeframe)
  }, [timeframe])

  useEffect(() => {
    lastMarketTickAt.current = 0
    setMarketStreamConnected(false)
    if (!isTestnet) return

    let disposed = false
    let retryTimer: ReturnType<typeof setTimeout> | undefined
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/trading')
      .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 30_000])
      .configureLogging(LogLevel.Warning)
      .build()

    const subscribe = async () => {
      await connection.invoke('SubscribeHyperliquidSymbol', marketSymbol)
      if (!disposed) setMarketStreamConnected(true)
    }
    const scheduleStart = () => {
      if (disposed || retryTimer) return
      retryTimer = setTimeout(() => {
        retryTimer = undefined
        void start()
      }, 2_000)
    }
    const start = async () => {
      try {
        await connection.start()
        if (disposed) { await connection.stop(); return }
        await subscribe()
      } catch {
        if (!disposed) {
          setMarketStreamConnected(false)
          await connection.stop().catch(() => undefined)
          scheduleStart()
        }
      }
    }

    connection.on('HyperliquidMidPriceUpdated', (tick: HyperliquidMidPriceTick) => {
      if (disposed || !sameCoin(tick.symbol, marketSymbol) || !Number.isFinite(+tick.mid)) return
      lastMarketTickAt.current = Date.now()
      setTestnetMid(tick.mid)
    })
    connection.onreconnecting(() => setMarketStreamConnected(false))
    connection.onreconnected(() => {
      void subscribe().catch(() => {
        setMarketStreamConnected(false)
        void connection.stop()
      })
    })
    connection.onclose(() => {
      setMarketStreamConnected(false)
      scheduleStart()
    })
    void start()

    return () => {
      disposed = true
      if (retryTimer) clearTimeout(retryTimer)
      setMarketStreamConnected(false)
      void connection.invoke('UnsubscribeHyperliquidSymbol', marketSymbol)
        .catch(() => undefined)
        .finally(() => connection.stop())
    }
  }, [isTestnet, marketSymbol])

  useEffect(() => {
    if (!isTestnet) { setInstruments(strategy?.symbol ? [strategy.symbol] : ['SOLUSDT']); return }
    let active = true
    void api.testnetInstruments().then(result => {
      if (!active) return
      setInstruments(result.universe.filter(item => !item.isDelisted && item.symbol).map(item => item.symbol))
    }).catch(() => {
      if (active) setInstruments(strategy?.symbol ? [strategy.symbol] : ['SOL'])
    })
    return () => { active = false }
  }, [isTestnet, strategy?.symbol])

  useEffect(() => {
    if (!strategy) { setInstrumentRules(null); return }
    let active = true
    setInstrumentRules(null)
    void api.instrumentRules(selectedExecutionAccountId, marketSymbol).then(value => {
      if (active) setInstrumentRules(value)
    }).catch(() => {
      if (active) setInstrumentRules(null)
    })
    return () => { active = false }
  }, [strategy ? selectedExecutionAccountId : undefined, marketSymbol])

  useEffect(() => {
    if (!isTestnet || !strategy) {
      setClearinghouseState(null); setSpotClearinghouseState(null); setExchangeOpenOrders(null); setExchangeOrderHistory(null); return
    }
    let active = true
    const accountId = selectedExecutionAccountId
    async function refreshLiveAccount() {
      try {
        const [state, spotState, openOrders] = await Promise.all([
          api.testnetClearinghouseState(accountId), api.testnetSpotClearinghouseState(accountId), api.testnetOpenOrders(accountId),
        ])
        if (!active) return
        setClearinghouseState(state); setSpotClearinghouseState(spotState); setExchangeOpenOrders(openOrders)
      } catch (e) {
        if (active) reportError(e instanceof Error ? e.message : 'Hyperliquid 账户数据加载失败')
      }
    }
    void refreshLiveAccount(); void refreshOrderHistory(false)
    const timer = setInterval(() => void refreshLiveAccount(), 10_000)
    return () => { active = false; clearInterval(timer) }
  }, [isTestnet, strategy ? selectedExecutionAccountId : undefined, refreshOrderHistory])

  async function refresh() {
    const sequence = ++refreshSequence.current
    setMarketLoading(true)
    try {
      const chartRequest = isTestnet ? api.testnetCandles(marketSymbol, timeframe) : api.candles()
      const bookRequest = isTestnet ? api.testnetBook(marketSymbol) : Promise.resolve(null)
      const accountRequest = isTestnet && strategy
        ? api.testnetAccountState(selectedExecutionAccountId, marketSymbol) : Promise.resolve(null)
      const [chart, snap, orderRows, book, actualAccount] = await Promise.all([
        chartRequest,
        cycle ? api.snapshot(cycle.cycleId) : Promise.resolve(null),
        cycle ? api.orders(cycle.cycleId) : Promise.resolve([]),
        bookRequest,
        accountRequest,
      ])
      if (sequence !== refreshSequence.current) return
      setCandles(isTestnet ? chart : aggregateCandles(chart, timeframe)); setSnapshot(snap); setOrders(orderRows)
      if (!isTestnet || Date.now() - lastMarketTickAt.current > 3_000) setTestnetMid(book?.mid ?? null)
      setAccountState(actualAccount)
    } catch (e) {
      if (sequence === refreshSequence.current) reportError(e instanceof Error ? e.message : '控制台数据加载失败')
    } finally {
      if (sequence === refreshSequence.current) setMarketLoading(false)
    }
  }

  useEffect(() => {
    void refresh(); const timer = setInterval(() => void refresh(), isTestnet ? 10_000 : 5_000)
    return () => clearInterval(timer)
  }, [cycle?.cycleId, strategy?.strategyId, strategy ? selectedExecutionAccountId : undefined, marketSymbol, timeframe, isTestnet])

  const entryOrderLines = useMemo(() => strategyMatchesMarket
    ? orders
      .filter(x => x.kind === 'ENTRY' && (x.status === 'NEW' || x.status === 'PARTIALLY_FILLED'))
      .map(x => ({ price: +x.price, side: x.side }))
    : [], [orders, strategyMatchesMarket])

  async function startCycle() {
    if (!strategy) return
    setBusy(true)
    try {
      const quote = isTestnet ? await api.testnetBook(strategy.symbol)
        : await fetch('/api/v1/market-data/acct_paper_01/SOLUSDT/snapshot').then(r => r.json()) as { mid: string }
      const preview = await api.preview(strategy.strategyId, strategy.version, quote.mid, runEnvironmentId, runAccountId)
      await api.start(strategy.strategyId, preview.previewId, quote.mid, preview.executionEnvironmentId)
      notify('Cycle 已启动，中心和网格计划已冻结'); await reload(); await refresh()
    } catch (e) { reportError(e instanceof Error ? e.message : '启动失败') } finally { setBusy(false) }
  }

  async function command(route: string, label: string) {
    if (!cycle) return
    setBusy(true)
    try { await api.command(cycle.cycleId, route, cycle.stateVersion); notify(label); await reload(); await refresh() }
    catch (e) { reportError(e instanceof Error ? e.message : '命令执行失败') } finally { setBusy(false) }
  }

  const mid = testnetMid ?? (strategyMatchesMarket ? snapshot?.market.mid : null) ?? candles.at(-1)?.close ?? '—'
  const first = +(candles[0]?.open ?? 0); const latest = +mid
  const change = first > 0 && Number.isFinite(latest) ? (latest - first) / first * 100 : 0
  const netPosition = isTestnet ? accountState?.netPosition ?? '0' : strategyMatchesMarket ? snapshot?.position.actualNetQuantity ?? '0' : '0'
  const unrealized = isTestnet ? accountState?.unrealizedPnl ?? '0' : strategyMatchesMarket ? snapshot?.basketPnl.unrealisedAtExecutablePrice ?? '0' : '0'
  const realised = snapshot?.basketPnl.realisedCyclePnl ?? '0'
  const fees = snapshot?.basketPnl.paidFees ?? '0'
  const funding = snapshot?.basketPnl.accruedFunding ?? '0'
  const liquidation = String(+realised + +unrealized - +fees - +funding)
  const maxNetLot = +(strategy?.configuration.maxNetLot ?? 0)
  const maxNetUsage = maxNetLot > 0 ? Math.abs(+netPosition) / maxNetLot * 100 : 0
  const unprotectedExposure = +(snapshot?.risk.unprotectedExposureNotionalUsdt ?? 0)
  const faultExposureThreshold = +(snapshot?.risk.faultExposureThresholdUsdt ?? strategy?.configuration.faultExposureThresholdUsdt ?? 10)
  const exposureTone: MetricTone = unprotectedExposure > faultExposureThreshold ? 'negative' : unprotectedExposure > 0 ? 'warning-text' : 'positive'
  const filledEntryCount = orders.filter(order => order.kind === 'ENTRY' && order.status === 'FILLED').length
  const filledTakeProfitCount = orders.filter(order => order.kind === 'TAKE_PROFIT' && order.status === 'FILLED').length
  const instrument = displaySymbol(marketSymbol, isTestnet)
  const quantitySymbol = coinFromSymbol(marketSymbol)
  const exchangePositions = clearinghouseState?.assetPositions.map(item => item.position) ?? null
  const exchangePnl = String(exchangePositions?.reduce((total, position) => total + +position.unrealizedPnl, 0) ?? 0)
  const unifiedUsdc = spotClearinghouseState?.balances.find(balance => balance.coin === 'USDC')
  const unifiedAvailable = unifiedUsdc && clearinghouseState
    ? availableBalance(unifiedUsdc.total, unifiedUsdc.hold)
    : null

  return <div className="dashboard-page">
    {strategy && <TopbarExecutionSelectors environments={runEnvironments} accounts={runAccounts}
      environmentId={runEnvironmentId} accountId={runAccountId} locked={!!cycle} busy={busy}
      onEnvironmentChange={value => { setRunEnvironmentId(value); setRunAccountId(''); onExecutionEnvironmentChange(value) }}
      onAccountChange={setRunAccountId}
    />}
    <section className="instrument-bar">
      <div className="instrument-summary"><h1><label className="symbol-picker" title="切换行情交易对"><span className="sr-only">交易对</span><select value={marketSymbol} onChange={event => { setSymbol(event.target.value); setTestnetMid(null); setAccountState(null); setCandles([]) }} aria-label="选择交易对">
        {!instruments.includes(marketSymbol) && <option value={marketSymbol}>{instrument}</option>}
        {instruments.map(item => <option key={item} value={item}>{displaySymbol(item, isTestnet)}</option>)}
      </select></label> 永续 <span className="mono">{format(mid, 3)}</span> <em className={change < 0 ? 'negative' : ''}>{change >= 0 ? '+' : ''}{change.toFixed(2)}%</em></h1>
        <div className="instrument-meta">
          <span className={`cycle-state-display ${cycle?.state.toLowerCase() ?? 'idle'}`}><i />Cycle · {cycle?.state ?? 'IDLE'}</span>
          <span className="instrument-last-update">Last Update: {time(isTestnet ? accountState?.asOf : snapshot?.health.lastReconciledAt ?? snapshot?.market.asOf)}</span>
        </div>
      </div>
      <div className="control-buttons">
        {strategy && <button className="secondary" onClick={() => setParametersOpen(true)}>View</button>}
        {!cycle && <button className="primary" disabled={!strategy || !runAccountId || busy} onClick={() => void startCycle()}>{busy ? '启动中…' : 'Start'}</button>}
        {cycle?.state === 'RUNNING' && <button className="primary" disabled={busy} onClick={() => void command('pause-entries', 'Entry 已暂停，已有 TP 保留')}>Pause Entry</button>}
        {cycle?.state === 'PAUSED' && <button className="primary" disabled={busy} onClick={() => void command('resume-entries', '已按固定中心恢复 Entry')}>Resume Entry</button>}
        {cycle && <button className="secondary" disabled={busy} onClick={() => void command('reconcile', 'Sync 完成')}>Sync</button>}
        {cycle && <button className="danger-outline" disabled={busy} onClick={() => void command('close', 'Cycle 已有序关闭并清零仓位')}>Exit</button>}
      </div>
    </section>
    <div className="dashboard-grid">
      <section className="chart-panel panel">
        <div className="chart-tools"><div className="timeframe-picker" role="group" aria-label="K 线周期">{TIMEFRAMES.map(item => <button key={item} type="button" className={timeframe === item ? 'active' : ''} aria-pressed={timeframe === item} onClick={() => { setTimeframe(item); setCandles([]) }}>{item === '1d' ? 'D' : item}</button>)}</div><i /><span>{isTestnet ? 'Hyperliquid candleSnapshot' : 'Paper candles'}</span><Icon name="settings" size={16} /></div>
        <TradingChart candles={candles} entryOrders={entryOrderLines}
          livePrice={isTestnet && Number.isFinite(latest) ? latest : undefined} />
        {marketLoading && <div className="chart-loading">正在加载 {instrument} · {timeframe}</div>}
      </section>
      <aside className="metric-stack">
        <MetricCard title="Overview" rows={[
          ['固定中心', snapshot ? format(snapshot.cycle.fixedCenterPrice, 3) : '—'], ['计划层数', strategy ? `${plannedLevelCount(strategy)} 层` : '—'],
          ['Pending Entry/ TP', snapshot ? `${snapshot.orders.activeEntryCount} / ${snapshot.orders.activeTakeProfitCount}` : '—'],
          ['Filled Entry/TP', cycle ? `${filledEntryCount} / ${filledTakeProfitCount}` : '—'],
          ['状态版本', cycle ? `#${cycle.stateVersion}` : '—'],

          ['权益', isTestnet ? unifiedUsdc ? `${format(unifiedUsdc.total)} USDC` : '—' : '13,420.50 USDC'],
          ['可提余额', isTestnet ? unifiedAvailable !== null ? `${format(unifiedAvailable)} USDC` : '—' : '8,420.00 USDC'],
          ['净仓位', `${signed(netPosition)} ${quantitySymbol}`], ['保证金使用', isTestnet ? `${format(accountState?.totalMarginUsed ?? '0')} USDC` : `${maxNetUsage.toFixed(1)}%`],
          ['MaxNetLot 使用', `${maxNetUsage.toFixed(1)}%`],
          ['未保护敞口 / 阈值', snapshot ? `$${format(unprotectedExposure)} / $${format(faultExposureThreshold)}` : '—', snapshot ? exposureTone : undefined],
        ]} />
        <MetricCard title="PnL" rows={[
          ['浮动', signedUsd(unrealized)], ['已实现', signedUsd(realised)], ['费用', `-$${format(Math.abs(+fees), 3)}`],
          ['资金费', signedUsd(String(-Number(funding)))], ['估算净值', signedUsd(liquidation)],
        ]} accent />
      </aside>
      <section className="orders-panel panel">
        <div className="tabs" role="tablist" aria-label="账户与交易明细">
          <button type="button" className={accountPanelTab === 'balances' ? 'active' : ''} onClick={() => setAccountPanelTab('balances')}>Balances <i>{unifiedUsdc ? 1 : '—'}</i></button>
          <button type="button" className={accountPanelTab === 'positions' ? 'active' : ''} onClick={() => setAccountPanelTab('positions')}>Positions <i>{exchangePositions?.length ?? '—'}</i></button>
          <button type="button" className={accountPanelTab === 'orders' ? 'active' : ''} onClick={() => setAccountPanelTab('orders')}>Open Orders <i>{exchangeOpenOrders?.length ?? '—'}</i></button>
          <button type="button" className={accountPanelTab === 'history' ? 'active' : ''} onClick={() => setAccountPanelTab('history')}>Order History <i>{exchangeOrderHistory?.length ?? '—'}</i></button>
          <button type="button" className={accountPanelTab === 'events' ? 'active' : ''} onClick={() => setAccountPanelTab('events')}>Events</button>
          <button type="button" className={accountPanelTab === 'alerts' ? 'active' : ''} onClick={() => setAccountPanelTab('alerts')}>Alerts</button>
        </div>
        {accountPanelTab === 'balances' && <BalanceTable state={clearinghouseState} spotState={spotClearinghouseState} pnl={exchangePnl} />}
        {accountPanelTab === 'positions' && <PositionTable positions={exchangePositions} />}
        {accountPanelTab === 'orders' && <OpenOrdersTable rows={exchangeOpenOrders} />}
        {accountPanelTab === 'history' && <OrderHistoryTable rows={exchangeOrderHistory} currentSymbol={marketSymbol} currentCycleId={cycle?.cycleId ?? null}
          refreshing={historyRefreshing} onRefresh={() => void refreshOrderHistory()} />}
        {accountPanelTab === 'events' && <Empty text="当前 Cycle 暂无策略事件" />}
        {accountPanelTab === 'alerts' && <Empty text="当前 Cycle 暂无风险告警" />}
      </section>
    </div>
    {parametersOpen && strategy && <Modal title="策略参数" icon="strategy" className="strategy-modal" onClose={() => setParametersOpen(false)}>
      <StrategyParameters strategy={strategy} tickSize={strategyMatchesMarket ? instrumentRules?.tickSize : null} quantityStep={strategyMatchesMarket ? instrumentRules?.quantityStep : null} onClose={() => setParametersOpen(false)} />
    </Modal>}
  </div>
}

function TopbarExecutionSelectors({ environments, accounts, environmentId, accountId, locked, busy,
  onEnvironmentChange, onAccountChange,
}: {
  environments: ExecutionEnvironment[]
  accounts: ExecutionAccount[]
  environmentId: string
  accountId: string
  locked: boolean
  busy: boolean
  onEnvironmentChange: (value: string) => void
  onAccountChange: (value: string) => void
}) {
  const [host, setHost] = useState<HTMLElement | null>(null)
  useEffect(() => { setHost(document.getElementById('execution-context-slot')) }, [])
  if (!host) return null
  const environmentKnown = environments.some(item => item.id === environmentId)
  const accountKnown = accounts.some(item => item.id === accountId)
  return createPortal(
    <>
      <select className="topbar-context-select environment" value={environmentId} disabled={locked || busy} aria-label="本次 Cycle 执行环境"
        onChange={event => onEnvironmentChange(event.target.value)}>
        {environmentId && !environmentKnown && <option value={environmentId}>{environmentId}</option>}
        {environments.map(item => <option key={item.id} value={item.id}>{item.displayName}</option>)}
      </select>
      <select className="topbar-context-select account" value={accountId} disabled={locked || busy || accounts.length <= 1} aria-label="本次 Cycle 执行账户"
        onChange={event => onAccountChange(event.target.value)}>
        {accountId && !accountKnown && <option value={accountId}>{accountId}</option>}
        {accounts.map(item => <option key={item.id} value={item.id}>{item.displayName}</option>)}
      </select>
    </>,
    host,
  )
}

function BalanceTable({ state, spotState, pnl }: { state: HyperliquidClearinghouseState | null; spotState: HyperliquidSpotClearinghouseState | null; pnl: string }) {
  if (!state || !spotState) return <Empty text="正在加载 Hyperliquid unified account balance…" />
  const summary = state.marginSummary
  const usdc = spotState.balances.find(balance => balance.coin === 'USDC')
  if (!usdc) return <Empty text="Hyperliquid 统一账户中没有 USDC 余额" />
  const available = availableBalance(usdc.total, usdc.hold)
  return <div className="table-wrap account-detail-table"><table><thead><tr><th>资产</th><th>总余额</th><th>可用余额</th><th>USDC 价值</th><th>PNL</th><th>保证金占用</th></tr></thead>
    <tbody><tr><td><strong>USDC</strong></td><td>{format(usdc.total, 4)} USDC</td><td>{format(available, 4)} USDC</td><td>${format(usdc.total)}</td><td className={+pnl < 0 ? 'negative' : 'positive'}>{signed(pnl)} USDC</td><td>{format(summary.totalMarginUsed)} USDC</td></tr></tbody></table></div>
}

function PositionTable({ positions }: { positions: HyperliquidPosition[] | null }) {
  if (!positions) return <Empty text="正在加载 Hyperliquid positions…" />
  return <div className="table-wrap account-detail-table"><table><thead><tr><th>市场</th><th>仓位</th><th>仓位价值</th><th>开仓价</th><th>标记价格</th><th>PNL (ROE %)</th><th>清算价</th><th>保证金</th><th>累计资金费</th></tr></thead>
    <tbody>{positions.slice(0, 20).map(position => <PositionRow key={position.coin} position={position} />)}</tbody></table>
    {positions.length === 0 && <Empty text="Hyperliquid 当前没有永续持仓" />}</div>
}

function PositionRow({ position }: { position: HyperliquidPosition }) {
  const mark = +position.szi !== 0 ? Math.abs(+position.positionValue / +position.szi) : 0
  const roe = +position.returnOnEquity * 100
  return <tr><td><strong className="positive">{position.coin}-USDC</strong></td><td className={+position.szi < 0 ? 'negative' : 'positive'}>{signed(position.szi)} {position.coin}</td><td>{format(Math.abs(+position.positionValue))} USDC</td><td>{position.entryPx ? format(position.entryPx, 3) : '—'}</td><td>{format(mark, 3)}</td><td className={+position.unrealizedPnl < 0 ? 'negative' : 'positive'}>{signed(position.unrealizedPnl)} ({roe >= 0 ? '+' : ''}{roe.toFixed(2)}%)</td><td>{position.liquidationPx ? format(position.liquidationPx, 3) : 'N/A'}</td><td>{format(position.marginUsed)} USDC ({position.leverage.value}x {position.leverage.type})</td><td>{format(position.cumFunding?.sinceOpen ?? '0')} USDC</td></tr>
}

function OpenOrdersTable({ rows }: { rows: HyperliquidOpenOrder[] | null }) {
  if (!rows) return <Empty text="正在加载 Hyperliquid frontendOpenOrders…" />
  return <div className="table-wrap open-orders-scroll"><table><thead><tr><th>时间</th><th>市场</th><th>Strategy</th><th>Level</th><th>方向</th><th>类型</th><th>限价</th><th>原始数量</th><th>剩余数量</th><th>Reduce Only</th><th>订单 ID</th></tr></thead>
    <tbody>{rows.map(order => {
      const remainingQuantity = Number(order.sz)
      const originalQuantity = Number(order.origSz)
      const isPartiallyFilled = Number.isFinite(remainingQuantity) && Number.isFinite(originalQuantity)
        && originalQuantity > 0 && remainingQuantity >= 0 && remainingQuantity < originalQuantity
      return <tr key={order.oid}><td>{exchangeTime(order.timestamp)}</td><td><strong>{order.coin}-USDC</strong></td>
        <StrategyOwnership value={order} /><OrderLevel value={order} /><td className={order.side === 'B' ? 'positive' : 'negative'}>{order.side === 'B' ? 'BUY' : 'SELL'}</td><td>{order.orderType}</td><td>{format(order.limitPx, 4)}</td>
        <td>{order.origSz}</td>
        <td>{isPartiallyFilled
          ? <span className="partial-fill-quantity" title={`已部分成交：原始 ${order.origSz}，剩余 ${order.sz}`}>{order.sz}</span>
          : order.sz}</td>
        <td>{order.reduceOnly ? 'YES' : 'NO'}</td><td className="dim">{order.oid}</td></tr>
    })}</tbody>
  </table>{rows.length === 0 && <Empty text="Hyperliquid 当前没有挂单" />}</div>
}

function OrderHistoryTable({ rows, currentSymbol, currentCycleId, refreshing, onRefresh }: {
  rows: HyperliquidHistoricalOrder[] | null; currentSymbol: string; currentCycleId: string | null;
  refreshing: boolean; onRefresh: () => void
}) {
  const [statusFilter, setStatusFilter] = useState<'ALL' | 'FILLED'>(() => localStorage.getItem('grid.orderHistoryStatus') === 'FILLED' ? 'FILLED' : 'ALL')
  const [onlyCurrentCycle, setOnlyCurrentCycle] = useState(() => localStorage.getItem('grid.orderHistoryOnlyCurrentCycle') === 'true')
  const [page, setPage] = useState(1)
  useEffect(() => setPage(1), [currentSymbol, currentCycleId])
  if (!rows) return <Empty text="正在加载 Hyperliquid historicalOrders…" />
  const scopedRows = onlyCurrentCycle
    ? rows.filter(item => currentCycleId !== null && item.cycleId === currentCycleId && sameCoin(item.order.coin, currentSymbol))
    : rows
  const filledCount = scopedRows.filter(item => item.status.toLowerCase() === 'filled').length
  const visibleRows = statusFilter === 'FILLED' ? scopedRows.filter(item => item.status.toLowerCase() === 'filled') : scopedRows
  const pageSize = 20
  const pageCount = Math.max(1, Math.ceil(visibleRows.length / pageSize))
  const currentPage = Math.min(page, pageCount)
  const pageRows = visibleRows.slice((currentPage - 1) * pageSize, currentPage * pageSize)
  const firstRow = visibleRows.length === 0 ? 0 : (currentPage - 1) * pageSize + 1
  const lastRow = Math.min(currentPage * pageSize, visibleRows.length)
  function selectOnlyCurrentCycle(value: boolean) {
    setOnlyCurrentCycle(value)
    setPage(1)
    localStorage.setItem('grid.orderHistoryOnlyCurrentCycle', String(value))
  }
  function selectStatus(value: 'ALL' | 'FILLED') { setStatusFilter(value); setPage(1); localStorage.setItem('grid.orderHistoryStatus', value) }
  return <><div className="history-filterbar" role="group" aria-label="Order History 状态筛选"><span>状态</span>
    <button type="button" className={statusFilter === 'ALL' ? 'active' : ''} aria-pressed={statusFilter === 'ALL'} onClick={() => selectStatus('ALL')}>All <i>{scopedRows.length}</i></button>
    <button type="button" className={statusFilter === 'FILLED' ? 'active' : ''} aria-pressed={statusFilter === 'FILLED'} onClick={() => selectStatus('FILLED')}>Filled <i>{filledCount}</i></button>
    <label className="history-current-cycle" title="只显示当前 Symbol 和当前 Cycle 的订单">
      <input type="checkbox" checked={onlyCurrentCycle} onChange={event => selectOnlyCurrentCycle(event.target.checked)} />
      Only Current Cycle
    </label>
    <button type="button" className="history-refresh" disabled={refreshing} onClick={onRefresh} title="重新加载历史订单">
      <Icon name="refresh" size={13} />{refreshing ? '刷新中…' : '刷新'}
    </button>
  </div><div className="table-wrap history-table-scroll"><table><thead><tr><th>更新时间</th><th>市场</th><th>Strategy</th><th>Level</th><th>方向</th><th>类型</th><th>限价</th><th>原始数量</th><th>剩余数量</th><th>状态</th><th>订单 ID</th></tr></thead>
    <tbody>{pageRows.map(item => <tr key={`${item.order.oid}-${item.statusTimestamp}`}><td>{exchangeTime(item.statusTimestamp)}</td><td><strong>{item.order.coin}-USDC</strong></td>
      <StrategyOwnership value={item} /><OrderLevel value={item} /><td className={item.order.side === 'B' ? 'positive' : 'negative'}>{item.order.side === 'B' ? 'BUY' : 'SELL'}</td><td>{item.order.orderType ?? 'Limit'}</td><td>{format(item.order.limitPx, 4)}</td>
      <td>{item.order.origSz}</td><td>{item.order.sz}</td><td><span className={`tag ${item.status.toLowerCase()}`}>{item.status}</span></td><td className="dim">{item.order.oid}</td></tr>)}</tbody>
  </table>{visibleRows.length === 0 && <Empty text={statusFilter === 'FILLED' ? '当前没有 Filled 历史订单' : 'Hyperliquid 当前没有历史订单'} />}</div>
  <div className="history-pagination" aria-label="Order History 分页">
    <span>{firstRow}–{lastRow} / {visibleRows.length}</span>
    <div><button type="button" disabled={currentPage <= 1} onClick={() => setPage(currentPage - 1)}>上一页</button>
      <b>{currentPage} / {pageCount}</b>
      <button type="button" disabled={currentPage >= pageCount} onClick={() => setPage(currentPage + 1)}>下一页</button></div>
  </div></>
}

function StrategyOwnership({ value }: { value: HyperliquidOrderAttribution }) {
  if (!value.strategyId) return <td><span className="tag external">EXTERNAL</span></td>
  return <td className="order-owner" title={`${value.strategyName ?? 'Strategy'} · ${value.strategyId}`}>
    <strong>{value.strategyName ?? 'Strategy'}</strong><span className="dim">{shortId(value.strategyId)}</span>
  </td>
}

function OrderLevel({ value }: { value: HyperliquidOrderAttribution }) {
  const label = value.levelLabel
  const side = label?.startsWith('B') ? 'buy' : label?.startsWith('S') ? 'sell' : ''
  return <td className={`order-level ${side}`}>{label ?? '—'}</td>
}

type MetricTone = 'positive' | 'negative' | 'warning-text'
type MetricRow = [string, string, MetricTone?]
function MetricCard({ title, rows, accent }: { title: string; rows: MetricRow[]; accent?: boolean }) {
  return <section className="metric-card panel"><h3>{title}</h3><dl>{rows.map(([key, value, tone], index) => <div key={key} className={accent && index === rows.length - 1 ? 'total' : ''}><dt>{key}</dt><dd className={tone ?? (value.startsWith('+') ? 'positive' : value.startsWith('-') ? 'negative' : '')}>{value}</dd></div>)}</dl></section>
}
export function Empty({ text }: { text: string }) { return <div className="empty"><span>◇</span>{text}</div> }
function format(value: string | number, digits = 2) { const n = +value; return Number.isFinite(n) ? n.toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits }) : String(value) }
function signed(value: string) { return +value >= 0 ? `+${format(value)}` : format(value) }
function signedUsd(value: string) { return +value >= 0 ? `+$${format(value)}` : `-$${format(Math.abs(+value))}` }
function plannedLevelCount(strategy: Strategy) { const config = strategy.activeCycle?.frozenConfiguration ?? strategy.configuration; return config.maxLevelsPerSide * ((config.gridMode ?? 'TWO_WAY') === 'TWO_WAY' ? 2 : 1) }
function availableBalance(total: string, hold: string) { return String(Math.max(0, +total - +hold)) }
function time(value?: string) { return value ? new Date(value).toLocaleTimeString('zh-CN', { hour12: false }) : '—' }
function exchangeTime(value: number) { return new Date(value).toLocaleString('zh-CN', { hour12: false }) }
function shortId(value: string) { return value.length > 16 ? `${value.slice(0, 8)}…${value.slice(-6)}` : value }
function coinFromSymbol(symbol?: string) { return (symbol ?? '').toUpperCase().replace(/[-_/]?(USDC|USDT)$/, '') }
function sameCoin(left?: string, right?: string) { return !!left && !!right && coinFromSymbol(left) === coinFromSymbol(right) }
function displaySymbol(symbol: string, isTestnet: boolean) { const coin = coinFromSymbol(symbol); return isTestnet ? `${coin}-USDC` : symbol.toUpperCase() }
function aggregateCandles(candles: Candle[], timeframe: Timeframe) {
  const minutes: Record<Timeframe, number> = { '1m': 1, '5m': 5, '15m': 15, '1h': 60, '4h': 240, '1d': 1440 }
  const bucketSeconds = minutes[timeframe] * 60
  if (bucketSeconds === 60) return candles
  const buckets = new Map<number, Candle>()
  for (const candle of candles) {
    const time = Math.floor(candle.time / bucketSeconds) * bucketSeconds
    const current = buckets.get(time)
    if (!current) { buckets.set(time, { ...candle, time }); continue }
    current.high = String(Math.max(+current.high, +candle.high))
    current.low = String(Math.min(+current.low, +candle.low))
    current.close = candle.close
    current.volume = String(+current.volume + +candle.volume)
  }
  return [...buckets.values()]
}
