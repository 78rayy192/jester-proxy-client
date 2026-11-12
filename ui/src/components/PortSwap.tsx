import { useShallow } from 'zustand/react/shallow'
import { useEffect, useState } from 'react'
import { api } from '../lib/api'
import { useApp } from '../store/app'

const presets: { id: string; label: string; mixed: number; socks: number; api: number }[] = [
  { id: 'clash', label: '7890', mixed: 7890, socks: 7891, api: 9090 },
  { id: 'alt', label: '7897', mixed: 7897, socks: 7898, api: 9097 },
  { id: 'v2', label: '10808', mixed: 10808, socks: 10809, api: 9095 },
  { id: 'high', label: '2080', mixed: 2080, socks: 2081, api: 2090 },
]

function randomPorts() {
  const base = 20000 + Math.floor(Math.random() * 20000)
  return { mixed: base, socks: base + 1, api: base + 2 }
}

export function PortSwap() {
  const { settings, status, busy, refresh } = useApp(useShallow(({ settings, status, busy, refresh }) => ({ settings, status, busy, refresh })))
  const [mixed, setMixed] = useState(settings?.mixedPort || 7890)
  const [socks, setSocks] = useState(settings?.socksPort || 7891)
  const [ext, setExt] = useState(settings?.externalPort || 9090)
  const [msg, setMsg] = useState('')
  const [pending, setPending] = useState(false)

  useEffect(() => {
    if (pending || !settings) return
    setMixed(settings.mixedPort)
    setSocks(settings.socksPort)
    setExt(settings.externalPort)
  }, [settings?.mixedPort, settings?.socksPort, settings?.externalPort, pending])

  if (!settings) return null

  const dirty = mixed !== settings.mixedPort || socks !== settings.socksPort || ext !== settings.externalPort

  async function apply(m = mixed, s = socks, e = ext) {
    setPending(true)
    setMsg('')
    setMixed(m)
    setSocks(s)
    setExt(e)
    try {
      const r = await api.applyPorts(m, s, e)
      setMsg(r.message)
      await refresh()
    } catch (err) {
      setMsg(err instanceof Error ? err.message : 'Не удалось изменить порты')
    } finally {
      setPending(false)
    }
  }

  return (
    <div>
      <div className="port-pills">
        {presets.map((p) => (
          <button
            key={p.id}
            type="button"
            className={'btn' + (settings.mixedPort === p.mixed ? ' primary' : '')}
            disabled={pending || busy}
            onClick={() => apply(p.mixed, p.socks, p.api)}
          >
            {p.label}
          </button>
        ))}
        <button
          type="button"
          className="btn"
          disabled={pending || busy}
          onClick={() => {
            const r = randomPorts()
            apply(r.mixed, r.socks, r.api)
          }}
        >
          Случайный набор
        </button>
      </div>
      <div className="port-fields">
        <label>Прокси<input type="number" value={mixed} onChange={(e) => setMixed(Number(e.target.value))} /></label>
        <label>SOCKS<input type="number" value={socks} onChange={(e) => setSocks(Number(e.target.value))} /></label>
        <label>API<input type="number" value={ext} onChange={(e) => setExt(Number(e.target.value))} /></label>
      </div>
      <div className="btn-row" style={{ marginTop: 10 }}>
        <button type="button" className="btn primary" disabled={pending || busy || !dirty} onClick={() => apply()}>
          {pending ? 'Применение…' : status?.running ? 'Применить и перезапустить' : 'Сохранить'}
        </button>
        <button type="button" className="btn ghost copy" onClick={() => navigator.clipboard.writeText('127.0.0.1:' + settings.mixedPort)}>
          127.0.0.1:{settings.mixedPort}
        </button>
      </div>
      <p className="hint">SOCKS {settings.socksPort}, интерфейс управления {settings.externalPort}. Если отдельные сайты не открываются, выберите другой набор портов.</p>
      {msg && <div className="banner" style={{ marginTop: 10, marginBottom: 0 }}>{msg}</div>}
    </div>
  )
}
