import { useEffect, useState, type ReactNode } from 'react'
import { Icon } from './Icon'

export type Route = 'dashboard' | 'strategies' | 'orders' | 'alerts' | 'settings' | 'create'

const nav: { route: Route; label: string; icon: string }[] = [
  { route: 'dashboard', label: '控制台', icon: 'dashboard' },
  { route: 'strategies', label: '策略', icon: 'strategy' },
  { route: 'orders', label: '订单与成交', icon: 'orders' },
  { route: 'alerts', label: '风险与告警', icon: 'alert' },
  { route: 'settings', label: '系统设置', icon: 'settings' },
]

export function Layout({ route, environment, onRoute, onEmergency, children }: {
  route: Route; environment: 'PAPER' | 'TESTNET'; onRoute: (route: Route) => void; onEmergency: () => void; children: ReactNode
}) {
  const isTestnet = environment === 'TESTNET'
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
        <Icon name={item.icon} size={22} /><span>{item.label}</span>{item.route === 'alerts' && <i>2</i>}
      </button>)}</nav>
      <div className="sidebar-safety"><Icon name="shield" /><div><b>实盘硬锁已启用</b><span>仅 Replay / Paper / Testnet</span></div></div>
    </aside>
    <div className="workspace">
      <header className="topbar">
        <div className="brand">GRID TRADING</div><div id="execution-context-slot" className="execution-context-slot">{route !== 'dashboard' && <span className="env">{environment}</span>}</div>
        <span className="connection"><i />{isTestnet ? 'Hyperliquid Testnet · 已连接' : '本地模拟 · 已连接'}</span><span className="latency">{isTestnet ? '官方行情 · 10s 刷新' : '行情延迟 < 10ms'}</span>
        <time>UTC {clock.toISOString().slice(11, 19)}</time>
        <button className="danger-outline" onClick={onEmergency}>Shutdown</button>
      </header>
      <main>{children}</main>
    </div>
  </div>
}
