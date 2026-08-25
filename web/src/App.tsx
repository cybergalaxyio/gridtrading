import { useCallback, useEffect, useMemo, useState } from 'react'
import { api } from './api'
import { Layout, type Route } from './components/Layout'
import { Modal } from './components/Modal'
import { DashboardPage } from './pages/HyperliquidDashboardPage'
import { StrategiesPage } from './pages/StrategiesPage'
import { OrdersPage } from './pages/OrdersPage'
import { AlertsPage } from './pages/AlertsPage'
import { SettingsPage } from './pages/TestnetSettingsPage'
import { CreateStrategyPage } from './pages/CreateStrategyPage'
import type { Strategy } from './types'

export default function App() {
  const [route, setRoute] = useState<Route>('dashboard')
  const [strategies, setStrategies] = useState<Strategy[]>([])
  const [editingStrategy, setEditingStrategy] = useState<Strategy | null>(null)
  const [loadedStrategyId, setLoadedStrategyId] = useState<string | null>(null)
  const [emergency, setEmergency] = useState(false)
  const [toast, setToast] = useState<string>('')
  const [error, setError] = useState<string>('')

  const reload = useCallback(async () => {
    try { setStrategies(await api.strategies()); setError('') }
    catch (e) { setError(e instanceof Error ? e.message : '无法连接后端') }
  }, [])
  useEffect(() => { void reload() }, [reload])
  useEffect(() => { if (!toast) return; const timer = setTimeout(() => setToast(''), 3500); return () => clearTimeout(timer) }, [toast])
  const preferredStrategy = useMemo(() => strategies.find(x => x.strategyId === loadedStrategyId)
    ?? strategies.find(x => x.activeCycle)
    ?? strategies.find(x => x.exchangeAccountId !== 'acct_paper_01')
    ?? strategies[0], [strategies, loadedStrategyId])
  const active = strategies.find(x => x.activeCycle)?.activeCycle ?? null
  const environment = preferredStrategy?.exchangeAccountId && preferredStrategy.exchangeAccountId !== 'acct_paper_01' ? 'TESTNET' : 'PAPER'

  async function emergencyFlatten() {
    if (!active) { setEmergency(false); setToast('当前没有运行中的策略或残留仓位'); return }
    try {
      await api.command(active.cycleId, 'emergency-flatten', active.stateVersion, true)
      setEmergency(false); setToast('紧急停止已执行：挂单已撤销，实际净仓位已确认清零'); await reload()
    } catch (e) { setError(e instanceof Error ? e.message : '紧急停止失败') }
  }

  return <Layout route={route} environment={environment} onRoute={setRoute} onEmergency={() => setEmergency(true)}>
    {error && <div className="global-error"><b>连接提示</b><span>{error}</span><button onClick={() => void reload()}>重试</button></div>}
    {toast && <div className="toast">✓ {toast}</div>}
    {route === 'dashboard' && <DashboardPage strategies={strategies} loadedStrategyId={loadedStrategyId} reload={reload} notify={setToast} reportError={setError} />}
    {route === 'strategies' && <StrategiesPage strategies={strategies} onLoad={strategy => { setLoadedStrategyId(strategy.strategyId); setRoute('dashboard'); setToast(`${strategy.name} 已载入`) }} onCreate={() => { setEditingStrategy(null); setRoute('create') }} onEdit={strategy => { setEditingStrategy(strategy); setRoute('create') }} />}
    {route === 'orders' && <OrdersPage activeCycleId={active?.cycleId} reportError={setError} />}
    {route === 'alerts' && <AlertsPage notify={setToast} reportError={setError} />}
    {route === 'settings' && <SettingsPage />}
    {route === 'create' && <CreateStrategyPage initialStrategy={editingStrategy} onCancel={() => { setEditingStrategy(null); setRoute('strategies') }} onSaved={async editing => { await reload(); setEditingStrategy(null); setRoute('strategies'); setToast(editing ? '策略已更新；运行中的 Cycle 继续使用冻结参数' : '策略已创建，可预览并人工启动 Cycle') }} reportError={setError} />}
    {emergency && <Modal title="紧急停止确认" icon="alert" onClose={() => setEmergency(false)}>
      <div className="emergency-copy"><p>此操作将立即禁止新单，撤销所有策略挂单，Sync 后使用 Taker Reduce-only 方式清零实际净仓位。</p>
        <dl><div><dt>当前运行策略</dt><dd>{active ? '1' : '0'}</dd></div><div><dt>受影响交易对</dt><dd>{active ? 'SOL-USDC' : '—'}</dd></div><div><dt>执行环境</dt><dd>{environment}</dd></div></dl>
        <label className="confirm-line"><input type="checkbox" defaultChecked /> 我理解紧急平仓可能产生滑点与 Taker 手续费</label>
        <div className="modal-actions"><button className="secondary" onClick={() => setEmergency(false)}>取消</button><button className="danger" onClick={() => void emergencyFlatten()}>撤单并清零仓位</button></div>
      </div>
    </Modal>}
  </Layout>
}
