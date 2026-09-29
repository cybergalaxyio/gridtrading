import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react'
import { api } from '../api'
import type { OrderApproval } from '../types'

type ApprovalState = {
  orders: OrderApproval[]; busy: string | null; error: string;
  refresh: () => Promise<void>; approve: (id: string) => Promise<void>; reject: (id: string) => Promise<void>;
}
const OrderApprovalsContext = createContext<ApprovalState>({
  orders: [], busy: null, error: '', refresh: async () => {}, approve: async () => {}, reject: async () => {},
})

export function OrderApprovalsProvider({ children }: { children: ReactNode }) {
  const [orders, setOrders] = useState<OrderApproval[]>([])
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState('')
  const sequence = useRef(0)
  const submitting = useRef(false)
  const refresh = useCallback(async () => {
    const current = ++sequence.current
    try {
      const rows = await api.orderApprovals()
      if (current === sequence.current) setOrders(rows)
    } catch (e) {
      if (current === sequence.current) setError(e instanceof Error ? e.message : '待确认订单读取失败')
    }
  }, [])
  useEffect(() => {
    void refresh()
    const timer = setInterval(() => void refresh(), 2000)
    return () => { clearInterval(timer); sequence.current++ }
  }, [refresh])

  async function decide(id: string, decision: 'approve' | 'reject') {
    if (submitting.current) return
    submitting.current = true; setBusy(id); setError('')
    sequence.current++ // Discard a poll started before this decision.
    try {
      if (decision === 'approve') await api.approveOrder(id)
      else await api.rejectOrder(id)
      sequence.current++
      setOrders(current => current.map(order => order.id === id
        ? { ...order, status: decision === 'approve' ? 'APPROVED' : 'REJECTED' } : order))
      await refresh()
    } catch (e) {
      setError(e instanceof Error ? e.message : '订单操作失败')
      await refresh()
    } finally { submitting.current = false; setBusy(null) }
  }
  return <OrderApprovalsContext.Provider value={{ orders, busy, error, refresh: async () => { setError(''); await refresh() },
    approve: id => decide(id, 'approve'), reject: id => decide(id, 'reject') }}>{children}</OrderApprovalsContext.Provider>
}

export function useOrderApprovals() { return useContext(OrderApprovalsContext) }
