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
  settingsEpoch: number
  patching: boolean
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
  settingsEpoch: 0,
  patching: false,
  live: { up: 0, down: 0, mem: 0, now: '' },
  setLive: (partial) => set({ live: { ...get().live, ...partial } }),
  refresh: async () => {
    const epoch = get().settingsEpoch
    try {
      const [status, settings, profiles] = await Promise.all([api.status(), api.settings(), api.profiles()])
      // Don't clobber a newer local patch with a stale GET.
      if (get().patching || get().settingsEpoch !== epoch) {
        set({ status, profiles })
        return
      }
      set({ status, settings, profiles, error: '' })
    } catch (e) {
      set({ error: e instanceof Error ? e.message : 'Ошибка связи с хостом' })
    }
  },
  patchSettings: async (partial) => {
    const cur = get().settings
    if (!cur) return
    const epoch = get().settingsEpoch + 1
    const next = {
      ...cur,
      ...partial,
      dns: { ...cur.dns, ...(partial.dns || {}) },
      appearance: { ...cur.appearance, ...(partial.appearance || {}) },
    }
    set({ settings: next, settingsEpoch: epoch, patching: true })
    try {
      const saved = await api.saveSettings(next)
      if (get().settingsEpoch === epoch) set({ settings: saved })
    } catch (e) {
      set({ error: e instanceof Error ? e.message : 'Ошибка сохранения' })
      await get().refresh()
    } finally {
      if (get().settingsEpoch === epoch) set({ patching: false })
    }
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
