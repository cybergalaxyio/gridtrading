import { useEffect, useMemo, useRef, useState } from 'react'
import { api } from '../api'
import { TradingChart } from '../components/TradingChart'
import { Icon } from '../components/Icon'
import type { Candle, HyperliquidAccountState, HyperliquidClearinghouseState, HyperliquidHistoricalOrder, HyperliquidOpenOrder, HyperliquidPosition, HyperliquidSpotClearinghouseState, Order, Snapshot, Strategy } from '../types'

const TIMEFRAMES = ['1m', '5m', '15m', '1h', '4h', '1d'] as const
type Timeframe = typeof TIMEFRAMES[number]
type AccountPanelTab = 'balances' | 'positions' | 'orders' | 'history' | 'events' | 'alerts'

export function DashboardPage({ strategies, reload, notify, reportError }: {
  strategies: Strategy[]; reload: () => Promise<void>; notify: (message: string) => void; reportError: (message: string) => void
}) {
  const strategy = strategies.find(x => x.activeCycle)
    ?? strategies.find(x => x.exchangeAccountId !== 'acct_paper_01')
    ?? strategies[0]
  const cycle = strategy?.activeCycle
  const isTestnet = !!strategy && strategy.exchangeAccountId !== 'acct_paper_01'
  const [candles, setCandles] = useState<Candle[]>([])
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null)
  const [orders, setOrders] = useState<Order[]>([])
  const [testnetMid, setTestnetMid] = useState<string | null>(null)
  const [accountState, setAccountState] = useState<HyperliquidAccountState | null>(null)
  const [clearinghouseState, setClearinghouseState] = useState<HyperliquidClearinghouseState | null>(null)
  const [spotClearinghouseState, setSpotClearinghouseState] = useState<HyperliquidSpotClearinghouseState | null>(null)
  const [exchangeOpenOrders, setExchangeOpenOrders] = useState<HyperliquidOpenOrder[] | null>(null)
  const [exchangeOrderHistory, setExchangeOrderHistory] = useState<HyperliquidHistoricalOrder[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [accountPanelTab, setAccountPanelTab] = useState<AccountPanelTab>('balances')
  const [marketLoading, setMarketLoading] = useState(false)
  const [instruments, setInstruments] = useState<string[]>([])
  const [symbol, setSymbol] = useState(() => localStorage.getItem('grid.dashboardSymbol') ?? '')
  const [timeframe, setTimeframe] = useState<Timeframe>(() => {
    const stored = localStorage.getItem('grid.dashboardTimeframe')
    return TIMEFRAMES.includes(stored as Timeframe) ? stored as Timeframe : '1m'
  })
  const refreshSequence = useRef(0)
  const marketSymbol = symbol || strategy?.symbol || (isTestnet ? 'SOL' : 'SOLUSDT')
  const strategyMatchesMarket = sameCoin(strategy?.symbol, marketSymbol)


  useEffect(() => {
    if (!symbol && strategy?.symbol) setSymbol(strategy.symbol)
  }, [strategy?.symbol, symbol])

  useEffect(() => {
    localStorage.setItem('grid.dashboardSymbol', marketSymbol)
  }, [marketSymbol])

  useEffect(() => {
    localStorage.setItem('grid.dashboardTimeframe', timeframe)
  }, [timeframe])

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
    if (!isTestnet || !strategy) {
      setClearinghouseState(null); setSpotClearinghouseState(null); setExchangeOpenOrders(null); setExchangeOrderHistory(null); return
    }
    let active = true
    const accountId = strategy.exchangeAccountId
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
    async function refreshHistory() {
      try {
        const history = await api.testnetOrderHistory(accountId)
        if (active) setExchangeOrderHistory(history)
      } catch (e) {
        if (active) reportError(e instanceof Error ? e.message : 'Hyperliquid 历史订单加载失败')
      }
    }
    void refreshLiveAccount(); void refreshHistory()
    const timer = setInterval(() => void refreshLiveAccount(), 10_000)
    return () => { active = false; clearInterval(timer) }
  }, [isTestnet, strategy?.exchangeAccountId])

  async function refresh() {
    const sequence = ++refreshSequence.current
    setMarketLoading(true)
    try {
      const chartRequest = isTestnet ? api.testnetCandles(marketSymbol, timeframe) : api.candles()
      const bookRequest = isTestnet ? api.testnetBook(marketSymbol) : Promise.resolve(null)
      const accountRequest = isTestnet && strategy
        ? api.testnetAccountState(strategy.exchangeAccountId, marketSymbol) : Promise.resolve(null)
      const [chart, snap, orderRows, book, actualAccount] = await Promise.all([
        chartRequest,
        cycle ? api.snapshot(cycle.cycleId) : Promise.resolve(null),
        cycle ? api.orders(cycle.cycleId) : Promise.resolve([]),
        bookRequest,
        accountRequest,
      ])
      if (sequence !== refreshSequence.current) return
      setCandles(isTestnet ? chart : aggregateCandles(chart, timeframe)); setSnapshot(snap); setOrders(orderRows)
      setTestnetMid(book?.mid ?? null); setAccountState(actualAccount)
    } catch (e) {
      if (sequence === refreshSequence.current) reportError(e instanceof Error ? e.message : '控制台数据加载失败')
    } finally {
      if (sequence === refreshSequence.current) setMarketLoading(false)
    }
  }

  useEffect(() => {
    void refresh(); const timer = setInterval(() => void refresh(), isTestnet ? 10_000 : 5_000)
    return () => clearInterval(timer)
  }, [cycle?.cycleId, strategy?.strategyId, strategy?.exchangeAccountId, marketSymbol, timeframe, isTestnet])

  const levelPrices = useMemo(() => strategyMatchesMarket ? orders.filter(x => x.status === 'NEW').map(x => +x.price) : [], [orders, strategyMatchesMarket])

  async function startCycle() {
    if (!strategy) return
    setBusy(true)
    try {
      const quote = isTestnet ? await api.testnetBook(strategy.symbol)
        : await fetch('/api/v1/market-data/acct_paper_01/SOLUSDT/snapshot').then(r => r.json()) as { mid: string }
      const preview = await api.preview(strategy.strategyId, strategy.version, quote.mid)
      await api.start(strategy.strategyId, preview.previewId, quote.mid, isTestnet ? 'TESTNET' : 'PAPER')
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
  const state = cycle?.state ?? 'WAITING_FOR_OPERATOR'
  const netPosition = isTestnet ? accountState?.netPosition ?? '0' : strategyMatchesMarket ? snapshot?.position.actualNetQuantity ?? '0' : '0'
  const unrealized = isTestnet ? accountState?.unrealizedPnl ?? '0' : strategyMatchesMarket ? snapshot?.basketPnl.unrealisedAtExecutablePrice ?? '0' : '0'
  const realised = snapshot?.basketPnl.realisedCyclePnl ?? '0'
  const fees = snapshot?.basketPnl.paidFees ?? '0'
  const liquidation = String(+realised + +unrealized - +fees)
  const maxNetLot = +(strategy?.configuration.maxNetLot ?? 0)
  const maxNetUsage = maxNetLot > 0 ? Math.abs(+netPosition) / maxNetLot * 100 : 0
  const instrument = displaySymbol(marketSymbol, isTestnet)
  const quantitySymbol = coinFromSymbol(marketSymbol)
  const exchangePositions = clearinghouseState?.assetPositions.map(item => item.position) ?? null
  const exchangePnl = String(exchangePositions?.reduce((total, position) => total + +position.unrealizedPnl, 0) ?? 0)
  const unifiedUsdc = spotClearinghouseState?.balances.find(balance => balance.coin === 'USDC')
  const unifiedAvailable = unifiedUsdc && clearinghouseState
    ? availableBalance(unifiedUsdc.total, unifiedUsdc.hold)
    : null

  return <div className="dashboard-page">
    <section className="instrument-bar">
      <div><h1><label className="symbol-picker" title="切换行情交易对"><span className="sr-only">交易对</span><select value={marketSymbol} onChange={event => { setSymbol(event.target.value); setTestnetMid(null); setAccountState(null); setCandles([]) }} aria-label="选择交易对">
        {!instruments.includes(marketSymbol) && <option value={marketSymbol}>{instrument}</option>}
        {instruments.map(item => <option key={item} value={item}>{displaySymbol(item, isTestnet)}</option>)}
      </select></label> 永续 <span className="mono">{format(mid, 3)}</span> <em className={change < 0 ? 'negative' : ''}>{change >= 0 ? '+' : ''}{change.toFixed(2)}%</em></h1>
        <p>Strategy: {strategy?.name ?? '尚未创建'} <b className={`state ${state.toLowerCase()}`}>{state}</b>
          <span>{isTestnet ? 'Hyperliquid Testnet · 官方 API' : 'Paper · 本地模拟'} · 单向 · 1x | 上次同步 {isTestnet ? time(accountState?.asOf) : snapshot ? '刚刚' : '—'}</span>
          {!strategyMatchesMarket && <span className="market-view-note">仅浏览行情 · 策略运行于 {displaySymbol(strategy?.symbol ?? '', isTestnet)}</span>}</p></div>
      <div className="control-buttons">
        {!cycle && <button className="primary" disabled={!strategy || busy} onClick={() => void startCycle()}>{busy ? '启动中…' : '确认预览并开启'}</button>}
        {cycle?.state === 'RUNNING' && <button className="primary" disabled={busy} onClick={() => void command('pause-entries', 'Entry 已暂停，已有 TP 保留')}>暂停 Entry</button>}
        {cycle?.state === 'PAUSED' && <button className="primary" disabled={busy} onClick={() => void command('resume-entries', '已按固定中心恢复 Entry')}>继续</button>}
        {cycle && <button className="secondary" disabled={busy} onClick={() => void command('reconcile', '人工对账完成')}>对账</button>}
        {cycle && <button className="danger-outline" disabled={busy} onClick={() => void command('close', 'Cycle 已有序关闭并清零仓位')}>关闭 Cycle</button>}
      </div>
    </section>
    <div className="dashboard-grid">
      <section className="chart-panel panel">
        <div className="chart-tools"><div className="timeframe-picker" role="group" aria-label="K 线周期">{TIMEFRAMES.map(item => <button key={item} type="button" className={timeframe === item ? 'active' : ''} aria-pressed={timeframe === item} onClick={() => { setTimeframe(item); setCandles([]) }}>{item === '1d' ? 'D' : item}</button>)}</div><i /><span>{isTestnet ? 'Hyperliquid candleSnapshot' : 'Paper candles'}</span><Icon name="settings" size={16} /></div>
        <TradingChart candles={candles} levels={levelPrices} center={strategyMatchesMarket && snapshot ? +snapshot.cycle.fixedCenterPrice : undefined} />
        {marketLoading && <div className="chart-loading">正在加载 {instrument} · {timeframe}</div>}
      </section>
      <aside className="metric-stack">
        <MetricCard title="策略摘要" rows={[
          ['固定中心', snapshot ? format(snapshot.cycle.fixedCenterPrice, 3) : '—'], ['计划层数', strategy ? `${strategy.configuration.maxLevelsPerSide * 2} 层` : '—'],
          ['Entry / TP', snapshot ? `${snapshot.orders.activeEntryCount} / ${snapshot.orders.activeTakeProfitCount}` : '—'], ['状态版本', cycle ? `#${cycle.stateVersion}` : '—'],
        ]} />
        <MetricCard title="账户与持仓" rows={[
          ['权益', isTestnet ? unifiedUsdc ? `${format(unifiedUsdc.total)} USDC` : '—' : '13,420.50 USDT'],
          ['可提余额', isTestnet ? unifiedAvailable !== null ? `${format(unifiedAvailable)} USDC` : '—' : '8,420.00 USDT'],
          ['净仓位', `${signed(netPosition)} ${quantitySymbol}`], ['保证金使用', isTestnet ? `${format(accountState?.totalMarginUsed ?? '0')} USDC` : `${maxNetUsage.toFixed(1)}%`],
          ['MaxNetLot 使用', `${maxNetUsage.toFixed(1)}%`],
        ]} />
        <MetricCard title="Basket 清算盈亏" rows={[
          ['浮动', signed(unrealized)], ['已实现', signed(realised)], ['费用', `-${format(fees, 3)}`], ['估算净值', signed(liquidation)],
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
        {accountPanelTab === 'history' && <OrderHistoryTable rows={exchangeOrderHistory} />}
        {accountPanelTab === 'events' && <Empty text="当前 Cycle 暂无策略事件" />}
        {accountPanelTab === 'alerts' && <Empty text="当前 Cycle 暂无风险告警" />}
      </section>
    </div>
  </div>
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
  return <div className="table-wrap"><table><thead><tr><th>时间</th><th>市场</th><th>方向</th><th>类型</th><th>限价</th><th>原始数量</th><th>剩余数量</th><th>Reduce Only</th><th>订单 ID</th></tr></thead>
    <tbody>{rows.slice(0, 50).map(order => <tr key={order.oid}><td>{exchangeTime(order.timestamp)}</td><td><strong>{order.coin}-USDC</strong></td>
      <td className={order.side === 'B' ? 'positive' : 'negative'}>{order.side === 'B' ? 'BUY' : 'SELL'}</td><td>{order.orderType}</td><td>{format(order.limitPx, 4)}</td>
      <td>{order.origSz}</td><td>{order.sz}</td><td>{order.reduceOnly ? 'YES' : 'NO'}</td><td className="dim">{order.oid}</td></tr>)}</tbody>
  </table>{rows.length === 0 && <Empty text="Hyperliquid 当前没有挂单" />}</div>
}

function OrderHistoryTable({ rows }: { rows: HyperliquidHistoricalOrder[] | null }) {
  if (!rows) return <Empty text="正在加载 Hyperliquid historicalOrders…" />
  return <div className="table-wrap"><table><thead><tr><th>更新时间</th><th>市场</th><th>方向</th><th>类型</th><th>限价</th><th>原始数量</th><th>剩余数量</th><th>状态</th><th>订单 ID</th></tr></thead>
    <tbody>{rows.slice(0, 100).map(item => <tr key={`${item.order.oid}-${item.statusTimestamp}`}><td>{exchangeTime(item.statusTimestamp)}</td><td><strong>{item.order.coin}-USDC</strong></td>
      <td className={item.order.side === 'B' ? 'positive' : 'negative'}>{item.order.side === 'B' ? 'BUY' : 'SELL'}</td><td>{item.order.orderType ?? 'Limit'}</td><td>{format(item.order.limitPx, 4)}</td>
      <td>{item.order.origSz}</td><td>{item.order.sz}</td><td><span className={`tag ${item.status.toLowerCase()}`}>{item.status}</span></td><td className="dim">{item.order.oid}</td></tr>)}</tbody>
  </table>{rows.length === 0 && <Empty text="Hyperliquid 当前没有历史订单" />}</div>
}

function MetricCard({ title, rows, accent }: { title: string; rows: [string, string][]; accent?: boolean }) {
  return <section className="metric-card panel"><h3>{title}</h3><dl>{rows.map(([key, value], index) => <div key={key} className={accent && index === rows.length - 1 ? 'total' : ''}><dt>{key}</dt><dd className={value.startsWith('+') ? 'positive' : value.startsWith('-') ? 'negative' : ''}>{value}</dd></div>)}</dl></section>
}
export function Empty({ text }: { text: string }) { return <div className="empty"><span>◇</span>{text}</div> }
function format(value: string | number, digits = 2) { const n = +value; return Number.isFinite(n) ? n.toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits }) : String(value) }
function signed(value: string) { return +value >= 0 ? `+${format(value)}` : format(value) }
function availableBalance(total: string, hold: string) { return String(Math.max(0, +total - +hold)) }
function time(value?: string) { return value ? new Date(value).toLocaleTimeString('zh-CN', { hour12: false }) : '—' }
function exchangeTime(value: number) { return new Date(value).toLocaleString('zh-CN', { hour12: false }) }
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
