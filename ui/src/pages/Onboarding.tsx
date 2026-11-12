import { useEffect, useRef, useState } from 'react'
import { useShallow } from 'zustand/react/shallow'
import { api } from '../lib/api'
import { useApp } from '../store/app'

export function Onboarding() {
  const { status, settings, profiles, busy, run, patchSettings, setPage, refresh } = useApp(useShallow((s) => ({
    status: s.status, settings: s.settings, profiles: s.profiles, busy: s.busy,
    run: s.run, patchSettings: s.patchSettings, setPage: s.setPage, refresh: s.refresh,
  })))
  const [step, setStep] = useState(0)
  const [paused, setPaused] = useState(false)
  const dialog = useRef<HTMLDivElement>(null)
  const visible = !!settings && !settings.firstRunCompleted && !paused

  useEffect(() => {
    if (!visible) return
    const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null
    dialog.current?.focus()
    return () => { previous?.focus() }
  }, [visible])

  if (!visible) return null

  const steps = [
    {
      title: 'Ядро',
      text: 'Загрузите сетевое ядро. Без него подключение недоступно.',
      done: !!status?.coreInstalled,
      action: (
        <button className="btn primary" disabled={busy || !!status?.coreInstalled} onClick={() => run(async () => { await api.download(); await refresh() })}>
          {status?.coreInstalled ? 'Готово' : 'Загрузить ядро'}
        </button>
      ),
    },
    {
      title: 'Профиль',
      text: 'Добавьте ссылку подписки или конфигурацию.',
      done: profiles.length > 0,
      action: (
        <button className="btn primary" onClick={() => { setPaused(true); setPage('profiles') }}>Открыть профили</button>
      ),
    },
    {
      title: 'Запуск',
      text: 'Выберите режим маршрутизации и запустите подключение. Для системного туннеля нужны права администратора.',
      done: !!status?.running,
      action: (
        <button className="btn primary" disabled={busy || !!status?.running || !status?.coreInstalled || !profiles.length} onClick={() => run(async () => {
          const r = await api.start()
          if (r.needsAdmin || r.message === 'admin-required') await api.elevate()
          await refresh()
        })}>
          {status?.running ? 'Активен' : 'Запустить'}
        </button>
      ),
    },
  ]

  const current = steps[step]

  return (
    <div className="onboard-overlay" onKeyDown={(e) => {
      e.stopPropagation()
      if (e.key === 'Escape') { e.preventDefault(); setPaused(true); return }
      if (e.key !== 'Tab') return
      const controls = dialog.current?.querySelectorAll<HTMLElement>('button:not(:disabled), [href], input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex="0"]')
      if (!controls?.length) { e.preventDefault(); return }
      const first = controls[0]
      const last = controls[controls.length - 1]
      if (e.shiftKey && (document.activeElement === first || document.activeElement === dialog.current)) { e.preventDefault(); last.focus() }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus() }
    }}>
      <div ref={dialog} className="card onboard-card" role="dialog" aria-modal="true" aria-labelledby="onboard-title" aria-describedby="onboard-description" tabIndex={-1}>
        <div className="eyebrow">Первый запуск</div>
        <h2 id="onboard-title" style={{ marginTop: 8 }}>{current.title}</h2>
        <p id="onboard-description" className="hint">{current.text}</p>
        <div className="onboard-steps">
          {steps.map((s, i) => (
            <button key={s.title} type="button" aria-label={`Шаг ${i + 1}: ${s.title}${s.done ? ', выполнен' : ''}`} aria-current={i === step ? 'step' : undefined} className={'onboard-dot' + (i === step ? ' on' : '') + (s.done ? ' done' : '')} onClick={() => setStep(i)} />
          ))}
        </div>
        <div className="btn-row" style={{ marginTop: 18 }}>
          {current.action}
          {step < steps.length - 1 ? (
            <button className="btn" onClick={() => setStep(step + 1)}>Далее</button>
          ) : (
            <button className="btn primary" disabled={busy} onClick={() => patchSettings({ firstRunCompleted: true })}>Готово</button>
          )}
          <button className="btn" disabled={busy} onClick={() => patchSettings({ firstRunCompleted: true })}>Пропустить</button>
        </div>
      </div>
    </div>
  )
}
