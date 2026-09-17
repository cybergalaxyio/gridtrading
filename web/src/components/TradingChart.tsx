import { useEffect, useMemo, useRef, useState } from 'react'
import { CandlestickSeries, ColorType, createChart, HistogramSeries, LineSeries, LineStyle, type IChartApi, type IPriceLine, type ISeriesApi, type Time } from 'lightweight-charts'
import type { Candle } from '../types'
import { chartIndicators, displayedCandles, intervalSeconds } from '../lib/chartIndicators'

type EntryOrderLine = { price: number; side: string }
type ChartState = { chart: IChartApi; candles: ISeriesApi<'Candlestick'>; volume: ISeriesApi<'Histogram'>; bands: ISeriesApi<'Line'>[]; atr: ISeriesApi<'Line'> | null; orders: IPriceLine[] }

export function TradingChart({ candles, entryOrders = [], livePrice, contextKey = '', timeframe = '1m', showBB = true, showATR = true }: {
  candles: Candle[]; entryOrders?: EntryOrderLine[]; livePrice?: number; contextKey?: string; timeframe?: string; showBB?: boolean; showATR?: boolean
}) {
  const host = useRef<HTMLDivElement>(null)
  const state = useRef<ChartState | null>(null)
  const fittedContext = useRef<string | null>(null)
  const previousRows = useRef<Candle[]>([])
  const [hoverTime, setHoverTime] = useState<number | null>(null)
  const seconds = intervalSeconds[timeframe] ?? 60
  const rows = useMemo(() => displayedCandles(candles, livePrice, seconds), [candles, livePrice, seconds])
  const points = useMemo(() => chartIndicators(rows), [rows])
  const hovered = points.find(x => x.time === hoverTime)
  const point = hovered ?? points.at(-1)
  const provisional = point ? point.time + seconds > Date.now() / 1000 : false

  useEffect(() => {
    if (!host.current) return
    const chart = createChart(host.current, {
      autoSize: true,
      layout: { background: { type: ColorType.Solid, color: '#08121b' }, textColor: '#8391a2', attributionLogo: true },
      grid: { vertLines: { color: '#17232d' }, horzLines: { color: '#17232d' } },
      crosshair: { vertLine: { color: '#3b5264' }, horzLine: { color: '#3b5264' } },
      rightPriceScale: { borderColor: '#2a3947', scaleMargins: { top: .13, bottom: .24 } },
      timeScale: { borderColor: '#2a3947', timeVisible: true, secondsVisible: false },
    })
    const series = chart.addSeries(CandlestickSeries, {
      upColor: '#25d777', downColor: '#f2575b', borderVisible: false, wickUpColor: '#25d777', wickDownColor: '#f2575b',
      priceLineColor: '#f3c64d', priceLineStyle: LineStyle.Dashed,
    })
    const volume = chart.addSeries(HistogramSeries, { priceFormat: { type: 'volume' }, priceScaleId: 'volume' })
    volume.priceScale().applyOptions({ scaleMargins: { top: .82, bottom: 0 } })
    state.current = { chart, candles: series, volume, bands: [], atr: null, orders: [] }
    fittedContext.current = null
    chart.subscribeCrosshairMove(event => setHoverTime(typeof event.time === 'number' ? event.time : null))
    const resizePanes = () => {
      const panes = chart.panes()
      if (panes.length > 1) {
        const total = Math.max(0, (host.current?.clientHeight ?? 400) - 28)
        panes[1].setHeight(Math.max(100, Math.floor(total * .25)))
      }
    }
    const observer = new ResizeObserver(resizePanes)
    observer.observe(host.current)
    return () => { observer.disconnect(); state.current = null; chart.remove() }
  }, [])

  useEffect(() => {
    const s = state.current
    if (!s) return
    const range = s.chart.timeScale().getVisibleLogicalRange()
    if (showBB && !s.bands.length) {
      s.bands = ['#5688b6', '#90aacf', '#5688b6'].map((color, i) => s.chart.addSeries(LineSeries, {
        color, lineWidth: 1, lineStyle: i === 1 ? LineStyle.Dashed : LineStyle.Solid,
        priceLineVisible: false, lastValueVisible: false, crosshairMarkerVisible: false,
      }))
    } else if (!showBB && s.bands.length) { s.bands.forEach(line => s.chart.removeSeries(line)); s.bands = [] }
    if (showATR && !s.atr) {
      s.atr = s.chart.addSeries(LineSeries, { color: '#d7b878', lineWidth: 1, priceLineVisible: false, lastValueVisible: false,
        priceFormat: { type: 'price', precision: 6, minMove: .000001 } }, 1)
      s.atr.priceScale().applyOptions({ scaleMargins: { top: .15, bottom: .12 } })
      s.chart.panes()[0].setStretchFactor(3)
      s.chart.panes()[1].setStretchFactor(1)
      s.chart.panes()[1].setHeight(Math.max(100, Math.floor(((host.current?.clientHeight ?? 400) - 28) * .25)))
    } else if (!showATR && s.atr) { s.chart.removeSeries(s.atr); s.atr = null }
    if (range) s.chart.timeScale().setVisibleLogicalRange(range)
  }, [showBB, showATR])

  useEffect(() => {
    const s = state.current
    if (!s) return
    const range = s.chart.timeScale().getVisibleLogicalRange()
    // Anchor the fractional logical range to a candle, preserving exact zoom
    // even when a rolling history drops its oldest bars on refresh.
    const anchorIndex = Math.min(previousRows.current.length - 1, Math.max(0, Math.floor(range?.from ?? 0)))
    const anchorTime = previousRows.current[anchorIndex]?.time
    const newAnchorIndex = rows.findIndex(row => row.time === anchorTime)
    s.candles.setData(rows.map(c => ({ time: c.time as Time, open: +c.open, high: +c.high, low: +c.low, close: +c.close })))
    s.volume.setData(rows.map(c => ({ time: c.time as Time, value: +c.volume, color: +c.close >= +c.open ? '#173e31' : '#4a252b' })))
    const keys = ['upper', 'middle', 'lower'] as const
    s.bands.forEach((line, i) => line.setData(points.map(p => p[keys[i]] === null ? { time: p.time as Time } : { time: p.time as Time, value: p[keys[i]]! })))
    s.atr?.setData(points.map(p => p.atr === null ? { time: p.time as Time } : { time: p.time as Time, value: p.atr }))
    if (rows.length && fittedContext.current !== contextKey) {
      s.chart.timeScale().fitContent(); fittedContext.current = contextKey; setHoverTime(null)
    } else if (range && rows.length) {
      const shift = newAnchorIndex >= 0 ? newAnchorIndex - anchorIndex : 0
      s.chart.timeScale().setVisibleLogicalRange({ from: range.from + shift, to: range.to + shift })
    }
    previousRows.current = rows
  }, [rows, points, contextKey, showBB, showATR])

  useEffect(() => {
    const s = state.current
    if (!s) return
    s.orders.forEach(line => s.candles.removePriceLine(line))
    s.orders = entryOrders.slice(0, 20).map(order => s.candles.createPriceLine({ price: order.price,
      color: order.side.toUpperCase() === 'BUY' ? '#226a4b' : '#83383c', lineWidth: 1,
      lineStyle: LineStyle.Dashed, axisLabelVisible: false, title: '',
    }))
  }, [entryOrders])

  return <div className="chart-body">
    <div className="chart-host" ref={host} />
    {(showBB || showATR) && <div className="chart-indicator-legend" aria-label="Chart indicator values">
      {showBB && <span className="bb-legend">BB(20, 2) <b>{number(point?.lower)} / {number(point?.middle)} / {number(point?.upper)}</b></span>}
      {showATR && <span className="atr-legend">ATR(14) <b>{number(point?.atr)}</b></span>}
      <small>{timeframe}{provisional ? ' · provisional' : ' · closed'}</small>
    </div>}
  </div>
}
function number(value: number | null | undefined) { return value == null ? '—' : value.toLocaleString('en-US', { maximumFractionDigits: 6 }) }
