import { useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import { TradingChart } from '../components/TradingChart'
import { Icon } from '../components/Icon'
import type { Candle, Order, Snapshot, Strategy } from '../types'

export function DashboardPage({ strategies, reload, notify, reportError }: {
  strategies: Strategy[]; reload: () => Promise<void>; notify: (message: string) => void; reportError: (message: string) => void
}) {
  const strategy = strategies.find(x => x.activeCycle) ?? strategies[0]
  const cycle = strategy?.activeCycle
  const [candles, setCandles] = useState<Candle[]>([])
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null)
  const [orders, setOrders] = useState<Order[]>([])
  const [busy, setBusy] = useState(false)
  const [testnetMid, setTestnetMid] = useState<string | null>(null)

  async function refresh() {
    try {
      const [chart, snap, orderRows] = await Promise.all([
        api.candles(), cycle ? api.snapshot(cycle.cycleId) : Promise.resolve(null), cycle ? api.orders(cycle.cycleId) : api.orders(),
      ])
      setCandles(chart); setSnapshot(snap); setOrders(orderRows)
      if (strategy && strategy.exchangeAccountId !== "acct_paper_01") { const book = await api.testnetBook(strategy.symbol); setTestnetMid(book.mid) } else setTestnetMid(null)
    } catch (e) { reportError(e instanceof Error ? e.message : '控制台数据加载失败') }
  }
  useEffect(() => { void refresh(); const timer = setInterval(() => void refresh(), 5000); return () => clearInterval(timer) }, [cycle?.cycleId, strategy?.strategyId, strategy?.exchangeAccountId, strategy?.symbol])

  const levelPrices = useMemo(() => orders.filter(x => x.status === 'NEW').map(x => +x.price), [orders])

  async function startCycle() {
    if (!strategy) return
    setBusy(true)
    try {
      const isTestnet = strategy.exchangeAccountId !== 'acct_paper_01'
      const quote = isTestnet ? await api.testnetBook(strategy.symbol) : await fetch('/api/v1/market-data/acct_paper_01/SOLUSDT/snapshot').then(r => r.json()) as { mid: string }
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
  const state = cycle?.state ?? 'WAITING_FOR_OPERATOR'
  return <div className="dashboard-page">
    <section className="instrument-bar">
      <div><h1>SOL-USDC 永续 <span className="mono">{format(mid, 3)}</span> <em>+0.82%</em></h1>
        <p>Strategy: {strategy?.name ?? '尚未创建'} <b className={`state ${state.toLowerCase()}`}>{state}</b> <span>{strategy?.exchangeAccountId === 'acct_paper_01' ? 'Paper' : 'Hyperliquid Testnet'} · {gridModeLabel(strategy)} · 1x | 上次同步 {snapshot ? '刚刚' : '—'}</span></p></div>
      <div className="control-buttons">
        {!cycle && <button className="primary" disabled={!strategy || busy} onClick={() => void startCycle()}>{busy ? '启动中…' : '确认预览并开启'}</button>}
        {cycle?.state === 'RUNNING' && <button className="primary" disabled={busy} onClick={() => void command('pause-entries', 'Entry 已暂停，已有 TP 保留')}>暂停 Entry</button>}
        {cycle?.state === 'PAUSED' && <button className="primary" disabled={busy} onClick={() => void command('resume-entries', '已按固定中心恢复 Entry')}>继续</button>}
        {cycle && <button className="secondary" disabled={busy} onClick={() => void command('reconcile', 'Sync 完成')}>Sync</button>}
        {cycle && <button className="danger-outline" disabled={busy} onClick={() => void command('close', 'Cycle 已有序关闭并清零仓位')}>关闭 Cycle</button>}
      </div>
    </section>
    <div className="dashboard-grid">
      <section className="chart-panel panel">
        <div className="chart-tools"><b>1m</b><span>5m</span><span>15m</span><span>1h</span><span>4h</span><i /><Icon name="settings" size={16} /></div>
        <TradingChart candles={candles} levels={levelPrices} center={snapshot ? +snapshot.cycle.fixedCenterPrice : undefined} />
      </section>
      <aside className="metric-stack">
        <MetricCard title="策略摘要" rows={[
          ['固定中心', snapshot ? format(snapshot.cycle.fixedCenterPrice, 3) : '—'], ['计划层数', strategy ? `${plannedLevelCount(strategy)} 层` : '—'],
          ['Entry / TP', snapshot ? `${snapshot.orders.activeEntryCount} / ${snapshot.orders.activeTakeProfitCount}` : '—'], ['状态版本', cycle ? `#${cycle.stateVersion}` : '—'],
        ]} />
        <MetricCard title="账户与持仓" rows={[
          ['权益', strategy?.exchangeAccountId === 'acct_paper_01' ? '13,420.50 USDC' : '见 Testnet 设置'], ['可用余额', strategy?.exchangeAccountId === 'acct_paper_01' ? '8,420.00 USDC' : '以交易所为准'], ['净仓位', snapshot ? `${signed(snapshot.position.actualNetQuantity)} SOL` : '0 SOL'],
          ['MaxNetLot 使用', snapshot ? `${format(snapshot.position.absoluteMaxNetLotUsagePct, 1)}%` : '0%'], ['Sync', snapshot?.health.reconciliation ?? 'IN_SYNC'],
        ]} />
        <MetricCard title="Basket 清算盈亏" rows={[
          ['浮动', snapshot ? signed(snapshot.basketPnl.unrealisedAtExecutablePrice) : '+0.00'], ['已实现', snapshot ? signed(snapshot.basketPnl.realisedCyclePnl) : '+0.00'],
          ['费用', snapshot ? `-${format(snapshot.basketPnl.paidFees, 3)}` : '-0.00'], ['净清算盈亏', snapshot ? signed(snapshot.basketPnl.liquidationPnl) : '+0.00'],
        ]} accent />
      </aside>
      <section className="orders-panel panel">
        <div className="tabs"><b>当前挂单 <i>{orders.filter(x => x.status === 'NEW').length}</i></b><span>最近成交</span><span>策略事件</span><span>风险告警</span></div>
        <div className="table-wrap"><table><thead><tr><th>时间</th><th>方向</th><th>类型</th><th>价格</th><th>数量</th><th>状态</th><th>网格层级</th><th>订单 ID</th></tr></thead>
          <tbody>{orders.slice(0, 8).map(order => <tr key={order.id}><td>{new Date(order.createdAt).toLocaleTimeString('zh-CN', { hour12: false })}</td>
            <td className={order.side === 'BUY' ? 'positive' : 'negative'}>{order.side}</td><td>{order.kind}</td><td>{format(order.price, 3)}</td>
            <td>{order.quantity}</td><td><span className={`tag ${order.status.toLowerCase()}`}>{order.status}</span></td><td>#{order.gridLevel}</td><td className="dim">{order.id.slice(0, 16)}…</td></tr>)}</tbody>
        </table>{orders.length === 0 && <Empty text="Cycle 启动后，滚动 Entry 将显示在这里" />}</div>
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
function plannedLevelCount(strategy: Strategy) { const config = strategy.activeCycle?.frozenConfiguration ?? strategy.configuration; return config.maxLevelsPerSide * ((config.gridMode ?? 'TWO_WAY') === 'TWO_WAY' ? 2 : 1) }
function gridModeLabel(strategy?: Strategy) { const mode = (strategy?.activeCycle?.frozenConfiguration ?? strategy?.configuration)?.gridMode ?? 'TWO_WAY'; return mode === 'BUY_ONLY' ? 'Buy Only' : mode === 'SELL_ONLY' ? 'Sell Only' : 'Two-Way' }
