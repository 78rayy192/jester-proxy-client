import { useShallow } from 'zustand/react/shallow'
import { useState } from 'react'
import { usePolling } from '../lib/usePolling'
import { api } from '../lib/api'
import { t } from '../lib/i18n'
import { useApp } from '../store/app'
import { MenuSelect } from '../components/MenuSelect'
import { EmptyState } from '../components/UiBits'

export function Logs() {
  const { settings, patchSettings, status, busy, run } = useApp(useShallow(({ settings, patchSettings, status, busy, run }) => ({ settings, patchSettings, status, busy, run })))
  const i = t(settings?.language || 'ru')
  const [text, setText] = useState('')
  const [filter, setFilter] = useState('')
  const lines = text.split(/\r?\n/).filter(Boolean)
  const shown = filter
    ? lines.filter((l) => l.toLowerCase().includes(filter.toLowerCase()))
    : lines

  const load = () => api.logs().then((r) => setText(r.text || '')).catch((e) => setText(String(e)))
  usePolling(load, 3000)

  return (
    <div>
      <div className="page-head">
        <div>
          <h1>{i.logs}</h1>
          <p>Журнал ядра. Уровень влияет на объём записей после перезапуска.</p>
        </div>
        <div className="btn-row">
          <MenuSelect
            variant="pill"
            label="Уровень журнала"
            value={settings?.logLevel || 'warning'}
            onChange={(level) => run(async () => {
              await patchSettings({ logLevel: level, silentLogs: level === 'silent' })
              if (status?.running) await api.restart()
            })}
            options={[
              { value: 'silent', label: 'Тихий' },
              { value: 'error', label: 'Ошибки' },
              { value: 'warning', label: 'Предупреждения' },
              { value: 'info', label: 'Информация' },
              { value: 'debug', label: 'Отладка' },
            ]}
          />
          <button className="btn" onClick={load}>Обновить</button>
          <button className="btn" disabled={!text} onClick={() => navigator.clipboard.writeText(text)}>Копировать всё</button>
        </div>
      </div>
      <div className="field" style={{ marginBottom: 12 }}>
        <input placeholder="Фильтр строк" value={filter} onChange={(e) => setFilter(e.target.value)} />
      </div>
      {!shown.length ? (
        <EmptyState
          title="Журнал пуст"
          text={settings?.silentLogs ? 'Включён тихий режим. Выберите другой уровень, чтобы видеть записи.' : 'Записи появятся после работы ядра.'}
        />
      ) : (
        <div className="log-lines">
          {shown.map((line, idx) => (
            <button
              key={idx + line.slice(0, 24)}
              type="button"
              className="log-line"
              title="Нажмите, чтобы скопировать"
              onClick={() => navigator.clipboard.writeText(line)}
            >
              {line}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
