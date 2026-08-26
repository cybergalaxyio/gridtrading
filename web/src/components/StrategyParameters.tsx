import { useMemo, useState } from 'react'
import { buildGridPreview, GridPreview } from './GridPreview'
import type { Strategy } from '../types'

export function StrategyParameters({ strategy, tickSize, quantityStep, onClose, onOpen, onEdit }: {
  strategy: Strategy; tickSize?: string | null; quantityStep?: string | null; onClose: () => void; onOpen?: () => void; onEdit?: () => void
}) {
  const frozen = strategy.activeCycle?.frozenConfiguration
  const config = { ...strategy.configuration, ...frozen }
  const effectiveTick = frozen?.tickSize ?? tickSize ?? strategy.configuration.tickSize
  const effectiveQuantityStep = frozen?.quantityStep ?? quantityStep ?? strategy.configuration.quantityStep
  const center = strategy.activeCycle?.fixedCenterPrice
  const [tab, setTab] = useState<'parameters' | 'grid'>('parameters')
  const previewLevels = useMemo(() => buildGridPreview(config, center ?? '', effectiveTick, effectiveQuantityStep), [config, center, effectiveTick, effectiveQuantityStep])
  const groups: { title: string; rows: [string, string][] }[] = [
    { title: '基础信息', rows: [
      ['Strategy ID', strategy.strategyId], ['版本', `v${strategy.version}`], ['交易账户', strategy.exchangeAccountId],
      ['环境', strategy.exchangeAccountId === 'acct_paper_01' ? 'PAPER' : 'TESTNET'], ['交易对', `${coin(strategy.symbol)}-USDC`],
      ['Cycle 状态', strategy.activeCycle?.state ?? '未运行'], ['网格模式', gridModeLabel(config.gridMode)], ['中心模式', config.centerSuggestionMode],
      ['Tick Size', effectiveTick ?? '等待市场规则'], ['Quantity Step', effectiveQuantityStep ?? '等待市场规则'],
    ] },
    { title: '网格参数', rows: [
      ['固定中心', strategy.activeCycle?.fixedCenterPrice ?? 'Cycle 启动时确认'], ['单侧最大层数', `${config.maxLevelsPerSide} 层`],
      ['单侧工作 Entry', `${config.workingEntriesPerSide} 单`], ['Initial Gap', +config.initialGapPoints === 0 ? `自动 ½ spacing · ${pointValue(+config.gridSpacingPoints / 2, effectiveTick)}` : pointValue(config.initialGapPoints, effectiveTick)],
      ['Grid Spacing', pointValue(config.gridSpacingPoints, effectiveTick)], ['Spacing Step', pointValue(config.gridSpacingStepPoints, effectiveTick)],
      ['Take Profit', pointValue(config.takeProfitPoints, effectiveTick)],
    ] },
    { title: '资金与风控', rows: [
      ['Base Lot Size', `${config.baseLotSize} ${coin(strategy.symbol)}`], ['每层几何增长', `${config.lotSizeIncreasePercent}%`],
      ['单笔上限', `${config.maxTradeLot} ${coin(strategy.symbol)}`], ['MaxNetLot', `${config.maxNetLot} ${coin(strategy.symbol)}`], ['Basket TP', `${config.basketTakeProfitUsdt} USDC`],
      ['Basket SL', +config.basketStopLossUsdt === 0 ? '不启用' : `${config.basketStopLossUsdt} USDC`],
      ['Fee', frozen ? `Maker ${rate(config.makerFeeRate)} · Taker ${rate(config.takerFeeRate)}` : '预览时从交易账户加载'], ['退出滑点储备', `${config.estimatedExitSlippagePct}%`],
      ['计入资金费', yesNo(config.includeFunding)],
    ] },
    { title: '执行设置', rows: [
      ['Entry Post-only', yesNo(config.postOnlyEntries)],
      ['TP 执行', `普通 Limit · 非 Reduce-only · Post-only ${yesNo(config.postOnlyTakeProfits)}`],
      ['Sync 周期', `${config.reconcileIntervalSeconds} 秒`], ['行情过期阈值', `${config.marketDataStaleSeconds} 秒`],
      ['命令超时', `${config.orderCommandTimeoutSeconds} 秒`], ['最大下单频率', `${config.maxOrderFrequency}/秒`],
      ['部分成交撤单等待', +(config.partialFillCancelAfterMinutes ?? 10) === 0 ? '不启用' : `${config.partialFillCancelAfterMinutes ?? 10} 分钟`],
      ['自动重启', yesNo(config.autoRestart)],
    ] },
  ]
  return <div className="strategy-parameters">
    <div className="parameter-summary"><div><b>{strategy.name}</b><span>{coin(strategy.symbol)}-USDC · {strategy.exchangeAccountId === 'acct_paper_01' ? 'Paper Simulator' : 'Hyperliquid Testnet'}</span></div>
      <span className={`parameter-source ${frozen ? 'frozen' : ''}`}>{frozen ? 'FROZEN CYCLE' : 'STRATEGY'}</span></div>
    <p className="parameter-note">{frozen ? '当前展示运行中 Cycle 的冻结参数；策略修改只影响未来 Cycle。' : '当前展示策略实例参数；启动 Cycle 时会冻结一份独立副本。'}</p>
    <div className="parameter-tabs" role="tablist" aria-label="策略参数视图">
      <button type="button" role="tab" aria-selected={tab === 'parameters'} className={tab === 'parameters' ? 'active' : ''} onClick={() => setTab('parameters')}>参数</button>
      <button type="button" role="tab" aria-selected={tab === 'grid'} className={tab === 'grid' ? 'active' : ''} onClick={() => setTab('grid')}>Grid Preview <i>{previewLevels.length || '—'}</i></button>
    </div>
    {tab === 'parameters' && <div className="parameter-groups">{groups.map(group => <section key={group.title}><h3>{group.title}</h3><dl>{group.rows.map(([label, value]) => <div key={label}><dt>{label}</dt><dd title={value}>{value}</dd></div>)}</dl></section>)}</div>}
    {tab === 'grid' && <div className="parameter-grid-tab">
      {center && effectiveTick && effectiveQuantityStep ? <>
        <div className="parameter-grid-meta"><span>固定中心 <b>{center}</b></span><span>Tick Size <b>{effectiveTick}</b></span><span>Quantity Step <b>{effectiveQuantityStep}</b></span></div>
        <GridPreview levels={previewLevels} center={center} tickSize={effectiveTick} quantityStep={effectiveQuantityStep} symbol={coin(strategy.symbol)} />
        <p>该 Grid 由当前 Cycle 的冻结参数预先计算；价格与 Lot Size 不会随策略实例后续修改而变化。</p>
      </> : <div className="parameter-grid-empty"><b>尚无固定 Grid</b><span>策略启动前还没有确认中心价格。请通过 Edit 生成预览；启动 Cycle 后这里会显示冻结 Grid。</span></div>}
    </div>}
    <div className="parameter-actions"><button className="secondary" onClick={onClose}>关闭</button>{onEdit && <button className="secondary" onClick={onEdit}>Edit</button>}{onOpen && <button className="primary" onClick={onOpen}>打开控制台</button>}</div>
  </div>
}

function pointValue(points: string | number, tickSize?: string | null) {
  if (!tickSize) return `${points} pts`
  const value = +points * +tickSize
  return `${points} pts → ${formatDecimal(value)}`
}
function formatDecimal(value: number) { return Number.isFinite(value) ? value.toLocaleString('en-US', { maximumFractionDigits: 10 }) : '—' }
function coin(symbol: string) { return symbol.toUpperCase().replace(/[-_/]?(USDC|USDT)$/, '') }
function yesNo(value: boolean) { return value ? '是' : '否' }
function rate(value: string) { return `${value} (${(+value * 100).toFixed(3)}%)` }
function gridModeLabel(mode: Strategy['configuration']['gridMode']) {
  return mode === 'BUY_ONLY' ? 'Buy Only（只下半边买单）' : mode === 'SELL_ONLY' ? 'Sell Only（只下上半边卖单）' : 'Two-Way（双向网格）'
}
