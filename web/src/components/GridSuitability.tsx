import { useEffect, useState } from 'react'
import { api } from '../api'
import type { AdvisoryStatus, GridAdvisoryResponse, Strategy } from '../types'

const labels: Record<AdvisoryStatus, string> = { FAVORABLE: 'Favorable', CAUTION: 'Caution', UNFAVORABLE: 'Unfavorable', INSUFFICIENT_DATA: 'Insufficient data' }
const checks = ['Trend', 'Volatility', 'Entry', 'Costs', 'Exposure']
const timeframes = ['15m', '1h', '4h', '1d']

export function GridSuitability({ environmentId, accountId, symbol, strategy }: {
  environmentId: string; accountId: string; symbol: string; strategy?: Strategy
}) {
  const [expanded, setExpanded] = useState(false)
  const [response, setResponse] = useState<{ key: string; value: GridAdvisoryResponse; receivedAt: number } | null>(null)
  const [failure, setFailure] = useState<{ key: string; message: string } | null>(null)
  const [now, setNow] = useState(Date.now)
  const key = JSON.stringify([environmentId, accountId, symbol, strategy?.strategyId, strategy?.version])
  const current = response?.key === key ? response : null
  const data = current?.value
  const error = failure?.key === key ? failure.message : null
  const expired = !!current && now - current.receivedAt >= 90_000
  const status = !data || expired ? 'INSUFFICIENT_DATA' : data.status
  const priority: Record<AdvisoryStatus, number> = { UNFAVORABLE: 0, INSUFFICIENT_DATA: 1, CAUTION: 2, FAVORABLE: 3 }
  const reasons = data?.checks.filter(c => c.status !== 'FAVORABLE').sort((a, b) => priority[a.status] - priority[b.status]).flatMap(c => c.reasons).slice(0, 2) ?? []

  useEffect(() => {
    let disposed = false
    let running = false
    let controller: AbortController | null = null
    setResponse(null); setFailure(null)
    const refresh = async () => {
      if (disposed || running || document.hidden) return
      running = true
      controller = new AbortController()
      const timeout = window.setTimeout(() => controller?.abort(), 25_000)
      try {
        const result = await api.gridAdvisory(environmentId, symbol, accountId || undefined, strategy?.strategyId, controller.signal)
        if (disposed) return
        if (result.strategyId && result.strategyVersion !== strategy?.version) throw new Error('Saved strategy changed. Refresh the strategy before using this assessment.')
        setResponse({ key, value: result, receivedAt: Date.now() }); setFailure(null); setNow(Date.now())
      } catch (e) {
        if (!disposed) setFailure({ key, message: e instanceof Error && e.name !== 'AbortError' ? e.message : 'Analysis timed out. Retrying automatically.' })
      } finally { window.clearTimeout(timeout); running = false }
    }
    void refresh()
    const timer = window.setInterval(() => void refresh(), 30_000)
    const clock = window.setInterval(() => setNow(Date.now()), 5_000)
    const visible = () => { if (!document.hidden) { setNow(Date.now()); void refresh() } }
    document.addEventListener('visibilitychange', visible)
    return () => { disposed = true; controller?.abort(); window.clearInterval(timer); window.clearInterval(clock); document.removeEventListener('visibilitychange', visible) }
  }, [key, environmentId, accountId, symbol, strategy?.strategyId, strategy?.version])

  return <section className={`grid-advisory panel advisory-${status.toLowerCase()}`} aria-label="Grid suitability">
    <div className="advisory-heading">
      <div><h2>{strategy?.activeCycle ? 'New-cycle suitability' : 'Grid Suitability'} <span>{symbol}</span></h2>
        <p>{strategy ? `${strategy.name} · ${mode(strategy.configuration.gridMode)} · saved v${strategy.version}` : 'Market analysis · no saved strategy selected'}</p></div>
      <span className={`advisory-status status-${status.toLowerCase()}`}>{!data && !error ? 'Analyzing…' : labels[status]}</span>
      <button className="advisory-expand" type="button" aria-expanded={expanded} aria-controls="grid-advisory-details" onClick={() => setExpanded(x => !x)}>{expanded ? 'Hide analysis ▴' : 'View analysis ▾'}</button>
    </div>
    <div className="advisory-summary" aria-live="polite">
      {data?.notice ? <p>{data.notice}</p> : reasons.length ? <p>{reasons.join(' ')}</p> : <p>{data ? 'All configured checks pass. Favorable conditions do not forecast a profit.' : 'Assessing market conditions and saved grid settings across four timeframes.'}</p>}
      {error && <p className="advisory-error">{current ? 'Outdated assessment. ' : ''}{error}</p>}
      {expired && <p className="advisory-error">No successful refresh in 90 seconds. Previous readings below are outdated.</p>}
    </div>
    <div className="advisory-footer">
      <div className="advisory-checks">{checks.map(title => {
        const check = data?.checks.find(c => c.title === title)
        const checkStatus = expired ? 'INSUFFICIENT_DATA' : check?.status ?? 'INSUFFICIENT_DATA'
        return <span key={title} className={`advisory-chip status-${checkStatus.toLowerCase()}`} title={check?.reasons.join(' ')}><i />{title}<b>{labels[checkStatus]}</b></span>
      })}</div>
      <small>15m · 1h · 4h · 1d{data ? ` · Updated ${utc(data.asOf)}` : ''}</small>
    </div>
    {expanded && <div id="grid-advisory-details" className="advisory-details">
      {strategy?.activeCycle && <p className="advisory-note">Assesses the latest saved settings for a new cycle. The running cycle uses its frozen settings. This advisory does not pause, exit, or restart it.</p>}
      <div className="advisory-timeframes" role="region" aria-label="Four-timeframe comparison" tabIndex={0}>
        <table><thead><tr><th>Timeframe</th><th>Trend / ADX</th><th>+DI / −DI</th><th>ATR / recent median</th><th>RSI</th><th>BB lower / middle / upper</th><th>Last closed (UTC)</th></tr></thead>
          <tbody>{timeframes.map(interval => {
            const f = data?.frames.find(x => x.interval === interval), p = f?.indicators
            return <tr key={interval}><th>{interval}</th>{f?.error || !p ? <td colSpan={6}>{f?.error ?? 'Waiting for market history'}</td> : <>
              <td>{p.plusDi == null || p.minusDi == null ? '—' : p.plusDi > p.minusDi ? 'Up' : p.plusDi < p.minusDi ? 'Down' : 'Unclear'} · {num(p.adx)}</td>
              <td>{num(p.plusDi)} / {num(p.minusDi)}</td><td>{num(p.atr)} / {num(f.atrRatio)}×</td><td>{num(p.rsi)}</td>
              <td>{num(p.lower)} / {num(p.middle)} / {num(p.upper)}</td><td>{f.closedAt ? new Date(f.closedAt * 1000).toISOString().slice(5, 16).replace('T', ' ') : '—'}</td>
            </>}</tr>
          })}</tbody></table>
      </div>
      <div className="advisory-detail-grid">{data?.checks.map(check => <article key={check.id}>
        <header><h3>{check.title}</h3><span className={`status-${(expired ? 'INSUFFICIENT_DATA' : check.status).toLowerCase()}`}>{labels[expired ? 'INSUFFICIENT_DATA' : check.status]}</span></header>
        <ul>{check.reasons.map(reason => <li key={reason}>{reason}</li>)}</ul>
        {check.metrics.length > 0 && <dl>{check.metrics.map(metric => <div key={metric.label}><dt>{metric.label}</dt><dd>{metric.value}</dd></div>)}</dl>}
        <p>{check.consideration}</p>
      </article>)}</div>
      {!!data?.scenarios.length && <div className="advisory-scenarios"><h3>Adverse-move scenarios · flat start · worse side</h3><div className="advisory-scenario-grid">{data.scenarios.map(s => <div key={`${s.interval}-${s.atrMultiple}`}><b>{s.interval} · {s.atrMultiple} ATR</b><span>{num(+s.loss)} USDC loss</span><small>{s.side} · {num(+s.filledQuantity)} filled</small></div>)}</div></div>}
      <p className="advisory-note">Initial heuristics · {data?.ruleVersion ?? 'grid-advisory-v1'} · Closed candles only. ATR scenarios are not forecasts or maximum-loss bounds. Advisory only; trading controls are unchanged.</p>
    </div>}
  </section>
}
function num(value: number | null | undefined) { return value == null ? '—' : value.toLocaleString('en-US', { maximumFractionDigits: 4 }) }
function utc(value: string) { return new Date(value).toISOString().slice(11, 19) + ' UTC' }
function mode(value: string) { return value === 'BUY_ONLY' ? 'Buy Only' : value === 'SELL_ONLY' ? 'Sell Only' : 'Two-Way' }
