import { create } from 'zustand'
import { api, type AppSettings, type CoreStatus, type Profile } from '../lib/api'

type Page = 'dashboard' | 'proxies' | 'connections' | 'rules' | 'profiles' | 'logs' | 'settings' | 'appearance'

type Store = {
  page: Page
  setPage: (p: Page) => void
  status: CoreStatus | null
  settings: AppSettings | null
  profiles: Profile[]
  error: string
  busy: boolean
  live: { up: number; down: number; mem: number; now: string }
  setLive: (partial: Partial<Store['live']>) => void
  refresh: () => Promise<void>
  patchSettings: (partial: Partial<AppSettings>) => Promise<void>
  run: (fn: () => Promise<unknown>) => Promise<void>
}

export const useApp = create<Store>((set, get) => ({
  page: 'dashboard',
  setPage: (page) => set({ page }),
  status: null,
  settings: null,
  profiles: [],
  error: '',
  busy: false,
  live: { up: 0, down: 0, mem: 0, now: '' },
  setLive: (partial) => set({ live: { ...get().live, ...partial } }),
  refresh: async () => {
    try {
      const [status, settings, profiles] = await Promise.all([api.status(), api.settings(), api.profiles()])
      set({ status, settings, profiles, error: '' })
    } catch (e) {
      set({ error: e instanceof Error ? e.message : 'Ошибка связи с хостом' })
    }
  },
  patchSettings: async (partial) => {
    const cur = get().settings
    if (!cur) return
    const next = { ...cur, ...partial, dns: { ...cur.dns, ...(partial.dns || {}) }, appearance: { ...cur.appearance, ...(partial.appearance || {}) } }
    const saved = await api.saveSettings(next)
    set({ settings: saved })
  },
  run: async (fn) => {
    set({ busy: true, error: '' })
    try {
      await fn()
      await get().refresh()
    } catch (e) {
      set({ error: e instanceof Error ? e.message : 'Ошибка' })
    } finally {
      set({ busy: false })
    }
  },
}))
