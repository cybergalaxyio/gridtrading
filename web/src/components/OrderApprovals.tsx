import { useEffect, useRef, useState } from 'react'
import { AccountLabel } from '../context/AccountsContext'
import { useOrderApprovals } from '../context/OrderApprovalsContext'
import { OrderApprovalActions, OrderApprovalStatus } from './OrderApprovalActions'
import { Modal } from './Modal'

export function OrderApprovals() {
  const { orders: allOrders, error, refresh } = useOrderApprovals()
  const orders = allOrders.filter(order => order.status !== 'REJECTED')
  const [open, setOpen] = useState(false)
  const known = useRef(new Set<string>())
  useEffect(() => {
    if (allOrders.some(order => order.status === 'PENDING' && !known.current.has(order.id))) setOpen(true)
    known.current = new Set(allOrders.map(order => order.id))
  }, [allOrders])

  return <>
    {(orders.length > 0 || error) && <div className="order-approval-banner" role="status">
      <span>{orders.length} 笔订单等待审核 / 发送{error ? ' · 订单列表需要刷新' : ''}</span>
      <button className="primary" onClick={() => { setOpen(true); void refresh() }}>查看订单</button>
    </div>}
    {open && <Modal title="逐笔订单确认" icon="shield" className="order-approval-modal" onClose={() => setOpen(false)}>
      <div className="order-approval-body">
        <p className="dim">以下是实际待发送订单。每次确认仅允许发送该笔订单；价格、数量或订单类型变化后需要重新确认。关闭窗口后，可在 Open Orders 中逐笔 Confirm 或 Reject。</p>
        {error && <p className="warning-text" role="alert">{error} <button className="secondary" onClick={() => { void refresh() }}>刷新</button></p>}
        {!orders.length && <p role="status">暂无待确认订单</p>}
        {orders.map(order => <section className="order-approval-card panel" key={order.id}>
          <header><strong className={order.side === 'BUY' ? 'positive' : 'negative'}>{order.side} · {order.symbol}</strong><span>{order.kind} · {order.action === 'AMEND' ? '改单' : '新订单'}</span></header>
          <dl>
            <div><dt>价格 / 限价</dt><dd>{order.price}</dd></div>
            <div><dt>发送数量</dt><dd>{order.quantity}</dd></div>
            <div><dt>订单类型</dt><dd>LIMIT · {order.timeInForce === 'Alo' ? 'Post Only (ALO)' : order.timeInForce === 'Ioc' ? 'IOC' : 'GTC'}</dd></div>
            <div><dt>Reduce Only</dt><dd>{order.reduceOnly ? 'YES' : 'NO'}</dd></div>
            <div><dt>账户</dt><dd><AccountLabel accountId={order.executionAccountId} environmentId={order.executionEnvironmentId} /></dd></div>
            <div><dt>环境</dt><dd className={order.executionEnvironmentId === 'hyperliquid-mainnet' ? 'warning-text' : ''}>{order.executionEnvironmentId}</dd></div>
            <div><dt>策略 / 层级</dt><dd>{order.strategyName} / {order.gridLevel < 0 ? '平仓' : order.gridLevel}</dd></div>
            <div><dt>Order ID</dt><dd>{order.orderId}</dd></div>
            <div><dt>生成时间</dt><dd>{new Date(order.createdAt).toLocaleString()}</dd></div>
          </dl>
          <footer><OrderApprovalStatus order={order} /><OrderApprovalActions order={order} /></footer>
        </section>)}
        <button className="secondary" onClick={() => setOpen(false)}>稍后处理</button>
      </div>
    </Modal>}
  </>
}
