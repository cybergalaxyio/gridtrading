import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api } from './api'
import { AccountLabel } from './context/AccountsContext'
import { Layout, type Route } from './components/Layout'
import { Modal } from './components/Modal'
import { DashboardPage } from './pages/HyperliquidDashboardPage'
import { StrategiesPage } from './pages/StrategiesPage'
import { OrdersPage } from './pages/OrdersPage'
import { AlertsPage } from './pages/AlertsPage'
import { SettingsPage } from './pages/TestnetSettingsPage'
import { CreateStrategyPage } from './pages/CreateStrategyPage'
import type { Cycle, Strategy } from './types'
import { buildActiveCycleRegistry, cycleSymbol, selectedCycle, type CycleSelection } from './lib/activeCycles'

export default function App() {
  const [route, setRoute] = useState<Route>('dashboard')
  const [strategies, setStrategies] = useState<Strategy[]>([])
  const [editingStrategy, setEditingStrategy] = useState<Strategy | null>(null)
  const [loadedStrategyId, setLoadedStrategyId] = useState<string | null>(null)
  const [emergency, setEmergency] = useState(false)
  const [emergencyBusy, setEmergencyBusy] = useState(false)
  const [emergencyAcknowledged, setEmergencyAcknowledged] = useState(true)
  const [toast, setToast] = useState<string>('')
  const [error, setError] = useState<string>('')
  const [activeCycles, setActiveCycles] = useState<Cycle[]>([])
  const [marketSelection, setMarketSelection] = useState<CycleSelection | null>(null)
  const [emergencyTarget, setEmergencyTarget] = useState<Cycle | null>(null)

  const reloadSequence = useRef(0)
  const reload = useCallback(async (background = false) => {
    const sequence = ++reloadSequence.current
    try {
      const [latest, cycles] = await Promise.all([api.strategies(), api.activeCycles()])
      if (sequence !== reloadSequence.current) return
      setStrategies(latest)
      setActiveCycles(cycles)
      setMarketSelection(current => {
        if (current) return current
        const strategy = latest.find(item => item.activeCycle)
          ?? latest.find(item => item.defaultExecutionEnvironmentId === 'hyperliquid-testnet') ?? latest[0]
        const cycle = cycles.find(item => item.strategyId === strategy?.strategyId) ?? cycles[0]
        return {
          environmentId: cycle?.executionEnvironmentId ?? strategy?.defaultExecutionEnvironmentId ?? 'paper-local',
          accountId: cycle?.executionAccountId ?? strategy?.defaultExecutionAccountId ?? '',
          symbol: localStorage.getItem('grid.dashboardSymbol') || (cycle ? cycleSymbol(cycle) : strategy?.symbol) || 'SOLUSDT',
          cycleId: null,
        }
      })
      if (!background) setError('')
    } catch (e) {
      if (!background && sequence === reloadSequence.current) setError(e instanceof Error ? e.message : '无法连接后端')
    }
  }, [])
  useEffect(() => {
    void reload()
    const timer = setInterval(() => void reload(true), 3000)
    return () => { clearInterval(timer); reloadSequence.current++ }
  }, [reload])
  useEffect(() => { if (!toast) return; const timer = setTimeout(() => setToast(''), 3500); return () => clearTimeout(timer) }, [toast])
  const cycleRegistry = useMemo(() => buildActiveCycleRegistry(activeCycles), [activeCycles])
  const selection: CycleSelection = marketSelection ?? { environmentId: 'paper-local', accountId: '', symbol: '', cycleId: null }
  const active = selectedCycle(cycleRegistry, selection)
  const environment = selection.environmentId === 'hyperliquid-mainnet' ? 'MAINNET' : selection.environmentId === 'hyperliquid-testnet' ? 'TESTNET' : 'PAPER'
  const emergencyCycle = activeCycles.find(cycle => cycle.cycleId === emergencyTarget?.cycleId) ?? null
  const emergencyStrategy = strategies.find(strategy => strategy.strategyId === emergencyTarget?.strategyId)
  const emergencyEnvironment = emergencyTarget?.executionEnvironmentId === 'hyperliquid-mainnet' ? 'MAINNET' : emergencyTarget?.executionEnvironmentId === 'hyperliquid-testnet' ? 'TESTNET' : 'PAPER'

  function loadStrategy(strategy: Strategy) {
    const cycles = activeCycles.filter(cycle => cycle.strategyId === strategy.strategyId)
    const cycle = cycles.length === 1 ? cycles[0] : null
    setLoadedStrategyId(strategy.strategyId)
    setMarketSelection({
      environmentId: cycle?.executionEnvironmentId ?? strategy.defaultExecutionEnvironmentId,
      accountId: cycle?.executionAccountId ?? strategy.defaultExecutionAccountId,
      symbol: cycle ? cycleSymbol(cycle) : strategy.symbol,
      cycleId: cycle?.cycleId ?? null,
    })
    setRoute('dashboard')
    setToast(`${strategy.name} 已载入`)
  }

  function openEmergency() {
    if (emergencyBusy) return
    if (!active) { setToast('当前市场没有选定的活动 Cycle；如有冲突，请先选择 Cycle'); return }
    setEmergencyTarget(active)
    setError('')
    setEmergencyAcknowledged(true)
    setEmergency(true)
  }

  async function emergencyFlatten() {
    if (emergencyBusy || !emergencyAcknowledged) return
    if (!emergencyCycle) { setEmergency(false); setToast('选定 Cycle 已结束，请刷新后重试'); return }
    const cycle = emergencyCycle
    setEmergencyBusy(true)
    setEmergency(false)
    setToast('')
    setError('')
    try {
      await api.command(cycle.cycleId, 'emergency-flatten', cycle.stateVersion, true)
      setToast('紧急停止已执行：挂单已撤销，本策略净敞口已清零')
      await reload()
    } catch (e) {
      setError(e instanceof Error ? `紧急停止失败：${e.message}` : '紧急停止失败')
    } finally {
      setEmergencyBusy(false)
    }
  }

  return <Layout route={route} environment={environment} emergencyBusy={emergencyBusy} onRoute={setRoute} onEmergency={openEmergency}>
    {error && <div className="global-error" role="alert"><b>操作提示</b><span>{error}</span><button onClick={() => setError('')}>关闭</button></div>}
    {emergencyBusy && <div className="global-operation" role="status" aria-live="polite"><i />紧急停止执行中：正在撤单、同步并处理策略敞口…</div>}
    {toast && <div className="toast">✓ {toast}</div>}
    {route === 'dashboard' && !marketSelection && <div className="empty">正在加载策略与活动 Cycle…</div>}
    {route === 'dashboard' && marketSelection && <DashboardPage strategies={strategies} loadedStrategyId={loadedStrategyId} reload={reload} notify={setToast} reportError={setError} cycleRegistry={cycleRegistry} selection={selection} onSelectionChange={setMarketSelection} />}
    {route === 'strategies' && <StrategiesPage strategies={strategies} onLoad={loadStrategy} onCreate={() => { setEditingStrategy(null); setRoute('create') }} onEdit={strategy => { setEditingStrategy(strategy); setRoute('create') }} />}
    {route === 'orders' && <OrdersPage activeCycleId={active?.cycleId} reportError={setError} />}
    {route === 'alerts' && <AlertsPage notify={setToast} reportError={setError} />}
    {route === 'settings' && <SettingsPage />}
    {route === 'create' && <CreateStrategyPage initialStrategy={editingStrategy} onCancel={() => { setEditingStrategy(null); setRoute('strategies') }} onSaved={async editing => { await reload(); setEditingStrategy(null); setRoute('strategies'); setToast(editing ? '策略已更新；运行中的 Cycle 继续使用冻结参数' : '策略已创建，可预览并人工启动 Cycle') }} reportError={setError} />}
    {emergency && <Modal title="紧急停止确认" icon="alert" onClose={() => setEmergency(false)}>
      <div className="emergency-copy"><p>{emergencyEnvironment === 'MAINNET' ? 'Mainnet：撤销本 Cycle 的策略挂单，检查账户挂单后，用 reduce-only IOC 关闭所选市场的实际仓位。请确认下方账户与策略。' : '此操作将立即禁止新单，撤销本 Cycle 的策略挂单，Sync 后使用 Taker IOC 对冲本策略净敞口。'}</p>
        <dl><div><dt>账户</dt><dd><AccountLabel accountId={emergencyTarget?.executionAccountId} environmentId={emergencyTarget?.executionEnvironmentId} /></dd></div><div><dt>当前运行策略</dt><dd>{emergencyStrategy?.name ?? emergencyTarget?.strategyId ?? '—'}</dd></div><div><dt>受影响交易对</dt><dd>{emergencyTarget ? cycleSymbol(emergencyTarget) : '—'}</dd></div><div><dt>Cycle ID</dt><dd>{emergencyTarget?.cycleId}</dd></div><div><dt>执行环境</dt><dd>{emergencyTarget ? emergencyEnvironment : '—'}</dd></div></dl>
        <label className="confirm-line"><input type="checkbox" checked={emergencyAcknowledged} onChange={event => setEmergencyAcknowledged(event.target.checked)} /> 我理解紧急平仓可能产生滑点与 Taker 手续费</label>
        <div className="modal-actions"><button className="secondary" onClick={() => setEmergency(false)}>取消</button><button className="danger" disabled={!emergencyAcknowledged || !emergencyCycle} onClick={() => void emergencyFlatten()}>撤单并清零仓位</button></div>
      </div>
    </Modal>}
  </Layout>
}
