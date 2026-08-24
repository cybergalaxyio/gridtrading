import { useEffect, useMemo, useState } from 'react'
import { api } from '../api'
import { Icon } from '../components/Icon'
import type { Alert } from '../types'

export function AlertsPage({ notify, reportError }: { notify: (message: string) => void; reportError: (message: string) => void }) {
  const [alerts, setAlerts] = useState<Alert[]>([])
  const [filter, setFilter] = useState('全部')
  async function load() { try { setAlerts(await api.alerts()) } catch (e) { reportError(e instanceof Error ? e.message : '告警加载失败') } }
  useEffect(() => { void load() }, [])
  const rows = useMemo(() => alerts.filter(x => filter === '全部' || x.severity === filter), [alerts, filter])
  async function ack(id: string) { try { await api.acknowledge(id); notify('告警已确认并写入审计记录'); await load() } catch (e) { reportError(e instanceof Error ? e.message : '确认失败') } }
  return <div className="page padded alerts-page"><div className="page-title"><div><h1>风险与告警控制中心</h1><p>实时监控系统状态、持仓敞口及策略异常告警。</p></div><div><button className="secondary"><Icon name="download" size={16} /> 导出日志</button></div></div>
    <div className="health-row"><Health title="交易所连接" value="正常" detail="Paper REST / Event Stream" tone="green" /><Health title="行情数据延迟" value="<10 ms" detail="市场快照新鲜" tone="green" />
      <Health title="订单状态同步" value="刚刚" detail="Reconciliation IN_SYNC" tone="green" /><Health title="风险评分" value="24 / 100" detail="低风险" tone="amber" /></div>
    <section className="panel alert-log"><header><h2>系统告警日志 <small>ALERT LOG</small></h2><div>{['全部', 'CRITICAL', 'WARNING', 'INFO'].map(x => <button key={x} className={filter === x ? 'active' : ''} onClick={() => setFilter(x)}>{x}</button>)}</div></header>
      <div className="table-wrap"><table><thead><tr><th>级别</th><th>时间</th><th>错误码</th><th>原因</th><th>系统行为</th><th>状态 / 操作</th></tr></thead><tbody>{rows.map(x => <tr key={x.id}><td><span className={`severity ${x.severity.toLowerCase()}`}><i />{x.severity}</span></td>
        <td className="mono">{new Date(x.createdAt).toLocaleTimeString('zh-CN', { hour12: false })}</td><td className="mono dim">{x.code}</td><td><b>{x.message}</b></td><td>{action(x.code)}</td>
        <td>{x.acknowledged ? <span className="acknowledged"><Icon name="check" size={14} /> 已确认</span> : <button className="link" onClick={() => void ack(x.id)}>确认告警</button>}</td></tr>)}</tbody></table></div>
      <footer className="list-footer">显示 {rows.length} 条告警 <span>Advisory 颜色不自动改变 Cycle；Mandatory Safety 始终生效</span></footer></section>
  </div>
}
function Health({ title, value, detail, tone }: { title: string; value: string; detail: string; tone: string }) { return <section className="health-card panel"><span className={`health-dot ${tone}`} /><div><small>{title}</small><b>{value}</b><em>{detail}</em></div></section> }
function action(code: string) { return code.includes('STALE') ? '阻止创建新敞口' : code.includes('RECONCILIATION') ? '无需操作' : '建议人工复核策略参数' }
