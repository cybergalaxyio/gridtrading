import type { StrategyConfig } from '../types'

export type GridPreviewLevel = { side: 'SELL' | 'BUY'; level: number; price: number; quantity: number }

export function buildGridPreview(config: StrategyConfig, centerText: string, tickSize?: string | null, quantityStep?: string | null): GridPreviewLevel[] {
  const center = +centerText; const tick = +(tickSize ?? 0); const quantityIncrement = +(quantityStep ?? 0)
  if (!(center > 0) || !(tick > 0) || !(quantityIncrement > 0) || config.maxLevelsPerSide < 1) return []
  const initialGap = (+config.initialGapPoints > 0 ? +config.initialGapPoints : +config.gridSpacingPoints / 2) * tick
  let buyPrice = roundDown(center - initialGap, tick); let sellPrice = roundUp(center + initialGap, tick)
  const buys: GridPreviewLevel[] = []; const sells: GridPreviewLevel[] = []
  const mode = config.gridMode ?? 'TWO_WAY'
  const includeBuy = mode !== 'SELL_ONLY'; const includeSell = mode !== 'BUY_ONLY'
  for (let level = 0; level < config.maxLevelsPerSide; level++) {
    if (level > 0) {
      const spacing = (+config.gridSpacingPoints + level * +config.gridSpacingStepPoints) * tick
      buyPrice = roundDown(buyPrice - spacing, tick); sellPrice = roundUp(sellPrice + spacing, tick)
    }
    const theoretical = +config.baseLotSize * Math.pow(1 + +config.lotSizeIncreasePercent / 100, level)
    const capped = +config.maxTradeLot > 0 ? Math.min(theoretical, +config.maxTradeLot) : theoretical
    const quantity = roundDown(capped, quantityIncrement)
    if (includeBuy) buys.push({ side: 'BUY', level, price: buyPrice, quantity })
    if (includeSell) sells.push({ side: 'SELL', level, price: sellPrice, quantity })
  }
  return [...sells.reverse(), ...buys]
}

export function GridPreview({ levels, center, tickSize, quantityStep, symbol }: {
  levels: GridPreviewLevel[]; center: string; tickSize?: string | null; quantityStep?: string | null; symbol: string
}) {
  if (!levels.length || !tickSize || !quantityStep) return <div className="grid-level-preview empty-grid"><span>固定中心和交易规则就绪后显示 Grid Preview</span></div>
  const sells = levels.filter(level => level.side === 'SELL'); const buys = levels.filter(level => level.side === 'BUY')
  const row = (item: GridPreviewLevel) => <div key={`${item.side}-${item.level}`} className={`grid-level-row ${item.side.toLowerCase()} ${item.price <= 0 || item.quantity <= 0 ? 'invalid' : ''}`}>
    <span>{item.side[0]}{item.level}</span><b>{formatByStep(item.price, +tickSize)}</b><em>{formatByStep(item.quantity, +quantityStep)} {symbol}</em>
  </div>
  return <div className="grid-level-preview"><header><span>LEVEL</span><b>PRICE</b><em>LOT SIZE</em></header><div className="grid-level-scroll">
    {sells.map(row)}<div className="grid-center-row"><span>C</span><b>{center}</b><em>FIXED CENTER</em></div>{buys.map(row)}
  </div><footer>{levels.length} 层 · Lot Size 已按 {quantityStep} 规范化</footer></div>
}

function roundDown(value: number, step: number) { return Math.floor((value + Number.EPSILON) / step) * step }
function roundUp(value: number, step: number) { return Math.ceil((value - Number.EPSILON) / step) * step }
function formatByStep(value: number, step: number) { const digits = Math.max(0, Math.ceil(-Math.log10(step))); return Number.isFinite(value) ? value.toFixed(Math.min(10, digits)) : '—' }
