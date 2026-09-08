import { useState } from 'react'
import { api } from '../lib/api'
import { t } from '../lib/i18n'
import { useApp } from '../store/app'

export function Profiles() {
  const { settings, profiles, busy, run } = useApp()
  const i = t(settings?.language || 'ru')
  const [name, setName] = useState('Мой профиль')
  const [kind, setKind] = useState<'subscription' | 'paste'>('subscription')
  const [source, setSource] = useState('')
  const [payload, setPayload] = useState('')

  return (
    <div>
      <div className="page-head"><div><h1>{i.profiles}</h1><p>Happ, v2ray, Clash YAML — ссылка или вставка. Узлы сами переводятся в mihomo.</p></div></div>
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
          {profiles.length === 0 && <p className="hint">Пока пусто.</p>}
          {profiles.map((p) => (
            <div key={p.id} className="card" style={{ marginBottom: 10, boxShadow: 'none', background: p.id === settings?.activeProfileId ? 'color-mix(in srgb, var(--accent) 12%, transparent)' : 'transparent' }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8 }}>
                <strong>{p.name}</strong>
                {p.id === settings?.activeProfileId && <span className="badge on">активен</span>}
              </div>
              <p className="hint">{p.kind} · {(p.bytes / 1024).toFixed(1)} KB · {new Date(p.updatedAt).toLocaleString()}</p>
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
