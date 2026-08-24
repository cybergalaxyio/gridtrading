import { Icon } from '../components/Icon'

export function SettingsPage() {
  return <div className="settings-page"><aside className="settings-nav"><h2>SETTINGS</h2>{['系统状态', '交易所账户', '通知设置', '界面设置', '审计与监控'].map((x, i) => <button className={i === 0 ? 'active' : ''} key={x}><Icon name={i === 0 ? 'dashboard' : i === 1 ? 'strategy' : i === 4 ? 'alert' : 'settings'} size={18} />{x}</button>)}</aside>
    <div className="settings-content"><div className="page-title"><div><h1>系统状态</h1><p>监控交易引擎健康状况与网络延迟</p></div></div>
      <div className="system-cards"><SystemMetric label="引擎版本" value="v1.0.0-paper" caption="安全模式" /><SystemMetric label="运行时间" value="00d 00h 01m" caption="当前进程" /><SystemMetric label="服务器时间差" value="+0.0ms" caption="UTC 同步" /></div>
      <section className="panel service-table"><h2>服务组件</h2><table><thead><tr><th>服务</th><th>说明</th><th>延迟</th><th>状态</th></tr></thead><tbody>
        {[['SQLite Database', 'WAL · Foreign Keys · Busy Timeout', '4ms', '正常'], ['Paper Exchange Gateway', '确定性本地模拟环境', '<10ms', '正常'], ['Hyperliquid Testnet', '只读边界，未配置签名密钥', '—', '禁用交易'], ['SignalR Event Hub', '/hubs/trading', '2ms', '正常']].map(x => <tr key={x[0]}><td><b>{x[0]}</b></td><td className="dim">{x[1]}</td><td className="mono">{x[2]}</td><td><span className={x[3] === '正常' ? 'positive' : 'warning-text'}>● {x[3]}</span></td></tr>)}</tbody></table></section>
      <section className="panel accounts"><h2>交易所账户</h2><div className="account-row"><div className="account-logo">P</div><div><b>Weekend Paper</b><span>acct_paper_01</span></div><span className="env-badge">PAPER</span><span>无需凭证</span><strong className="positive">✓ 检查通过</strong></div>
        <div className="account-row"><div className="account-logo">H</div><div><b>Hyperliquid Testnet</b><span>查询与签名身份分离</span></div><span className="env-badge">TESTNET</span><span>未配置 API Wallet</span><strong className="dim">只读</strong></div></section>
      <section className="safety-banner"><Icon name="shield" /><div><b>Funded Live Account 硬锁</b><p>V1 服务端拒绝 Mainnet / Live 账户和实盘启动请求。实盘启用需要另行授权与部署配置，不可从此界面绕过。</p></div></section>
    </div>
  </div>
}
function SystemMetric({ label, value, caption }: { label: string; value: string; caption: string }) { return <section className="panel"><small>{label.toUpperCase()}</small><b>{value}</b><span>{caption}</span></section> }
