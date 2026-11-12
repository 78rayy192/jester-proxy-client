import { memo, useEffect, useRef, useState } from 'react'
import { useShallow } from 'zustand/react/shallow'
import { api, clash, formatBytes, formatSpeed, type LeakCheckResult, type Profile } from '../lib/api'
import { t } from '../lib/i18n'
import { useApp } from '../store/app'
import { PortSwap } from '../components/PortSwap'
import { Sparkline } from '../components/Sparkline'
import { EmptyState, Flag, formatExpire, formatQuota, routeLabel, splitServerName } from '../components/UiBits'

type GuardianState = { message: string; lane: string; rttMs: number; deadManArmed: boolean; exitIp?: string | null }

// Only this small card subscribes to the frequently changing traffic counters.
const TrafficCard = memo(function TrafficCard({ active, guardian, graph, language }: {
  active?: Profile; guardian: GuardianState | null; graph: boolean; language: string
}) {
  const live = useApp((s) => s.live)
  const i = t(language)
  const [history, setHistory] = useState(() => ({ up: Array<number>(40).fill(0), down: Array<number>(40).fill(0) }))
  useEffect(() => {
    if (!graph || document.hidden) return
    setHistory((h) => ({ up: [...h.up.slice(1), live.up], down: [...h.down.slice(1), live.down] }))
  }, [live.up, live.down, graph])

  return (
    <div className="card traffic-card">
      <div className="card-heading"><h2>Трафик</h2><span className="hint">Текущая скорость</span></div>
      {graph && <Sparkline up={history.up} down={history.down} />}
      <div className="traffic-stats">
        <div className="stat rate" title={formatSpeed(live.down)}>{formatSpeed(live.down)}<small><span className="traffic-key down" />{i.down}</small></div>
        <div className="stat rate" title={formatSpeed(live.up)}>{formatSpeed(live.up)}<small><span className="traffic-key up" />{i.up}</small></div>
        <div className="stat rate" title={formatBytes(live.mem)}>{formatBytes(live.mem)}<small>{i.ram} · ядро</small></div>
      </div>
      {active?.kind === 'subscription' && (active.download != null || active.expireUnix) && (
        <div className="sub-meta">
          <div>Подписка · {formatQuota((active.upload || 0) + (active.download || 0), active.total || 0)}</div>
          {formatExpire(active.expireUnix) && <div>До {formatExpire(active.expireUnix)}</div>}
          {active.hwidLimited && <div className="warn-text">Ограничение устройств у поставщика</div>}
        </div>
      )}
      {guardian && (
        <div className="sub-meta guardian-meta">
          <div>Канал · {guardian.lane || '—'}{guardian.rttMs ? ` · ${guardian.rttMs} мс` : ''}</div>
          {guardian.exitIp && <div>Адрес выхода · {guardian.exitIp}</div>}
          {guardian.message && <div className="hint">{guardian.message}</div>}
        </div>
      )}
    </div>
  )
})

