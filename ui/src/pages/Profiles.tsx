import { useShallow } from 'zustand/react/shallow'
import { useState } from 'react'
import { api } from '../lib/api'
import { t } from '../lib/i18n'
import { useApp } from '../store/app'
import { MenuSelect } from '../components/MenuSelect'
import { EmptyState, formatExpire, formatQuota } from '../components/UiBits'

export function Profiles() {
  const { settings, profiles, busy, run, refresh } = useApp(useShallow(({ settings, profiles, busy, run, refresh }) => ({ settings, profiles, busy, run, refresh })))
  const i = t(settings?.language || 'ru')
  const [name, setName] = useState('Мой профиль')
  const [kind, setKind] = useState<'subscription' | 'paste'>('subscription')
  const [source, setSource] = useState('')
  const [payload, setPayload] = useState('')
  const [updateMsg, setUpdateMsg] = useState('')

  return (
    <div>
      <div className="page-head">
        <div>
          <h1>{i.profiles}</h1>
          <p>Поддерживаются подписки и конфигурации Clash, v2ray и Hysteria2.</p>
        </div>
        <button
          className="btn"
          disabled={busy}
          onClick={() => run(async () => {
            const r = await api.updateAllProfiles()
            setUpdateMsg(`Обновлено: ${r.updated}. Ошибок: ${r.failed}.`)
            await refresh()
          })}
        >
          Обновить все
        </button>
      </div>
      {updateMsg && <div className="banner" style={{ marginBottom: 12 }}>{updateMsg}</div>}
      <div className="grid cols-2">
        <div className="card">
          <h2>{i.importProfile}</h2>
          <div className="field"><label>{i.name}</label><input value={name} onChange={(e) => setName(e.target.value)} /></div>
          <div className="mode-pills" style={{ marginBottom: 14 }}>
            <button className={kind === 'subscription' ? 'on' : ''} onClick={() => setKind('subscription')}>{i.subUrl}</button>
            <button className={kind === 'paste' ? 'on' : ''} onClick={() => setKind('paste')}>{i.pasteYaml}</button>
          </div>
          {kind === 'subscription' ? (
            <div className="field"><label>URL</label><input value={source} onChange={(e) => setSource(e.target.value)} placeholder="https://…" /></div>
          ) : (
            <div className="field"><label>YAML / vless / hysteria2</label><textarea rows={10} value={payload} onChange={(e) => setPayload(e.target.value)} /></div>
          )}
          <button className="btn primary" disabled={busy} onClick={() => run(() => api.createProfile({ name, kind, source, payload }))}>{i.save}</button>
        </div>
        <div className="card">
          <h2>Список</h2>
          {profiles.length === 0 && (
            <EmptyState title="Нет профилей" text="Добавьте ссылку подписки слева. После импорта серверы появятся в разделе «Прокси»." />
          )}
          {profiles.map((p) => (
            <div key={p.id} className="card" style={{ marginBottom: 10, boxShadow: 'none', background: p.id === settings?.activeProfileId ? 'color-mix(in srgb, var(--accent) 12%, transparent)' : 'transparent' }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8 }}>
                <strong>{p.name}</strong>
                {p.id === settings?.activeProfileId && <span className="badge on">Активен</span>}
              </div>
              <p className="hint">{p.kind} · {(p.bytes / 1024).toFixed(1)} KB · {new Date(p.updatedAt).toLocaleString()}</p>
              {p.kind === 'subscription' && (
                <p className="hint">
                  {formatQuota((p.upload || 0) + (p.download || 0), p.total || 0)}
                  {formatExpire(p.expireUnix) ? ` · до ${formatExpire(p.expireUnix)}` : ''}
                  {p.hwidLimited ? ' · лимит устройств' : ''}
                </p>
              )}
              <div className="field" style={{ marginTop: 8 }}>
                <label>Изоляция сессии · TLS fingerprint</label>
                <MenuSelect
                  label="Изоляция сессии"
                  value={p.fingerprintOverride || ''}
                  onChange={(value) => run(async () => {
                    await api.patchProfile(p.id, { fingerprintOverride: value || null })
                    await refresh()
                  })}
                  options={[
                    { value: '', label: 'Как в настройках' },
                    ...['chrome', 'firefox', 'safari', 'ios', 'android', 'edge', 'random'].map((value) => ({ value, label: value })),
                  ]}
                />
              </div>
              <div className="field">
                <label>Изоляция · DoH (по строке)</label>
                <textarea
                  rows={2}
                  defaultValue={(p.nameserverOverride || []).join('\n')}
                  key={p.id + '-dns-' + (p.nameserverOverride || []).join(',')}
                  onBlur={(e) => run(async () => {
                    await api.patchProfile(p.id, { nameserverOverride: e.target.value })
                    await refresh()
                  })}
                  placeholder="https://1.1.1.1/dns-query"
                />
              </div>
              <div className="btn-row">
                <button className="btn" onClick={() => run(() => api.activateProfile(p.id))}>Выбрать</button>
                {p.kind === 'subscription' && <button className="btn" onClick={() => run(() => api.updateProfile(p.id))}>Обновить</button>}
                <button className="btn danger" onClick={() => run(() => api.deleteProfile(p.id))}>Удалить</button>
              </div>
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
