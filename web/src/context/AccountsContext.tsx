import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { api } from '../api'
import type { HyperliquidAccount } from '../types'

const AccountsContext = createContext<{
  accounts: HyperliquidAccount[]; loading: boolean; error: string; revision: number; refresh: () => Promise<void>
}>({ accounts: [], loading: true, error: '', revision: 0, refresh: async () => {} })

export function AccountsProvider({ children }: { children: ReactNode }) {
  const [accounts, setAccounts] = useState<HyperliquidAccount[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [revision, setRevision] = useState(0)
  const sequence = useRef(0)
  const fingerprint = useRef('')
  const refresh = useCallback(async () => {
    const current = ++sequence.current
    try {
      const lists = await Promise.all([api.hyperliquidAccounts('hyperliquid-mainnet'), api.hyperliquidAccounts()])
      if (current !== sequence.current) return
      const rows = lists.flat()
      setAccounts(rows)
      // Only configuration changes invalidate account selectors, not nonce updates.
      const next = JSON.stringify(rows.map(x => [x.accountId, x.name, x.environment, x.enabled, x.agentAddress]))
      if (fingerprint.current !== next) { fingerprint.current = next; setRevision(x => x + 1) }
      setError('')
    } catch (e) {
      if (current === sequence.current) setError(e instanceof Error ? e.message : '账户读取失败')
    } finally {
      if (current === sequence.current) setLoading(false)
    }
  }, [])
  useEffect(() => {
    const update = () => { void refresh() }
    update()
    const timer = window.setInterval(update, 5000)
    window.addEventListener('focus', update)
    return () => { sequence.current++; window.clearInterval(timer); window.removeEventListener('focus', update) }
  }, [refresh])
  return <AccountsContext.Provider value={{ accounts, loading, error, revision, refresh }}>{children}</AccountsContext.Provider>
}

export function useAccounts() { return useContext(AccountsContext) }

export function AccountLabel({ accountId, environmentId }: { accountId?: string | null; environmentId?: string | null }) {
  const { accounts } = useAccounts()
  const account = accounts.find(x => x.accountId === accountId)
  const network = account?.environment ?? (environmentId === 'paper-local' ? 'PAPER' : environmentId === 'hyperliquid-mainnet' ? 'MAINNET' : environmentId === 'hyperliquid-testnet' ? 'TESTNET' : '')
  return <span className="account-label" title={account ? `${account.accountAddress} · ${accountId}` : accountId ?? ''}>
    {account?.name ?? (accountId === 'acct_paper_01' ? 'Weekend Paper' : accountId ?? '系统')}
    {network && <small className={network === 'MAINNET' ? 'warning-text' : 'dim'}>{network}</small>}
  </span>
}
