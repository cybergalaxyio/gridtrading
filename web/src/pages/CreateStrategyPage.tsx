import { useMemo, useState } from 'react'
import { api, defaultConfig } from '../api'
import { Icon } from '../components/Icon'
import type { Preview, StrategyConfig } from '../types'

export function CreateStrategyPage({ onCancel, onCreated, reportError }: {
  onCancel: () => void; onCreated: () => Promise<void>; reportError: (message: string) => void
}) {
  const [config, setConfig] = useState<StrategyConfig>({ ...defaultConfig })
  const [center, setCenter] = useState('145.250')
  const [preview, setPreview] = useState<Preview | null>(null)
  const [busy, setBusy] = useState(false)
  const [step, setStep] = useState(1)
  const totalLevels = config.maxLevelsPerSide * 2
  const previewRows = useMemo(() => preview?.levels.filter((_, i) => i < 12) ?? [], [preview])

  function set<K extends keyof StrategyConfig>(key: K, value: StrategyConfig[K]) { setConfig(x => ({ ...x, [key]: value })); setPreview(null) }
  async function suggestCenter() {
    try { const result = await fetch('/api/v1/market-data/acct_paper_01/SOLUSDT/center-suggestion?mode=CURRENT_MID').then(r => r.json()) as { suggestedCenterPrice: string }; setCenter(result.suggestedCenterPrice) }
    catch { reportError('无法获取最新中心建议') }
  }
  async function generatePreview() {
    setBusy(true); try { setPreview(await api.previewCandidate(config, center)); setStep(4) }
    catch (e) { reportError(e instanceof Error ? e.message : '预览失败') } finally { setBusy(false) }
  }
  async function save() {
    setBusy(true); try { await api.createStrategy(config); await onCreated() }
    catch (e) { reportError(e instanceof Error ? e.message : '保存失败') } finally { setBusy(false) }
  }

  return <div className="create-page page padded"><div className="page-title"><div><h1>新建策略</h1><p>配置 Weekend Grid V1 的固定中心、网格参数和强制风险边界。</p></div><span className="env-badge">PAPER</span></div>
    <div className="steps">{[['基本信息', 1], ['网格参数', 2], ['资金与风控', 3], ['预览确认', 4]].map(([label, number]) => <button key={number} className={step >= +number ? 'done' : ''} onClick={() => setStep(+number)}><i>{step > +number ? '✓' : number}</i><span>{label}</span></button>)}</div>
    <div className="create-layout">
      <section className="form-panel panel">
        {step === 1 && <><SectionTitle title="基本信息" subtitle="V1 仅允许 Paper / Replay / Testnet，且固定为单向净持仓。" /><div className="form-grid">
          <Field label="策略名称"><input value={config.name} onChange={e => set('name', e.target.value)} /></Field>
          <Field label="交易所账户"><select value={config.exchangeAccountId} onChange={e => set('exchangeAccountId', e.target.value)}><option value="acct_paper_01">Weekend Paper · PAPER</option><option value="acct_hyperliquid_testnet">Hyperliquid · TESTNET（只读）</option></select></Field>
          <Field label="交易对"><select value={config.symbol} onChange={e => set('symbol', e.target.value)}><option>SOLUSDT</option></select></Field>
          <Field label="持仓模式"><input value="ONE_WAY（V1 固定）" disabled /></Field>
          <Field label="中心建议模式"><select value={config.centerSuggestionMode} onChange={e => set('centerSuggestionMode', e.target.value as StrategyConfig['centerSuggestionMode'])}><option value="CURRENT_MID">Current Mid</option><option value="VWAP_EMA">VWAP / EMA</option><option value="MANUAL">Manual</option></select></Field>
          <Field label="确认中心价格" hint="Cycle 启动后保持固定"><div className="input-action"><input value={center} onChange={e => { setCenter(e.target.value); setPreview(null) }} /><button onClick={() => void suggestCenter()}>获取建议</button></div></Field>
        </div></>}
        {step === 2 && <><SectionTitle title="网格参数" subtitle="全部距离使用交易所 Points；SOLUSDT Tick Size 由后端读取为 0.001。" /><div className="form-grid three">
          <NumberField label="单侧最大层数" value={config.maxLevelsPerSide} onChange={v => set('maxLevelsPerSide', +v)} suffix="层" />
          <NumberField label="单侧工作 Entry" value={config.workingEntriesPerSide} onChange={v => set('workingEntriesPerSide', +v)} suffix="单" />
          <NumberField label="Initial Gap" value={config.initialGapPoints} onChange={v => set('initialGapPoints', v)} suffix="pts" hint="0 = 间距的一半" />
          <NumberField label="Grid Spacing" value={config.gridSpacingPoints} onChange={v => set('gridSpacingPoints', v)} suffix="pts" />
          <NumberField label="Spacing Step" value={config.gridSpacingStepPoints} onChange={v => set('gridSpacingStepPoints', v)} suffix="pts" />
          <NumberField label="Take Profit" value={config.takeProfitPoints} onChange={v => set('takeProfitPoints', v)} suffix="pts" />
          <NumberField label="Base Lot Size" value={config.baseLotSize} onChange={v => set('baseLotSize', v)} suffix="SOL" />
          <NumberField label="每层几何增长" value={config.lotSizeIncreasePercent} onChange={v => set('lotSizeIncreasePercent', v)} suffix="%" />
          <NumberField label="单笔上限" value={config.maxTradeLot} onChange={v => set('maxTradeLot', v)} suffix="SOL" hint="0 = 不启用" />
        </div><div className="check-row"><label><input type="checkbox" checked={config.postOnlyEntries} onChange={e => set('postOnlyEntries', e.target.checked)} /> Entry 只做 Maker (Post-only)</label><label><input type="checkbox" checked={config.postOnlyTakeProfits} onChange={e => set('postOnlyTakeProfits', e.target.checked)} /> TP 尽量 Post-only</label></div></>}
        {step === 3 && <><SectionTitle title="资金与强制风控" subtitle="MaxNetLot 对当前仓位与所有活动买卖订单做最坏情形容量预留。" /><div className="form-grid three">
          <NumberField label="MaxNetLot 硬上限" value={config.maxNetLot} onChange={v => set('maxNetLot', v)} suffix="SOL" />
          <NumberField label="Basket 止盈" value={config.basketTakeProfitUsdt} onChange={v => set('basketTakeProfitUsdt', v)} suffix="USDT" />
          <NumberField label="Basket 止损" value={config.basketStopLossUsdt} onChange={v => set('basketStopLossUsdt', v)} suffix="USDT" />
          <NumberField label="Maker Fee" value={config.makerFeeRate} onChange={v => set('makerFeeRate', v)} suffix="rate" />
          <NumberField label="Taker Fee" value={config.takerFeeRate} onChange={v => set('takerFeeRate', v)} suffix="rate" />
          <NumberField label="退出滑点储备" value={config.estimatedExitSlippagePct} onChange={v => set('estimatedExitSlippagePct', v)} suffix="%" />
          <NumberField label="行情过期阈值" value={config.marketDataStaleSeconds} onChange={v => set('marketDataStaleSeconds', +v)} suffix="秒" />
          <NumberField label="对账周期" value={config.reconcileIntervalSeconds} onChange={v => set('reconcileIntervalSeconds', +v)} suffix="秒" />
          <NumberField label="命令超时" value={config.orderCommandTimeoutSeconds} onChange={v => set('orderCommandTimeoutSeconds', +v)} suffix="秒" />
        </div><div className="safety-note"><Icon name="shield" /><p><b>强制安全规则不可关闭</b><span>陈旧行情、对账未完成、仓位/残留单不为零、MaxNetLot 超限都会阻止启动或新建敞口。</span></p></div></>}
        {step === 4 && <><SectionTitle title="预览确认" subtitle="Preview 不会创建订单；启动时后端仍会重新校验全部前置条件。" />{preview ? <>
          <div className="preview-stats"><div><small>价格区间</small><b>{(+preview.outermostBuyPrice).toFixed(3)} — {(+preview.outermostSellPrice).toFixed(3)}</b></div><div><small>计划挂单层数</small><b>{totalLevels}</b></div><div><small>单侧计划数量</small><b>{preview.maximumPlannedQuantityPerSide} SOL</b></div><div><small>单侧名义价值</small><b>~ {(+preview.maximumPlannedNotionalPerSide).toFixed(2)} USDT</b></div></div>
          <div className="preview-table table-wrap"><table><thead><tr><th>方向</th><th>Level</th><th>Entry Price</th><th>Lot Size</th><th>Notional</th><th>Cumulative Qty</th></tr></thead><tbody>{previewRows.map(x => <tr key={`${x.side}${x.levelIndex}`}><td className={x.side === 'BUY' ? 'positive' : 'negative'}>{x.side}</td><td>#{x.levelIndex}</td><td>{(+x.entryPrice).toFixed(3)}</td><td>{x.plannedQuantity}</td><td>{(+x.orderNotional).toFixed(2)}</td><td>{x.cumulativeQuantity}</td></tr>)}</tbody></table></div>
          <p className="preview-expiry">此预览于 {new Date(preview.expiresAt).toLocaleTimeString('zh-CN', { hour12: false })} 过期 · 当前仅保存模板，不会自动启动</p>
        </> : <div className="preview-placeholder"><span>▦</span><h3>尚未生成完整网格计划</h3><p>完成参数填写后点击“生成预览”，后端将按 Tick Size、Quantity Step 和最小名义价值校验全部层级。</p><button className="primary" onClick={() => void generatePreview()} disabled={busy}>{busy ? '计算中…' : '生成预览'}</button></div>}</>}
        <footer className="form-actions"><button className="secondary" onClick={step === 1 ? onCancel : () => setStep(x => x - 1)}>{step === 1 ? '取消' : '上一步'}</button><span />
          {step < 3 && <button className="primary" onClick={() => setStep(x => x + 1)}>下一步</button>}
          {step === 3 && <button className="primary" onClick={() => void generatePreview()} disabled={busy}>{busy ? '计算中…' : '生成完整预览'}</button>}
          {step === 4 && preview && <button className="primary" onClick={() => void save()} disabled={busy}>{busy ? '保存中…' : '确认并保存策略模板'}</button>}
        </footer>
      </section>
      <aside className="preview-side panel"><h3>配置摘要</h3><dl><div><dt>交易对</dt><dd>{config.symbol}</dd></div><div><dt>固定中心</dt><dd>{center}</dd></div><div><dt>计划层数</dt><dd>{totalLevels}</dd></div><div><dt>基础数量</dt><dd>{config.baseLotSize} SOL</dd></div><div><dt>MaxNetLot</dt><dd>{config.maxNetLot} SOL</dd></div><div><dt>Basket TP / SL</dt><dd>{config.basketTakeProfitUsdt} / {config.basketStopLossUsdt}</dd></div></dl>
        <div className="mini-grid"><span style={{ top: '10%' }} /><span style={{ top: '27%' }} /><span className="center" style={{ top: '50%' }} /><span style={{ top: '73%' }} /><span style={{ top: '90%' }} /><i>SELL GRID</i><b>FIXED CENTER</b><em>BUY GRID</em></div>
        <p className="readonly-note">交易规则来自后端 Instrument Metadata，前端不能覆盖 Tick Size、Quantity Step 或最小订单限制。</p></aside>
    </div>
  </div>
}

function SectionTitle({ title, subtitle }: { title: string; subtitle: string }) { return <header className="section-title"><h2>{title}</h2><p>{subtitle}</p></header> }
function Field({ label, hint, children }: { label: string; hint?: string; children: React.ReactNode }) { return <label className="field"><span>{label}{hint && <small>{hint}</small>}</span>{children}</label> }
function NumberField({ label, value, suffix, hint, onChange }: { label: string; value: string | number; suffix: string; hint?: string; onChange: (value: string) => void }) { return <Field label={label} hint={hint}><div className="number-input"><input type="number" min="0" step="any" value={value} onChange={e => onChange(e.target.value)} /><span>{suffix}</span></div></Field> }
