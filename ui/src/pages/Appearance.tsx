import { useApp } from '../store/app'
import { useShallow } from 'zustand/react/shallow'
import { api } from '../lib/api'
import { t } from '../lib/i18n'
import type { Appearance } from '../lib/api'
import { MenuSelect } from '../components/MenuSelect'

const fonts = ['SF Pro', 'Segoe UI', 'Segoe UI Variable Display', 'Georgia', 'Consolas']

export function AppearancePage() {
  const { settings, patchSettings, refresh, run } = useApp(useShallow((s) => ({
    settings: s.settings, patchSettings: s.patchSettings, refresh: s.refresh, run: s.run,
  })))
  const i = t(settings?.language || 'ru')
  if (!settings) return <div className="empty"><h3>{i.appearance}</h3><p>Служба приложения недоступна.</p></div>
  const a = settings.appearance
  const set = (partial: Partial<Appearance>) => patchSettings({ appearance: partial as never })

  return (
    <div>
      <div className="page-head"><div><div className="page-kicker">Ваш интерфейс</div><h1>{i.appearance}</h1><p>Спокойные цвета, прозрачные слои и мягкий свет. Изменения применяются сразу.</p></div></div>
      <div className="grid cols-2">
        <div className="card">
          <h2>Цвет</h2>
          <div className="field row"><label htmlFor="appearance-accent">Акцент</label><input id="appearance-accent" className="color" type="color" value={a.accent} onChange={(e) => set({ accent: e.target.value })} /></div>
          <div className="field row"><label htmlFor="appearance-secondary">Второй акцент</label><input id="appearance-secondary" className="color" type="color" value={a.accentMuted} onChange={(e) => set({ accentMuted: e.target.value })} /></div>
          <div className="field row"><label htmlFor="appearance-background">Фон</label><input id="appearance-background" className="color" type="color" value={a.background} onChange={(e) => set({ background: e.target.value })} /></div>
          <div className="field row"><label htmlFor="appearance-text">Текст</label><input id="appearance-text" className="color" type="color" value={a.text} onChange={(e) => set({ text: e.target.value })} /></div>
          <div className="field"><label>Стиль фона</label>
            <MenuSelect label="Стиль фона" value={a.backgroundStyle} onChange={(value) => set({ backgroundStyle: value })} options={[
              { value: 'mesh', label: 'Мягкий свет' },
              { value: 'solid', label: 'Сплошной' },
              { value: 'image', label: 'Изображение' },
            ]} />
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
          <h2>Материал и форма</h2>
          <div className="field row"><label id="appearance-glass-label">Прозрачное стекло</label><button type="button" role="switch" aria-labelledby="appearance-glass-label" aria-checked={a.glass} className={'toggle' + (a.glass ? ' on' : '')} onClick={() => set({ glass: !a.glass, glassBlur: a.glass ? 0 : 6 })} /></div>
          <div className="field"><label htmlFor="appearance-glass-density">Плотность стекла · {Math.round((a.glassTintOpacity ?? 0.1) * 100)}%</label><input id="appearance-glass-density" className="range" type="range" min={0.06} max={0.28} step={0.01} value={a.glassTintOpacity ?? 0.1} onChange={(e) => set({ glassTintOpacity: Number(e.target.value) })} /><span className="hint">Меньше — прозрачнее.</span></div>
          <div className="field"><label>Скругление, {a.radius}</label><input className="range" type="range" min={4} max={28} value={a.radius} onChange={(e) => set({ radius: Number(e.target.value) })} /></div>
          <div className="field"><label>Боковая панель</label>
            <MenuSelect label="Боковая панель" value={a.sidebar} onChange={(value) => set({ sidebar: value })} options={[
              { value: 'full', label: 'Полная' },
              { value: 'compact', label: 'Компактная' },
            ]} />
          </div>
          <div className="field row"><label id="appearance-motion-label">Анимации</label><button type="button" role="switch" aria-labelledby="appearance-motion-label" aria-checked={a.animations ?? true} className={'toggle' + ((a.animations ?? true) ? ' on' : '')} onClick={() => set({ animations: !(a.animations ?? true) })} /></div>
          <div className="field row"><label id="appearance-graph-label">График трафика</label><button type="button" role="switch" aria-labelledby="appearance-graph-label" aria-checked={a.trafficGraph ?? true} className={'toggle' + ((a.trafficGraph ?? true) ? ' on' : '')} onClick={() => set({ trafficGraph: !(a.trafficGraph ?? true) })} /></div>
        </div>
        <div className="card">
          <h2>Шрифты</h2>
          <div className="field"><label>Интерфейс</label>
            <MenuSelect label="Интерфейс" value={a.fontUi} onChange={(value) => set({ fontUi: value })} options={fonts.map((value) => ({ value, label: value }))} />
          </div>
          <div className="field"><label>Заголовки</label>
            <MenuSelect label="Заголовки" value={a.fontDisplay} onChange={(value) => set({ fontDisplay: value })} options={fonts.map((value) => ({ value, label: value }))} />
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
