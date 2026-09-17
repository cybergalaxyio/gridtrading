import { useEffect, useState, type ReactNode } from 'react'
import { Icon } from './Icon'

export type Route = 'dashboard' | 'strategies' | 'orders' | 'alerts' | 'settings' | 'create'

const nav: { route: Route; label: string; icon: string }[] = [
  { route: 'dashboard', label: 'Dashboard', icon: 'dashboard' },
  { route: 'strategies', label: 'Strategies', icon: 'strategy' },
  { route: 'orders', label: 'Orders & Fills', icon: 'orders' },
  { route: 'alerts', label: 'Risks', icon: 'alert' },
  { route: 'settings', label: 'Settings', icon: 'settings' },
]

export function Layout({ route, environment, emergencyBusy, onRoute, onEmergency, children }: {
  route: Route; environment: 'PAPER' | 'TESTNET' | 'MAINNET'; emergencyBusy: boolean;
  onRoute: (route: Route) => void; onEmergency: () => void; children: ReactNode
}) {
  const isHyperliquid = environment !== 'PAPER'
  const [clock, setClock] = useState(new Date())
  const [sidebarCollapsed, setSidebarCollapsed] = useState(() => localStorage.getItem('grid.sidebarCollapsed') === 'true')
  useEffect(() => { const timer = setInterval(() => setClock(new Date()), 1000); return () => clearInterval(timer) }, [])
  useEffect(() => { localStorage.setItem('grid.sidebarCollapsed', String(sidebarCollapsed)) }, [sidebarCollapsed])
  return <div className={`app-shell${sidebarCollapsed ? ' sidebar-collapsed' : ''}`}>
    <aside className="sidebar">
      <div className="profile"><div className="avatar">G</div><div><strong>Grid Operator</strong><span>V1.0.0 · {environment}</span></div></div>
      <button className="sidebar-toggle" type="button" onClick={() => setSidebarCollapsed(value => !value)}
        aria-label={sidebarCollapsed ? '展开侧栏' : '收起侧栏'} title={sidebarCollapsed ? '展开侧栏' : '收起侧栏'}>
        <Icon name={sidebarCollapsed ? 'expand' : 'collapse'} size={16} />
      </button>
      <nav>{nav.map(item => <button key={item.route} className={route === item.route || (route === 'create' && item.route === 'strategies') ? 'active' : ''} onClick={() => onRoute(item.route)}>
        <Icon name={item.icon} size={22} /><span>{item.label}</span>
      </button>)}</nav>
      <div className="sidebar-safety"><Icon name="shield" /><div><b>执行环境隔离</b><span>Mainnet 使用真实资金</span></div></div>
    </aside>
    <div className="workspace">
      <header className="topbar">
        <div className="brand">GRID TRADING</div><div id="execution-context-slot" className="execution-context-slot">{route !== 'dashboard' && <span className={`env${environment === 'MAINNET' ? ' mainnet-highlight' : ''}`}>{environment === 'MAINNET' ? 'MAINNET · REAL FUNDS' : environment}</span>}</div>
        <span className="connection"><i />{isHyperliquid ? `Hyperliquid ${environment}` : '本地模拟 · 已连接'}</span><span className="latency">{isHyperliquid ? '官方行情 · 10s 刷新' : '行情延迟 < 10ms'}</span>
        <time>UTC {clock.toISOString().slice(11, 19)}</time>
        <button className="danger-outline" disabled={emergencyBusy} aria-busy={emergencyBusy} onClick={onEmergency}>{emergencyBusy ? 'Stopping…' : 'Shutdown'}</button>
      </header>
      <main>{children}</main>
    </div>
  </div>
}
