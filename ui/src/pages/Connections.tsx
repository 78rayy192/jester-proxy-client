import { useShallow } from 'zustand/react/shallow'
import { useMemo, useState } from 'react'
import { usePolling } from '../lib/usePolling'
import { clash, formatBytes, type ClashConn } from '../lib/api'
import { t } from '../lib/i18n'
import { useApp } from '../store/app'
import { EmptyState } from '../components/UiBits'

export function Connections() {
  const { settings, status, busy, run } = useApp(useShallow(({ settings, status, busy, run }) => ({ settings, status, busy, run })))
  const i = t(settings?.language || 'ru')
  const [rows, setRows] = useState<ClashConn[]>([])
  const [totals, setTotals] = useState({ down: 0, up: 0 })
  const [q, setQ] = useState('')
  const [page, setPage] = useState(0)

  usePolling(async () => {
      try {
        const r = await clash.connections()
        setRows(r.connections || [])
        setTotals({ down: r.downloadTotal, up: r.uploadTotal })
      } catch { /* ignore */ }
  }, 2000, !!status?.running)

  const filtered = useMemo(() => {
    const s = q.trim().toLowerCase()
    if (!s) return rows
    return rows.filter((c) =>
      (c.metadata?.host || '').toLowerCase().includes(s)
      || (c.metadata?.process || '').toLowerCase().includes(s)
      || (c.chains || []).join(' ').toLowerCase().includes(s)
      || (c.rulePayload || '').toLowerCase().includes(s))
  }, [rows, q])
  const currentPage = Math.min(page, Math.max(0, Math.ceil(filtered.length / 100) - 1))
  const visibleRows = filtered.slice(currentPage * 100, (currentPage + 1) * 100)

  if (!status?.running) {
    return (
      <EmptyState
        title={i.connections}
        text="Запустите ядро, чтобы видеть активные соединения."
      />
    )
  }

  return (
    <div>
      <div className="page-head">
        <div>
          <h1>{i.connections}</h1>
          <p>{filtered.length}/{rows.length} · ↓ {formatBytes(totals.down)} · ↑ {formatBytes(totals.up)}</p>
        </div>
        <button className="btn danger" disabled={busy} onClick={() => run(async () => { await clash.closeAll(); setRows([]) })}>{i.closeAll}</button>
      </div>
      <input className="search" aria-label="Поиск соединений" placeholder="Узел, процесс или правило" value={q} onChange={(e) => { setQ(e.target.value); setPage(0) }} style={{ marginBottom: 14 }} />
      {!filtered.length ? (
        <EmptyState
          title="Нет соединений"
          text={q.trim() ? 'Ничего не найдено по фильтру.' : 'Активных соединений пока нет.'}
        />
      ) : (
      <div className="card" style={{ overflow: 'auto' }}>
        <table className="table">
          <thead>
            <tr><th>Узел</th><th>Процесс</th><th>Маршрут</th><th>Правило</th><th>Входящий</th><th>Исходящий</th></tr>
          </thead>
          <tbody>
            {visibleRows.map((c) => (
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
        {filtered.length > 100 && <div className="btn-row" style={{ marginTop: 12 }}>
          <button className="btn" disabled={currentPage === 0} onClick={() => setPage(currentPage - 1)}>Назад</button>
          <span className="hint">{currentPage + 1} / {Math.ceil(filtered.length / 100)}</span>
          <button className="btn" disabled={(currentPage + 1) * 100 >= filtered.length} onClick={() => setPage(currentPage + 1)}>Далее</button>
        </div>}
      </div>
      )}
    </div>
  )
}
