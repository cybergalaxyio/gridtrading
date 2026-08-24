import { useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import { Icon } from '../components/Icon'
import { Empty } from './DashboardPage'
import type { Order } from '../types'

export function OrdersPage({ activeCycleId, reportError }: { activeCycleId?: string; reportError: (message: string) => void }) {
  const [orders, setOrders] = useState<Order[]>([])
  const [tab, setTab] = useState('当前挂单')
  const [side, setSide] = useState('全部')
  async function load() { try { setOrders(await api.orders(activeCycleId)) } catch (e) { reportError(e instanceof Error ? e.message : '订单加载失败') } }
  useEffect(() => { void load() }, [activeCycleId])
  const rows = useMemo(() => orders.filter(x => (side === '全部' || x.side === side) && (tab !== '当前挂单' || ['NEW', 'PARTIALLY_FILLED'].includes(x.status))), [orders, side, tab])
  return <div className="page orders-page"><div className="subnav"><div>{['当前挂单', '历史订单', '成交记录'].map(x => <button className={tab === x ? 'active' : ''} key={x} onClick={() => setTab(x)}>{x}</button>)}</div></div>
    <div className="filterbar"><label>策略:<select><option>{activeCycleId ? activeCycleId.slice(0, 18) : '全部策略'}</option></select></label><label>标的:<select><option>SOLUSDT</option><option>全部</option></select></label>
      <label>方向:<select value={side} onChange={e => setSide(e.target.value)}><option>全部</option><option>BUY</option><option>SELL</option></select></label><label>状态:<select><option>全部</option><option>NEW</option><option>PARTIALLY_FILLED</option></select></label>
      <button className="secondary icon-button" onClick={() => void load()}><Icon name="refresh" size={16} /> 刷新</button></div>
    <section className="orders-list panel"><div className="table-wrap"><table><thead><tr><th>时间</th><th>Cycle</th><th>Symbol</th><th>方向</th><th>类型</th><th>价格</th><th>数量</th><th>Filled%</th><th>状态</th><th>Order ID</th></tr></thead>
      <tbody>{rows.map(x => <tr key={x.id}><td>{new Date(x.createdAt).toLocaleString('zh-CN', { hour12: false })}</td><td className="dim">{x.cycleId.slice(-10)}</td><td><b>{x.symbol.replace('USDT', '/USDT')}</b></td>
        <td className={x.side === 'BUY' ? 'positive' : 'negative'}>{x.side}</td><td>{x.kind}</td><td className="mono">{(+x.price).toFixed(3)}</td><td className="mono">{x.quantity}</td>
        <td><div className="fill-cell"><span>{Math.round(+x.filledQuantity / +x.quantity * 100)}%</span><i><b style={{ width: `${+x.filledQuantity / +x.quantity * 100}%` }} /></i></div></td><td><span className={`tag ${x.status.toLowerCase()}`}>{x.status}</span></td><td className="dim">{x.id.slice(0, 15)}…</td></tr>)}</tbody></table>
      {rows.length === 0 && <Empty text="当前筛选条件下没有订单" />}</div><footer className="list-footer">共 {rows.length} 条记录 <span>所有资金相关状态以后端与交易所对账结果为准</span></footer></section>
  </div>
}
