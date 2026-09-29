import { useOrderApprovals } from '../context/OrderApprovalsContext'
import type { OrderApproval } from '../types'

export function OrderApprovalActions({ order }: { order: OrderApproval }) {
  const { busy, approve, reject } = useOrderApprovals()
  return <div className="order-approval-actions">
    <button type="button" className="primary" disabled={busy !== null || order.status === 'APPROVED'}
      aria-label={`Confirm ${order.orderId}`} onClick={() => void approve(order.id)}>
      {busy === order.id ? '处理中…' : order.status === 'APPROVED' ? 'Confirmed' : 'Confirm'}
    </button>
    <button type="button" className="danger-outline" disabled={busy !== null || order.status === 'REJECTED'}
      aria-label={`Reject ${order.orderId}`} onClick={() => void reject(order.id)}>Reject</button>
  </div>
}

export function OrderApprovalStatus({ order }: { order: OrderApproval }) {
  return <span className={`approval-status ${order.status.toLowerCase()}`}>
    {order.status === 'APPROVED' ? 'Confirmed · 等待发送' : order.status === 'REJECTED' ? 'Rejected · 未发送' : 'Pending confirmation · 未发送'}
  </span>
}
