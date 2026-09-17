import type { Candle } from '../types'

export type ChartIndicator = { time: number; atr: number | null; middle: number | null; upper: number | null; lower: number | null }
export const intervalSeconds: Record<string, number> = { '1m': 60, '5m': 300, '15m': 900, '1h': 3600, '4h': 14400, '1d': 86400 }

// Seed ATR with the first 14 true ranges, using high-low for the first bar.
// Keep these definitions in parity with Domain/Advisory/TechnicalIndicators.cs.
export function chartIndicators(candles: Candle[]): ChartIndicator[] {
  let atr = 0
  return candles.map((bar, i) => {
    const high = +bar.high, low = +bar.low
    const tr = i === 0 ? high - low : Math.max(high - low, Math.abs(high - +candles[i - 1].close), Math.abs(low - +candles[i - 1].close))
    if (i < 14) { atr += tr; if (i === 13) atr /= 14 }
    else atr = (atr * 13 + tr) / 14
    let middle: number | null = null, upper: number | null = null, lower: number | null = null
    if (i >= 19) {
      const closes = candles.slice(i - 19, i + 1).map(x => +x.close)
      middle = closes.reduce((a, b) => a + b, 0) / 20
      const deviation = Math.sqrt(closes.reduce((sum, close) => sum + (close - middle!) ** 2, 0) / 20)
      upper = middle + 2 * deviation; lower = middle - 2 * deviation
    }
    return { time: bar.time, atr: i >= 13 ? atr : null, middle, upper, lower }
  })
}

export function displayedCandles(candles: Candle[], livePrice: number | undefined, seconds: number, now = Date.now()): Candle[] {
  const valid = candles.filter(c => Number.isFinite(c.time) && [+c.open, +c.high, +c.low, +c.close, +c.volume].every(Number.isFinite)
    && +c.low > 0 && +c.high >= Math.max(+c.open, +c.close) && +c.low <= Math.min(+c.open, +c.close))
  const rows = [...new Map(valid.map(c => [c.time, c])).values()].sort((a, b) => a.time - b.time)
  const last = rows.at(-1)
  if (last && livePrice !== undefined && Number.isFinite(livePrice) && livePrice > 0 && last.time === Math.floor(now / 1000 / seconds) * seconds)
    rows[rows.length - 1] = { ...last, high: String(Math.max(+last.high, livePrice)), low: String(Math.min(+last.low, livePrice)), close: String(livePrice) }
  return rows
}
