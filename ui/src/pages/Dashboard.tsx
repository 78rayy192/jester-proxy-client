import { useEffect, useState } from 'react'
import { api, clash, formatBytes, formatSpeed, type LeakCheckResult } from '../lib/api'
import { t } from '../lib/i18n'
import { useApp } from '../store/app'
import { PortSwap } from '../components/PortSwap'
import { Sparkline } from '../components/Sparkline'

export function Dashboard() {
  const { status, settings, profiles, busy, run, live, patchSettings, setLive } = useApp()
  const i = t(settings?.language || 'ru')
  const [histU, setHistU] = useState<number[]>(Array(40).fill(0))
  const [histD, setHistD] = useState<number[]>(Array(40).fill(0))
  const [leak, setLeak] = useState<LeakCheckResult | null>(null)
  const [leakBusy, setLeakBusy] = useState(false)
  const [panicBusy, setPanicBusy] = useState(false)
  const [rushBusy, setRushBusy] = useState(false)
  const [learnBusy, setLearnBusy] = useState(false)
  const [rushInfo, setRushInfo] = useState('')

  useEffect(() => {
    setHistU((h) => [...h.slice(1), live.up])
    setHistD((h) => [...h.slice(1), live.down])
  }, [live.up, live.down])

  const active = profiles.find((p) => p.id === settings?.activeProfileId)
  const shield = settings?.stealthMode !== false
  const rush = settings?.rfRushMode !== false
  const score = leak?.score

  async function checkLeak() {
    setLeakBusy(true)
    try { setLeak(await api.leakCheck()) }
    catch (e) {
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
      setRushInfo(r.message + (r.node ? '' : ''))
      if (r.node) setLive({ now: r.node })
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

  return (
    <div>
      <div className="page-head">
        <div>
          <h1>Jester</h1>
          <p>{i.tagline} Пробел — запуск и остановка.</p>
        </div>
        <div className="btn-row">
          <button type="button" className="btn danger" disabled={panicBusy || busy} onClick={() => phantomPanic()}>
            {panicBusy ? 'Выполняется…' : 'Экстренное отключение'}
          </button>
          <div className="mode-pills">
            {(['rule', 'global', 'direct'] as const).map((m) => (
              <button key={m} className={settings?.mode === m ? 'on' : ''} onClick={() => run(async () => {
                await patchSettings({ mode: m })
                if (status?.running) await clash.patchMode(m)
              })}>{i[m === 'rule' ? 'rule' : m === 'global' ? 'global' : 'direct']}</button>
            ))}
          </div>
        </div>
      </div>

      <div className="grid cols-2">
        <div className="card hero-card">
          <div className="hero-toggle">
            <div>
              <div className="eyebrow">{status?.running ? (shield ? 'Защита включена' : 'Подключение активно') : 'Ожидание'}</div>
              <h3>{settings?.tunEnabled ? i.tun : i.mixed}</h3>
              <p className="hint">{settings?.tunEnabled ? i.tunHint : 'Системный прокси 127.0.0.1:' + (settings?.mixedPort || 7890)}</p>
            </div>
            <button className={'toggle lg' + (status?.running ? ' on' : '')} disabled={busy} onClick={() => run(async () => {
              if (status?.running) await api.stop()
              else {
                const r = await api.start()
                if (r.needsAdmin || r.message === 'admin-required') await api.elevate()
              }
            })} />
          </div>
          <div className="btn-row" style={{ marginTop: 18 }}>
            {!status?.coreInstalled && <button className="btn primary" disabled={busy} onClick={() => run(() => api.download())}>{i.downloadCore}</button>}
            {status?.needsAdmin && <button className="btn" onClick={() => run(() => api.elevate())}>{i.elevate}</button>}
            <button className="btn" disabled={busy || !status?.running} onClick={() => run(() => api.restart())}>{i.restart}</button>
            <button className="btn" disabled={busy} onClick={() => run(async () => {
              await patchSettings({ tunEnabled: !settings?.tunEnabled, systemProxy: settings?.tunEnabled ? settings.systemProxy : false })
              if (status?.running) await api.restart()
            })}>
              {settings?.tunEnabled ? i.mixed : i.tun}
            </button>
          </div>
        </div>
        <div className="card">
          <h2>Трафик</h2>
          {settings?.appearance?.trafficGraph !== false && <Sparkline up={histU} down={histD} />}
          <div className="grid cols-3" style={{ marginTop: 8 }}>
            <div className="stat rate" title={formatSpeed(live.down)}>{formatSpeed(live.down)}<small>{i.down}</small></div>
            <div className="stat rate" title={formatSpeed(live.up)}>{formatSpeed(live.up)}<small>{i.up}</small></div>
            <div className="stat rate" title={formatBytes(live.mem)}>{formatBytes(live.mem)}<small>{i.ram}</small></div>
          </div>
        </div>
      </div>

      <div className="shield-row">
        <div className={'shield-chip' + (shield ? ' on' : '')}>
          <span className="shield-dot" />
          {i.shield} · {shield ? i.shieldOn : i.shieldOff}
        </div>
        <div className={'shield-chip' + (rush ? ' on' : '')}>
          <span className="shield-dot" />
          Ускоренный канал · {rush ? 'включён' : 'отключён'}
        </div>
        <div className="shield-chip">{settings?.useMrsRules !== false ? 'Бинарные правила' : 'Текстовые правила'}</div>
        <div className="shield-chip">{settings?.blockQuic !== false ? 'QUIC заблокирован' : 'QUIC разрешён'}</div>
        <div className="shield-chip">{settings?.blockStun !== false ? 'STUN заблокирован' : 'STUN разрешён'}</div>
        <div className="shield-chip">{settings?.dnsViaProxy !== false ? 'DNS через туннель' : 'DNS напрямую'}</div>
        <div className="shield-chip">{settings?.randomFingerprint !== false ? 'Случайный TLS' : 'TLS Chrome'}</div>
        <div className="shield-chip">{settings?.hideFromCapture !== false ? 'Захват отключён' : 'Захват разрешён'}</div>
        <button
          type="button"
          className={'btn' + (shield ? '' : ' primary')}
          disabled={busy}
          onClick={() => run(async () => {
            const next = !(settings?.stealthMode !== false)
            await patchSettings({
              stealthMode: next, blockQuic: next, blockStun: next, dnsViaProxy: next,
              dnsRespectRules: next, randomFingerprint: next, silentLogs: next,
              useMrsRules: next, hideFromCapture: next,
            })
            if (status?.running) await api.restart()
          })}
        >
          {shield ? 'Отключить защиту' : 'Включить защиту'}
        </button>
      </div>

      <div className="card rush-card" style={{ marginTop: 16 }}>
        <div className="group-head">
          <div>
            <h2 style={{ margin: 0 }}>Ускоренный канал</h2>
            <p className="hint" style={{ marginTop: 6 }}>
              Приоритет протоколов Hysteria2, TUIC и VLESS. Сети Российской Федерации направляются напрямую. Резервный список заблокированных ресурсов сохраняется между запусками.
            </p>
          </div>
          <div className="btn-row">
            <button type="button" className={'btn' + (rush ? '' : ' primary')} disabled={busy} onClick={() => run(async () => {
              await patchSettings({ rfRushMode: !rush, preferRushLane: true })
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
          </div>
        </div>
        {rushInfo && <div className="banner" style={{ marginTop: 12, marginBottom: 0 }}>{rushInfo}</div>}
      </div>

      <div className="card ghost-pulse" style={{ marginTop: 16 }}>
        <div className="group-head">
          <div>
            <h2 style={{ margin: 0 }}>Контроль конфиденциальности</h2>
            <p className="hint" style={{ marginTop: 6 }}>Сравнение внешнего адреса и адреса выхода, состояние защиты и проверка UDP-портов.</p>
          </div>
          <button type="button" className="btn primary" disabled={!status?.running || leakBusy} onClick={() => checkLeak()}>
            {leakBusy ? 'Проверка…' : 'Выполнить проверку'}
          </button>
        </div>

        {leak ? (
          <>
            <div className="pulse-score-row">
              <div className={'pulse-ring' + (score != null && score >= 80 ? ' good' : score != null && score >= 55 ? ' mid' : ' bad')}>
                <div className="pulse-num">{score ?? '—'}</div>
                <div className="pulse-lbl">Показатель</div>
              </div>
              <div className="grid cols-3" style={{ flex: 1, minWidth: 0 }}>
                <div className="stat" style={{ fontSize: 16 }}>{leak.realIp || (leak.tunMode ? 'Туннель' : '—')}<small>Внешний адрес</small></div>
                <div className="stat" style={{ fontSize: 16 }}>{leak.exitIp || '—'}<small>Адрес выхода</small></div>
                <div className="stat" style={{ fontSize: 16 }}>{leak.masked ? 'OK' : '—'}<small>{leak.message}</small></div>
              </div>
            </div>
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
        ) : (
          <p className="hint">Запустите проверку, чтобы сравнить внешний адрес и адрес выхода.</p>
        )}
      </div>

      <div className="grid cols-3" style={{ marginTop: 16 }}>
        <div className="card">
          <h2>Профиль</h2>
          <div className="stat" style={{ fontSize: 20, fontFamily: 'var(--font-ui)' }}>{active?.name || i.noProfile}</div>
          <p className="hint">{live.now ? 'Сервер · ' + live.now : 'Добавьте профиль и запустите подключение.'}</p>
        </div>
        <div className="card">
          <h2>Ядро</h2>
          <div className="core-ver">{status?.version || 'Ядро'}</div>
          <p className="hint">Процесс {status?.pid || '—'} · {status?.coreInstalled ? 'ядро установлено' : 'ядро отсутствует'} · WinTUN {status?.wintunInstalled ? 'установлен' : 'отсутствует'}</p>
        </div>
        <div className="card port-card" style={{ gridColumn: '1 / -1' }}>
          <h2>Порты подключения</h2>
          <PortSwap />
        </div>
      </div>
    </div>
  )
}
