import { useEffect, useMemo, useState } from 'react'
import { api, clash, delayTone, type ClashProxy } from '../lib/api'
import { t } from '../lib/i18n'
import { useApp } from '../store/app'

const GROUP_TYPES = new Set(['Selector', 'URLTest', 'Fallback', 'LoadBalance', 'Relay'])
const BUILTIN = new Set(['DIRECT', 'REJECT', 'REJECT-DROP', 'PASS', 'COMPATIBLE', 'GLOBAL'])

function lastDelay(node?: ClashProxy) {
  const h = node?.history
  if (!h?.length) return undefined
  return h[h.length - 1]?.delay
}

function isServer(p?: ClashProxy) {
  if (!p?.name) return false
  if (BUILTIN.has(p.name.toUpperCase())) return false
  if (GROUP_TYPES.has(p.type)) return false
  if (p.type === 'Direct' || p.type === 'Reject' || p.type === 'Pass') return false
  return true
}

function splitName(name: string) {
  const mark = name.match(/^(\p{Regional_Indicator}{2})\s*/u)
  if (!mark) return { flag: '', title: name }
  return { flag: mark[1], title: name.slice(mark[0].length) || name }
}

function isDead(delay?: number) {
  return delay != null && (delay <= 0 || delay >= 10000)
}