export function Dashboard() {
  const { status, settings, profiles, busy, run, patchSettings, setLive, setPage } = useApp(useShallow((s) => ({
    status: s.status, settings: s.settings, profiles: s.profiles, busy: s.busy, run: s.run,
    patchSettings: s.patchSettings, setLive: s.setLive, setPage: s.setPage,
  })))
  const selectedNode = useApp((s) => s.live.now)
  const i = t(settings?.language || 'ru')
  const [leak, setLeak] = useState<LeakCheckResult | null>(null)
  const [leakBusy, setLeakBusy] = useState(false)
  const [panicBusy, setPanicBusy] = useState(false)
  const [rushBusy, setRushBusy] = useState(false)
  const [learnBusy, setLearnBusy] = useState(false)
  const [rushInfo, setRushInfo] = useState('')
  const [guardian, setGuardian] = useState<GuardianState | null>(null)
  const leakRevision = useRef(0)

  useEffect(() => {
    let alive = true
    let inFlight = false
    let timer: ReturnType<typeof setTimeout> | undefined
    const tick = async () => {
      if (!alive || document.hidden || inFlight) return
      clearTimeout(timer)
      inFlight = true
      try {
        const g = await api.guardian()
        if (alive) setGuardian(g)
      } catch { /* ignore */ }
      finally {
        inFlight = false
        if (alive && !document.hidden) timer = setTimeout(tick, status?.running ? 8000 : 15000)
      }
    }
    const visibility = () => { clearTimeout(timer); if (!document.hidden) void tick() }
    tick()
    document.addEventListener('visibilitychange', visibility)
    return () => { alive = false; clearTimeout(timer); document.removeEventListener('visibilitychange', visibility) }
  }, [status?.running])

  useEffect(() => { leakRevision.current++; setLeak(null) }, [status?.running, status?.pid, settings?.activeProfileId, settings?.mode,
    settings?.tunEnabled, settings?.systemProxy, settings?.stealthMode, settings?.blockQuic,
    settings?.blockStun, settings?.dnsViaProxy, settings?.dnsRespectRules, settings?.rfRushMode,
    settings?.strictRoute, settings?.ipv6, settings?.allowLan, selectedNode])

  const active = profiles.find((p) => p.id === settings?.activeProfileId)
  const shield = settings?.stealthMode !== false
  const rush = settings?.rfRushMode !== false
  const score = leak?.score
  const node = selectedNode || settings?.lastSelectedNode || ''
  const nodeLabel = splitServerName(node)
  const route = routeLabel(settings)
  const noCore = !status?.coreInstalled
  const noProfile = !active

  async function toggleCore() {
    await run(async () => {
      if (status?.running) await api.stop()
      else {
        const r = await api.start()
        if (r.needsAdmin || r.message === 'admin-required') await api.elevate()
      }
    })
  }

  async function checkLeak() {
    const revision = leakRevision.current
    setLeakBusy(true)
    try {
      const result = await api.leakCheck()
      if (revision === leakRevision.current) setLeak(result)
    }
    catch (e) {
      if (revision !== leakRevision.current) return
      setLeak({
        realIp: null, exitIp: null, masked: false,
        running: !!status?.running,
        message: e instanceof Error ? e.message : 'Проверка не выполнена',
        score: 0,
        checks: [],
      })
    } finally { setLeakBusy(false) }
  }

  async function phantomPanic() {
    if (!confirm('Остановить ядро, сбросить системный прокси и свернуть окно в область уведомлений?')) return
    setPanicBusy(true)
    try {
      const r = await api.ghostPanic()
      setLeak(null)
      await useApp.getState().refresh()
      alert(r.message)
    } catch (e) {
      alert(e instanceof Error ? e.message : 'Ошибка')
    } finally { setPanicBusy(false) }
  }

  async function activateRush() {
    setRushBusy(true)
    setRushInfo('')
    try {
      const r = await api.ghostRush()
      setRushInfo(r.message)
      if (r.node) {
        setLive({ now: r.node })
        await patchSettings({ lastSelectedNode: r.node })
      }
      await useApp.getState().refresh()
    } catch (e) {
      setRushInfo(e instanceof Error ? e.message : 'Не удалось выбрать сервер')
    } finally { setRushBusy(false) }
  }

  async function learnBypass() {
    setLearnBusy(true)
    try {
      const r = await api.ghostLearn()
      setRushInfo(r.message)
      await useApp.getState().refresh()
    } catch (e) {
      setRushInfo(e instanceof Error ? e.message : 'Не удалось сохранить маршрут')
    } finally { setLearnBusy(false) }
  }

  async function applyPreset(name: 'speed' | 'stability' | 'protection') {
    await run(async () => {
      await api.applyPreset(name)
      if (status?.running) await api.restart()
      await useApp.getState().refresh()
    })
  }

  return (
    <div className="dashboard">
      <div className="page-head dashboard-head">
        <div>
          <h1>Подключение</h1>
          <p>Управление подключением и трафиком.</p>
        </div>
        <div className="btn-row">
          <div className="mode-pills" aria-label="Режим маршрутизации">
            {(['rule', 'global', 'direct'] as const).map((m) => (
              <button type="button" key={m} aria-pressed={settings?.mode === m} className={settings?.mode === m ? 'on' : ''} disabled={busy} onClick={() => run(async () => {
                await patchSettings({ mode: m })
                if (status?.running) await clash.patchMode(m)
              })}>{i[m === 'rule' ? 'rule' : m === 'global' ? 'global' : 'direct']}</button>
            ))}
          </div>
        </div>
      </div>

      {guardian?.deadManArmed && (
        <div className="banner danger" style={{ marginBottom: 14 }}>
          Защитная блокировка прокси после сбоя ядра.
          <button className="btn" style={{ marginLeft: 12 }} disabled={busy} onClick={() => run(async () => {
            await api.disarmDeadMan()
            await useApp.getState().refresh()
          })}>Снять блокировку</button>
        </div>
      )}

      {noCore && (
        <div className="card" style={{ marginBottom: 16 }}>
          <EmptyState
            title="Ядро не установлено"
            text="Загрузите ядро, затем добавьте подписку и запустите подключение."
            action={<button className="btn primary" disabled={busy} onClick={() => run(() => api.download())}>{i.downloadCore}</button>}
          />
        </div>
      )}

      <div className="grid dashboard-top">
        <div className={'card hero-card' + (status?.running ? ' online' : '')}>
          <div className="hero-card-header">
            <div className="eyebrow"><span className={'dot' + (status?.running ? ' on' : '')} />{status?.running ? 'Соединение активно' : 'Готов к подключению'}</div>
            <span className="route-chip">{route}</span>
          </div>
          <div className="hero-orb">
            <button type="button" className={'power-button' + (status?.running ? ' connected' : '')} disabled={busy || noCore || noProfile} onClick={() => toggleCore()} aria-label={status?.running ? 'Остановить подключение' : 'Запустить подключение'} title={status?.running ? 'Остановить подключение' : 'Запустить подключение'} aria-busy={busy}>
              <svg aria-hidden="true" width="30" height="30" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round"><path d="M12 3v9M6.35 5.9a8 8 0 1 0 11.3 0" /></svg>
            </button>
            <h3>{busy ? 'Подождите…' : status?.running ? 'Подключено' : 'Не подключено'}</h3>
            <p className="hero-caption">{busy ? 'Применяем параметры соединения' : status?.running ? 'Нажмите, чтобы отключиться' : 'Нажмите кнопку или клавишу пробела'}</p>
          </div>
          <button type="button" className="selected-server" onClick={() => setPage('proxies')} aria-label={node ? `Выбрать сервер, сейчас ${nodeLabel.title}` : 'Выбрать сервер'}>
            <span className="server-avatar" aria-hidden="true">{nodeLabel.code ? <Flag code={nodeLabel.code} /> : '↗'}</span>
            <span className="selected-server-copy"><span className="hint">Сервер подключения</span><strong>{node ? nodeLabel.title : 'Выберите сервер'}</strong></span>
            <span className="server-chevron" aria-hidden="true">↗</span>
          </button>
          <div className="btn-row hero-actions">
            {status?.needsAdmin && <button className="btn" onClick={() => run(() => api.elevate())}>{i.elevate}</button>}
            <button className="btn" disabled={busy || !status?.running} onClick={() => run(() => api.restart())}>{i.restart}</button>
            <button className="btn" disabled={busy} onClick={() => run(async () => {
              await patchSettings({ tunEnabled: !settings?.tunEnabled, systemProxy: settings?.tunEnabled ? settings.systemProxy : false })
              if (status?.running) await api.restart()
            })}>
              {settings?.tunEnabled ? i.mixed : i.tun}
            </button>
          </div>
          <div className="preset-row performance-presets" role="group" aria-label="Приоритет подключения">
            <button type="button" aria-pressed={settings?.performancePreset === 'speed'} className={'btn' + (settings?.performancePreset === 'speed' ? ' primary' : '')} disabled={busy} onClick={() => applyPreset('speed')}>Скорость</button>
            <button type="button" aria-pressed={settings?.performancePreset === 'stability'} className={'btn' + (settings?.performancePreset === 'stability' ? ' primary' : '')} disabled={busy} onClick={() => applyPreset('stability')}>Стабильность</button>
            <button type="button" aria-pressed={settings?.performancePreset === 'protection'} className={'btn' + (settings?.performancePreset === 'protection' ? ' primary' : '')} disabled={busy} onClick={() => applyPreset('protection')}>Защита</button>
          </div>
          <details className="connection-options">
            <summary>Параметры сети <span aria-hidden="true">⌄</span></summary>
            <div className="preset-row persona-presets">
            {(['auto', 'home', 'mobile', 'corporate'] as const).map((p) => (
              <button
                key={p}
                type="button"
                className={'btn' + (settings?.networkPersona === p ? ' primary' : '')}
                disabled={busy}
                onClick={() => run(async () => {
                  await api.applyPersona(p)
                  if (status?.running) await api.restart()
                  await useApp.getState().refresh()
                })}
              >
                {p === 'auto' ? 'Авто' : p === 'home' ? 'Дом' : p === 'mobile' ? 'Мобильная' : 'Корпоративная'}
              </button>
            ))}
            </div>
            <button type="button" className="btn danger panic-action" disabled={panicBusy || busy} onClick={() => phantomPanic()}>
              {panicBusy ? 'Выполняется…' : 'Экстренное отключение'}
            </button>
          </details>
        </div>
        <TrafficCard active={active} guardian={guardian} graph={settings?.appearance?.trafficGraph !== false} language={settings?.language || 'ru'} />
      </div>

      <div className="shield-row connection-status-strip" role="group" aria-label="Параметры подключения">
        <div className={'shield-chip' + (shield ? ' on' : '')}>
          <span className="shield-dot" />
          {i.shield} {shield ? 'включена' : 'отключена'}
        </div>
        <div className={'shield-chip' + (rush ? ' on' : '')}>
          <span className="shield-dot" />
          {rush ? 'Ускорение включено' : 'Ускорение отключено'}
        </div>
        <div className="shield-chip">{settings?.dnsViaProxy !== false ? 'DNS через прокси' : 'DNS напрямую'}</div>
        <button
          type="button"
          className={'btn' + (shield ? '' : ' primary')}
          disabled={busy}
          onClick={() => run(async () => {
            const next = !(settings?.stealthMode !== false)
            await patchSettings({
              stealthMode: next, blockQuic: next, blockStun: next, dnsViaProxy: next,
              dnsRespectRules: next, randomFingerprint: next, silentLogs: next,
              useMrsRules: next, hideFromCapture: next, performancePreset: 'custom',
            })
            if (status?.running) await api.restart()
          })}
        >
          {shield ? 'Отключить' : 'Включить защиту'}
        </button>
      </div>

      <div className="card ghost-pulse privacy-card">
        <div className="group-head">
          <div>
            <div className="section-kicker">Конфиденциальность</div>
            <h2 style={{ margin: 0 }}>Проверить соединение</h2>
            <p className="hint" style={{ marginTop: 6 }}>Адрес выхода, DNS и параметры защиты.</p>
          </div>
          <button type="button" className="btn" disabled={!status?.running || leakBusy} onClick={() => checkLeak()}>
            {leakBusy ? 'Проверка…' : 'Проверить'}
          </button>
        </div>
        {leak ? (
          <>
            <div className="pulse-score-row">
              <div className={'pulse-ring' + (leak.tunMode && !leak.masked ? '' : score != null && score >= 80 ? ' good' : score != null && score >= 55 ? ' mid' : ' bad')}>
                <div className="pulse-num">{score ?? '—'}</div>
                <div className="pulse-lbl">Настройки</div>
              </div>
              <div className="grid cols-3" style={{ flex: 1, minWidth: 0 }}>
                <div className="stat" style={{ fontSize: 16 }}>{leak.realIp || (leak.tunMode ? 'Туннель' : '—')}<small>Внешний адрес</small></div>
                <div className="stat" style={{ fontSize: 16 }}>{leak.exitIp || '—'}<small>Адрес выхода</small></div>
                <div className="stat" style={{ fontSize: 16 }}>{leak.masked ? 'Адреса различаются' : 'Не подтверждено'}<small title={leak.message}>{leak.message}</small></div>
              </div>
            </div>
            <p className="hint">Оценка проверок конфигурации: {score ?? '—'} / 100. Результат не подтверждает анонимность.</p>
            {!!leak.checks?.length && (
              <div className="pulse-checks">
                {leak.checks.map((c) => (
                  <div key={c.id} className={'pulse-check' + (c.ok ? ' on' : '')} title={c.detail || ''}>
                    <span className="shield-dot" />
                    {c.label}
                  </div>
                ))}
              </div>
            )}
          </>
        ) : null}
      </div>

      <details className="card advanced-card rush-card">
        <summary className="advanced-summary"><span><strong>Маршрутизация и ускорение</strong><span className="hint">Серверы, правила и сетевые инструменты</span></span><span aria-hidden="true">⌄</span></summary>
        <div className="advanced-body">
          <p className="hint">Ускоренный канал отдаёт приоритет Hysteria2, TUIC и VLESS. Сети Российской Федерации направляются напрямую.</p>
          <div className="shield-row advanced-policies">
            <span className="shield-chip">{settings?.useMrsRules !== false ? 'Бинарные правила' : 'Текстовые правила'}</span>
            <span className="shield-chip">Блокировка QUIC · {settings?.blockQuic !== false ? 'вкл.' : 'выкл.'}</span>
            <span className="shield-chip">Фильтр STUN · {settings?.blockStun !== false ? 'вкл.' : 'выкл.'}</span>
          </div>
          <div className="btn-row advanced-actions">
            <button type="button" className={'btn' + (rush ? '' : ' primary')} disabled={busy} onClick={() => run(async () => {
              await patchSettings({ rfRushMode: !rush, preferRushLane: true, performancePreset: 'custom' })
              if (status?.running) await api.restart()
            })}>
              {rush ? 'Отключить' : 'Включить'}
            </button>
            <button type="button" className="btn primary" disabled={rushBusy || busy} onClick={() => activateRush()}>
              {rushBusy ? 'Выбор сервера…' : 'Оптимальный сервер'}
            </button>
            <button type="button" className="btn" disabled={learnBusy || !status?.running} onClick={() => learnBypass()}>
              {learnBusy ? 'Сохранение…' : 'Сохранить маршрут'}
            </button>
            <button type="button" className="btn" disabled={busy || !status?.running} onClick={() => run(async () => {
              const r = await api.learnDirect()
              setRushInfo(r.message)
              await useApp.getState().refresh()
            })}>
              Smart Direct
            </button>
            <button type="button" className="btn" disabled={busy || !status?.running} onClick={() => run(() => api.warmStart())}>
              Warm start
            </button>
          </div>
          {rushInfo && <div className="banner" style={{ marginTop: 12, marginBottom: 0 }}>{rushInfo}</div>}
        </div>
      </details>

      <details className="card advanced-card system-info">
        <summary className="advanced-summary"><span><strong>Информация о подключении</strong><span className="hint">Профиль, ядро и локальные порты</span></span><span aria-hidden="true">⌄</span></summary>
        <div className="grid cols-2 advanced-body">
        <div className="card">
          <h2>Профиль</h2>
          <div className="stat" style={{ fontSize: 20, fontFamily: 'var(--font-ui)' }}>{active?.name || i.noProfile}</div>
          <p className="hint">{node ? 'Сервер · ' + node : 'Добавьте профиль и запустите подключение.'}</p>
        </div>
        <div className="card">
          <h2>Ядро</h2>
          <div className="core-ver">{status?.version || 'Ядро'}</div>
          <p className="hint">
            Приложение {status?.appVersion || '—'} · процесс {status?.pid || '—'} · {status?.coreInstalled ? 'ядро установлено' : 'ядро отсутствует'}
          </p>
        </div>
        <div className="card port-card" style={{ gridColumn: '1 / -1' }}>
          <h2>Порты подключения</h2>
          <PortSwap />
        </div>
        </div>
      </details>
    </div>
  )
}
