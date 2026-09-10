import { useMemo, useState } from 'react'
import { Icon } from '../components/Icon'
import type { Strategy } from '../types'

export function StrategiesPage({ strategies, onLoad, onCreate, onEdit }: { strategies: Strategy[]; onLoad: (strategy: Strategy) => void; onCreate: () => void; onEdit: (strategy: Strategy) => void }) {
  const [filter, setFilter] = useState('全部')
  const [search, setSearch] = useState('')
  const rows = useMemo(() => strategies.filter(x => (!search || `${x.name}${x.symbol}`.toLowerCase().includes(search.toLowerCase())) &&
    (filter === '全部' || (filter === '运行中' && x.activeCycle?.state === 'RUNNING') || (filter === '已暂停' && x.activeCycle?.state === 'PAUSED') || (filter === '已停止' && !x.activeCycle))), [strategies, filter, search])
  return <div className="page padded"><div className="page-title"><div><h1>策略列表</h1><p>管理策略实例。修改只影响未来 Cycle，运行中的参数已冻结。</p></div><button className="primary" onClick={onCreate}><Icon name="plus" size={17} /> 新建策略</button></div>
    <section className="panel list-panel"><div className="list-toolbar"><div className="filter-tabs">{['全部', '运行中', '已暂停', '已停止'].map(x => <button key={x} className={filter === x ? 'active' : ''} onClick={() => setFilter(x)}>{x} ({count(strategies, x)})</button>)}</div>
      <label className="search"><Icon name="search" size={16} /><input placeholder="搜索策略或交易对" value={search} onChange={e => setSearch(e.target.value)} /></label></div>
      <div className="table-wrap"><table><thead><tr><th>策略名称</th><th>交易对</th><th>交易所 / 环境</th><th>状态</th><th>中心模式</th><th>层数</th><th>风险上限</th><th>操作</th></tr></thead>
        <tbody>{rows.map((x, index) => <tr key={x.strategyId}><td><b>{x.name}</b><small>#{String(index + 1).padStart(3, '0')} · v{x.version}</small></td><td className="mono">{displaySymbol(x.symbol)}</td><td>{x.defaultExecutionEnvironmentId === 'paper-local' ? 'Paper Simulator' : 'Hyperliquid'}<small>{x.defaultExecutionEnvironmentId === 'paper-local' ? '本地安全环境' : 'TESTNET'}</small></td>
          <td><span className={`status-pill ${x.activeCycle?.state.toLowerCase() ?? 'stopped'}`}><i />{x.activeCycle?.riskPaused ? (x.activeCycle.operatorPaused ? '风险 + 人工暂停' : '风险暂停开仓') : stateCn(x.activeCycle?.state)}</span></td><td>{x.configuration.centerSuggestionMode}</td><td>{plannedLevelCount(x)}</td>
          <td className="mono">{x.configuration.maxNetLot} SOL</td><td><div className="strategy-actions"><button className="link" onClick={() => onEdit(x)}>Edit</button><button className="link" onClick={() => onLoad(x)}>Load</button></div></td></tr>)}</tbody></table>
        {rows.length === 0 && <div className="empty"><span>◇</span>没有符合筛选条件的策略</div>}
      </div><footer className="list-footer">显示 {rows.length} / {strategies.length} 条策略 <span>V1 · 单个策略仅允许一个非终态 Cycle</span></footer>
    </section>
  </div>
}
function stateCn(state?: string) { return state === 'RUNNING' ? '运行中' : state === 'PAUSED' ? '已暂停' : '已停止' }
function count(items: Strategy[], filter: string) { if (filter === '全部') return items.length; return items.filter(x => stateCn(x.activeCycle?.state) === filter).length }
function displaySymbol(symbol: string) { return `${symbol.toUpperCase().replace(/[-_/]?(USDC|USDT)$/, '')}-USDC` }
function plannedLevelCount(strategy: Strategy) { const config = strategy.activeCycle?.frozenConfiguration ?? strategy.configuration; return config.maxLevelsPerSide * ((config.gridMode ?? 'TWO_WAY') === 'TWO_WAY' ? 2 : 1) }
