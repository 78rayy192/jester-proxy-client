import { useEffect, useMemo, useState } from 'react'
import { clash, formatBytes, type ClashConn } from '../lib/api'
import { t } from '../lib/i18n'
import { useApp } from '../store/app'

export function Connections() {
  const { settings, status } = useApp()
  const i = t(settings?.language || 'ru')
  const [rows, setRows] = useState<ClashConn[]>([])
  const [totals, setTotals] = useState({ down: 0, up: 0 })
  const [q, setQ] = useState('')

  useEffect(() => {
    if (!status?.running) return
    const tick = async () => {
      try {
        const r = await clash.connections()
        setRows(r.connections || [])
        setTotals({ down: r.downloadTotal, up: r.uploadTotal })
      } catch { /* ignore */ }
    }
    tick()
    const id = setInterval(tick, 2000)
    return () => clearInterval(id)
  }, [status?.running])

  const filtered = useMemo(() => {
    const s = q.trim().toLowerCase()
    if (!s) return rows
    return rows.filter((c) =>
      (c.metadata?.host || '').toLowerCase().includes(s)
      || (c.metadata?.process || '').toLowerCase().includes(s)
      || (c.chains || []).join(' ').toLowerCase().includes(s)
      || (c.rulePayload || '').toLowerCase().includes(s))
  }, [rows, q])

  if (!status?.running) return <div className="empty"><h3>{i.connections}</h3><p>Ядро не запущено.</p></div>

  return (
    <div>
      <div className="page-head">
        <div>
          <h1>{i.connections}</h1>
          <p>{filtered.length}/{rows.length} · ↓ {formatBytes(totals.down)} · ↑ {formatBytes(totals.up)}</p>
        </div>
        <button className="btn danger" onClick={() => clash.closeAll()}>{i.closeAll}</button>
      </div>
      <input className="search" placeholder="Узел, процесс или правило" value={q} onChange={(e) => setQ(e.target.value)} style={{ marginBottom: 14 }} />
      <div className="card" style={{ overflow: 'auto' }}>
        <table className="table">
          <thead>
            <tr><th>Узел</th><th>Процесс</th><th>Маршрут</th><th>Правило</th><th>Входящий</th><th>Исходящий</th></tr>
          </thead>
          <tbody>
            {filtered.map((c) => (
              <tr key={c.id}>
                <td>{c.metadata?.host || c.metadata?.destinationIP || '—'}</td>
                <td className="mono">{c.metadata?.process || '—'}</td>
                <td>{(c.chains || []).slice().reverse().join(' → ')}</td>
                <td>{c.rule} {c.rulePayload}</td>
                <td className="mono">{formatBytes(c.download)}</td>
                <td className="mono">{formatBytes(c.upload)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