export function Proxies() {
  const { settings, status, busy, run, setLive, refresh } = useApp()
  const i = t(settings?.language || 'ru')
  const [proxies, setProxies] = useState<Record<string, ClashProxy>>({})
  const [q, setQ] = useState('')
  const [sort, setSort] = useState<'default' | 'delay' | 'name'>('delay')
  const [hideDead, setHideDead] = useState(false)
  const [testing, setTesting] = useState(false)
  const [delays, setDelays] = useState<Record<string, number>>({})
  const [now, setNow] = useState('')
  const [winProxy, setWinProxy] = useState(false)
  const [testError, setTestError] = useState('')

  const load = () => clash.proxies().then((r) => {
    setProxies(r.proxies)
    const selected = r.proxies.PROXY?.now || r.proxies.GLOBAL?.now || ''
    setNow(selected)
    if (selected) setLive({ now: selected })
  }).catch(() => setProxies({}))

  useEffect(() => {
    if (!status?.running) return
    load()
    api.sysproxyStatus().then((s) => setWinProxy(!!s.windowsProxy)).catch(() => undefined)
  }, [status?.running])

  const servers = useMemo(() => Object.values(proxies).filter(isServer), [proxies])
  const url = settings?.latencyUrl || 'https://www.gstatic.com/generate_204'
  const timeout = settings?.latencyTimeoutMs || 5000

  function delayOf(name: string) {
    if (delays[name] != null) return delays[name]
    return lastDelay(proxies[name])
  }

  function selectGroup() {
    if (proxies.PROXY) return 'PROXY'
    if (proxies.GLOBAL) return 'GLOBAL'
    const g = Object.values(proxies).find((x) => x.type === 'Selector')
    return g?.name || 'PROXY'
  }

  async function testServers() {
    setTesting(true)
    setTestError('')
    try {
      const map = await api.testDelays(url, timeout)
      setDelays((p) => ({ ...p, ...map }))
      if (!Object.keys(map).length) setTestError('Серверы не ответили. Убедитесь, что ядро запущено.')
      await load()
    } catch (e) {
      setTestError(e instanceof Error ? e.message : 'Проверка задержки не выполнена')
    } finally {
      setTesting(false)
    }
  }

  async function pick(name: string) {
    await clash.select(selectGroup(), name)
    await load()
  }

  async function toggleTun() {
    const on = !settings?.tunEnabled
    if (on) {
      await api.sysproxy(false).catch(() => undefined)
      await useApp.getState().patchSettings({ tunEnabled: true, systemProxy: false })
      if (status?.running) await api.restart()
    } else {
      await useApp.getState().patchSettings({ tunEnabled: false })
      if (status?.running) await api.restart()
    }
    await refresh()
    const s = await api.sysproxyStatus().catch(() => null)
    if (s) setWinProxy(!!s.windowsProxy)
  }

  async function toggleSysProxy() {
    const on = !(settings?.systemProxy && !settings?.tunEnabled && winProxy)
    const res = await api.sysproxy(on)
    await refresh()
    setWinProxy(!!res.windowsProxy)
  }

  if (!status?.running) return <div className="empty"><h3>{i.proxies}</h3><p>Сначала запустите ядро.</p></div>

  const query = q.trim().toLowerCase()
  let names = servers.map((s) => s.name)
  if (query) names = names.filter((n) => n.toLowerCase().includes(query))
  if (hideDead) names = names.filter((n) => !isDead(delayOf(n)))
  if (sort === 'name') names = [...names].sort((a, b) => a.localeCompare(b, 'ru'))
  if (sort === 'delay') names = [...names].sort((a, b) => (delayOf(a) ?? 99999) - (delayOf(b) ?? 99999))

  const proxyOn = !!(settings?.systemProxy && !settings?.tunEnabled)

  return (
    <div>
      <div className="page-head">
        <div>
          <h1>{i.proxies}</h1>
          <p>Серверы из активной подписки. Маршрутизация настраивается в разделе «Правила».</p>
        </div>
        <div className="btn-row">
          <button type="button" className="btn primary" disabled={testing} onClick={() => testServers()}>
            {testing ? 'Проверка…' : i.test}
          </button>
        </div>
      </div>

      {testError && <div className="banner warn">{testError}</div>}

      <div className="mode-bar">
        <div className="mode-card">
          <div>
            <div className="k">Туннель</div>
            <div className="hint">Системный сетевой адаптер</div>
          </div>
          <button type="button" className={'toggle' + (settings?.tunEnabled ? ' on' : '')} disabled={busy} onClick={() => run(toggleTun)} />
        </div>
        <div className="mode-card">
          <div>
            <div className="k">{i.sysProxy}</div>
            <div className="hint">
              Windows → 127.0.0.1:{settings?.mixedPort}
              {winProxy ? ' · включён в системе' : ' · отключён в системе'}
            </div>
          </div>
          <button type="button" className={'toggle' + (proxyOn && winProxy ? ' on' : '')} disabled={busy} onClick={() => run(toggleSysProxy)} />
        </div>
        <div className="mode-card">
          <div>
            <div className="k">Сервер</div>
            <div className="hint trunc">{now || 'не выбран'}</div>
          </div>
        </div>
      </div>

      <div className="toolbar">
        <input className="search" placeholder="Поиск сервера" value={q} onChange={(e) => setQ(e.target.value)} id="proxy-search" />
        <select value={sort} onChange={(e) => setSort(e.target.value as typeof sort)}>
          <option value="delay">По задержке</option>
          <option value="name">По имени</option>
          <option value="default">Порядок профиля</option>
        </select>
        <button type="button" className={'btn' + (hideDead ? ' primary' : '')} onClick={() => setHideDead((v) => !v)}>
          {hideDead ? 'Недоступные скрыты' : 'Скрыть недоступные'}
        </button>
      </div>

      <div className="node-grid">
        {names.map((name) => {
          const node = proxies[name]
          const delay = delayOf(name)
          const tone = delayTone(delay)
          const label = splitName(name)
          return (
            <button
              key={name}
              type="button"
              className={'node ' + tone + (now === name ? ' now' : '')}
              onClick={() => run(() => pick(name))}
            >
              <div className="name">{label.flag && <span className="flag">{label.flag}</span>}<span>{label.title}</span></div>
              <div className="meta">
                <span>{node?.type || ''}{node?.udp ? ' · UDP' : ''}</span>
                <span className={'ping ' + tone}>
                  {testing && delays[name] == null && delay == null ? '…' : delay != null && delay > 0 && delay < 10000 ? delay + ' ms' : '—'}
                </span>
              </div>
            </button>
          )
        })}
        {!names.length && <div className="empty" style={{ gridColumn: '1 / -1' }}><p>В подписке нет доступных серверов.</p></div>}
      </div>
    </div>
  )
}
