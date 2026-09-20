import { useEffect } from 'react'
import { clash, formatSpeed, hostMsg, api, sanitizeBps } from './lib/api'
import { t } from './lib/i18n'
import { useApp } from './store/app'
import { Icon, icons } from './components/Icons'
import { Dashboard } from './pages/Dashboard'
import { Proxies } from './pages/Proxies'
import { Connections } from './pages/Connections'
import { Rules } from './pages/Rules'
import { Profiles } from './pages/Profiles'
import { Logs } from './pages/Logs'
import { Settings } from './pages/Settings'
import { AppearancePage } from './pages/Appearance'

const nav = [
  ['dashboard', 'dash', icons.dash],
  ['proxies', 'proxies', icons.proxies],
  ['connections', 'connections', icons.connections],
  ['rules', 'rules', icons.rules],
  ['profiles', 'profiles', icons.profiles],
  ['logs', 'logs', icons.logs],
  ['settings', 'settings', icons.settings],
  ['appearance', 'appearance', icons.appearance],
] as const

function hexToRgb(hex: string): [number, number, number] | null {
  const raw = hex.trim().replace('#', '')
  if (!/^[0-9a-fA-F]{3}$|^[0-9a-fA-F]{6}$/.test(raw)) return null
  const full = raw.length === 3 ? raw.split('').map((c) => c + c).join('') : raw
  return [parseInt(full.slice(0, 2), 16), parseInt(full.slice(2, 4), 16), parseInt(full.slice(4, 6), 16)]
}

function rgba(hex: string, alpha: number) {
  const rgb = hexToRgb(hex)
  if (!rgb) return `rgba(0,0,0,${alpha})`
  return `rgba(${rgb[0]}, ${rgb[1]}, ${rgb[2]}, ${alpha})`
}

function luminance(hex: string) {
  const rgb = hexToRgb(hex)
  if (!rgb) return 0
  const lin = rgb.map((v) => {
    const c = v / 255
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
  })
  return 0.2126 * lin[0] + 0.7152 * lin[1] + 0.0722 * lin[2]
}

function mixHex(a: string, b: string, t: number) {
  const left = hexToRgb(a)
  const right = hexToRgb(b)
  if (!left || !right) return a
  const m = (i: number) => Math.round(left[i] + (right[i] - left[i]) * t)
  return `#${[m(0), m(1), m(2)].map((n) => n.toString(16).padStart(2, '0')).join('')}`
}

