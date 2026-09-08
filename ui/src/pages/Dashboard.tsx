import { useEffect, useState } from 'react'
import { api, clash, formatBytes, formatSpeed } from '../lib/api'
import { t } from '../lib/i18n'
import { useApp } from '../store/app'
import { Sparkline } from '../components/Sparkline'

export function Dashboard() {
  const { status, settings, profiles, busy, run, live, patchSettings } = useApp()
  const i = t(settings?.language || 'ru')
  const [histU, setHistU] = useState<number[]>(Array(40).fill(0))
  const [histD, setHistD] = useState<number[]>(Array(40).fill(0))
  const [leak, setLeak] = useState<Awaited<ReturnType<typeof api.leakCheck>> | null>(null)
  const [leakBusy, setLeakBusy] = useState(false)

  useEffect(() => {
    setHistU((h) => [...h.slice(1), live.up])
    setHistD((h) => [...h.slice(1), live.down])
  }, [live.up, live.down])

  const active = profiles.find((p) => p.id === settings?.activeProfileId)
  const shield = settings?.stealthMode !== false

  async function checkLeak() {
    setLeakBusy(true)
    try { setLeak(await api.leakCheck()) }
    catch (e) {
      setLeak({
        realIp: null, exitIp: null, masked: false,
        running: !!status?.running,
        message: e instanceof Error ? e.message : 'ошибка',
      })
    } finally { setLeakBusy(false) }
  }

  return (
    <div>
      <div className="page-head">
        <div>
          <h1>Jester</h1>
          <p>{i.tagline} Пробел — старт/стоп.</p>
        </div>
        <div className="mode-pills">
          {(['rule', 'global', 'direct'] as const).map((m) => (
            <button key={m} className={settings?.mode === m ? 'on' : ''} onClick={() => run(async () => {
              await patchSettings({ mode: m })
              if (status?.running) await clash.patchMode(m)
            })}>{i[m === 'rule' ? 'rule' : m === 'global' ? 'global' : 'direct']}</button>
          ))}
        </div>
      </div>

      <div className="grid cols-2">
        <div className="card hero-card">
          <div className="hero-toggle">
            <div>
              <div className="eyebrow">{status?.running ? (shield ? 'неуловим · онлайн' : 'канал открыт') : 'тишина'}</div>
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
            {status?.needsAdmin && <button className="btn" onClick={() => api.elevate()}>{i.elevate}</button>}
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
          <h2>Поток</h2>
          {settings?.appearance.trafficGraph !== false && <Sparkline up={histU} down={histD} />}
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
        <div className="shield-chip">{settings?.useMrsRules !== false ? 'rules .mrs' : 'rules text'}</div>
        <div className="shield-chip">{settings?.blockQuic !== false ? 'anti-QUIC' : 'QUIC'}</div>
        <div className="shield-chip">{settings?.blockStun !== false ? 'anti-STUN' : 'STUN'}</div>
        <div className="shield-chip">{settings?.dnsViaProxy !== false ? 'DNS→VPN' : 'DNS прямо'}</div>
        <div className="shield-chip">{settings?.randomFingerprint !== false ? 'TLS random' : 'TLS chrome'}</div>
        <div className="shield-chip">{settings?.hideFromCapture !== false ? 'no-capture' : 'capture'}</div>
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
          {shield ? 'Ослабить щит' : 'Включить щит'}
        </button>
      </div>

      <div className="card" style={{ marginTop: 16 }}>
        <div className="group-head">
          <h2 style={{ margin: 0 }}>Проверка маски</h2>
          <button type="button" className="btn primary" disabled={!status?.running || leakBusy} onClick={() => checkLeak()}>
            {leakBusy ? 'Проверка…' : 'Сверить IP'}
          </button>
        </div>
        {leak ? (
          <div className="grid cols-3" style={{ marginTop: 10 }}>
            <div className="stat" style={{ fontSize: 16 }}>{leak.realIp || '—'}<small>Реальный</small></div>
            <div className="stat" style={{ fontSize: 16 }}>{leak.exitIp || '—'}<small>Через Jester</small></div>
            <div className="stat" style={{ fontSize: 16 }}>{leak.masked ? 'OK' : '—'}<small>{leak.message}</small></div>
          </div>
        ) : (
          <p className="hint">Сравнивает IP напрямую и через прокси. Если «Через Jester» другой — маска работает.</p>
        )}
      </div>

      <div className="grid cols-3" style={{ marginTop: 16 }}>
        <div className="card">
          <h2>Профиль</h2>
          <div className="stat" style={{ fontSize: 20, fontFamily: 'var(--font-ui)' }}>{active?.name || i.noProfile}</div>
          <p className="hint">{live.now ? 'Узел · ' + live.now : 'Добавьте профиль, затем запустите'}</p>
        </div>
        <div className="card">
          <h2>Ядро</h2>
          <div className="core-ver">{status?.version || 'mihomo'}</div>
          <p className="hint">PID {status?.pid || '—'} · {status?.coreInstalled ? 'готово' : 'нет файла'} · wintun {status?.wintunInstalled ? 'ok' : 'нет'}</p>
        </div>
        <div className="card">
          <h2>Порты</h2>
          <button className="btn ghost copy" onClick={() => navigator.clipboard.writeText('127.0.0.1:' + (settings?.mixedPort || 7890))}>
            127.0.0.1:{settings?.mixedPort}
          </button>
          <p className="hint">socks {settings?.socksPort} · api {settings?.externalPort} · LAN {settings?.allowLan ? 'открыт' : 'только localhost'}</p>
        </div>
      </div>
    </div>
  )
}
