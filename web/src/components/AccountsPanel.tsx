import { useEffect, useRef, useState, type FormEvent } from 'react'
import { api } from '../api'
import { useAccounts } from '../context/AccountsContext'
import type { HyperliquidAccount, HyperliquidHealth } from '../types'
import { Icon } from './Icon'
import { Modal } from './Modal'

type Editor = { kind: 'create' } | { kind: 'rename' | 'credentials'; account: HyperliquidAccount }
const environmentId = (account: HyperliquidAccount) => account.environment === 'MAINNET' ? 'hyperliquid-mainnet' : 'hyperliquid-testnet'
const errorMessage = (error: unknown) => error instanceof Error ? error.message : '请求失败，请重试'

export function AccountsPanel() {
  const { accounts, loading, error: catalogError, revision, refresh } = useAccounts()
  const [health, setHealth] = useState<Record<string, HyperliquidHealth>>({})
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [checking, setChecking] = useState<Record<string, boolean>>({})
  const [editor, setEditor] = useState<Editor | null>(null)
  const [name, setName] = useState('')
  const [network, setNetwork] = useState('hyperliquid-testnet')
  const [address, setAddress] = useState('')
  const [privateKey, setPrivateKey] = useState('')
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const requests = useRef<Record<string, number>>({})

  async function test(account: HyperliquidAccount) {
    const id = account.accountId
    const sequence = (requests.current[id] ?? 0) + 1
    requests.current[id] = sequence
    setChecking(current => ({ ...current, [id]: true }))
    setErrors(current => ({ ...current, [id]: '' }))
    try {
      const result = await api.hyperliquidHealth(id, environmentId(account))
      if (requests.current[id] === sequence) setHealth(current => ({ ...current, [id]: result }))
    } catch (e) {
      if (requests.current[id] === sequence) {
        setErrors(current => ({ ...current, [id]: errorMessage(e) }))
        setHealth(current => { const next = { ...current }; delete next[id]; return next })
      }
    } finally {
      if (requests.current[id] === sequence) setChecking(current => ({ ...current, [id]: false }))
    }
  }

  useEffect(() => {
    for (const account of accounts) void test(account)
    return () => { for (const id of Object.keys(requests.current)) requests.current[id]++ }
    // Account configuration changes invalidate health; five-second catalog polling does not.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [revision])

  function open(value: Editor) {
    setEditor(value); setPrivateKey(''); setError(''); setNotice('')
    setName(value.kind === 'create' ? '' : value.account.name)
    setAddress(value.kind === 'create' ? '' : value.account.accountAddress)
    setNetwork(value.kind === 'create' ? 'hyperliquid-testnet' : environmentId(value.account))
  }
  function close() { if (!busy) { setPrivateKey(''); setEditor(null); setError('') } }

  async function save(event: FormEvent) {
    event.preventDefault()
    if (!editor || busy) return
    const target = editor
    const submittedKey = privateKey
    setPrivateKey(''); setBusy('editor'); setError(''); setNotice('')
    try {
      if (target.kind === 'create') {
        await api.createAccount(network, { name, accountAddress: address, agentPrivateKey: submittedKey })
        setNotice('账户已保存并禁用。请测试并启用，然后选择账户创建策略。')
      } else if (target.kind === 'rename') {
        await api.renameAccount(network, target.account.accountId, { name })
        setNotice('账户名称已更新')
      } else {
        await api.replaceAccountCredentials(network, target.account.accountId, { agentPrivateKey: submittedKey })
        setNotice('API Wallet 已替换，账户已禁用。请重新测试并启用。')
      }
      setEditor(null)
      await refresh()
    } catch (e) { setError(errorMessage(e)) }
    finally { setBusy('') }
  }

  async function changeEnabled(account: HyperliquidAccount, enable: boolean) {
    if (busy) return
    setBusy(account.accountId); setError(''); setNotice('')
    try {
      if (enable) await api.enableAccount(environmentId(account), account.accountId)
      else await api.disableAccount(environmentId(account), account.accountId)
      setNotice(`${account.name} ${enable ? '已验证并启用；点击策略 Start 才会交易' : '已禁用；交易历史保留'}`)
      await refresh()
    } catch (e) { setError(errorMessage(e)); await refresh() }
    finally { setBusy('') }
  }

  return <div className="settings-content account-settings">
    <div className="page-title"><div><h1>交易所账户</h1><p>添加多个 API Wallet，在不同账户上同时运行策略。</p></div><button className="primary" disabled={!!busy} onClick={() => open({ kind: 'create' })}><Icon name="plus" size={16} /> 添加账户</button></div>
    {notice && <p role="status" className="positive">{notice}</p>}
    {(catalogError || error && !editor) && <p role="alert" className="warning-text">{catalogError || error}</p>}
    <section className="panel account-paper"><b>Weekend Paper</b><span className="env-badge">PAPER</span><span className="dim">无需凭证</span></section>
    {loading && <p className="dim">正在读取账户…</p>}
    {!loading && accounts.length === 0 && !catalogError && <section className="panel account-empty"><h2>还没有交易所账户</h2><p>点击“添加账户”，填写主账户公开地址和已授权的 API Wallet 私钥。</p></section>}
    <div className="account-cards">{accounts.map(account => {
      const check = health[account.accountId]
      const blocked = account.blockers.length > 0
      return <section className="panel account-card" key={account.accountId}>
        <header><div><h2>{account.name}</h2><span className={`env-badge ${account.environment === 'MAINNET' ? 'warning-text' : ''}`}>{account.environment === 'MAINNET' ? 'MAINNET · REAL FUNDS' : 'TESTNET'}</span></div><strong className={account.enabled ? 'positive' : 'dim'}>{account.enabled ? '已启用' : '已禁用'}</strong></header>
        <dl><div><dt>主账户</dt><dd className="mono">{account.accountAddress}</dd></div><div><dt>API Wallet</dt><dd className="mono">{account.agentAddress}</dd></div><div><dt>活动 Cycle</dt><dd>{account.activeCycleCount}</dd></div>
          <div><dt>连接 / 授权</dt><dd>{checking[account.accountId] ? '检查中…' : errors[account.accountId] ? '检查失败' : check ? check.agentApproved ? '已连接 · API Wallet 已授权' : '已连接 · API Wallet 未授权' : '尚未检查'}</dd></div>
          <div><dt>Trading Equity / 可用余额</dt><dd>{check ? `${check.tradingEquity} / ${check.availableBalance} USDC` : '—'}</dd></div>
          <div><dt>资金就绪</dt><dd>{check ? +check.tradingEquity > 0 && +check.availableBalance > 0 ? '有可用资金；Start 时检查持仓与挂单' : '需要入金或释放保证金' : '—'}</dd></div>
          {check && <div><dt>检查时间</dt><dd>{new Date(check.asOf).toLocaleString()}</dd></div>}
        </dl>
        {errors[account.accountId] && <p role="alert" className="warning-text">{errors[account.accountId]}</p>}
        {blocked && <p className="account-blockers">暂停账户或替换密钥前，请关闭活动 Cycle，并关闭待执行的自动重启：{account.blockers.map(x => `${x.strategyName} (${x.reason === 'ACTIVE_CYCLE' ? '活动 Cycle' : '等待自动重启'})`).join('、')}</p>}
        <div className="account-actions">
          <button className="secondary" disabled={!!busy || checking[account.accountId]} onClick={() => void test(account)}>测试连接</button>
          {!account.enabled && <button className="primary" disabled={!!busy} onClick={() => void changeEnabled(account, true)}>测试并启用</button>}
          <button className="secondary" disabled={!!busy} onClick={() => open({ kind: 'rename', account })}>重命名</button>
          <button className="secondary" disabled={!!busy || blocked} onClick={() => open({ kind: 'credentials', account })}>替换 API Wallet</button>
          {account.enabled && <button className="danger-outline" disabled={!!busy || blocked} onClick={() => void changeEnabled(account, false)}>禁用</button>}
        </div>
      </section>
    })}</div>
    <section className="safety-banner"><Icon name="shield" /><div><b>本机账户管理</b><p>仅输入已授权的 API Wallet 私钥。密钥只在保存时发送到本机后端，加密保存后不会返回浏览器。添加或测试账户不会下单。</p><p>后端需要固定的 <code>GRID_TRADING_CREDENTIAL_KEY</code>；网络和主账户地址保存后不可修改。</p></div></section>
    {editor && <Modal title={editor.kind === 'create' ? '添加账户' : editor.kind === 'rename' ? '重命名账户' : '替换 API Wallet'} icon="settings" onClose={close}>
      <form className="account-form" onSubmit={event => void save(event)}>
        {editor.kind !== 'credentials' && <label>账户名称<input required maxLength={100} value={name} onChange={event => setName(event.target.value)} disabled={!!busy} /></label>}
        {editor.kind === 'create' && <><label>网络<select value={network} onChange={event => setNetwork(event.target.value)} disabled={!!busy}><option value="hyperliquid-testnet">Hyperliquid Testnet</option><option value="hyperliquid-mainnet">Hyperliquid Mainnet · REAL FUNDS</option></select></label><label>主账户公开地址<input required value={address} onChange={event => setAddress(event.target.value)} placeholder="0x…" autoComplete="off" spellCheck={false} disabled={!!busy} /></label></>}
        {editor.kind !== 'create' && <p>{editor.account.name} · {editor.account.environment}<br /><span className="mono">{editor.account.accountAddress}</span></p>}
        {editor.kind !== 'rename' && <label>API Wallet 私钥<input type="password" required value={privateKey} onChange={event => setPrivateKey(event.target.value)} autoComplete="off" spellCheck={false} placeholder="0x…" disabled={!!busy} /><small>请勿输入主钱包私钥。提交后清空此字段；重试时需重新输入。</small></label>}
        {error && <p role="alert" className="warning-text">{error}</p>}
        <div className="modal-actions"><button type="button" className="secondary" disabled={!!busy} onClick={close}>取消</button><button type="submit" className="primary" disabled={!!busy}>{busy ? '保存中…' : '保存'}</button></div>
      </form>
    </Modal>}
  </div>
}
