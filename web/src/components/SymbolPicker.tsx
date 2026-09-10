import { useEffect, useId, useRef, useState } from 'react'
import { Icon } from './Icon'

export function SymbolPicker({ value, options, formatLabel, onChange }: {
  value: string
  options: string[]
  formatLabel: (symbol: string) => string
  onChange: (symbol: string) => void
}) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [active, setActive] = useState(value)
  const root = useRef<HTMLSpanElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const search = useRef<HTMLInputElement>(null)
  const list = useRef<HTMLSpanElement>(null)
  const id = useId()
  const symbols = [...new Set([value, ...options])]
  const filtered = symbols.filter(symbol => formatLabel(symbol).toLowerCase().includes(query.trim().toLowerCase()))
  const activeIndex = Math.max(0, filtered.indexOf(active))

  useEffect(() => {
    if (!open) return
    search.current?.focus()
    function dismiss(event: PointerEvent) {
      if (!root.current?.contains(event.target as Node)) setOpen(false)
    }
    document.addEventListener('pointerdown', dismiss)
    return () => document.removeEventListener('pointerdown', dismiss)
  }, [open])

  useEffect(() => {
    if (open) list.current?.children[activeIndex]?.scrollIntoView({ block: 'nearest' })
  }, [open, activeIndex, query])

  function show() {
    setQuery('')
    setActive(value)
    setOpen(true)
  }

  function choose(symbol: string) {
    if (symbol !== value) onChange(symbol)
    setOpen(false)
    trigger.current?.focus()
  }

  return <span className="symbol-picker" ref={root} onBlur={event => {
    if (!event.currentTarget.contains(event.relatedTarget as Node | null)) setOpen(false)
  }} onKeyDown={event => {
    if (event.key === 'Escape' && open) {
      event.preventDefault()
      event.stopPropagation()
      setOpen(false)
      trigger.current?.focus()
    }
  }}>
    <button ref={trigger} type="button" className="symbol-picker-trigger" aria-label={`选择交易对: ${formatLabel(value)}`}
      aria-haspopup="dialog" aria-expanded={open} aria-controls={open ? `${id}-popover` : undefined}
      onClick={() => open ? setOpen(false) : show()}
      onKeyDown={event => {
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') { event.preventDefault(); show() }
      }}>
      <span>{formatLabel(value)}</span><span className="symbol-picker-chevron" aria-hidden="true" />
    </button>
    {open && <span id={`${id}-popover`} className="symbol-picker-popover" role="dialog" aria-label="选择交易对">
      <span className="symbol-picker-search">
        <Icon name="search" size={15} />
        <input ref={search} type="text" role="combobox" aria-label="搜索交易对" placeholder="搜索交易对…"
          value={query} autoComplete="off" spellCheck={false} aria-expanded="true" aria-autocomplete="list"
          aria-controls={`${id}-list`} aria-activedescendant={filtered.length ? `${id}-option-${activeIndex}` : undefined}
          onChange={event => { setQuery(event.target.value); setActive('') }}
          onKeyDown={event => {
            if (event.nativeEvent.isComposing) return
            if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
              event.preventDefault()
              const step = event.key === 'ArrowDown' ? 1 : -1
              if (filtered.length) setActive(filtered[(activeIndex + step + filtered.length) % filtered.length])
            } else if (event.key === 'Enter') {
              event.preventDefault()
              if (filtered[activeIndex]) choose(filtered[activeIndex])
            }
          }} />
        <span className="symbol-picker-shortcut" aria-hidden="true">esc</span>
      </span>
      <span className="symbol-picker-list" id={`${id}-list`} ref={list} role="listbox" aria-label="交易对">
        {filtered.map((symbol, index) => <span key={symbol} id={`${id}-option-${index}`} role="option"
          aria-selected={symbol === value}
          className={`symbol-picker-option${index === activeIndex ? ' highlighted' : ''}${symbol === value ? ' selected' : ''}`}
          onPointerMove={() => setActive(symbol)} onMouseDown={event => event.preventDefault()} onClick={() => choose(symbol)}>
          <span>{formatLabel(symbol)}</span>{symbol === value && <Icon name="check" size={16} />}
        </span>)}
      </span>
      {!filtered.length && <span className="symbol-picker-empty" role="status">未找到交易对</span>}
    </span>}
  </span>
}
