import { useEffect, useRef, useState } from 'react'
import { useShallow } from 'zustand/react/shallow'
import { api, clash, delayTone, type ClashProxy } from '../lib/api'
import { useApp } from '../store/app'
import { Flag, splitServerName } from '../components/UiBits'

const GROUP_TYPES = new Set(['Selector', 'URLTest', 'Fallback', 'LoadBalance', 'Relay'])
const BUILTIN = new Set(['DIRECT', 'REJECT', 'REJECT-DROP', 'PASS', 'COMPATIBLE', 'GLOBAL'])

type QrDetector = { detect: (source: CanvasImageSource) => Promise<{ rawValue?: string }[]> }

function isServer(p?: ClashProxy) {
  if (!p?.name) return false
  if (BUILTIN.has(p.name.toUpperCase())) return false
  if (GROUP_TYPES.has(p.type)) return false
  if (p.type === 'Direct' || p.type === 'Reject' || p.type === 'Pass') return false
  return true
}

function hostLabel(source: string) {
  try { return new URL(source).hostname.replace(/^www\./, '') || 'Подписка' }
  catch { return 'Подписка' }
}

function groupOf(proxies: Record<string, ClashProxy>) {
  if (proxies.PROXY) return 'PROXY'
  if (proxies.GLOBAL) return 'GLOBAL'
  return Object.values(proxies).find((item) => item.type === 'Selector')?.name || 'PROXY'
}

function lastDelay(node?: ClashProxy) {
  const history = node?.history
  if (!history?.length) return undefined
  return history[history.length - 1]?.delay
}

function pingText(ms?: number) {
  if (ms == null || ms <= 0 || ms >= 10000) return '—'
  return `${Math.round(ms)} мс`
}

