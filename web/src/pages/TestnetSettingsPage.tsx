import { useEffect, useState } from 'react'
import { api } from '../api'
import { Icon } from '../components/Icon'
import { AccountsPanel } from '../components/AccountsPanel'
import type { TelegramSettings } from '../types'

type SettingsSection = 'accounts' | 'notifications'

const emptyTelegram: TelegramSettings = {
  configured: false, enabled: false, tokenStored: false, chatId: '',
}

export function SettingsPage() {
  const [section, setSection] = useState<SettingsSection>('accounts')
  const [telegram, setTelegram] = useState<TelegramSettings>(emptyTelegram)
  const [botToken, setBotToken] = useState('')
  const [chatId, setChatId] = useState('')
  const [telegramError, setTelegramError] = useState('')
  const [telegramNotice, setTelegramNotice] = useState('')
  const [telegramBusy, setTelegramBusy] = useState(false)
  const [confirmRemove, setConfirmRemove] = useState(false)

  useEffect(() => {
    void api.telegramSettings().then(value => {
      setTelegram(value)
      setChatId(value.chatId)
    }).catch(e => setTelegramError(message(e, 'Telegram 设置读取失败')))
  }, [])


  async function saveTelegram(testAndEnable = false) {
    if (telegramBusy) return
    setTelegramBusy(true)
    setTelegramError('')
    setTelegramNotice('')
    setConfirmRemove(false)
    try {
      const saved = await api.saveTelegramSettings({
        chatId,
        ...(botToken.trim() ? { botToken: botToken.trim() } : {}),
      })
      setTelegram(saved)
      setChatId(saved.chatId)
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
      setTelegramNotice('Telegram 通知已禁用；禁用期间的告警不会补发')
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
    } catch {
      // Preserve the actionable error from the original operation.
    }
  }

  return <div className="settings-page">
    <aside className="settings-nav"><h2>SETTINGS</h2>
      <button className={section === 'accounts' ? 'active' : ''} onClick={() => setSection('accounts')}><Icon name="strategy" size={18} />交易所账户</button>
      <button className={section === 'notifications' ? 'active' : ''} onClick={() => setSection('notifications')}><Icon name="alert" size={18} />通知设置</button>
      <button disabled><Icon name="dashboard" size={18} />系统状态</button>
      <button disabled><Icon name="settings" size={18} />界面设置</button>
      <button disabled><Icon name="alert" size={18} />审计与监控</button>
    </aside>
    {section === 'accounts'
      ? <AccountsPanel />
      : <div className="settings-content telegram-settings">
          <div className="page-title"><div><h1>Telegram 通知</h1><p>将新产生的风险告警推送到一个私聊、群组或频道。</p></div></div>
          <div className="system-cards">
            <SystemMetric label="通知状态" value={telegram.enabled ? 'ENABLED' : telegram.configured ? 'DISABLED' : 'NOT CONFIGURED'} caption={telegram.enabled ? '新告警将自动推送' : '不会发送告警'} />
            <SystemMetric label="Bot" value={telegram.botUsername ? '@' + telegram.botUsername : telegram.tokenStored ? 'TOKEN STORED' : 'NO TOKEN'} caption={telegram.verifiedAt ? '验证于 ' + formatTime(telegram.verifiedAt) : '需要发送测试消息验证'} />
            <SystemMetric label="最近推送" value={telegram.lastDeliveryStatus ?? 'NO ATTEMPT'} caption={telegram.lastDeliveryAt ? formatTime(telegram.lastDeliveryAt) : '尚无推送记录'} />
          </div>
          <section className="panel telegram-form">
            <h2>Bot 与接收目标</h2>
            <div className="telegram-form-grid">
              <label><span>Bot Token</span><input type="password" autoComplete="off" value={botToken} onChange={event => setBotToken(event.target.value)} placeholder={telegram.tokenStored ? '已安全保存；留空则保持不变' : '123456789:AA…'} disabled={telegramBusy} /><small>Token 仅在保存时发送到本机后端，读取设置时绝不返回。</small></label>
              <label><span>Chat ID / Channel</span><input value={chatId} onChange={event => setChatId(event.target.value)} placeholder="-1001234567890 或 @channel" disabled={telegramBusy} /><small>支持私聊、群组的数字 ID，或机器人有发言权限的 @频道用户名。</small></label>
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
              <div><dt>目标</dt><dd>{telegram.chatId || '—'}</dd></div>
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
