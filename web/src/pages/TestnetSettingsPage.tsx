import { useEffect, useState } from 'react'
import { api } from '../api'
import { Icon } from '../components/Icon'
import { TradingControlPanel } from '../components/TradingControlPanel'
import { AccountsPanel } from '../components/AccountsPanel'
import type { TelegramSettings } from '../types'

type SettingsSection = 'accounts' | 'notifications' | 'trading-control'

const emptyTelegram: TelegramSettings = {
  configured: false, enabled: false, tokenStored: false, chatId: '',
}

export function SettingsPage() {
  const [section, setSection] = useState<SettingsSection>('accounts')
  const [telegram, setTelegram] = useState<TelegramSettings>(emptyTelegram)
  const [botToken, setBotToken] = useState('')
  const [chatId, setChatId] = useState('')
  const [orderActionsEnabled, setOrderActionsEnabled] = useState(false)
  const [orderActionsChatId, setOrderActionsChatId] = useState('')
  const [telegramError, setTelegramError] = useState('')
  const [telegramNotice, setTelegramNotice] = useState('')
  const [telegramBusy, setTelegramBusy] = useState(false)
  const [confirmRemove, setConfirmRemove] = useState(false)

  useEffect(() => {
    void api.telegramSettings().then(value => {
      setTelegram(value)
      setChatId(value.chatId)
      setOrderActionsEnabled(value.orderActionsEnabled ?? false)
      setOrderActionsChatId(value.orderActionsChatId ?? '')
    }).catch(e => setTelegramError(message(e, 'Telegram 设置读取失败')))
  }, [])


  async function saveTelegram(testAndEnable = false) {
    if (telegramBusy) return
    if (orderActionsEnabled && !orderActionsChatId.trim()) {
      setTelegramError('启用订单操作时，请填写 Order Actions Chat ID'); return
    }
    setTelegramBusy(true)
    setTelegramError('')
    setTelegramNotice('')
    setConfirmRemove(false)
    try {
      const saved = await api.saveTelegramSettings({
        chatId, orderActionsEnabled, orderActionsChatId,
        ...(botToken.trim() ? { botToken: botToken.trim() } : {}),
      })
      setTelegram(saved)
      setChatId(saved.chatId)
      setOrderActionsEnabled(saved.orderActionsEnabled ?? false)
      setOrderActionsChatId(saved.orderActionsChatId ?? '')
      setBotToken('')
      if (testAndEnable) {
        const enabled = await api.testAndEnableTelegram()
        setTelegram(enabled)
        setTelegramNotice('测试消息已发送，@' + (enabled.botUsername ?? 'bot') + ' 通知已启用')
      } else {
        setTelegramNotice(saved.enabled ? 'Telegram 设置未改变' : '设置已保存，请测试并启用')
      }
    } catch (e) {
      setTelegramError(message(e, testAndEnable ? 'Telegram 测试失败' : 'Telegram 设置保存失败'))
      void reloadTelegram()
    } finally {
      setTelegramBusy(false)
    }
  }

  async function disableTelegram() {
    if (telegramBusy) return
    setTelegramBusy(true)
    setTelegramError('')
    setTelegramNotice('')
    try {
      const value = await api.disableTelegram()
      setTelegram(value)
      setTelegramNotice('Telegram 通知及订单操作已禁用；旧订单按钮已失效')
    } catch (e) {
      setTelegramError(message(e, '禁用 Telegram 通知失败'))
    } finally {
      setTelegramBusy(false)
    }
  }

  async function removeTelegram() {
    if (!confirmRemove) {
      setConfirmRemove(true)
      return
    }
    setTelegramBusy(true)
    setTelegramError('')
    setTelegramNotice('')
    try {
      await api.removeTelegram()
      setTelegram(emptyTelegram)
      setChatId('')
      setOrderActionsEnabled(false)
      setOrderActionsChatId('')
      setBotToken('')
      setConfirmRemove(false)
      setTelegramNotice('Telegram 配置已清除')
    } catch (e) {
      setTelegramError(message(e, '清除 Telegram 配置失败'))
    } finally {
      setTelegramBusy(false)
    }
  }

  async function reloadTelegram() {
    try {
      const value = await api.telegramSettings()
      setTelegram(value)
      setChatId(value.chatId)
      setOrderActionsEnabled(value.orderActionsEnabled ?? false)
      setOrderActionsChatId(value.orderActionsChatId ?? '')
    } catch {
      // Preserve the actionable error from the original operation.
    }
  }

  return <div className="settings-page">
    <aside className="settings-nav"><h2>SETTINGS</h2>
      <button className={section === 'accounts' ? 'active' : ''} onClick={() => setSection('accounts')}><Icon name="strategy" size={18} />交易所账户</button>
      <button className={section === 'notifications' ? 'active' : ''} onClick={() => setSection('notifications')}><Icon name="alert" size={18} />通知设置</button>
      <button className={section === 'trading-control' ? 'active' : ''} onClick={() => setSection('trading-control')}><Icon name="shield" size={18} />交易控制</button>
      <button disabled><Icon name="dashboard" size={18} />系统状态</button>
      <button disabled><Icon name="settings" size={18} />界面设置</button>
      <button disabled><Icon name="alert" size={18} />审计与监控</button>
    </aside>
    {section === 'accounts'
      ? <AccountsPanel />
      : section === 'trading-control' ? <TradingControlPanel />
      : <div className="settings-content telegram-settings">
          <div className="page-title"><div><h1>Telegram 通知</h1><p>每笔确认下单、完全成交（Entry、TP、平仓）和新风险告警都会推送到一个私聊、群组或频道。</p></div></div>
          <div className="system-cards">
            <SystemMetric label="通知状态" value={telegram.enabled ? 'ENABLED' : telegram.configured ? 'DISABLED' : 'NOT CONFIGURED'} caption={telegram.enabled ? '每笔下单、完全成交和新告警将自动推送' : '不会发送通知'} />
            <SystemMetric label="Bot" value={telegram.botUsername ? '@' + telegram.botUsername : telegram.tokenStored ? 'TOKEN STORED' : 'NO TOKEN'} caption={telegram.verifiedAt ? '验证于 ' + formatTime(telegram.verifiedAt) : '需要发送测试消息验证'} />
            <SystemMetric label="最近推送" value={telegram.lastDeliveryStatus ?? 'NO ATTEMPT'} caption={telegram.lastDeliveryAt ? formatTime(telegram.lastDeliveryAt) : '尚无推送记录'} />
          </div>
          <section className="panel telegram-form">
            <h2>Bot 与接收目标</h2>
            <div className="telegram-form-grid">
              <label><span>Bot Token</span><input type="password" autoComplete="off" value={botToken} onChange={event => setBotToken(event.target.value)} placeholder={telegram.tokenStored ? '已安全保存；留空则保持不变' : '123456789:AA…'} disabled={telegramBusy} /><small>Token 仅在保存时发送到本机后端，读取设置时绝不返回。</small></label>
              <label><span>Chat ID / Channel</span><input value={chatId} onChange={event => setChatId(event.target.value)} placeholder="-1001234567890 或 @channel" disabled={telegramBusy} /><small>支持私聊、群组的数字 ID，或机器人有发言权限的 @频道用户名。</small></label>
            </div>
            <div className="telegram-order-actions">
              <label><input type="checkbox" checked={orderActionsEnabled} disabled={telegramBusy}
                onChange={event => setOrderActionsEnabled(event.target.checked)} /> 启用订单操作 / Enable order actions</label>
              <div className="telegram-approval-destination">
                <label htmlFor="telegram-actions-chat-id">Order Actions Chat ID / 订单操作私聊 ID</label>
                <input id="telegram-actions-chat-id" value={orderActionsChatId} disabled={telegramBusy}
                  onChange={event => setOrderActionsChatId(event.target.value)} placeholder="填写接收订单确认的私聊 Chat ID"
                  aria-required={orderActionsEnabled} aria-describedby="telegram-actions-chat-help" />
                <p id="telegram-actions-chat-help" className="dim">启用订单操作时必须填写，不会自动使用通知目标。可以与通知 Chat ID 相同，也可以指定另一个私聊；通知仍发送到上方目标。</p>
              </div>
              <p className="dim">先向机器人发送 /start，再填写订单操作私聊 ID，点击“测试并启用”。仅该私聊用户可以点击 Confirm / Reject；发送 /pending 可分页查看当前订单，包括已拒绝订单。</p>
              <p className="dim">遵循交易控制的金额门槛。确认仅授权该笔订单，实际提交由交易系统完成。Telegram 不可用时，订单仍可在控制台处理。</p>
            </div>
            <div className="telegram-actions">
              <button className="secondary" disabled={telegramBusy} onClick={() => void saveTelegram(false)}>保存设置</button>
              <button className="primary" disabled={telegramBusy} onClick={() => void saveTelegram(true)}>{telegramBusy ? '处理中…' : '测试并启用'}</button>
              {telegram.enabled && <button className="secondary" disabled={telegramBusy} onClick={() => void disableTelegram()}>禁用通知</button>}
              {telegram.configured && <button className={confirmRemove ? 'danger' : 'danger-outline'} disabled={telegramBusy} onClick={() => void removeTelegram()}>{confirmRemove ? '再次点击确认清除' : '清除配置'}</button>}
            </div>
            {telegramNotice && <p className="telegram-feedback positive">{telegramNotice}</p>}
            {telegramError && <p className="telegram-feedback warning-text">{telegramError}</p>}
          </section>
          <section className="panel telegram-status">
            <h2>投递状态</h2>
            <dl>
              <div><dt>订单操作</dt><dd>{telegram.orderActionsReady ? 'READY · 私聊订单操作已启用' : telegram.orderActionsEnabled ? '需要测试并验证私聊' : 'DISABLED'}</dd></div>
              {telegram.lastActionError && <div><dt>最近订单操作错误</dt><dd className="warning-text">{telegram.lastActionError}</dd></div>}
              <div><dt>通知目标</dt><dd>{telegram.chatId || '—'}</dd></div>
              <div><dt>订单操作目标</dt><dd>{telegram.orderActionsChatId || '—'}</dd></div>
              <div><dt>最近测试</dt><dd>{telegram.lastTestedAt ? formatTime(telegram.lastTestedAt) : '—'}</dd></div>
              <div><dt>测试结果</dt><dd className={telegram.lastTestError ? 'warning-text' : ''}>{telegram.lastTestError ?? (telegram.verifiedAt ? '成功' : '—')}</dd></div>
              <div><dt>最近推送</dt><dd>{telegram.lastDeliveryAt ? formatTime(telegram.lastDeliveryAt) : '—'}</dd></div>
              <div><dt>投递结果</dt><dd className={telegram.lastDeliveryStatus === 'FAILED' ? 'warning-text' : ''}>{telegram.lastDeliveryError ?? telegram.lastDeliveryStatus ?? '—'}</dd></div>
            </dl>
          </section>
          <section className="safety-banner"><Icon name="shield" /><div><b>加密与投递边界</b><p>需要后端设置 <code>GRID_TRADING_CREDENTIAL_KEY</code>。Bot Token 使用 AES-256-GCM 加密；Telegram 故障不会阻塞交易，也不会自动重试失败的消息。</p></div></section>
        </div>}
  </div>
}

function SystemMetric({ label, value, caption }: { label: string; value: string; caption: string }) { return <section className="panel"><small>{label.toUpperCase()}</small><b>{value}</b><span>{caption}</span></section> }
function formatTime(value: string) { return new Date(value).toLocaleString('zh-CN', { hour12: false }) }
function message(error: unknown, fallback: string) { return error instanceof Error ? error.message : fallback }
