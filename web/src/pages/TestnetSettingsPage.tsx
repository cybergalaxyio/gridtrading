import { useEffect, useState } from 'react'
import { api } from '../api'
import { Icon } from '../components/Icon'
import type { HyperliquidAccount, HyperliquidHealth, TelegramSettings } from '../types'

type SettingsSection = 'accounts' | 'notifications'

const emptyTelegram: TelegramSettings = {
  configured: false, enabled: false, tokenStored: false, chatId: '',
}

export function SettingsPage() {
  const [section, setSection] = useState<SettingsSection>('accounts')
  const [accounts, setAccounts] = useState<HyperliquidAccount[]>([])
  const [health, setHealth] = useState<Record<string, HyperliquidHealth>>({})
  const [accountError, setAccountError] = useState('')
  const [telegram, setTelegram] = useState<TelegramSettings>(emptyTelegram)
  const [botToken, setBotToken] = useState('')
  const [chatId, setChatId] = useState('')
  const [telegramError, setTelegramError] = useState('')
  const [telegramNotice, setTelegramNotice] = useState('')
  const [telegramBusy, setTelegramBusy] = useState(false)
  const [confirmRemove, setConfirmRemove] = useState(false)

  useEffect(() => {
    void Promise.all([api.hyperliquidAccounts(), api.hyperliquidAccounts('hyperliquid-mainnet')]).then(rows => rows.flat()).then(async rows => {
      setAccounts(rows)
      const checks = await Promise.allSettled(rows.map(async row => [row.accountId, await api.hyperliquidHealth(row.accountId, row.environment === 'MAINNET' ? 'hyperliquid-mainnet' : 'hyperliquid-testnet')] as const))
      setHealth(Object.fromEntries(checks.flatMap(result => result.status === 'fulfilled' ? [result.value] : [])))
      if (checks.some(result => result.status === 'rejected')) setAccountError('Some account checks failed. Verify credentials and network connectivity.')
    }).catch(e => setAccountError(message(e, 'Hyperliquid 状态读取失败')))
    void api.telegramSettings().then(value => {
      setTelegram(value)
      setChatId(value.chatId)
    }).catch(e => setTelegramError(message(e, 'Telegram 设置读取失败')))
  }, [])

  const ready = accounts.some(x => health[x.accountId]?.tradingReady)

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
      ? <AccountsPanel accounts={accounts} health={health} ready={ready} error={accountError} />
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

function AccountsPanel({ accounts, health, ready, error }: { accounts: HyperliquidAccount[]; health: Record<string, HyperliquidHealth>; ready: boolean; error: string }) {
  return <div className="settings-content"><div className="page-title"><div><h1>交易所账户</h1><p>API Wallet 按账户网络签名；查询始终使用主账户公开地址。</p></div></div>
    <div className="system-cards"><SystemMetric label="Hyperliquid 交易" value={ready ? 'READY' : 'NOT CONFIGURED'} caption={ready ? 'API Wallet 已授权' : '等待后端环境变量'} /><SystemMetric label="密钥入口" value="BACKEND ONLY" caption="不会经过浏览器" /><SystemMetric label="网络边界" value="TESTNET / MAINNET" caption="按账户网络执行" /></div>
    <section className="panel accounts"><h2>交易所账户</h2><div className="account-row"><div className="account-logo">P</div><div><b>Weekend Paper</b><span>acct_paper_01</span></div><span className="env-badge">PAPER</span><span>无需凭证</span><strong className="positive">✓ 检查通过</strong></div>
      {accounts.map(account => { const check = health[account.accountId]; return <div className="account-row" key={account.accountId}><div className="account-logo">H</div><div><b>{account.name}</b><span>{short(account.accountAddress)} 查询 · {short(account.agentAddress)} 签名</span></div><span className="env-badge">{account.environment}</span><span>{check ? accountMode(check.accountMode) + ' · Trading Equity ' + check.tradingEquity + ' USDC · 可用 ' + check.availableBalance + ' USDC · 持仓 ' + check.netPosition : '检查中…'}</span><strong className={check?.tradingReady ? 'positive' : 'warning-text'}>{check?.tradingReady ? '✓ 可交易' : '未就绪'}</strong></div> })}
      {accounts.length === 0 && <div className="account-row"><div className="account-logo">H</div><div><b>Hyperliquid Testnet</b><span>请在启动后端前设置 3 个环境变量</span></div><span className="env-badge">TESTNET</span><span>未配置 API Wallet</span><strong className="dim">禁用</strong></div>}
    </section>
    <section className="panel service-table"><h2>安全配置方式</h2><p className="dim">设置 <code>GRID_TRADING_CREDENTIAL_KEY</code>、<code>GRID_TRADING_HL_TESTNET_ACCOUNT_ADDRESS</code> 和 <code>GRID_TRADING_HL_TESTNET_AGENT_PRIVATE_KEY</code>（Mainnet 使用对应的 MAINNET 变量） 后重启后端。私钥经 AES-GCM 加密后存入 SQLite；前端与账户 API 只返回公开地址。</p>{error && <p className="warning-text">{error}</p>}</section>
    <section className="safety-banner"><Icon name="shield" /><div><b>Mainnet · 真实资金</b><p>Mainnet 使用独立 API Wallet、官方主机与签名域。按你保存的策略参数执行；点击 Start 才会启动新的交易 Cycle。</p></div></section>
  </div>
}

function SystemMetric({ label, value, caption }: { label: string; value: string; caption: string }) { return <section className="panel"><small>{label.toUpperCase()}</small><b>{value}</b><span>{caption}</span></section> }
function short(value: string) { return value.length > 14 ? value.slice(0, 8) + '…' + value.slice(-4) : value }
function accountMode(value: string) { return value === 'unifiedAccount' ? 'Unified Account' : value === 'portfolioMargin' ? 'Portfolio Margin' : 'Standard Account' }
function formatTime(value: string) { return new Date(value).toLocaleString('zh-CN', { hour12: false }) }
function message(error: unknown, fallback: string) { return error instanceof Error ? error.message : fallback }