function wallpaperUrl(src?: string | null) {
  const value = (src || '').trim()
  if (!value) return ''
  if (/^https?:\/\//i.test(value)) return value
  return `/api/appearance/wallpaper?v=${encodeURIComponent(value.slice(-48))}`
}

function applyTheme() {
  const a = useApp.getState().settings?.appearance
  if (!a) return
  const r = document.documentElement.style
  const light = luminance(a.background) > 0.42
  r.setProperty('--bg', a.background)
  r.setProperty('--bg-elev', light ? mixHex(a.background, '#ffffff', 0.55) : mixHex(a.background, '#ffffff', 0.07))
  r.setProperty('--surface', rgba(a.text, light ? 0.06 : 0.05))
  r.setProperty('--surface-2', rgba(a.text, light ? 0.1 : 0.09))
  r.setProperty('--line', rgba(a.text, light ? 0.16 : 0.14))
  r.setProperty('--line-soft', rgba(a.text, light ? 0.1 : 0.08))
  r.setProperty('--text', a.text)
  r.setProperty('--muted', rgba(a.text, light ? 0.68 : 0.62))
  r.setProperty('--faint', rgba(a.text, light ? 0.45 : 0.38))
  r.setProperty('--accent', a.accent)
  r.setProperty('--accent-2', a.accentMuted)
  r.setProperty('--panel', light ? rgba(a.text, 0.06) : 'rgba(0, 0, 0, 0.28)')
  r.setProperty('--radius', a.radius + 'px')
  r.setProperty('--blur', '0px')
  r.setProperty('--font-ui', '"Segoe UI Variable Text", "Segoe UI", sans-serif')
  r.setProperty('--font-display', '"Segoe UI Variable Display", "Segoe UI", sans-serif')
  document.documentElement.dataset.theme = light ? 'light' : 'dark'
  document.documentElement.style.colorScheme = light ? 'light' : 'dark'
  document.documentElement.dataset.motion = a.animations === false ? 'off' : 'on'
  document.body.style.opacity = String(Math.max(0.92, a.windowOpacity || 1))
}

export function App() {
  const { page, setPage, status, settings, error, refresh, live, setLive, run } = useApp()
  const i = t(settings?.language || 'ru')

  useEffect(() => {
    refresh()
    const id = setInterval(refresh, status?.running ? 8000 : 4000)
    return () => clearInterval(id)
  }, [refresh, status?.running])

  useEffect(() => { applyTheme() }, [settings?.appearance])

  useEffect(() => {
    if (!status?.running) {
      setLive({ up: 0, down: 0, mem: 0, now: '' })
      return
    }

    let lastUp = 0
    let lastDown = 0
    let lastAt = 0
    let lastSseAt = 0
    let alive = true
    let prevUp = 0
    let prevDown = 0

    const pushSpeed = (up: number, down: number) => {
      if (!alive) return
      const nextUp = sanitizeBps(up, prevUp)
      const nextDown = sanitizeBps(down, prevDown)
      prevUp = nextUp
      prevDown = nextDown
      setLive({ up: nextUp, down: nextDown })
    }

    const es = new EventSource('/clash/traffic')
    es.onmessage = (e) => {
      try {
        const j = JSON.parse(e.data) as { up: number; down: number }
        lastSseAt = Date.now()
        pushSpeed(j.up, j.down)
      } catch { /* ignore */ }
    }
    const esm = new EventSource('/clash/memory')
    esm.onmessage = (e) => {
      try {
        const mem = (JSON.parse(e.data) as { inuse: number }).inuse
        if (alive && Number.isFinite(mem) && mem >= 0) setLive({ mem })
      } catch { /* ignore */ }
    }

    const poll = window.setInterval(async () => {
      try {
        const c = await clash.connections()
        const now = Date.now()
        const upTotal = Number(c.uploadTotal) || 0
        const downTotal = Number(c.downloadTotal) || 0
        // Prefer SSE; only derive from totals if traffic stream is stale.
        if (now - lastSseAt > 2000 && lastAt > 0 && alive) {
          const dt = Math.max(0.5, (now - lastAt) / 1000)
          // Totals reset (restart / close-all) — resync without a fake spike.
          if (upTotal >= lastUp && downTotal >= lastDown) {
            pushSpeed((upTotal - lastUp) / dt, (downTotal - lastDown) / dt)
          }
        }
        lastUp = upTotal
        lastDown = downTotal
        lastAt = now
      } catch { /* ignore */ }
    }, 1000)

    clash.proxies().then((p) => {
      const prefer = ['PROXY', 'GLOBAL']
      for (const name of prefer) {
        const g = p.proxies[name]
        if (g?.now) { setLive({ now: g.now }); return }
      }
      const g = Object.values(p.proxies).find((x) => x.type === 'Selector' && x.now)
      if (g?.now) setLive({ now: g.now })
    }).catch(() => undefined)

    return () => {
      alive = false
      es.close()
      esm.close()
      window.clearInterval(poll)
    }
  }, [status?.running, setLive])

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement)?.tagName
      const typing = tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT'
      if (e.key === '/' && !typing) {
        e.preventDefault()
        setPage('proxies')
        queueMicrotask(() => document.getElementById('proxy-search')?.focus())
        return
      }
      if (typing) return
      if (e.key === ' ') {
        const el = e.target as HTMLElement
        if (el.closest('button, a, [role="button"], [contenteditable="true"]')) return
        if (useApp.getState().busy) return
        e.preventDefault()
        run(async () => {
          const st = useApp.getState().status
          if (st?.running) await api.stop()
          else {
            const r = await api.start()
            if (r.needsAdmin || r.message === 'admin-required') await api.elevate()
          }
        })
      }
      const idx = Number(e.key)
      if (idx >= 1 && idx <= nav.length) setPage(nav[idx - 1][0])
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [run, setPage])

  const pages = {
    dashboard: <Dashboard />,
    proxies: <Proxies />,
    connections: <Connections />,
    rules: <Rules />,
    profiles: <Profiles />,
    logs: <Logs />,
    settings: <Settings />,
    appearance: <AppearancePage />,
  }

  return (
    <div
      className={'app' + (settings?.appearance?.glass ? ' glass' : '')}
      data-bg={settings?.appearance?.backgroundStyle || 'mesh'}
      style={settings?.appearance?.backgroundStyle === 'image' && settings.appearance.backgroundImage
        ? {
            backgroundImage: `linear-gradient(color-mix(in srgb, var(--bg) 68%, transparent), color-mix(in srgb, var(--bg) 82%, transparent)), url("${wallpaperUrl(settings.appearance.backgroundImage)}")`,
            backgroundSize: 'cover',
            backgroundPosition: 'center',
          }
        : undefined}
    >
      <header
        className="titlebar"
        onMouseDown={(e) => {
          if (e.button !== 0) return
          const t = e.target as HTMLElement
          if (t.closest('.win-btns')) return
          hostMsg('drag')
        }}
      >
        <div className="brand">
          <img className="brand-mark" src="/jester-mark.png" alt="" draggable={false} />
          JESTER
        </div>
        <div className="live-chip">
          <span>↓ {formatSpeed(live.down)}</span>
          <span>↑ {formatSpeed(live.up)}</span>
          {live.now ? <span className="now-chip">{live.now}</span> : null}
        </div>
        <div className="win-btns">
          <button type="button" title="Свернуть" aria-label="Свернуть" onMouseDown={(e) => e.stopPropagation()} onClick={() => hostMsg('minimize')}>─</button>
          <button type="button" title="Развернуть" aria-label="Развернуть" onMouseDown={(e) => e.stopPropagation()} onClick={() => hostMsg('maximize')}>▢</button>
          <button type="button" className="close" title="Закрыть" aria-label="Закрыть" onMouseDown={(e) => e.stopPropagation()} onClick={() => hostMsg('close')}>✕</button>
        </div>
      </header>
      <div className={'shell' + (settings?.appearance?.sidebar === 'compact' ? ' compact' : '')}>
        <aside className="sidebar">
          {nav.map(([id, key, d], n) => (
            <button
              key={id}
              type="button"
              className={'nav-btn' + (page === id ? ' active' : '')}
              data-label={i[key]}
              title={i[key]}
              aria-label={i[key]}
              aria-current={page === id ? 'page' : undefined}
              onClick={() => setPage(id)}
            >
              <Icon d={d} />
              <span>{i[key]}</span>
              <kbd aria-hidden="true">{n + 1}</kbd>
            </button>
          ))}
          <div className="grow" />
          <div className="core-pill" title={status?.running ? i.online : i.idle}>
            <div className="pill-avatar">
              <img className="pill-mark" src="/jester.png" alt="" draggable={false} />
              <span className={'dot' + (status?.running ? ' on' : status?.needsAdmin ? ' warn' : '')} />
            </div>
            <div className="pill-meta">
              <div className="k">Ядро</div>
              <div className="v">{status?.running ? i.online : i.idle}</div>
            </div>
          </div>
        </aside>
        <main className="content">
          {error && <div className="card" style={{ marginBottom: 16, borderColor: 'var(--danger)' }}>{error}</div>}
          {pages[page]}
        </main>
      </div>
      {settings?.appearance?.customCss ? <style>{settings.appearance.customCss}</style> : null}
    </div>
  )
}
