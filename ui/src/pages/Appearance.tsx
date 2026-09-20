import { useApp } from '../store/app'
import { api } from '../lib/api'
import { t } from '../lib/i18n'
import type { Appearance } from '../lib/api'

const fonts = ['Segoe UI', 'Segoe UI Variable Display', 'Georgia', 'Consolas']
const presets: { name: string; a: Partial<Appearance> }[] = [
  { name: 'Латунь', a: { accent: '#e6c48a', accentMuted: '#a89068', background: '#090a0e', text: '#f7f3ec', backgroundStyle: 'mesh', glass: false, radius: 16, fontUi: 'Segoe UI', fontDisplay: 'Segoe UI' } },
  { name: 'Чернила', a: { accent: '#f2f2f2', accentMuted: '#8a8a8a', background: '#090909', text: '#f5f5f5', backgroundStyle: 'solid', glass: false, radius: 12, fontUi: 'Segoe UI', fontDisplay: 'Segoe UI' } },
  { name: 'Бумага', a: { accent: '#6b4e2e', accentMuted: '#6a655c', background: '#ebe6dc', text: '#1a1c20', backgroundStyle: 'solid', glass: false, radius: 14, fontUi: 'Segoe UI', fontDisplay: 'Segoe UI' } },
  { name: 'Зелёный', a: { accent: '#5ecf8e', accentMuted: '#3d8a5e', background: '#07120c', text: '#e8f7ee', backgroundStyle: 'mesh', glass: false, radius: 16, fontUi: 'Segoe UI', fontDisplay: 'Segoe UI' } },
  { name: 'Красный', a: { accent: '#e87878', accentMuted: '#a84f4f', background: '#140a0a', text: '#f8ecec', backgroundStyle: 'mesh', glass: false, radius: 16, fontUi: 'Segoe UI', fontDisplay: 'Segoe UI' } },
  { name: 'Фиолетовый', a: { accent: '#b794f6', accentMuted: '#7c5cbf', background: '#0e0a16', text: '#f0eaf8', backgroundStyle: 'mesh', glass: false, radius: 16, fontUi: 'Segoe UI', fontDisplay: 'Segoe UI' } },
]

export function AppearancePage() {
  const { settings, patchSettings, refresh, run } = useApp()
  const i = t(settings?.language || 'ru')
  if (!settings) return <div className="empty"><h3>{i.appearance}</h3><p>Служба приложения недоступна.</p></div>
  const a = settings.appearance
  const set = (partial: Partial<Appearance>) => patchSettings({ appearance: partial as never })

  return (
    <div>
      <div className="page-head"><div><h1>{i.appearance}</h1><p>Цвет, типографика и фон применяются сразу.</p></div></div>
      <div className="btn-row" style={{ marginBottom: 16, flexWrap: 'wrap' }}>
        {presets.map((p) => <button key={p.name} className="btn" onClick={() => set(p.a)}>{p.name}</button>)}
      </div>
      <div className="grid cols-2">
        <div className="card">
          <h2>Цвет</h2>
          <div className="field row"><label>Акцент</label><input className="color" type="color" value={a.accent} onChange={(e) => set({ accent: e.target.value })} /></div>
          <div className="field row"><label>Второй акцент</label><input className="color" type="color" value={a.accentMuted} onChange={(e) => set({ accentMuted: e.target.value })} /></div>
          <div className="field row"><label>Фон</label><input className="color" type="color" value={a.background} onChange={(e) => set({ background: e.target.value })} /></div>
          <div className="field row"><label>Текст</label><input className="color" type="color" value={a.text} onChange={(e) => set({ text: e.target.value })} /></div>
          <div className="field"><label>Стиль фона</label>
            <select value={a.backgroundStyle} onChange={(e) => set({ backgroundStyle: e.target.value })}>
              <option value="mesh">Сетка</option>
              <option value="solid">Сплошной</option>
              <option value="image">Изображение</option>
            </select>
          </div>
          <div className="field"><label>Изображение фона</label>
            <input
              value={a.backgroundImage || ''}
              onChange={(e) => set({ backgroundImage: e.target.value, backgroundStyle: e.target.value.trim() ? 'image' : a.backgroundStyle })}
              placeholder="Путь к файлу или адрес https://"
            />
            <div className="btn-row" style={{ marginTop: 8 }}>
              <button
                type="button"
                className="btn"
                onClick={() => run(async () => {
                  const next = await api.pickWallpaper()
                  if (!('cancelled' in next && next.cancelled)) await refresh()
                })}
              >
                Выбрать файл
              </button>
            </div>
          </div>
        </div>
        <div className="card">
          <h2>Форма</h2>
          <div className="field row"><label>Размытие фона</label><button className={'toggle' + (a.glass ? ' on' : '')} onClick={() => set({ glass: !a.glass })} /></div>
          <div className="field"><label>Сила размытия, {a.glassBlur}</label><input className="range" type="range" min={0} max={48} value={a.glassBlur} onChange={(e) => set({ glassBlur: Number(e.target.value) })} /></div>
          <div className="field"><label>Скругление, {a.radius}</label><input className="range" type="range" min={4} max={32} value={a.radius} onChange={(e) => set({ radius: Number(e.target.value) })} /></div>
          <div className="field"><label>Непрозрачность окна, {a.windowOpacity}</label><input className="range" type="range" min={0.72} max={1} step={0.01} value={a.windowOpacity} onChange={(e) => set({ windowOpacity: Number(e.target.value) })} /></div>
          <div className="field"><label>Боковая панель</label>
            <select value={a.sidebar} onChange={(e) => set({ sidebar: e.target.value })}>
              <option value="full">Полная</option>
              <option value="compact">Компактная</option>
            </select>
          </div>
          <div className="field row"><label>Анимации</label><button className={'toggle' + ((a.animations ?? true) ? ' on' : '')} onClick={() => set({ animations: !(a.animations ?? true) })} /></div>
          <div className="field row"><label>График трафика</label><button className={'toggle' + ((a.trafficGraph ?? true) ? ' on' : '')} onClick={() => set({ trafficGraph: !(a.trafficGraph ?? true) })} /></div>
        </div>
        <div className="card">
          <h2>Шрифты</h2>
          <div className="field"><label>Интерфейс</label>
            <select value={a.fontUi} onChange={(e) => set({ fontUi: e.target.value })}>{fonts.map((f) => <option key={f}>{f}</option>)}</select>
          </div>
          <div className="field"><label>Заголовки</label>
            <select value={a.fontDisplay} onChange={(e) => set({ fontDisplay: e.target.value })}>{fonts.map((f) => <option key={f}>{f}</option>)}</select>
          </div>
        </div>
        <div className="card">
          <h2>Дополнительные стили</h2>
          <div className="field">
            <textarea rows={12} value={a.customCss} onChange={(e) => set({ customCss: e.target.value })} placeholder={':root { --accent: #e2b93b; }'} />
          </div>
        </div>
      </div>
    </div>
  )
}
