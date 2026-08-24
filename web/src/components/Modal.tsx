import type { ReactNode } from 'react'
import { Icon } from './Icon'

export function Modal({ title, icon, onClose, children }: { title: string; icon: string; onClose: () => void; children: ReactNode }) {
  return <div className="modal-backdrop" role="dialog" aria-modal="true"><section className="modal">
    <header><span className="modal-icon"><Icon name={icon} /></span><h2>{title}</h2><button aria-label="关闭" onClick={onClose}><Icon name="close" /></button></header>
    {children}
  </section></div>
}
