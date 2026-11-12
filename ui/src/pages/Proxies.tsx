import { useShallow } from 'zustand/react/shallow'
import { useEffect, useMemo, useState } from 'react'
import { api, clash, delayTone, type ClashProxy } from '../lib/api'
import { t } from '../lib/i18n'
import { useApp } from '../store/app'
import { MenuSelect } from '../components/MenuSelect'
import { EmptyState, Flag, splitServerName } from '../components/UiBits'

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

function isDead(delay?: number) {
  return delay != null && (delay <= 0 || delay >= 10000)
}

export function Proxies() {
  const { settings, status, busy, run, setLive, refresh, patchSettings } = useApp(useShallow(({ settings, status, busy, run, setLive, refresh, patchSettings }) => ({ settings, status, busy, run, setLive, refresh, patchSettings })))
  const i = t(settings?.language || 'ru')
  const [proxies, setProxies] = useState<Record<string, ClashProxy>>({})
  const [q, setQ] = useState('')
  const [proto, setProto] = useState('all')
  const [sort, setSort] = useState<'default' | 'delay' | 'name'>('delay')
  const [hideDead, setHideDead] = useState(false)
  const [onlyFav, setOnlyFav] = useState(false)
  const [testing, setTesting] = useState(false)
  const [delays, setDelays] = useState<Record<string, number>>({})
  const [now, setNow] = useState('')
  const [winProxy, setWinProxy] = useState(false)
  const [testError, setTestError] = useState('')
  const favorites = settings?.favoriteNodes || []

  const load = () => clash.proxies().then(async (r) => {
    setProxies(r.proxies)
    const selected = r.proxies.PROXY?.now || r.proxies.GLOBAL?.now || ''
    setNow(selected)
    if (selected) {
      setLive({ now: selected })
      if (selected !== settings?.lastSelectedNode)
        await patchSettings({ lastSelectedNode: selected })
    }
  }).catch(() => setProxies({}))

  useEffect(() => {
    if (!status?.running) return
    load()
    api.sysproxyStatus().then((s) => setWinProxy(!!s.windowsProxy)).catch(() => undefined)
  }, [status?.running])

  const servers = useMemo(() => Object.values(proxies).filter(isServer), [proxies])
  const protocols = useMemo(() => {
    const set = new Set(servers.map((s) => (s.type || '').toLowerCase()).filter(Boolean))
    return Array.from(set).sort()
  }, [servers])
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
    await patchSettings({ lastSelectedNode: name })
    await load()
  }

  async function toggleFavorite(name: string, e: React.MouseEvent) {
    e.stopPropagation()
    const set = new Set(favorites)
    if (set.has(name)) set.delete(name)
    else set.add(name)
    await patchSettings({ favoriteNodes: Array.from(set) })
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

  if (!status?.running) {
    return <EmptyState title={i.proxies} text="Сначала запустите ядро на обзоре, затем выберите сервер." />
  }

  const query = q.trim().toLowerCase()
  let names = servers.map((s) => s.name)
  if (onlyFav) names = names.filter((n) => favorites.includes(n))
  if (proto !== 'all') names = names.filter((n) => (proxies[n]?.type || '').toLowerCase() === proto)
  if (query) {
    names = names.filter((n) => {
      const node = proxies[n]
      const hay = `${n} ${node?.type || ''} ${splitServerName(n).title}`.toLowerCase()
      return hay.includes(query)
    })
  }
  if (hideDead) names = names.filter((n) => !isDead(delayOf(n)))
  if (sort === 'name') names = [...names].sort((a, b) => a.localeCompare(b, 'ru'))
  if (sort === 'delay') names = [...names].sort((a, b) => (delayOf(a) ?? 99999) - (delayOf(b) ?? 99999))

  const favFirst = [...names].sort((a, b) => Number(favorites.includes(b)) - Number(favorites.includes(a)))
  if (sort === 'default') names = favFirst
  else if (onlyFav) names = favFirst
  else names = [...favorites.filter((f) => names.includes(f)), ...names.filter((n) => !favorites.includes(n))]

  const proxyOn = !!(settings?.systemProxy && !settings?.tunEnabled)
  const current = splitServerName(now)

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
          <button type="button" role="switch" aria-label="Системный туннель" aria-checked={!!settings?.tunEnabled} className={'toggle' + (settings?.tunEnabled ? ' on' : '')} disabled={busy} onClick={() => run(toggleTun)} />
        </div>
        <div className="mode-card">
          <div>
            <div className="k">{i.sysProxy}</div>
            <div className="hint">
              Windows → 127.0.0.1:{settings?.mixedPort}
              {winProxy ? ' · включён в системе' : ' · отключён в системе'}
            </div>
          </div>
          <button type="button" role="switch" aria-label="Системный прокси Windows" aria-checked={proxyOn && winProxy} className={'toggle' + (proxyOn && winProxy ? ' on' : '')} disabled={busy} onClick={() => run(toggleSysProxy)} />
        </div>
        <div className="mode-card">
          <div>
            <div className="k">Сервер</div>
            <div className="hint trunc flag-line">{now ? <><Flag code={current.code} /><span>{current.title}</span></> : 'не выбран'}</div>
          </div>
        </div>
      </div>

      <div className="toolbar">
        <input className="search" placeholder="Поиск: страна, флаг, протокол" value={q} onChange={(e) => setQ(e.target.value)} id="proxy-search" />
        <MenuSelect variant="pill" label="Протокол" value={proto} onChange={setProto} options={[{ value: 'all', label: 'Все протоколы' }, ...protocols.map((p) => ({ value: p, label: p }))]} />
        <MenuSelect variant="pill" label="Сортировка" value={sort} onChange={(value) => setSort(value as typeof sort)} options={[
          { value: 'delay', label: 'По задержке' },
          { value: 'name', label: 'По имени' },
          { value: 'default', label: 'Избранные сверху' },
        ]} />
        <button type="button" className={'btn' + (onlyFav ? ' primary' : '')} onClick={() => setOnlyFav((v) => !v)}>
          Избранные
        </button>
        <button type="button" className={'btn' + (hideDead ? ' primary' : '')} onClick={() => setHideDead((v) => !v)}>
          {hideDead ? 'Недоступные скрыты' : 'Скрыть недоступные'}
        </button>
      </div>

      <div className="node-grid">
        {names.map((name) => {
          const node = proxies[name]
          const delay = delayOf(name)
          const tone = delayTone(delay)
          const label = splitServerName(name)
          const fav = favorites.includes(name)
          return (
            <button
              key={name}
              type="button"
              className={'node ' + tone + (now === name ? ' now' : '') + (fav ? ' fav' : '')}
              onClick={() => run(() => pick(name))}
            >
              <div className="name">
                {label.code ? <Flag code={label.code} /> : null}
                <span>{label.title}</span>
                <span
                  className={'fav-star' + (fav ? ' on' : '')}
                  onClick={(e) => { e.stopPropagation(); void run(() => toggleFavorite(name, e)) }}
                  title={fav ? 'Убрать из избранного' : 'В избранное'}
                >★</span>
              </div>
              <div className="meta">
                <span>{node?.type || ''}{node?.udp ? ' · UDP' : ''}</span>
                <span className={'ping ' + tone}>
                  {testing && delays[name] == null && delay == null ? '…' : delay != null && delay > 0 && delay < 10000 ? delay + ' ms' : '—'}
                </span>
              </div>
            </button>
          )
        })}
        {!names.length && (
          <div style={{ gridColumn: '1 / -1' }}>
            <EmptyState title="Серверы не найдены" text="Обновите подписку или измените фильтр поиска." />
          </div>
        )}
      </div>
    </div>
  )
}
