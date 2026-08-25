import type { ReactNode } from 'react'
import { Icon } from './Icon'

export function Modal({ title, icon, onClose, children, className = '' }: { title: string; icon: string; onClose: () => void; children: ReactNode; className?: string }) {
  return <div className="modal-backdrop" role="dialog" aria-modal="true"><section className={`modal ${className}`}>
    <header><span className="modal-icon"><Icon name={icon} /></span><h2>{title}</h2><button aria-label="关闭" onClick={onClose}><Icon name="close" /></button></header>
    {children}
  </section></div>
}
