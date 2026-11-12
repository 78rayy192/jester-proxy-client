import { useEffect, useId, useRef, useState } from 'react'
import { createPortal } from 'react-dom'

type Option = { value: string; label: string }

export function MenuSelect({ value, onChange, options, variant = 'field', disabled, label }: {
  value: string
  onChange: (value: string) => void
  options: Option[]
  variant?: 'pill' | 'field'
  disabled?: boolean
  label?: string
}) {
  const [open, setOpen] = useState(false)
  const [box, setBox] = useState({ top: 0, left: 0, width: 0 })
  const root = useRef<HTMLDivElement>(null)
  const listId = useId()
  const current = options.find((o) => o.value === value)?.label || value || '—'

  const place = () => {
    const button = root.current?.querySelector('button')
    if (!button) return
    const rect = button.getBoundingClientRect()
    const width = Math.max(rect.width, variant === 'pill' ? 168 : 180)
    const left = Math.min(Math.max(8, rect.left), window.innerWidth - width - 8)
    const below = rect.bottom + 6
    const height = Math.min(options.length * 36 + 12, 280)
    const top = below + height > window.innerHeight - 8 ? Math.max(8, rect.top - 6 - height) : below
    setBox({ top, left, width })
  }

  useEffect(() => {
    if (!open) return
    place()
    const close = (event: PointerEvent) => {
      const target = event.target as Node | null
      if (target && root.current?.contains(target)) return
      if (target instanceof Element && target.closest('.menu-select-list')) return
      setOpen(false)
    }
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false) }
    document.addEventListener('pointerdown', close)
    document.addEventListener('keydown', onKey)
    window.addEventListener('resize', place)
    window.addEventListener('scroll', place, true)
    return () => {
      document.removeEventListener('pointerdown', close)
      document.removeEventListener('keydown', onKey)
      window.removeEventListener('resize', place)
      window.removeEventListener('scroll', place, true)
    }
  }, [open, options.length, variant])

  return (
    <div className={'menu-select ' + variant} ref={root}>
      <button
        type="button"
        className={variant === 'pill' ? 'btn menu-select-btn' : 'menu-select-btn'}
        disabled={disabled}
        aria-label={label}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={listId}
        onClick={() => { place(); setOpen((v) => !v) }}
      >
        <span>{current}</span>
        <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.2" aria-hidden="true"><path d="M6 9l6 6 6-6" /></svg>
      </button>
      {open && createPortal(
        <div id={listId} className="menu-select-list" role="listbox" style={{ top: box.top, left: box.left, width: box.width }}>
          {options.map((option) => (
            <button
              key={option.value}
              type="button"
              role="option"
              aria-selected={option.value === value}
              className={option.value === value ? 'on' : ''}
              onClick={() => { onChange(option.value); setOpen(false) }}
            >
              {option.label}
            </button>
          ))}
        </div>,
        document.body,
      )}
    </div>
  )
}
