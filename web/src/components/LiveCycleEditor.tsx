import { useRef, useState, type ReactNode } from 'react'
import { api } from '../api'
import type { Cycle, StrategyConfig } from '../types'
import { liveCyclePreview } from '../lib/liveCyclePreview'
import { GridPreview } from './GridPreview'

export function LiveCycleEditor({ cycle, config, onSaved, onCancel, children }: {
  cycle: Cycle; config: StrategyConfig; onSaved: (cycle: Cycle) => void; onCancel: () => void; children?: ReactNode
}) {
  const [baseline, setBaseline] = useState({ cycle, config })
  const [tp, setTp] = useState(config.takeProfitPoints)
  const [levels, setLevels] = useState(String(config.maxLevelsPerSide))
  const [lot, setLot] = useState(config.baseLotSize)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const attempt = useRef<{ signature: string; key: string } | null>(null)
  const current = baseline.config
  const tick = current.tickSize ?? '0'
  const step = current.quantityStep ?? '0'
  const proposed = { ...current, takeProfitPoints: tp, maxLevelsPerSide: +levels, baseLotSize: lot }
  const preview = liveCyclePreview(baseline.cycle.effectivePlan?.levels ?? baseline.cycle.frozenPlan?.levels ?? [], proposed, +tick, +step)
  const latestCycle = cycle.stateVersion >= baseline.cycle.stateVersion ? cycle : baseline.cycle
  const eligible = cycle.cycleId === baseline.cycle.cycleId && !latestCycle.isTerminal && ['RUNNING', 'PAUSED'].includes(latestCycle.state)
  const valid = eligible && Number.isFinite(+tp) && +tp > 0 && Number.isFinite(+lot) && +lot > 0 &&
    Number.isInteger(+levels) && +levels >= current.maxLevelsPerSide && +levels <= 200 && preview.length > 0 &&
    preview.every(x => x.quantity > 0 && x.price > 0)
  const changed = +tp !== +current.takeProfitPoints || +levels !== current.maxLevelsPerSide || +lot !== +current.baseLotSize

  async function save() {
    if (!valid || !changed || busy) return
    setBusy(true); setError('')
    const body = { takeProfitPoints: tp, maxLevelsPerSide: +levels, baseLotSize: lot }
    const signature = JSON.stringify([baseline.cycle.cycleId, baseline.cycle.stateVersion, body])
    if (attempt.current?.signature !== signature) attempt.current = { signature, key: crypto.randomUUID() }
    try {
      await api.updateCycleParameters(baseline.cycle.cycleId, body, baseline.cycle.stateVersion, attempt.current.key)
      onSaved(await api.cycle(baseline.cycle.cycleId))
    } catch (e) { setError(e instanceof Error ? e.message : '保存失败') }
    finally { setBusy(false) }
  }
  async function refresh() {
    setBusy(true); setError('')
    try {
      const fresh = await api.cycle(baseline.cycle.cycleId)
      const settings = fresh.effectiveConfiguration ?? fresh.frozenConfiguration
      if (!settings) throw new Error('无法读取 Cycle 参数')
      setBaseline({ cycle: fresh, config: settings }); setTp(settings.takeProfitPoints)
      setLevels(String(settings.maxLevelsPerSide)); setLot(settings.baseLotSize); attempt.current = null
    } catch (e) { setError(e instanceof Error ? e.message : '刷新失败') }
    finally { setBusy(false) }
  }
  return <section className="live-cycle-editor">
    <div className="live-cycle-editor-scroll" role="region" aria-label="当前 Cycle 编辑内容" tabIndex={0}>
    {children}
    <h3>Edit current cycle · 编辑当前 Cycle</h3>
    <p>仅影响本 Cycle。TP 和 Base Lot Size 用于保存后新建的 Entry（包括正常网格替换单）；已有挂单、部分成交订单和持仓保留原数量与 TP 距离。未来 Cycle 使用策略设置。</p>
    <div className="live-cycle-fields">
      <label>Take Profit (pts)<small>当前 {current.takeProfitPoints} pts</small><input type="number" min="0" step="any" value={tp} disabled={busy} onChange={e => setTp(e.target.value)} /><small>新距离 ≈ {Number.isFinite(+tp * +tick) ? +tp * +tick : '—'}</small></label>
      <label>单侧最大层数<small>当前 {current.maxLevelsPerSide} 层 · 仅可增加</small><input type="number" min={current.maxLevelsPerSide} max="200" step="1" value={levels} disabled={busy} onChange={e => setLevels(e.target.value)} /></label>
      <label>Base Lot Size<small>当前 {current.baseLotSize}</small><input type="number" min="0" step="any" value={lot} disabled={busy} onChange={e => setLot(e.target.value)} /><small>沿用每层增长、单笔上限及数量步长</small></label>
    </div>
    <p>预览为未来订单的计划数量；实际下单仍受 MaxNetLot 和暂停条件限制，现有挂单可能保留不同数量。</p>
    {preview.length > 0 && <GridPreview levels={preview} center={baseline.cycle.effectivePlan?.centerPrice ?? baseline.cycle.fixedCenterPrice} tickSize={tick} quantityStep={step} symbol={current.symbol.replace(/[-_/]?(USDC|USDT)$/, '')} />}
    {!eligible && <p role="alert">当前 Cycle 状态不允许修改。</p>}
    {!valid && eligible && <p role="alert">请输入正数 TP / Base Lot Size，层数须为当前层数至 200 的整数，数量须满足步长要求。</p>}
    {error && <p role="alert">{error} <button type="button" className="link" disabled={busy} onClick={() => void refresh()}>重新加载当前值</button></p>}
    </div>
    <div className="parameter-actions live-cycle-editor-actions"><button type="button" className="secondary" disabled={busy} onClick={onCancel}>取消</button><button type="button" className="primary" disabled={busy || !valid || !changed} onClick={() => void save()}>{busy ? '保存中…' : '保存到当前 Cycle'}</button></div>
  </section>
}
