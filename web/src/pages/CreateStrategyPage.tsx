import { useEffect, useMemo, useState } from 'react'
import { api, defaultConfig } from '../api'
import { buildGridPreview, GridPreview } from '../components/GridPreview'
import { Icon } from '../components/Icon'
import type { ExchangeInstrumentRules, Preview, Strategy, StrategyConfig } from '../types'

export function CreateStrategyPage({ initialStrategy, onCancel, onSaved, reportError }: {
  initialStrategy?: Strategy | null; onCancel: () => void; onSaved: (editing: boolean) => Promise<void>; reportError: (message: string) => void
}) {
  const [config, setConfig] = useState<StrategyConfig>(() => initialStrategy ? {
    ...defaultConfig, ...initialStrategy.configuration, name: initialStrategy.name,
    exchangeAccountId: initialStrategy.exchangeAccountId, symbol: initialStrategy.symbol, workingEntriesPerSide: 1,
  } : { ...defaultConfig })
  const [environment, setEnvironment] = useState<'PAPER' | 'TESTNET'>(() => initialStrategy?.exchangeAccountId !== 'acct_paper_01' ? 'TESTNET' : 'PAPER')
  const [center, setCenter] = useState(initialStrategy?.activeCycle?.fixedCenterPrice ?? '145.250')
  const [preview, setPreview] = useState<Preview | null>(null)
  const [busy, setBusy] = useState(false)
  const [instrument, setInstrument] = useState<ExchangeInstrumentRules | null>(null)
  const [instrumentLoading, setInstrumentLoading] = useState(false)
  const [instrumentError, setInstrumentError] = useState('')
  const [testnetAccounts, setTestnetAccounts] = useState<{ accountId: string; name: string }[]>([])
  const [symbols, setSymbols] = useState<string[]>(initialStrategy ? [initialStrategy.symbol] : ['SOLUSDT'])
  const identityLocked = !!initialStrategy?.activeCycle
  useEffect(() => { void api.testnetAccounts().then(setTestnetAccounts).catch(() => setTestnetAccounts([])) }, [])
  useEffect(() => {
    if (environment === 'PAPER') { setSymbols(['SOLUSDT']); return }
    let active = true
    void api.testnetInstruments().then(result => {
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
    if (environment === 'TESTNET' && !config.exchangeAccountId && testnetAccounts[0])
      setConfig(current => ({ ...current, exchangeAccountId: testnetAccounts[0].accountId }))
  }, [environment, config.exchangeAccountId, testnetAccounts])
  useEffect(() => {
    if (!config.exchangeAccountId || !config.symbol) { setInstrument(null); setInstrumentError(''); setInstrumentLoading(false); return }
    let active = true
    setInstrument(null); setInstrumentError(''); setInstrumentLoading(true)
    void api.instrumentRules(config.exchangeAccountId, config.symbol).then(value => {
      if (active) setInstrument(value)
    }).catch(() => {
      if (active) setInstrumentError('无法加载交易规则')
    }).finally(() => {
      if (active) setInstrumentLoading(false)
    })
    return () => { active = false }
  }, [config.exchangeAccountId, config.symbol])
  const [step, setStep] = useState(1)
  const totalLevels = plannedLevelCount(config)
  const previewRows = useMemo(() => preview?.levels.filter((_, i) => i < 12) ?? [], [preview])
  const gridPreview = useMemo(() => buildGridPreview(config, center, instrument?.tickSize, instrument?.quantityStep), [config, center, instrument])

  function set<K extends keyof StrategyConfig>(key: K, value: StrategyConfig[K]) { setConfig(x => ({ ...x, [key]: value })); setPreview(null) }
  function changeEnvironment(value: 'PAPER' | 'TESTNET') {
    setEnvironment(value); setPreview(null); setInstrument(null)
    setConfig(current => ({ ...current,
      exchangeAccountId: value === 'PAPER' ? 'acct_paper_01' : testnetAccounts[0]?.accountId ?? '',
      symbol: value === 'PAPER' ? 'SOLUSDT' : current.symbol,
    }))
  }
  async function suggestCenter() {
    try { const value = config.exchangeAccountId === 'acct_paper_01' ? (await fetch('/api/v1/market-data/acct_paper_01/SOLUSDT/center-suggestion?mode=CURRENT_MID').then(r => r.json()) as { suggestedCenterPrice: string }).suggestedCenterPrice : (await api.testnetBook(config.symbol)).mid; setCenter(value) }
    catch { reportError('无法获取最新中心建议') }
  }
  async function generatePreview() {
    setBusy(true); try { setPreview(await api.previewCandidate(config, center)); setStep(4) }
    catch (e) { reportError(e instanceof Error ? e.message : '预览失败') } finally { setBusy(false) }
  }
  async function save() {
    setBusy(true); try { initialStrategy ? await api.updateStrategy(initialStrategy.strategyId, config) : await api.createStrategy(config); await onSaved(!!initialStrategy) }
    catch (e) { reportError(e instanceof Error ? e.message : '保存失败') } finally { setBusy(false) }
  }

  return <div className="create-page page padded"><div className="page-title"><div><h1>{initialStrategy ? 'Edit Strategy' : '新建策略'}</h1><p>先选择环境、交易账户和 Symbol，系统加载 Tick Size 后再配置 Points 参数。</p></div><span className="env-badge">{environment}</span></div>
    <div className="steps">{[['基本信息', 1], ['网格参数', 2], ['资金与风控', 3], ['预览确认', 4]].map(([label, number]) => <button key={number} className={step >= +number ? 'done' : ''} onClick={() => setStep(+number)}><i>{step > +number ? '✓' : number}</i><span>{label}</span></button>)}</div>
    <div className="create-layout">
      <section className="form-panel panel">
        {step === 1 && <><SectionTitle title="基本信息" subtitle={identityLocked ? '当前 Cycle 正在运行：策略参数可以修改，但环境、账户和 Symbol 已锁定。' : '选择环境、账户与 Symbol 后自动加载 Tick Size 和 Quantity Step。'} /><div className="form-grid">
          <Field label="策略名称"><input value={config.name} onChange={e => set('name', e.target.value)} /></Field>
          <Field label="交易环境"><select value={environment} disabled={identityLocked} onChange={e => changeEnvironment(e.target.value as 'PAPER' | 'TESTNET')}><option value="PAPER">PAPER</option><option value="TESTNET">HYPERLIQUID TESTNET</option></select></Field>
          <Field label="交易账户"><select value={config.exchangeAccountId} disabled={identityLocked} onChange={e => set('exchangeAccountId', e.target.value)}>{environment === 'PAPER' ? <option value="acct_paper_01">Weekend Paper</option> : <>{!testnetAccounts.some(x => x.accountId === config.exchangeAccountId) && config.exchangeAccountId && <option value={config.exchangeAccountId}>{config.exchangeAccountId}</option>}{testnetAccounts.map(account => <option key={account.accountId} value={account.accountId}>{account.name}</option>)}{testnetAccounts.length === 0 && !config.exchangeAccountId && <option value="">请先在设置中配置 Testnet 账户</option>}</>}</select></Field>
          <Field label="Symbol"><select value={config.symbol} disabled={identityLocked || symbols.length === 0} onChange={e => set('symbol', e.target.value)}>{!symbols.includes(config.symbol) && config.symbol && <option value={config.symbol}>{config.symbol}</option>}{symbols.map(symbol => <option key={symbol} value={symbol}>{displaySymbol(symbol, environment)}</option>)}</select></Field>
          <Field label="网格模式"><select value={config.gridMode ?? 'TWO_WAY'} onChange={e => set('gridMode', e.target.value as StrategyConfig['gridMode'])}><option value="BUY_ONLY">Buy Only（只下半边买单）</option><option value="SELL_ONLY">Sell Only（只下上半边卖单）</option><option value="TWO_WAY">Two-Way（双向网格）</option></select></Field>
          <Field label="Tick Size" hint={instrument?.environment ?? ''}><input value={instrumentLoading ? '加载中…' : (instrument?.tickSize ?? instrumentError) || '—'} disabled /></Field>
          <Field label="Quantity Step" hint={instrument ? `szDecimals ${instrument.sizeDecimals}` : ''}><input value={instrumentLoading ? '加载中…' : (instrument?.quantityStep ?? instrumentError) || '—'} disabled /></Field>
        </div><div className="check-row"><label><input type="checkbox" checked={config.autoRestart} onChange={e => set('autoRestart', e.target.checked)} /> Cycle 结束后自动重启</label></div></>}
        {step === 2 && <><SectionTitle title="网格参数" subtitle={`中心价格与网格距离共同定义计划；当前 Tick Size ${instrument?.tickSize ?? (instrumentLoading ? '加载中…' : '不可用')}。`} /><div className="form-grid">
          <Field label="中心建议模式"><select value={config.centerSuggestionMode} onChange={e => set('centerSuggestionMode', e.target.value as StrategyConfig['centerSuggestionMode'])}><option value="CURRENT_MID">Current Mid</option><option value="VWAP_EMA">VWAP / EMA</option><option value="MANUAL">Manual</option></select></Field>
          <Field label="确认中心价格" hint="Cycle 启动后保持固定"><div className="input-action"><input value={center} onChange={e => { setCenter(e.target.value); setPreview(null) }} /><button onClick={() => void suggestCenter()}>获取建议</button></div></Field>
        </div><div className="form-grid three grid-parameter-fields">
          <NumberField label="单侧最大层数" value={config.maxLevelsPerSide} onChange={v => set('maxLevelsPerSide', +v)} suffix="层" />
          <NumberField label="单侧工作 Entry" value={1} onChange={() => undefined} suffix="单" hint="固定维持 1 张；成交后按当前价格选择下一有效网格层" />
          <NumberField label="Initial Gap" value={config.initialGapPoints} onChange={v => set('initialGapPoints', v)} suffix="pts" hint={+config.initialGapPoints === 0 ? `自动取 Grid Spacing 一半；${pointHint(String(+config.gridSpacingPoints / 2), instrument)}` : pointHint(config.initialGapPoints, instrument)} />
          <NumberField label="Grid Spacing" value={config.gridSpacingPoints} onChange={v => set('gridSpacingPoints', v)} suffix="pts" hint={pointHint(config.gridSpacingPoints, instrument)} />
          <NumberField label={<span className="label-with-help">Spacing Step <InfoTooltip text="控制网格越往外扩张时，每一层间距增加多少 Points。Level n 与前一层的距离 = Grid Spacing + n × Spacing Step；设为 0 时所有层等距。例如 250 / 10：L1 间距 260 pts，L2 间距 270 pts。" /></span>} value={config.gridSpacingStepPoints} onChange={v => set('gridSpacingStepPoints', v)} suffix="pts" hint={pointHint(config.gridSpacingStepPoints, instrument)} />
          <NumberField label="Take Profit" value={config.takeProfitPoints} onChange={v => set('takeProfitPoints', v)} suffix="pts" hint={pointHint(config.takeProfitPoints, instrument)} />
          <NumberField label="Base Lot Size" value={config.baseLotSize} onChange={v => set('baseLotSize', v)} suffix={coin(config.symbol)} hint={quantityHint(config.baseLotSize, instrument)} />
          <NumberField label="每层几何增长" value={config.lotSizeIncreasePercent} onChange={v => set('lotSizeIncreasePercent', v)} suffix="%" hint={lastLevelQuantityHint(config, instrument)} />
          <NumberField label="单笔上限" value={config.maxTradeLot} onChange={v => set('maxTradeLot', v)} suffix={coin(config.symbol)} hint="0 = 不启用" />
        </div><div className="check-row"><label><input type="checkbox" checked={config.postOnlyEntries} onChange={e => set('postOnlyEntries', e.target.checked)} /> Entry 只做 Maker (Post-only)</label>
          <label><input type="checkbox" checked={config.postOnlyTakeProfits} onChange={e => set('postOnlyTakeProfits', e.target.checked)} /> TP 普通 Limit，尽量 Post-only</label></div></>}
        {step === 3 && <><SectionTitle title="资金与强制风控" subtitle="MaxNetLot 对当前仓位与所有活动买卖订单做最坏情形容量预留。" /><div className="form-grid three">
          <NumberField label="MaxNetLot 硬上限" value={config.maxNetLot} onChange={v => set('maxNetLot', v)} suffix="SOL" />
          <NumberField label="Basket 止盈" value={config.basketTakeProfitUsdt} onChange={v => set('basketTakeProfitUsdt', v)} suffix="USDC" />
          <NumberField label="Basket 止损" value={config.basketStopLossUsdt} onChange={v => set('basketStopLossUsdt', v)} suffix="USDC" hint="0 = 不启用" />
          <NumberField label={<HelpLabel label="退出滑点储备" text="估算立即退出或强制平仓时可能产生的不利价格偏移。系统按当前仓位名义价值 × 此百分比预留退出成本，并从 Basket 清算 PnL 中扣除；它不会修改挂单价格。" />} value={config.estimatedExitSlippagePct} onChange={v => set('estimatedExitSlippagePct', v)} suffix="%" />
          <NumberField label={<HelpLabel label="行情过期阈值" text="允许用于交易判断的行情最大年龄。行情更新时间超过该秒数时，应停止创建新敞口，避免使用陈旧价格下单；已有减仓与安全退出仍可继续。" />} value={config.marketDataStaleSeconds} onChange={v => set('marketDataStaleSeconds', +v)} suffix="秒" />
          <NumberField label="Sync 周期" value={config.reconcileIntervalSeconds} onChange={v => set('reconcileIntervalSeconds', +v)} suffix="秒" />
          <NumberField label={<HelpLabel label="命令超时" text="发送下单、撤单或平仓命令后等待交易所确认的最长时间。超时后不能假定命令失败，系统需要通过 Sync 查询最终状态，避免重复下单。" />} value={config.orderCommandTimeoutSeconds} onChange={v => set('orderCommandTimeoutSeconds', +v)} suffix="秒" />
          <NumberField label={<HelpLabel label="最大下单频率" text="限制策略每秒最多发送多少条下单指令，用于削峰并降低触发交易所限频的风险。数值越低，批量铺设网格所需时间越长。" />} value={config.maxOrderFrequency} onChange={v => set('maxOrderFrequency', +v)} suffix="单/秒" />
        </div><div className="check-row"><label><input type="checkbox" checked={config.includeFunding} onChange={e => set('includeFunding', e.target.checked)} /> Basket PnL 计入资金费</label><span>Maker / Taker Fee 从交易账户自动加载</span></div><div className="safety-note"><Icon name="shield" /><p><b>强制安全规则不可关闭</b><span>陈旧行情、Sync 未完成、仓位/残留单不为零、MaxNetLot 超限都会阻止启动或新建敞口。</span></p></div></>}
        {step === 4 && <><SectionTitle title="预览确认" subtitle="Preview 不会创建订单；启动时后端仍会重新校验全部前置条件。" />{preview ? <>
          <div className="preview-stats"><div><small>价格区间</small><b>{(+preview.outermostBuyPrice).toFixed(3)} — {(+preview.outermostSellPrice).toFixed(3)}</b></div><div><small>计划挂单层数</small><b>{totalLevels}</b></div><div><small>单侧计划数量</small><b>{preview.maximumPlannedQuantityPerSide} SOL</b></div><div><small>单侧名义价值</small><b>~ {(+preview.maximumPlannedNotionalPerSide).toFixed(2)} USDC</b></div></div>
          <div className="preview-table table-wrap"><table><thead><tr><th>方向</th><th>Level</th><th>Entry Price</th><th>Lot Size</th><th>Notional</th><th>Cumulative Qty</th></tr></thead><tbody>{previewRows.map(x => <tr key={`${x.side}${x.levelIndex}`}><td className={x.side === 'BUY' ? 'positive' : 'negative'}>{x.side}</td><td>#{x.levelIndex}</td><td>{(+x.entryPrice).toFixed(3)}</td><td>{x.plannedQuantity}</td><td>{(+x.orderNotional).toFixed(2)}</td><td>{x.cumulativeQuantity}</td></tr>)}</tbody></table></div>
          <p className="preview-expiry">此预览于 {new Date(preview.expiresAt).toLocaleTimeString('zh-CN', { hour12: false })} 过期 · 当前仅保存策略，不会自动启动</p>
        </> : <div className="preview-placeholder"><span>▦</span><h3>尚未生成完整网格计划</h3><p>完成参数填写后点击“生成预览”，后端将按 Tick Size、Quantity Step 和最小名义价值校验全部层级。</p><button className="primary" onClick={() => void generatePreview()} disabled={busy}>{busy ? '计算中…' : '生成预览'}</button></div>}</>}
        <footer className="form-actions"><button className="secondary" onClick={step === 1 ? onCancel : () => setStep(x => x - 1)}>{step === 1 ? '取消' : '上一步'}</button><span />
          {step < 3 && <button className="primary" onClick={() => setStep(x => x + 1)}>下一步</button>}
          {step === 3 && <button className="primary" onClick={() => void generatePreview()} disabled={busy}>{busy ? '计算中…' : '生成完整预览'}</button>}
          {step === 4 && preview && <button className="primary" onClick={() => void save()} disabled={busy}>{busy ? '保存中…' : initialStrategy ? '保存修改' : '确认并保存策略'}</button>}
        </footer>
      </section>
      <aside className="preview-side panel"><h3>配置摘要</h3><dl><div><dt>交易对</dt><dd>{config.symbol}</dd></div><div><dt>Tick Size</dt><dd>{instrument?.tickSize ?? '—'}</dd></div><div><dt>Quantity Step</dt><dd>{instrument?.quantityStep ?? '—'}</dd></div><div><dt>固定中心</dt><dd>{center}</dd></div><div><dt>计划层数</dt><dd>{totalLevels}</dd></div><div><dt>基础数量</dt><dd>{config.baseLotSize} SOL</dd></div><div><dt>MaxNetLot</dt><dd>{config.maxNetLot} SOL</dd></div><div><dt>Basket TP / SL</dt><dd>{config.basketTakeProfitUsdt} / {config.basketStopLossUsdt}</dd></div></dl>
        <GridPreview levels={gridPreview} center={center} tickSize={instrument?.tickSize} quantityStep={instrument?.quantityStep} symbol={coin(config.symbol)} />
        <p className="readonly-note">交易规则来自后端 Instrument Metadata，前端不能覆盖 Tick Size、Quantity Step 或最小订单限制。</p></aside>
    </div>
  </div>
}

function SectionTitle({ title, subtitle }: { title: string; subtitle: string }) { return <header className="section-title"><h2>{title}</h2><p>{subtitle}</p></header> }
function Field({ label, hint, children }: { label: React.ReactNode; hint?: string; children: React.ReactNode }) { return <label className="field"><span>{label}{hint && <small>{hint}</small>}</span>{children}</label> }
function NumberField({ label, value, suffix, hint, onChange }: { label: React.ReactNode; value: string | number; suffix: string; hint?: string; onChange: (value: string) => void }) { return <Field label={label} hint={hint}><div className="number-input"><input type="number" min="0" step="any" value={value} onChange={e => onChange(e.target.value)} /><span>{suffix}</span></div></Field> }
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
function displaySymbol(symbol: string, _environment: 'PAPER' | 'TESTNET') { return `${coin(symbol)}-USDC` }
function plannedLevelCount(config: StrategyConfig) { return config.maxLevelsPerSide * ((config.gridMode ?? 'TWO_WAY') === 'TWO_WAY' ? 2 : 1) }
