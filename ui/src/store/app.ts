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

let refreshing: Promise<void> | null = null
let saving: Promise<void> | null = null
const unchanged = <T,>(previous: T, next: T): T => JSON.stringify(previous) === JSON.stringify(next) ? previous : next

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
  setLive: (partial) => {
    const live = get().live
    if (Object.entries(partial).some(([key, value]) => live[key as keyof typeof live] !== value))
      set({ live: { ...live, ...partial } })
  },
  refresh: () => {
    if (refreshing) return refreshing
    const epoch = get().settingsEpoch
    refreshing = (async () => { try {
      const [status, settings, profiles] = await Promise.all([api.status(), api.settings(), api.profiles()])
      const current = get()
      const common = { status: unchanged(current.status, status), profiles: unchanged(current.profiles, profiles) }
      // Don't clobber a newer local patch with a stale GET.
      if (get().patching || get().settingsEpoch !== epoch) {
        set(common)
        return
      }
      set({ ...common, settings: unchanged(current.settings, settings), error: '' })
    } catch (e) {
      set({ error: e instanceof Error ? e.message : 'Ошибка связи с хостом' })
    } finally { refreshing = null } })()
    return refreshing
  },
  patchSettings: (partial) => {
    const cur = get().settings
    if (!cur) return Promise.resolve()
    const epoch = get().settingsEpoch + 1
    const next = {
      ...cur,
      ...partial,
      dns: { ...cur.dns, ...(partial.dns || {}) },
      appearance: { ...cur.appearance, ...(partial.appearance || {}) },
    }
    set({ settings: next, settingsEpoch: epoch, patching: true, error: '' })
    if (saving) return saving
    // Batch edits from the same event turn, then serialize writes. A slow older
    // PUT must never arrive after a newer one and restore stale settings.
    let confirmed = cur
    saving = Promise.resolve().then(async () => {
      try {
        while (true) {
          const snapshot = get()
          const saved = await api.saveSettings(snapshot.settings!)
          confirmed = saved
          if (get().settingsEpoch === snapshot.settingsEpoch) {
            set({ settings: saved, error: '' })
            break
          }
        }
      } catch (e) {
        set({ settings: confirmed, error: e instanceof Error ? e.message : 'Ошибка сохранения' })
        throw e
      } finally {
        set({ patching: false })
        saving = null
      }
    })
    // Field changes may intentionally ignore the promise; command handlers that
    // await it still receive failures and must not restart with unsaved values.
    void saving.catch(() => undefined)
    return saving
  },
  run: async (fn) => {
    if (get().busy) return
    set({ busy: true, error: '' })
    try {
      if (saving) {
        await saving
        if (get().error) return
      }
      await fn()
      await get().refresh()
    } catch (e) {
      set({ error: e instanceof Error ? e.message : 'Ошибка' })
    } finally {
      set({ busy: false })
    }
  },
}))
