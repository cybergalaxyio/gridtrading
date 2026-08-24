import { useMemo, useState } from 'react'
import { Icon } from '../components/Icon'
import type { Strategy } from '../types'

export function StrategiesPage({ strategies, onOpen, onCreate }: { strategies: Strategy[]; onOpen: () => void; onCreate: () => void }) {
  const [filter, setFilter] = useState('全部')
  const [search, setSearch] = useState('')
  const rows = useMemo(() => strategies.filter(x => (!search || `${x.name}${x.symbol}`.toLowerCase().includes(search.toLowerCase())) &&
    (filter === '全部' || (filter === '运行中' && x.activeCycle?.state === 'RUNNING') || (filter === '已暂停' && x.activeCycle?.state === 'PAUSED') || (filter === '已停止' && !x.activeCycle))), [strategies, filter, search])
  return <div className="page padded"><div className="page-title"><div><h1>策略列表</h1><p>管理策略模板。修改只影响未来 Cycle，运行中的参数已冻结。</p></div><button className="primary" onClick={onCreate}><Icon name="plus" size={17} /> 新建策略</button></div>
    <section className="panel list-panel"><div className="list-toolbar"><div className="filter-tabs">{['全部', '运行中', '已暂停', '已停止'].map(x => <button key={x} className={filter === x ? 'active' : ''} onClick={() => setFilter(x)}>{x} ({count(strategies, x)})</button>)}</div>
      <label className="search"><Icon name="search" size={16} /><input placeholder="搜索策略或交易对" value={search} onChange={e => setSearch(e.target.value)} /></label></div>
      <div className="table-wrap"><table><thead><tr><th>策略名称</th><th>交易对</th><th>交易所 / 环境</th><th>状态</th><th>中心模式</th><th>层数</th><th>风险上限</th><th>操作</th></tr></thead>
        <tbody>{rows.map((x, index) => <tr key={x.strategyId}><td><b>{x.name}</b><small>#{String(index + 1).padStart(3, '0')} · v{x.version}</small></td><td className="mono">{x.symbol}</td><td>Paper Simulator<small>本地安全环境</small></td>
          <td><span className={`status-pill ${x.activeCycle?.state.toLowerCase() ?? 'stopped'}`}><i />{stateCn(x.activeCycle?.state)}</span></td><td>{x.configuration.centerSuggestionMode}</td><td>{x.configuration.maxLevelsPerSide * 2}</td>
          <td className="mono">{x.configuration.maxNetLot} SOL</td><td><button className="link" onClick={onOpen}>打开 →</button></td></tr>)}</tbody></table>
        {rows.length === 0 && <div className="empty"><span>◇</span>没有符合筛选条件的策略</div>}
      </div><footer className="list-footer">显示 {rows.length} / {strategies.length} 条策略 <span>V1 · 单个策略仅允许一个非终态 Cycle</span></footer>
    </section>
  </div>
}
function stateCn(state?: string) { return state === 'RUNNING' ? '运行中' : state === 'PAUSED' ? '已暂停' : '已停止' }
function count(items: Strategy[], filter: string) { if (filter === '全部') return items.length; return items.filter(x => stateCn(x.activeCycle?.state) === filter).length }
