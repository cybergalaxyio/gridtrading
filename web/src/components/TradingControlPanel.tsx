import { useEffect, useState } from 'react'
import { api } from '../api'
import type { TradingControlSettings } from '../types'

export function TradingControlPanel() {
  const [settings, setSettings] = useState<TradingControlSettings | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')

  async function load() {
    setError('')
    try { setSettings(await api.tradingControlSettings()) }
    catch (e) { setError(e instanceof Error ? e.message : '交易控制设置读取失败') }
  }
  useEffect(() => { void load() }, [])

  async function toggle() {
    if (!settings || busy) return
    setBusy(true); setError(''); setNotice('')
    try {
      const saved = await api.saveTradingControlSettings({ requireManualOrderConfirmation: !settings.requireManualOrderConfirmation })
      setSettings(saved)
      setNotice(saved.requireManualOrderConfirmation ? '已启用人工下单确认' : '已关闭人工下单确认')
    } catch (e) { setError(e instanceof Error ? e.message : '交易控制设置保存失败') }
    finally { setBusy(false) }
  }

  return <div className="settings-content trading-control-settings">
    <div className="page-title"><div><h1>交易控制</h1><p>Trading Control · 逐笔审核实际发送的订单。</p></div></div>
    <section className="panel trading-control-card">
      <div className="trading-control-row">
        <div><h2 id="manual-order-label">下单前需要人工确认</h2><p id="manual-order-description">启用后，每笔实际订单都会进入待确认列表。查看方向、价格、数量与订单类型后，逐笔确认才会发送，包括策略自动生成的订单。</p></div>
        <button type="button" className="settings-switch" role="switch" aria-checked={settings?.requireManualOrderConfirmation ?? false}
          aria-labelledby="manual-order-label" aria-describedby="manual-order-description" disabled={!settings || busy} onClick={() => void toggle()}><span /></button>
      </div>
      <p className="dim">适用于所有账户的入场、补单、止盈、改单及平仓（含紧急停止）。未确认的止盈或平仓订单不会发送；撤单和对账继续执行。关闭开关后，已有待确认订单仍需逐笔确认。</p>
      {!settings && !error && <p role="status">正在读取设置…</p>}
      {busy && <p role="status">正在保存…</p>}
      {notice && <p className="positive" role="status">{notice}</p>}
      {error && <p className="warning-text" role="alert">{error} {!settings && <button className="secondary" onClick={() => void load()}>重试</button>}</p>}
    </section>
  </div>
}
