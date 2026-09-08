import { useEffect, useState } from 'react'
import { api, clash, type ClashRule, type RuleListStatus } from '../lib/api'
import { t } from '../lib/i18n'
import { useApp } from '../store/app'

export function Rules() {
  const { settings, status, busy, patchSettings, run, refresh } = useApp()
  const i = t(settings?.language || 'ru')
  const [rules, setRules] = useState<ClashRule[]>([])
  const [list, setList] = useState<RuleListStatus | null>(null)
  const [proxyText, setProxyText] = useState('')
  const [directText, setDirectText] = useState('')

  useEffect(() => {
    if (!settings) return
    setProxyText(settings.customProxyRules || '')
    setDirectText(settings.customDirectRules || '')
  }, [settings?.customProxyRules, settings?.customDirectRules])

  useEffect(() => {
    api.rulesStatus().then(setList).catch(() => setList(null))
    if (!status?.running) return
    clash.rules().then((r) => setRules(r.rules || [])).catch(() => setRules([]))
  }, [status?.running, settings?.refilterEnabled])

  if (!settings) return <div className="empty"><h3>{i.rules}</h3><p>Нет связи с хостом — правила недоступны.</p></div>

  async function saveLists() {
    await patchSettings({
      customProxyRules: proxyText,
      customDirectRules: directText,
    })
    await api.rulesUpdate().catch(() => undefined)
    if (status?.running) await api.restart()
    await refresh()
    setList(await api.rulesStatus())
    if (status?.running) {
      const r = await clash.rules().catch(() => ({ rules: [] as ClashRule[] }))
      setRules(r.rules || [])
    }
  }

  async function downloadList() {
    await api.rulesUpdate()
    setList(await api.rulesStatus())
    if (status?.running) await api.restart()
    await refresh()
  }

  return (
    <div>
      <div className="page-head">
        <div>
          <h1>{i.rules}</h1>
          <p>Заблокированное — через VPN. Остальное — напрямую. Как в Happ.</p>
        </div>
        <div className="btn-row">
          <button type="button" className="btn" disabled={busy} onClick={() => run(downloadList)}>Скачать список</button>
          <button type="button" className="btn primary" disabled={busy} onClick={() => run(saveLists)}>Применить</button>
        </div>
      </div>

      <div className="grid cols-2">
        <div className="card">
          <h2>Re:filter</h2>
          <p className="hint">Официальный список заблокированных доменов/IP в РФ. Обновляется с GitHub.</p>
          <div className="field row">
            <div><label>Включить список</label><div className="hint">Совпадения → PROXY (VPN)</div></div>
            <button type="button" className={'toggle' + (settings.refilterEnabled ? ' on' : '')} onClick={() => run(async () => {
              await patchSettings({ refilterEnabled: !settings.refilterEnabled })
              if (status?.running) await api.restart()
            })} />
          </div>
          <div className="field row">
            <div><label>IP-список (ipsum)</label><div className="hint">Блокировки по адресам</div></div>
            <button type="button" className={'toggle' + (settings.refilterUseIp ? ' on' : '')} onClick={() => patchSettings({ refilterUseIp: !settings.refilterUseIp })} />
          </div>
          <div className="field row">
            <div><label>Всё остальное</label><div className="hint">MATCH в конце</div></div>
            <select value={settings.finalOutbound || 'DIRECT'} onChange={(e) => run(async () => {
              await patchSettings({ finalOutbound: e.target.value })
              if (status?.running) await api.restart()
            })}>
              <option value="DIRECT">напрямую (быстрее)</option>
              <option value="PROXY">через VPN</option>
            </select>
          </div>
          <div className="stat" style={{ fontSize: 15, marginTop: 8 }}>
            {list ? <>доменов {list.domainsCount.toLocaleString('ru')} · IP {list.ipsCount.toLocaleString('ru')}</> : 'список ещё не скачан'}
            <small>{list?.domainsUpdatedAt ? 'обновлён ' + new Date(list.domainsUpdatedAt).toLocaleString('ru') : 'нажмите «Скачать список»'}</small>
          </div>
        </div>

        <div className="card">
          <h2>Свои правила</h2>
          <p className="hint">По одному домену в строке. Можно вставить свой список.</p>
          <div className="field">
            <label>Через VPN</label>
            <textarea rows={6} placeholder={'youtube.com\ninstagram.com\ndiscord.com'} value={proxyText} onChange={(e) => setProxyText(e.target.value)} />
          </div>
          <div className="field">
            <label>Напрямую</label>
            <textarea rows={4} placeholder={'gosuslugi.ru\nbank.ru'} value={directText} onChange={(e) => setDirectText(e.target.value)} />
          </div>
        </div>
      </div>

      {status?.running && (
        <div className="card" style={{ marginTop: 16, overflow: 'auto' }}>
          <h2>Активные правила ядра · {rules.length}</h2>
          <table className="table">
            <thead><tr><th>Тип</th><th>Значение</th><th>Куда</th></tr></thead>
            <tbody>
              {rules.slice(0, 80).map((r, idx) => (
                <tr key={idx}><td className="mono">{r.type}</td><td>{r.payload}</td><td>{r.proxy}</td></tr>
              ))}
            </tbody>
          </table>
          {rules.length > 80 && <p className="hint">Показаны первые 80 из {rules.length}.</p>}
        </div>
      )}
    </div>
  )
}
