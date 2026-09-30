import type { GridLevel, StrategyConfig } from '../types'

// Preview only. The server validates exact decimal quantities before committing.
export function liveCyclePreview(current: GridLevel[], config: StrategyConfig, tick: number, step: number) {
  if (!Number.isInteger(config.maxLevelsPerSide) || config.maxLevelsPerSide < 1 || config.maxLevelsPerSide > 200 ||
      !(tick > 0) || !(step > 0) || !(+config.baseLotSize > 0)) return []
  const result: { side: 'BUY' | 'SELL'; level: number; price: number; quantity: number }[] = []
  for (const side of ['BUY', 'SELL'] as const) {
    const saved = current.filter(x => x.side === side).sort((a, b) => a.levelIndex - b.levelIndex)
    if (!saved.length) continue
    let price = +saved[0].entryPrice
    for (let level = 0; level < config.maxLevelsPerSide; level++) {
      const existing = saved.find(x => x.levelIndex === level)
      if (existing) price = +existing.entryPrice
      else {
        const spacing = (+config.gridSpacingPoints + level * +config.gridSpacingStepPoints) * tick
        const ticks = (price + (side === 'BUY' ? -spacing : spacing)) / tick
        price = (side === 'BUY' ? Math.floor(ticks + 1e-9) : Math.ceil(ticks - 1e-9)) * tick
      }
      const raw = +config.baseLotSize * (1 + +config.lotSizeIncreasePercent / 100) ** level
      const capped = +config.maxTradeLot > 0 ? Math.min(raw, +config.maxTradeLot) : raw
      const quantity = Math.floor(capped / step + 1e-9) * step
      if (!Number.isFinite(price) || !Number.isFinite(quantity)) return []
      result.push({ side, level, price, quantity })
    }
  }
  return result.sort((a, b) => a.level - b.level || a.side.localeCompare(b.side))
}
