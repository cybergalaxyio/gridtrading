import { useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import { TradingChart } from '../components/TradingChart'
import { Icon } from '../components/Icon'
import type { Candle, HyperliquidAccountState, Order, Snapshot, Strategy } from '../types'

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
  const [busy, setBusy] = useState(false)

  async function refresh() {
    try {
      const chartRequest = isTestnet && strategy ? api.testnetCandles(strategy.symbol) : api.candles()
      const bookRequest = isTestnet && strategy ? api.testnetBook(strategy.symbol) : Promise.resolve(null)
      const accountRequest = isTestnet && strategy
        ? api.testnetAccountState(strategy.exchangeAccountId, strategy.symbol) : Promise.resolve(null)
      const [chart, snap, orderRows, book, actualAccount] = await Promise.all([
        chartRequest,
        cycle ? api.snapshot(cycle.cycleId) : Promise.resolve(null),
        cycle ? api.orders(cycle.cycleId) : Promise.resolve([]),
        bookRequest,
        accountRequest,
      ])
      setCandles(chart); setSnapshot(snap); setOrders(orderRows)
      setTestnetMid(book?.mid ?? null); setAccountState(actualAccount)
    } catch (e) { reportError(e instanceof Error ? e.message : '控制台数据加载失败') }
  }

  useEffect(() => {
    void refresh(); const timer = setInterval(() => void refresh(), isTestnet ? 10_000 : 5_000)
    return () => clearInterval(timer)
  }, [cycle?.cycleId, strategy?.strategyId, strategy?.exchangeAccountId, strategy?.symbol, isTestnet])

  const levelPrices = useMemo(() => orders.filter(x => x.status === 'NEW').map(x => +x.price), [orders])

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

  const mid = testnetMid ?? snapshot?.market.mid ?? candles.at(-1)?.close ?? '—'
  const first = +(candles[0]?.open ?? 0); const latest = +mid
  const change = first > 0 && Number.isFinite(latest) ? (latest - first) / first * 100 : 0
  const state = cycle?.state ?? 'WAITING_FOR_OPERATOR'
  const netPosition = isTestnet ? accountState?.netPosition ?? '0' : snapshot?.position.actualNetQuantity ?? '0'
  const unrealized = isTestnet ? accountState?.unrealizedPnl ?? '0' : snapshot?.basketPnl.unrealisedAtExecutablePrice ?? '0'
  const realised = snapshot?.basketPnl.realisedCyclePnl ?? '0'
  const fees = snapshot?.basketPnl.paidFees ?? '0'
  const liquidation = String(+realised + +unrealized - +fees)
  const maxNetLot = +(strategy?.configuration.maxNetLot ?? 0)
  const maxNetUsage = maxNetLot > 0 ? Math.abs(+netPosition) / maxNetLot * 100 : 0
  const instrument = isTestnet ? 'SOL-USDC' : 'SOLUSDT'

  return <div className="dashboard-page">
    <section className="instrument-bar">
      <div><h1>{instrument} 永续 <span className="mono">{format(mid, 3)}</span> <em className={change < 0 ? 'negative' : ''}>{change >= 0 ? '+' : ''}{change.toFixed(2)}%</em></h1>
        <p>Strategy: {strategy?.name ?? '尚未创建'} <b className={`state ${state.toLowerCase()}`}>{state}</b>
          <span>{isTestnet ? 'Hyperliquid Testnet · 官方 API' : 'Paper · 本地模拟'} · 单向 · 1x | 上次同步 {isTestnet ? time(accountState?.asOf) : snapshot ? '刚刚' : '—'}</span></p></div>
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
        <div className="chart-tools"><b>1m</b><span>5m</span><span>15m</span><span>1h</span><span>4h</span><i /><span>{isTestnet ? 'Hyperliquid candleSnapshot' : 'Paper candles'}</span><Icon name="settings" size={16} /></div>
        <TradingChart candles={candles} levels={levelPrices} center={snapshot ? +snapshot.cycle.fixedCenterPrice : undefined} />
      </section>
      <aside className="metric-stack">
        <MetricCard title="策略摘要" rows={[
          ['固定中心', snapshot ? format(snapshot.cycle.fixedCenterPrice, 3) : '—'], ['计划层数', strategy ? `${strategy.configuration.maxLevelsPerSide * 2} 层` : '—'],
          ['Entry / TP', snapshot ? `${snapshot.orders.activeEntryCount} / ${snapshot.orders.activeTakeProfitCount}` : '—'], ['状态版本', cycle ? `#${cycle.stateVersion}` : '—'],
        ]} />
        <MetricCard title="账户与持仓" rows={[
          ['权益', isTestnet ? `${format(accountState?.accountValue ?? '0')} USDC` : '13,420.50 USDT'],
          ['可提余额', isTestnet ? `${format(accountState?.withdrawable ?? '0')} USDC` : '8,420.00 USDT'],
          ['净仓位', `${signed(netPosition)} SOL`], ['保证金使用', isTestnet ? `${format(accountState?.totalMarginUsed ?? '0')} USDC` : `${maxNetUsage.toFixed(1)}%`],
          ['MaxNetLot 使用', `${maxNetUsage.toFixed(1)}%`],
        ]} />
        <MetricCard title="Basket 清算盈亏" rows={[
          ['浮动', signed(unrealized)], ['已实现', signed(realised)], ['费用', `-${format(fees, 3)}`], ['估算净值', signed(liquidation)],
        ]} accent />
      </aside>
      <section className="orders-panel panel">
        <div className="tabs"><b>当前挂单 <i>{orders.filter(x => x.status === 'NEW').length}</i></b><span>最近成交</span><span>策略事件</span><span>风险告警</span></div>
        <div className="table-wrap"><table><thead><tr><th>时间</th><th>方向</th><th>类型</th><th>价格</th><th>数量</th><th>状态</th><th>网格层级</th><th>订单 ID</th></tr></thead>
          <tbody>{orders.slice(0, 8).map(order => <tr key={order.id}><td>{new Date(order.createdAt).toLocaleTimeString('zh-CN', { hour12: false })}</td>
            <td className={order.side === 'BUY' ? 'positive' : 'negative'}>{order.side}</td><td>{order.kind}</td><td>{format(order.price, 3)}</td>
            <td>{order.quantity}</td><td><span className={`tag ${order.status.toLowerCase()}`}>{order.status}</span></td><td>#{order.gridLevel}</td><td className="dim">{order.id.slice(0, 16)}…</td></tr>)}</tbody>
        </table>{orders.length === 0 && <Empty text="Cycle 启动后，Testnet 订单将在对账后显示" />}</div>
      </section>
    </div>
  </div>
}

function MetricCard({ title, rows, accent }: { title: string; rows: [string, string][]; accent?: boolean }) {
  return <section className="metric-card panel"><h3>{title}</h3><dl>{rows.map(([key, value], index) => <div key={key} className={accent && index === rows.length - 1 ? 'total' : ''}><dt>{key}</dt><dd className={value.startsWith('+') ? 'positive' : value.startsWith('-') ? 'negative' : ''}>{value}</dd></div>)}</dl></section>
}
export function Empty({ text }: { text: string }) { return <div className="empty"><span>◇</span>{text}</div> }
function format(value: string | number, digits = 2) { const n = +value; return Number.isFinite(n) ? n.toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits }) : String(value) }
function signed(value: string) { return +value >= 0 ? `+${format(value)}` : format(value) }
function time(value?: string) { return value ? new Date(value).toLocaleTimeString('zh-CN', { hour12: false }) : '—' }
