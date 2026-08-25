import { useEffect, useState } from 'react'
import { api } from '../api'
import { Icon } from '../components/Icon'
import type { HyperliquidAccount, HyperliquidHealth } from '../types'

export function SettingsPage() {
  const [accounts, setAccounts] = useState<HyperliquidAccount[]>([])
  const [health, setHealth] = useState<Record<string, HyperliquidHealth>>({})
  const [error, setError] = useState('')

  useEffect(() => {
    void api.testnetAccounts().then(async rows => {
      setAccounts(rows)
      const checks = await Promise.all(rows.map(async row => [row.accountId, await api.testnetHealth(row.accountId)] as const))
      setHealth(Object.fromEntries(checks))
    }).catch(e => setError(e instanceof Error ? e.message : 'Testnet 状态读取失败'))
  }, [])

  const ready = accounts.some(x => health[x.accountId]?.tradingReady)
  return <div className="settings-page"><aside className="settings-nav"><h2>SETTINGS</h2>{['系统状态', '交易所账户', '通知设置', '界面设置', '审计与监控'].map((x, i) => <button className={i === 1 ? 'active' : ''} key={x}><Icon name={i === 0 ? 'dashboard' : i === 1 ? 'strategy' : i === 4 ? 'alert' : 'settings'} size={18} />{x}</button>)}</aside>
    <div className="settings-content"><div className="page-title"><div><h1>交易所账户</h1><p>API Wallet 只负责 Testnet 签名；查询始终使用主账户公开地址。</p></div></div>
      <div className="system-cards"><SystemMetric label="Testnet 交易" value={ready ? 'READY' : 'NOT CONFIGURED'} caption={ready ? 'API Wallet 已授权' : '等待后端环境变量'} /><SystemMetric label="密钥入口" value="BACKEND ONLY" caption="不会经过浏览器" /><SystemMetric label="网络边界" value="TESTNET" caption="Mainnet 硬锁" /></div>
      <section className="panel accounts"><h2>交易所账户</h2><div className="account-row"><div className="account-logo">P</div><div><b>Weekend Paper</b><span>acct_paper_01</span></div><span className="env-badge">PAPER</span><span>无需凭证</span><strong className="positive">✓ 检查通过</strong></div>
        {accounts.map(account => { const check = health[account.accountId]; return <div className="account-row" key={account.accountId}><div className="account-logo">H</div><div><b>{account.name}</b><span>{short(account.accountAddress)} 查询 · {short(account.agentAddress)} 签名</span></div><span className="env-badge">TESTNET</span><span>{check ? `权益 ${check.accountValue} USDC · 持仓 ${check.netPosition}` : '检查中…'}</span><strong className={check?.tradingReady ? 'positive' : 'warning-text'}>{check?.tradingReady ? '✓ 可交易' : '未就绪'}</strong></div> })}
        {accounts.length === 0 && <div className="account-row"><div className="account-logo">H</div><div><b>Hyperliquid Testnet</b><span>请在启动后端前设置 3 个环境变量</span></div><span className="env-badge">TESTNET</span><span>未配置 API Wallet</span><strong className="dim">禁用</strong></div>}
      </section>
      <section className="panel service-table"><h2>安全配置方式</h2><p className="dim">设置 <code>GRID_TRADING_CREDENTIAL_KEY</code>、<code>GRID_TRADING_HL_TESTNET_ACCOUNT_ADDRESS</code> 和 <code>GRID_TRADING_HL_TESTNET_AGENT_PRIVATE_KEY</code> 后重启后端。私钥经 AES-GCM 加密后存入 SQLite；前端与账户 API 只返回公开地址。</p>{error && <p className="warning-text">{error}</p>}</section>
      <section className="safety-banner"><Icon name="shield" /><div><b>Funded Live Account 硬锁</b><p>所有签名端点固定为 Hyperliquid 官方 Testnet HTTPS 主机。Mainnet / Live 账户会被服务端拒绝，无法从界面绕过。</p></div></section>
    </div>
  </div>
}

function SystemMetric({ label, value, caption }: { label: string; value: string; caption: string }) { return <section className="panel"><small>{label.toUpperCase()}</small><b>{value}</b><span>{caption}</span></section> }
function short(value: string) { return value.length > 14 ? `${value.slice(0, 8)}…${value.slice(-4)}` : value }
