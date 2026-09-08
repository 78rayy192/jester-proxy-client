import { useApp } from '../store/app'
import { t } from '../lib/i18n'
import type { Appearance } from '../lib/api'

const fonts = ['Sora', 'Syne', 'IBM Plex Mono', 'Outfit', 'Fraunces', 'Segoe UI', 'Manrope', 'Georgia']
const presets: { name: string; a: Partial<Appearance> }[] = [
  { name: 'Ink', a: { accent: '#f2f2f2', accentMuted: '#8a8a8a', background: '#050505', text: '#f5f5f5', backgroundStyle: 'solid', glass: false, radius: 8 } },
  { name: 'Grain', a: { accent: '#ffffff', accentMuted: '#777777', background: '#000000', text: '#fafafa', backgroundStyle: 'mesh', glass: false, radius: 6 } },
  { name: 'Paper', a: { accent: '#111111', accentMuted: '#666666', background: '#f4f1ea', text: '#111111', backgroundStyle: 'solid', glass: false, radius: 8 } },
]

export function AppearancePage() {
  const { settings, patchSettings } = useApp()
  const i = t(settings?.language || 'ru')
  if (!settings) return <div className="empty"><h3>{i.appearance}</h3><p>Нет связи с хостом — оформление недоступно.</p></div>
  const a = settings.appearance
  const set = (partial: Partial<Appearance>) => patchSettings({ appearance: { ...a, ...partial } })

  return (
    <div>
      <div className="page-head"><div><h1>{i.appearance}</h1><p>Цвета, стекло, шрифты, фон. Меняется сразу.</p></div></div>
      <div className="btn-row" style={{ marginBottom: 16 }}>
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
              <option value="mesh">mesh</option>
              <option value="solid">solid</option>
              <option value="image">image</option>
            </select>
          </div>
          <div className="field"><label>Картинка фона (URL)</label><input value={a.backgroundImage || ''} onChange={(e) => set({ backgroundImage: e.target.value })} /></div>
        </div>
        <div className="card">
          <h2>Форма</h2>
          <div className="field row"><label>Стекло</label><button className={'toggle' + (a.glass ? ' on' : '')} onClick={() => set({ glass: !a.glass })} /></div>
          <div className="field"><label>Blur {a.glassBlur}px</label><input className="range" type="range" min={0} max={48} value={a.glassBlur} onChange={(e) => set({ glassBlur: Number(e.target.value) })} /></div>
          <div className="field"><label>Радиус {a.radius}px</label><input className="range" type="range" min={4} max={32} value={a.radius} onChange={(e) => set({ radius: Number(e.target.value) })} /></div>
          <div className="field"><label>Прозрачность окна {a.windowOpacity}</label><input className="range" type="range" min={0.72} max={1} step={0.01} value={a.windowOpacity} onChange={(e) => set({ windowOpacity: Number(e.target.value) })} /></div>
          <div className="field"><label>Сайдбар</label>
            <select value={a.sidebar} onChange={(e) => set({ sidebar: e.target.value })}>
              <option value="full">полный</option>
              <option value="compact">компактный</option>
            </select>
          </div>
          <div className="field row"><label>Анимации</label><button className={'toggle' + (a.animations ? ' on' : '')} onClick={() => set({ animations: !a.animations })} /></div>
          <div className="field row"><label>График трафика</label><button className={'toggle' + (a.trafficGraph ? ' on' : '')} onClick={() => set({ trafficGraph: !a.trafficGraph })} /></div>
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
          <h2>Свой CSS</h2>
          <div className="field">
            <textarea rows={12} value={a.customCss} onChange={(e) => set({ customCss: e.target.value })} placeholder={':root { --accent: #e2b93b; }'} />
          </div>
        </div>
      </div>
    </div>
  )
}
