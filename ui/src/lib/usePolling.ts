import { useEffect, useRef } from 'react'

/** Serial polling that sleeps while the window is hidden. */
export function usePolling(task: () => Promise<unknown>, interval: number, enabled = true) {
  const latest = useRef(task)
  latest.current = task
  useEffect(() => {
    if (!enabled) return
    let active = true
    let inFlight = false
    let timer = 0
    const tick = async () => {
      if (!active || document.hidden || inFlight) return
      inFlight = true
      clearTimeout(timer)
      try { await latest.current() }
      catch { /* Individual views report actionable errors. */ }
      finally {
        inFlight = false
        if (active && !document.hidden) timer = window.setTimeout(tick, interval)
      }
    }
    const visibility = () => { clearTimeout(timer); if (!document.hidden) void tick() }
    document.addEventListener('visibilitychange', visibility)
    void tick()
    return () => { active = false; clearTimeout(timer); document.removeEventListener('visibilitychange', visibility) }
  }, [interval, enabled])
}
