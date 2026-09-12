import { useEffect, useMemo, useState } from 'react'
import { api, defaultConfig } from '../api'
import { buildGridPreview, GridPreview } from '../components/GridPreview'
import { Icon } from '../components/Icon'
import type { ExecutionAccount, ExecutionEnvironment, ExchangeInstrumentRules, Preview, Strategy, StrategyConfig } from '../types'

export function CreateStrategyPage({ initialStrategy, onCancel, onSaved, reportError }: {
  initialStrategy?: Strategy | null; onCancel: () => void; onSaved: (editing: boolean) => Promise<void>; reportError: (message: string) => void
}) {
  const [config, setConfig] = useState<StrategyConfig>(() => initialStrategy ? {
    ...defaultConfig, ...initialStrategy.configuration, name: initialStrategy.name,
    strategyType: initialStrategy.strategyType, defaultExecutionEnvironmentId: initialStrategy.defaultExecutionEnvironmentId,
    defaultExecutionAccountId: initialStrategy.defaultExecutionAccountId, exchangeAccountId: initialStrategy.defaultExecutionAccountId,
    symbol: initialStrategy.symbol, workingEntriesPerSide: 1,
  } : { ...defaultConfig })
  const [environment, setEnvironment] = useState(() => initialStrategy?.defaultExecutionEnvironmentId ?? defaultConfig.defaultExecutionEnvironmentId)
  const [center, setCenter] = useState(initialStrategy?.configuration.manualCenterPrice ?? '')
  const [preview, setPreview] = useState<Preview | null>(null)
  const [busy, setBusy] = useState(false)
  const [instrument, setInstrument] = useState<ExchangeInstrumentRules | null>(null)
  const [instrumentLoading, setInstrumentLoading] = useState(false)
  const [instrumentError, setInstrumentError] = useState('')
  const [environments, setEnvironments] = useState<ExecutionEnvironment[]>([])
  const [accounts, setAccounts] = useState<ExecutionAccount[]>([])
  const [symbols, setSymbols] = useState<string[]>(initialStrategy ? [initialStrategy.symbol] : ['SOLUSDT'])
  useEffect(() => { void api.executionEnvironments().then(setEnvironments).catch(() => setEnvironments([])) }, [])
  useEffect(() => {
    let active = true
    void api.executionAccounts(environment).then(items => {
      if (!active) return
      setAccounts(items)
      setConfig(current => {
        const accountId = items.some(x => x.id === current.defaultExecutionAccountId)
          ? current.defaultExecutionAccountId : items[0]?.id ?? ''
        return { ...current, defaultExecutionEnvironmentId: environment,
          defaultExecutionAccountId: accountId, exchangeAccountId: accountId }
      })
    }).catch(() => { if (active) setAccounts([]) })
    return () => { active = false }
  }, [environment])
  useEffect(() => {
    if (environment === 'paper-local') { setSymbols(['SOLUSDT']); return }
    let active = true
    void api.hyperliquidInstruments(environment).then(result => {
      if (!active) return
      const available = result.universe.filter(item => !item.isDelisted && item.symbol).map(item => item.symbol)
      setSymbols(available)
      setConfig(current => {
        const match = available.find(item => sameCoin(item, current.symbol))
        return match && match !== current.symbol ? { ...current, symbol: match } : !current.symbol && available[0] ? { ...current, symbol: available[0] } : current
      })
    }).catch(() => setSymbols(current => current.length ? current : ['SOL']))
    return () => { active = false }
  }, [environment])
  useEffect(() => {
    if (!config.defaultExecutionAccountId || !config.symbol) { setInstrument(null); setInstrumentError(''); setInstrumentLoading(false); return }
    let active = true
    setInstrument(null); setInstrumentError(''); setInstrumentLoading(true)
    void api.instrumentRules(config.defaultExecutionAccountId, config.symbol).then(value => {
      if (active) setInstrument(value)
    }).catch(() => {
      if (active) setInstrumentError('无法加载交易规则')
    }).finally(() => {
      if (active) setInstrumentLoading(false)
    })
    return () => { active = false }
  }, [config.defaultExecutionAccountId, config.symbol])
  const [step, setStep] = useState(1)
  const totalLevels = plannedLevelCount(config)
  const previewRows = useMemo(() => preview?.levels.filter((_, i) => i < 12) ?? [], [preview])
  const isManual = config.centerSuggestionMode === 'MANUAL'
  const displayCenter = isManual ? center : preview?.confirmedCenterPrice ?? ''
  const gridPreview = useMemo(() => isManual
    ? buildGridPreview(config, center, instrument?.tickSize, instrument?.quantityStep)
    : (preview?.levels.map(level => ({ side: level.side as 'BUY' | 'SELL', level: level.levelIndex, price: +level.entryPrice, quantity: +level.plannedQuantity })) ?? []),
    [config, center, instrument, isManual, preview])

  function set<K extends keyof StrategyConfig>(key: K, value: StrategyConfig[K]) { setConfig(x => ({ ...x, [key]: value })); setPreview(null) }
  function toggleEntryFillLimit(enabled: boolean) {
    setConfig(current => ({
      ...current, entryFillLimitEnabled: enabled,
      entryFillWindowMinutes: !enabled && !isPositiveInt32(current.entryFillWindowMinutes)
        ? defaultConfig.entryFillWindowMinutes : current.entryFillWindowMinutes,
      maxEntryFillsPerSide: !enabled && !isPositiveInt32(current.maxEntryFillsPerSide)
        ? defaultConfig.maxEntryFillsPerSide : current.maxEntryFillsPerSide,
    }))
    setPreview(null)
  }
  function changeEnvironment(value: string) {
    setEnvironment(value); setPreview(null); setInstrument(null); setAccounts([])
    setConfig(current => ({ ...current, defaultExecutionEnvironmentId: value,
      defaultExecutionAccountId: '', exchangeAccountId: '', symbol: value === 'paper-local' ? 'SOLUSDT' : current.symbol }))
  }
  function changeAccount(value: string) {
    setConfig(current => ({ ...current, defaultExecutionAccountId: value, exchangeAccountId: value })); setPreview(null)
  }
  function validateForm() {
    if (!config.name.trim()) { setStep(1); reportError('请填写策略名称'); return false }
    if (!config.defaultExecutionAccountId) { setStep(1); reportError('请选择执行账户'); return false }
    if (isManual && (!center.trim() || !Number.isFinite(Number(center)) || Number(center) <= 0)) {
      setStep(2); reportError('Manual 模式需要填写大于 0 的中心价格'); return false
    }
    const numericFields: [keyof StrategyConfig, string, number][] = [
      ['maxLevelsPerSide', '单侧最大层数', 2], ['initialGapPoints', 'Initial Gap', 2],
      ['gridSpacingPoints', 'Grid Spacing', 2], ['gridSpacingStepPoints', 'Spacing Step', 2],
      ['takeProfitPoints', 'Take Profit', 2], ['baseLotSize', 'Base Lot Size', 3],
      ['lotSizeIncreasePercent', '每层几何增长', 3], ['maxTradeLot', '单笔上限', 3],
      ['maxNetLot', 'MaxNetLot', 3], ['basketTakeProfitUsdt', 'Basket 止盈', 3],
      ['basketStopLossUsdt', 'Basket 止损', 3], ['faultExposureThresholdUsdt', '风险暂停敞口阈值', 3],
      ['estimatedExitSlippagePct', '退出滑点储备', 3], ['marketDataStaleSeconds', '行情过期阈值', 3],
      ['reconcileIntervalSeconds', 'Sync 周期', 3], ['orderCommandTimeoutSeconds', '命令超时', 3],
      ['maxOrderFrequency', '最大下单频率', 3], ['partialFillCancelAfterMinutes', '部分成交撤单等待', 3],
    ]
    for (const [key, label, fieldStep] of numericFields) {
      if (!String(config[key]).trim() || !Number.isFinite(Number(config[key]))) {
        setStep(fieldStep); reportError(`请填写有效数字：${label}（如不启用可选项，请明确填写 0）`); return false
      }
    }
    for (const [value, label] of [
      [config.entryFillWindowMinutes, 'Lookback Window (min)'],
      [config.maxEntryFillsPerSide, 'Max Filled Entries per Side'],
    ] as const) {
      if (!isPositiveInt32(value)) {
        setStep(3); reportError(`${label} must be a positive integer (maximum 2147483647).`); return false
      }
    }
    reportError('')
    return true
  }
  async function generatePreview() {
    if (!validateForm()) return
    setBusy(true); try { setPreview(await api.previewCandidate(config, center)); setStep(4) }
    catch (e) { reportError(e instanceof Error ? e.message : '预览失败') } finally { setBusy(false) }
  }
  async function save() {
    if (!validateForm()) return
    setBusy(true); try {
      const savedConfig = { ...config, manualCenterPrice: isManual ? center : null }
      initialStrategy ? await api.updateStrategy(initialStrategy.strategyId, savedConfig) : await api.createStrategy(savedConfig); await onSaved(!!initialStrategy)
    }
    catch (e) { reportError(e instanceof Error ? e.message : '保存失败') } finally { setBusy(false) }
  }

  return <div className="create-page page padded"><div className="page-title"><div><h1>{initialStrategy ? 'Edit Strategy' : '新建策略'}</h1><p>先定义 Strategy，再选择默认执行环境、账户和 Symbol；Tick Size 与 Quantity Step 会一起加载。</p></div><span className="env-badge">{environment}</span></div>
    <div className="steps">{[['基本信息', 1], ['网格参数', 2], ['资金与风控', 3], ['预览确认', 4]].map(([label, number]) => <button key={number} className={step >= +number ? 'done' : ''} onClick={() => setStep(+number)}><i>{step > +number ? '✓' : number}</i><span>{label}</span></button>)}</div>
    <div className="create-layout">
      <section className="form-panel panel">
        {step === 1 && <><SectionTitle title="基本信息" subtitle={initialStrategy?.activeCycle ? '当前 Cycle 继续使用冻结的环境、账户和参数；这里的修改只影响未来 Cycle。' : '选择环境、账户与 Symbol 后自动加载 Tick Size 和 Quantity Step。'} /><div className="form-grid">
          <Field label="策略名称"><input value={config.name} onChange={e => set('name', e.target.value)} /></Field>
          <Field label="默认执行环境"><select value={environment} onChange={e => changeEnvironment(e.target.value)}>{environments.map(item => <option key={item.id} value={item.id}>{item.displayName} · {item.network}</option>)}</select></Field>
          <Field label="默认执行账户"><select value={config.defaultExecutionAccountId} onChange={e => changeAccount(e.target.value)}>{accounts.map(account => <option key={account.id} value={account.id}>{account.displayName}</option>)}{accounts.length === 0 && <option value="">该环境尚未配置账户</option>}</select></Field>
          <Field label="Symbol"><select value={config.symbol} disabled={symbols.length === 0} onChange={e => set('symbol', e.target.value)}>{!symbols.includes(config.symbol) && config.symbol && <option value={config.symbol}>{config.symbol}</option>}{symbols.map(symbol => <option key={symbol} value={symbol}>{displaySymbol(symbol, environment)}</option>)}</select></Field>
          <Field label="网格模式"><select value={config.gridMode ?? 'TWO_WAY'} onChange={e => set('gridMode', e.target.value as StrategyConfig['gridMode'])}><option value="BUY_ONLY">Buy Only（只下半边买单）</option><option value="SELL_ONLY">Sell Only（只下上半边卖单）</option><option value="TWO_WAY">Two-Way（双向网格）</option></select></Field>
          <Field label="Tick Size" hint={instrument?.environment ?? ''}><input value={instrumentLoading ? '加载中…' : (instrument?.tickSize ?? instrumentError) || '—'} disabled /></Field>
          <Field label="Quantity Step" hint={instrument ? `szDecimals ${instrument.sizeDecimals}` : ''}><input value={instrumentLoading ? '加载中…' : (instrument?.quantityStep ?? instrumentError) || '—'} disabled /></Field>
        </div><div className="check-row"><label><input type="checkbox" checked={config.autoRestart} onChange={e => set('autoRestart', e.target.checked)} /> 止盈 / 止损关闭后自动开始下一轮</label><span>同一账户和环境；Current Mid 使用新一轮实时 Bid / Ask，Manual 使用保存的中心价。手动关闭、紧急平仓和故障不自动重启。</span></div></>}
        {step === 2 && <><SectionTitle title="网格参数" subtitle={`中心价格与网格距离共同定义计划；当前 Tick Size ${instrument?.tickSize ?? (instrumentLoading ? '加载中…' : '不可用')}。`} /><div className="form-grid">
          <Field label="中心模式"><select value={config.centerSuggestionMode} onChange={e => set('centerSuggestionMode', e.target.value as StrategyConfig['centerSuggestionMode'])}><option value="CURRENT_MID">Current Mid</option><option value="MANUAL">Manual</option></select></Field>
          {isManual ? <Field label="手动中心价格" hint="必填；保存后用于启动 Cycle"><input type="number" min="0" step="any" required value={center} onChange={e => { setCenter(e.target.value); setPreview(null) }} /></Field>
            : <Field label="启动时使用实时 Bid / Ask" hint="无需输入价格；启动后 Grid 保持固定"><span>Buy0 = Bid − Initial Gap ÷ 2；Sell0 = Ask + Initial Gap ÷ 2（按 Tick Size 换算）</span></Field>}
        </div><div className="form-grid three grid-parameter-fields">
          <NumberField label="单侧最大层数" value={config.maxLevelsPerSide} onChange={v => set('maxLevelsPerSide', +v)} suffix="层" />
          <NumberField label="单侧工作 Entry" value={1} onChange={() => undefined} suffix="单" hint="固定维持 1 张；成交后按当前价格选择下一有效网格层" />
          <NumberField label="Initial Gap" value={config.initialGapPoints} onChange={v => set('initialGapPoints', v)} suffix="pts" hint={`${+config.initialGapPoints === 0 ? '0 = 使用 Grid Spacing；' : ''}每侧偏移一半：${pointHint(String((+config.initialGapPoints > 0 ? +config.initialGapPoints : +config.gridSpacingPoints) / 2), instrument)}`} />
          <NumberField label="Grid Spacing" value={config.gridSpacingPoints} onChange={v => set('gridSpacingPoints', v)} suffix="pts" hint={pointHint(config.gridSpacingPoints, instrument)} />
          <NumberField label={<span className="label-with-help">Spacing Step <InfoTooltip text="控制网格越往外扩张时，每一层间距增加多少 Points。Level n 与前一层的距离 = Grid Spacing + n × Spacing Step；设为 0 时所有层等距。例如 250 / 10：L1 间距 260 pts，L2 间距 270 pts。" /></span>} value={config.gridSpacingStepPoints} onChange={v => set('gridSpacingStepPoints', v)} suffix="pts" hint={pointHint(config.gridSpacingStepPoints, instrument)} />
          <NumberField label="Take Profit" value={config.takeProfitPoints} onChange={v => set('takeProfitPoints', v)} suffix="pts" hint={pointHint(config.takeProfitPoints, instrument)} />
        </div><div className="check-row"><label><input type="checkbox" checked={config.postOnlyEntries} onChange={e => set('postOnlyEntries', e.target.checked)} /> Entry 只做 Maker (Post-only)</label>
          <label><input type="checkbox" checked={config.postOnlyTakeProfits} onChange={e => set('postOnlyTakeProfits', e.target.checked)} /> TP 普通 Limit，尽量 Post-only</label></div></>}
        {step === 3 && <><SectionTitle title="资金与强制风控" subtitle="MaxNetLot 对当前仓位与所有活动买卖订单做最坏情形容量预留。" /><div className="form-grid three">
          <NumberField label="Base Lot Size" value={config.baseLotSize} onChange={v => set('baseLotSize', v)} suffix={coin(config.symbol)} hint={quantityHint(config.baseLotSize, instrument)} />
          <NumberField label="每层几何增长" value={config.lotSizeIncreasePercent} onChange={v => set('lotSizeIncreasePercent', v)} suffix="%" hint={lastLevelQuantityHint(config, instrument)} />
          <NumberField label="单笔上限" value={config.maxTradeLot} onChange={v => set('maxTradeLot', v)} suffix={coin(config.symbol)} hint="0 = 不启用" />
          <NumberField label="MaxNetLot 硬上限" value={config.maxNetLot} onChange={v => set('maxNetLot', v)} suffix={coin(config.symbol)} />
          <NumberField label="Basket 止盈" value={config.basketTakeProfitUsdt} onChange={v => set('basketTakeProfitUsdt', v)} suffix="USDC" />
          <NumberField label="Basket 止损" value={config.basketStopLossUsdt} onChange={v => set('basketStopLossUsdt', v)} suffix="USDC" hint="0 = 不启用" />
          <NumberField label={<HelpLabel label="风险暂停敞口阈值" text="按未保护数量 × TP 参考价计算敞口；超过阈值时暂停开仓并撤销 Entry 未成交余量，继续维护 TP。连续两次对账低于阈值后解除风险暂停，人工暂停仍保留。设为 0 时需敞口归零才恢复。" />} value={config.faultExposureThresholdUsdt} onChange={v => set('faultExposureThresholdUsdt', v)} suffix="USD" />
          <NumberField label={<HelpLabel label="退出滑点储备" text="估算立即退出或强制平仓时可能产生的不利价格偏移。系统按当前仓位名义价值 × 此百分比预留退出成本，并从 Basket 清算 PnL 中扣除；它不会修改挂单价格。" />} value={config.estimatedExitSlippagePct} onChange={v => set('estimatedExitSlippagePct', v)} suffix="%" />
          <NumberField label={<HelpLabel label="行情过期阈值" text="允许用于交易判断的行情最大年龄。行情更新时间超过该秒数时，应停止创建新敞口，避免使用陈旧价格下单；已有减仓与安全退出仍可继续。" />} value={config.marketDataStaleSeconds} onChange={v => set('marketDataStaleSeconds', +v)} suffix="秒" />
          <NumberField label="Sync 周期" value={config.reconcileIntervalSeconds} onChange={v => set('reconcileIntervalSeconds', +v)} suffix="秒" />
          <NumberField label={<HelpLabel label="命令超时" text="发送下单、撤单或平仓命令后等待交易所确认的最长时间。超时后不能假定命令失败，系统需要通过 Sync 查询最终状态，避免重复下单。" />} value={config.orderCommandTimeoutSeconds} onChange={v => set('orderCommandTimeoutSeconds', +v)} suffix="秒" />
          <NumberField label={<HelpLabel label="最大下单频率" text="限制策略每秒最多发送多少条下单指令，用于削峰并降低触发交易所限频的风险。数值越低，批量铺设网格所需时间越长。" />} value={config.maxOrderFrequency} onChange={v => set('maxOrderFrequency', +v)} suffix="单/秒" />
          <NumberField label={<HelpLabel label="部分成交撤单等待" text="Entry 首次部分成交后开始计时；超过该时间仍未全部成交时，撤销剩余数量。已成交部分对应的 TP 会保留，并按当前价格补充新的 Entry。0 = 不启用。" />} value={config.partialFillCancelAfterMinutes} onChange={v => set('partialFillCancelAfterMinutes', +v)} suffix="分钟" />
        </div>
        <div className="check-row"><label><input type="checkbox" checked={config.entryFillLimitEnabled} aria-controls="entry-fill-limit-fields" onChange={event => toggleEntryFillLimit(event.target.checked)} /> Enable Entry Fill Limit</label></div>
        {config.entryFillLimitEnabled && <div id="entry-fill-limit-fields">
          <div className="form-grid">
            <NumberField label="Lookback Window (min)" value={config.entryFillWindowMinutes} min={1} step={1} onChange={value => set('entryFillWindowMinutes', +value)} suffix="min" />
            <NumberField label="Max Filled Entries per Side" value={config.maxEntryFillsPerSide} min={1} step={1} onChange={value => set('maxEntryFillsPerSide', +value)} suffix="orders" />
          </div>
          <p className="entry-fill-limit-hint">Counts fully filled Entry orders only, separately for Buy and Sell, including prior cycles. At the limit, same-side Entry remainders are cancelled; TP orders remain working.</p>
        </div>}
        <div className="check-row"><label><input type="checkbox" checked={config.includeFunding} onChange={e => set('includeFunding', e.target.checked)} /> Basket PnL 计入资金费</label><span>Maker / Taker Fee 从交易账户自动加载</span></div><div className="safety-note"><Icon name="shield" /><p><b>强制安全规则不可关闭</b><span>陈旧行情、Sync 未完成、仓位/残留单不为零、MaxNetLot 超限都会阻止启动或新建敞口。</span></p></div></>}
        {step === 4 && <><SectionTitle title="预览确认" subtitle="Preview 不会创建订单；启动时后端仍会重新校验全部前置条件。" />{preview ? <>
          <div className="preview-stats"><div><small>价格区间</small><b>{(+preview.outermostBuyPrice).toFixed(3)} — {(+preview.outermostSellPrice).toFixed(3)}</b></div><div><small>计划挂单层数</small><b>{totalLevels}</b></div><div><small>单侧计划数量</small><b>{preview.maximumPlannedQuantityPerSide} SOL</b></div><div><small>单侧名义价值</small><b>~ {(+preview.maximumPlannedNotionalPerSide).toFixed(2)} USDC</b></div></div>
          <div className="preview-table table-wrap"><table><thead><tr><th>方向</th><th>Level</th><th>Entry Price</th><th>Lot Size</th><th>Notional</th><th>Cumulative Qty</th></tr></thead><tbody>{previewRows.map(x => <tr key={`${x.side}${x.levelIndex}`}><td className={x.side === 'BUY' ? 'positive' : 'negative'}>{x.side}</td><td>#{x.levelIndex}</td><td>{(+x.entryPrice).toFixed(3)}</td><td>{x.plannedQuantity}</td><td>{(+x.orderNotional).toFixed(2)}</td><td>{x.cumulativeQuantity}</td></tr>)}</tbody></table></div>
          <p className="preview-expiry">{!isManual && 'Current Mid 预览仅供参考；启动时按最新 Bid / Ask 重新生成。'}此预览于 {new Date(preview.expiresAt).toLocaleTimeString('zh-CN', { hour12: false })} 过期 · 当前仅保存策略，不会自动启动</p>
        </> : <div className="preview-placeholder"><span>▦</span><h3>尚未生成完整网格计划</h3><p>完成参数填写后点击“生成预览”，后端将按 Tick Size、Quantity Step 和最小名义价值校验全部层级。</p><button className="primary" onClick={() => void generatePreview()} disabled={busy}>{busy ? '计算中…' : '生成预览'}</button></div>}</>}
        <footer className="form-actions"><button className="secondary" onClick={step === 1 ? onCancel : () => setStep(x => x - 1)}>{step === 1 ? '取消' : '上一步'}</button><span />
          {step < 3 && <button className="primary" onClick={() => setStep(x => x + 1)}>下一步</button>}
          {step === 3 && <button className="primary" onClick={() => void generatePreview()} disabled={busy}>{busy ? '计算中…' : '生成完整预览'}</button>}
          {step === 4 && preview && <button className="primary" onClick={() => void save()} disabled={busy}>{busy ? '保存中…' : initialStrategy ? '保存修改' : '确认并保存策略'}</button>}
        </footer>
      </section>
      <aside className="preview-side panel"><h3>配置摘要</h3><dl><div><dt>交易对</dt><dd>{config.symbol}</dd></div><div><dt>Tick Size</dt><dd>{instrument?.tickSize ?? '—'}</dd></div><div><dt>Quantity Step</dt><dd>{instrument?.quantityStep ?? '—'}</dd></div><div><dt>{isManual ? '手动中心' : '预览中间价'}</dt><dd>{displayCenter || (isManual ? '请输入中心价格' : '启动时自动获取')}</dd></div><div><dt>计划层数</dt><dd>{totalLevels}</dd></div><div><dt>基础数量</dt><dd>{config.baseLotSize} SOL</dd></div><div><dt>MaxNetLot</dt><dd>{config.maxNetLot} SOL</dd></div><div><dt>Basket TP / SL</dt><dd>{config.basketTakeProfitUsdt} / {config.basketStopLossUsdt}</dd></div></dl>
        <GridPreview levels={gridPreview} center={displayCenter} indicative={!isManual} tickSize={instrument?.tickSize} quantityStep={instrument?.quantityStep} symbol={coin(config.symbol)} />
        <p className="readonly-note">交易规则来自后端 Instrument Metadata，前端不能覆盖 Tick Size、Quantity Step 或最小订单限制。</p></aside>
    </div>
  </div>
}

function isPositiveInt32(value: number) { return Number.isInteger(value) && value >= 1 && value <= 2147483647 }

function SectionTitle({ title, subtitle }: { title: string; subtitle: string }) { return <header className="section-title"><h2>{title}</h2><p>{subtitle}</p></header> }
function Field({ label, hint, children }: { label: React.ReactNode; hint?: string; children: React.ReactNode }) { return <label className="field"><span>{label}{hint && <small>{hint}</small>}</span>{children}</label> }
function NumberField({ label, value, suffix, hint, onChange, min = 0, step = 'any' }: { label: React.ReactNode; value: string | number; suffix: string; hint?: string; onChange: (value: string) => void; min?: number; step?: number | string }) { return <Field label={label} hint={hint}><div className="number-input"><input type="number" min={min} step={step} value={value} onChange={e => onChange(e.target.value)} /><span>{suffix}</span></div></Field> }
function InfoTooltip({ text }: { text: string }) { return <span className="info-tooltip" tabIndex={0} aria-label={text}>i<span role="tooltip">{text}</span></span> }
function HelpLabel({ label, text }: { label: string; text: string }) { return <span className="label-with-help">{label} <InfoTooltip text={text} /></span> }
function pointHint(points: string, instrument: ExchangeInstrumentRules | null) { return instrument ? `${points} × ${instrument.tickSize} = ${formatDecimal(+points * +instrument.tickSize)}` : '等待 Tick Size' }
function quantityHint(quantity: string, instrument: ExchangeInstrumentRules | null) { return instrument ? `按 ${instrument.quantityStep} 向下规范化 → ${normalizeQuantity(+quantity, +instrument.quantityStep)}` : '等待 Quantity Step' }
function lastLevelQuantityHint(config: StrategyConfig, instrument: ExchangeInstrumentRules | null) {
  if (!instrument) return '等待 Quantity Step'
  const level = Math.max(0, config.maxLevelsPerSide - 1)
  const theoretical = +config.baseLotSize * Math.pow(1 + +config.lotSizeIncreasePercent / 100, level)
  const capped = +config.maxTradeLot > 0 ? Math.min(theoretical, +config.maxTradeLot) : theoretical
  return `Level ${level} → ${normalizeQuantity(capped, +instrument.quantityStep)} ${coin(config.symbol)}（已规范化）`
}
function normalizeQuantity(value: number, step: number) { return step > 0 ? formatDecimal(Math.floor((value + Number.EPSILON) / step) * step) : '—' }
function formatDecimal(value: number) { return Number.isFinite(value) ? value.toLocaleString('en-US', { maximumFractionDigits: 10 }) : '—' }
function coin(symbol: string) { return symbol.toUpperCase().replace(/[-_/]?(USDC|USDT)$/, '') || 'Qty' }
function sameCoin(left: string, right: string) { return coin(left) === coin(right) }
function displaySymbol(symbol: string, _environment: string) { return `${coin(symbol)}-USDC` }
function plannedLevelCount(config: StrategyConfig) { return config.maxLevelsPerSide * ((config.gridMode ?? 'TWO_WAY') === 'TWO_WAY' ? 2 : 1) }
