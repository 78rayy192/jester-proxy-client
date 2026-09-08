import { useEffect, useState } from 'react'
import { api } from '../lib/api'
import { t } from '../lib/i18n'
import { useApp } from '../store/app'

export function Logs() {
  const { settings } = useApp()
  const i = t(settings?.language || 'ru')
  const [text, setText] = useState('')
  const load = () => api.logs().then((r) => setText(r.text || 'Журнал пуст.')).catch((e) => setText(String(e)))
  useEffect(() => {
    load()
    const id = setInterval(load, 2500)
    return () => clearInterval(id)
  }, [])
  return (
    <div>
      <div className="page-head"><div><h1>{i.logs}</h1><p>Вывод mihomo</p></div><button className="btn" onClick={load}>Обновить</button></div>
      <div className="log">{text}</div>
    </div>
  )
}