export function AndroidHome() {
  const { status, settings, profiles, busy, run, refresh, patchSettings, setLive } = useApp(useShallow(
    ({ status, settings, profiles, busy, run, refresh, patchSettings, setLive }) =>
      ({ status, settings, profiles, busy, run, refresh, patchSettings, setLive }),
  ))
  const [url, setUrl] = useState('')
  const [query, setQuery] = useState('')
  const [names, setNames] = useState<string[]>([])
  const [current, setCurrent] = useState('')
  const [note, setNote] = useState('')
  const [scan, setScan] = useState(false)
  const [delays, setDelays] = useState<Record<string, number>>({})
  const [history, setHistory] = useState<Record<string, number>>({})
  const [testing, setTesting] = useState(false)
  const [working, setWorking] = useState<'update' | ''>('')
  const videoRef = useRef<HTMLVideoElement>(null)
  const active = profiles.find((item) => item.id === settings?.activeProfileId) || profiles[0]
  const shown = names.filter((name) => name.toLowerCase().includes(query.trim().toLowerCase()))
  const selected = splitServerName(current)

  useEffect(() => {
    let stop = false
    const pull = async () => {
      if (!active) {
        if (!stop) setNames([])
        return
      }
      try {
        if (status?.running) {
          const live = await clash.proxies()
          if (stop) return
          const group = live.proxies[groupOf(live.proxies)]
          const list = Object.values(live.proxies).filter(isServer).map((item) => item.name)
          const measured: Record<string, number> = {}
          for (const item of Object.values(live.proxies)) {
            const delay = lastDelay(item)
            if (delay != null) measured[item.name] = delay
          }
          setHistory(measured)
          setCurrent(group?.now || settings?.lastSelectedNode || '')
          if (list.length) {
            setNames(list)
            return
          }
        }
        const file = await api.profileNodes(active.id)
        if (stop) return
        setNames(Array.isArray(file.nodes) ? file.nodes : [])
        setCurrent(settings?.lastSelectedNode || '')
      } catch (e) {
        if (!stop) setNote(e instanceof Error ? e.message : 'Список серверов недоступен')
      }
    }
    void pull()
    const timer = status?.running ? window.setInterval(() => { void pull() }, 4000) : 0
    return () => {
      stop = true
      if (timer) window.clearInterval(timer)
    }
  }, [active?.id, status?.running, settings?.lastSelectedNode])

  useEffect(() => {
    setDelays({})
    setHistory({})
  }, [active?.id])

  useEffect(() => {
    if (!scan) return
    let dead = false
    let stream: MediaStream | null = null
    let frame = 0
    const stopStream = () => stream?.getTracks().forEach((track) => track.stop())
    void (async () => {
      try {
        stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: { ideal: 'environment' } } })
        if (dead) { stopStream(); return }
        const video = videoRef.current
        if (!video) return
        video.srcObject = stream
        await video.play()
        const Detector = (window as unknown as { BarcodeDetector?: new (options: { formats: string[] }) => QrDetector }).BarcodeDetector
        if (!Detector) {
          setNote('Сканер QR недоступен. Вставьте ссылку вручную.')
          return
        }
        const detector = new Detector({ formats: ['qr_code'] })
        const tick = async () => {
          if (dead || !videoRef.current) return
          try {
            const found = await detector.detect(videoRef.current)
            const value = found.find((code) => code.rawValue)?.rawValue?.trim()
            if (value) {
              setUrl(value)
              setScan(false)
              return
            }
          } catch { /* следующий кадр */ }
          frame = requestAnimationFrame(() => { void tick() })
        }
        frame = requestAnimationFrame(() => { void tick() })
      } catch (e) {
        if (!dead) setNote(e instanceof Error ? e.message : 'Камера недоступна')
        setScan(false)
      }
    })()
    return () => {
      dead = true
      cancelAnimationFrame(frame)
      stopStream()
    }
  }, [scan])

  async function addSubscription() {
    const source = url.trim()
    if (!source) return
    setNote('')
    await run(async () => {
      const created = await api.createProfile({ name: hostLabel(source), kind: 'subscription', source })
      await api.activateProfile(created.id)
      setUrl('')
      await refresh()
    })
  }

  async function refreshSubscription() {
    if (!active || active.kind !== 'subscription') return
    setWorking('update')
    setNote('')
    try {
      await run(async () => {
        await api.updateProfile(active.id)
        await refresh()
      })
    } finally {
      setWorking('')
    }
  }

  async function removeSubscription() {
    if (!active) return
    if (!confirm(`Удалить подписку «${active.name}»?`)) return
    setNote('')
    await run(async () => {
      await api.deleteProfile(active.id)
      await refresh()
    })
  }

  async function pingServers() {
    if (!active || testing) return
    setTesting(true)
    setNote('')
    try {
      if (!status?.running) {
        await api.start()
        await refresh()
      }
      const map = await api.testDelays(settings?.latencyUrl, settings?.latencyTimeoutMs || 5000)
      setDelays((prev) => ({ ...prev, ...map }))
      if (!Object.keys(map).length) setNote('Серверы не ответили')
    } catch (e) {
      setNote(e instanceof Error ? e.message : 'Пинг не выполнен')
    } finally {
      setTesting(false)
    }
  }

  async function choose(name: string) {
    setNote('')
    await run(async () => {
      if (!status?.running) await api.start()
      const live = await clash.proxies()
      await clash.select(groupOf(live.proxies), name)
      await patchSettings({ lastSelectedNode: name })
      setLive({ now: name })
      setCurrent(name)
      await refresh()
    })
  }

  return (
    <div className="phone-home">
      <section className="phone-power">
        <span className="route-chip">VPN</span>
        <button
          type="button"
          className={'power-button' + (status?.running ? ' connected' : '')}
          disabled={busy || !active}
          aria-label={status?.running ? 'Остановить подключение' : 'Запустить подключение'}
          onClick={() => run(async () => {
            if (status?.running) await api.stop()
            else await api.start()
            await refresh()
          })}
        >
          <svg aria-hidden="true" width="30" height="30" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round"><path d="M12 3v9M6.35 5.9a8 8 0 1 0 11.3 0" /></svg>
        </button>
        <strong>{busy ? 'Подождите…' : status?.running ? 'Подключено' : 'Не подключено'}</strong>
        <span className="muted">{current ? selected.title : 'Сервер не выбран'}</span>
      </section>

      {active ? (
        <section className="phone-servers">
          <div className="phone-sub">
            <div>
              <strong>{active.name}</strong>
              <span>{names.length ? `${names.length} серверов` : 'Серверы загружаются'}</span>
            </div>
            <div className="phone-tools">
              {active.kind === 'subscription' ? (
                <button type="button" className={'phone-icon' + (working === 'update' ? ' spin' : '')} disabled={busy} aria-label="Обновить подписку" onClick={() => refreshSubscription()}>
                  <svg aria-hidden="true" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M21 12a9 9 0 1 1-2.4-6.1M21 4v5h-5" /></svg>
                </button>
              ) : null}
              <button type="button" className={'phone-icon' + (testing ? ' spin' : '')} disabled={busy || testing} aria-label="Проверить пинг" onClick={() => pingServers()}>
                <svg aria-hidden="true" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round"><path d="M5 15a7 7 0 0 1 14 0" /><path d="M8.5 15a3.5 3.5 0 0 1 7 0" /><circle cx="12" cy="16.2" r="1.1" fill="currentColor" stroke="none" /></svg>
              </button>
              <button type="button" className="phone-icon danger" disabled={busy} aria-label="Удалить подписку" onClick={() => removeSubscription()}>
                <svg aria-hidden="true" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d="M4 7h16M9 7V5h6v2M7 7l1 13h8l1-13" /></svg>
              </button>
            </div>
          </div>
          <input value={query} onChange={(e) => setQuery(e.target.value)} placeholder="Поиск сервера" />
          <div className="phone-node-list">
            {shown.map((name) => {
              const label = splitServerName(name)
              const ms = delays[name] ?? history[name]
              return (
                <button key={name} type="button" className={'phone-node' + (name === current ? ' active' : '')} disabled={busy} onClick={() => choose(name)}>
                  <Flag code={label.code} />
                  <span className="phone-node-name">{label.title}</span>
                  <span className={'ping' + (ms == null ? '' : ' ' + delayTone(ms))}>{pingText(ms)}</span>
                </button>
              )
            })}
          </div>
          {!names.length ? <p className="muted">В подписке пока нет серверов.</p> : null}
        </section>
      ) : (
        <section className="phone-add card">
          <input value={url} onChange={(e) => setUrl(e.target.value)} placeholder="Ссылка подписки" inputMode="url" autoCapitalize="off" autoCorrect="off" />
          <div className="btn-row">
            <button type="button" className="btn primary" disabled={busy || !url.trim()} onClick={() => addSubscription()}>Добавить</button>
            <button type="button" className="btn" onClick={() => { setNote(''); setScan(true) }}>QR</button>
          </div>
        </section>
      )}
      {note ? <p className="muted">{note}</p> : null}
      {scan ? (
        <div className="phone-scan">
          <video ref={videoRef} playsInline muted autoPlay />
          <button type="button" className="btn" onClick={() => setScan(false)}>Закрыть</button>
        </div>
      ) : null}
    </div>
  )
}
