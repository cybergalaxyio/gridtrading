import { useEffect, useRef } from 'react'
import { CandlestickSeries, ColorType, createChart, HistogramSeries, LineStyle } from 'lightweight-charts'
import type { Candle } from '../types'

export function TradingChart({ candles, levels = [], center }: { candles: Candle[]; levels?: number[]; center?: number }) {
  const host = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!host.current || candles.length === 0) return
    const chart = createChart(host.current, {
      autoSize: true,
      layout: { background: { type: ColorType.Solid, color: '#08121b' }, textColor: '#8391a2', attributionLogo: true },
      grid: { vertLines: { color: '#17232d' }, horzLines: { color: '#17232d' } },
      crosshair: { vertLine: { color: '#7a899b', style: LineStyle.Dashed }, horzLine: { color: '#7a899b', style: LineStyle.Dashed } },
      rightPriceScale: { borderColor: '#2a3947', scaleMargins: { top: .08, bottom: .24 } },
      timeScale: { borderColor: '#2a3947', timeVisible: true, secondsVisible: false },
    })
    const series = chart.addSeries(CandlestickSeries, { upColor: '#25d777', downColor: '#f2575b', borderVisible: false, wickUpColor: '#25d777', wickDownColor: '#f2575b' })
    series.setData(candles.map(c => ({ time: c.time as never, open: +c.open, high: +c.high, low: +c.low, close: +c.close })))
    const volume = chart.addSeries(HistogramSeries, { priceFormat: { type: 'volume' }, priceScaleId: 'volume', color: '#33576f' })
    volume.priceScale().applyOptions({ scaleMargins: { top: .82, bottom: 0 } })
    volume.setData(candles.map(c => ({ time: c.time as never, value: +c.volume, color: +c.close >= +c.open ? '#173e31' : '#4a252b' })))
    levels.slice(0, 20).forEach((price, index) => series.createPriceLine({ price, color: index % 2 ? '#226a4b' : '#83383c', lineWidth: 1, lineStyle: LineStyle.Dashed, axisLabelVisible: false, title: '' }))
    if (center) series.createPriceLine({ price: center, color: '#8f5dff', lineWidth: 1, axisLabelVisible: true, title: 'CENTER' })
    chart.timeScale().fitContent()
    return () => chart.remove()
  }, [candles, center, levels])
  return <div className="chart-host" ref={host} />
}
