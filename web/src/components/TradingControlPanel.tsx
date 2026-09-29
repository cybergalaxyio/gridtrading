import { useEffect, useState } from 'react'
import { api } from '../api'
import type { TradingControlSettings } from '../types'

export function TradingControlPanel() {
  const [settings, setSettings] = useState<TradingControlSettings | null>(null)
  const [amount, setAmount] = useState('0')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')

  async function load() {
    setError('')
    try {
      const saved = await api.tradingControlSettings()
      setSettings(saved); setAmount(saved.minimumConfirmationNotional)
    }
    catch (e) { setError(e instanceof Error ? e.message : '交易控制设置读取失败') }
  }
  useEffect(() => { void load() }, [])

  async function toggle() {
    if (!settings || busy) return
    setBusy(true); setError(''); setNotice('')
    try {
      const saved = await api.saveTradingControlSettings({ ...settings, requireManualOrderConfirmation: !settings.requireManualOrderConfirmation })
      setSettings(saved)
      setNotice(saved.requireManualOrderConfirmation ? '已启用人工下单确认' : '已关闭人工下单确认')
    } catch (e) { setError(e instanceof Error ? e.message : '交易控制设置保存失败') }
    finally { setBusy(false) }
  }

  async function saveAmount() {
    if (!settings || busy) return
    if (!amount.trim() || !Number.isFinite(Number(amount)) || Number(amount) < 0) {
      setError('请输入大于或等于 0 的确认金额'); return
    }
    setBusy(true); setError(''); setNotice('')
    try {
      const saved = await api.saveTradingControlSettings({ ...settings, minimumConfirmationNotional: amount })
      setSettings(saved); setAmount(saved.minimumConfirmationNotional)
      setNotice('确认金额门槛已保存')
    } catch (e) { setError(e instanceof Error ? e.message : '交易控制设置保存失败') }
    finally { setBusy(false) }
  }

  return <div className="settings-content trading-control-settings">
    <div className="page-title"><div><h1>交易控制</h1><p>Trading Control · 逐笔审核实际发送的订单。</p></div></div>
    <section className="panel trading-control-card">
      <div className="trading-control-row">
        <div><h2 id="manual-order-label">下单前需要人工确认</h2><p id="manual-order-description">启用后，仅金额超过下方门槛的订单进入待确认列表。查看方向、价格、数量与订单类型后，逐笔确认才会发送，包括策略自动生成的订单。</p></div>
        <button type="button" className="settings-switch" role="switch" aria-checked={settings?.requireManualOrderConfirmation ?? false}
          aria-labelledby="manual-order-label" aria-describedby="manual-order-description" disabled={!settings || busy} onClick={() => void toggle()}><span /></button>
      </div>
      <p className="dim">适用于所有账户的入场、补单、止盈、改单及平仓（含紧急停止）。未确认的止盈或平仓订单不会发送；撤单和对账继续执行。关闭开关或提高门槛后，已有待确认或已拒绝的订单仍需逐笔确认。</p>
      <form className="confirmation-threshold" onSubmit={event => { event.preventDefault(); void saveAmount() }}>
        <label htmlFor="confirmation-amount">人工确认金额门槛（USDT / USDC）</label>
        <div className="confirmation-threshold-input">
          <input id="confirmation-amount" type="number" min="0" step="any" required inputMode="decimal"
            value={amount} onChange={event => { setAmount(event.target.value); setNotice('') }} disabled={!settings || busy}
            aria-describedby="confirmation-amount-help" />
          <button type="submit" className="primary" disabled={!settings || busy || amount === settings.minimumConfirmationNotional}>保存金额</button>
        </div>
        <p id="confirmation-amount-help" className="dim">订单金额 = 实际发送价格 × 数量（报价币种，不是保证金）。超过门槛才需确认，等于或低于门槛自动发送；设为 0 则所有订单都需确认。开关关闭时不对新订单应用门槛。</p>
      </form>
      {!settings && !error && <p role="status">正在读取设置…</p>}
      {busy && <p role="status">正在保存…</p>}
      {notice && <p className="positive" role="status">{notice}</p>}
      {error && <p className="warning-text" role="alert">{error} {!settings && <button className="secondary" onClick={() => void load()}>重试</button>}</p>}
    </section>
  </div>
}
