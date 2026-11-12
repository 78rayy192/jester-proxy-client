import { memo, useEffect, useRef } from 'react'

const CAP = 2 * 1024 * 1024 * 1024

function clamp(v: number) {
  if (!Number.isFinite(v) || v <= 0) return 0
  return Math.min(v, CAP)
}

export const Sparkline = memo(function Sparkline({ up, down }: { up: number[]; down: number[] }) {
  const ref = useRef<HTMLCanvasElement>(null)
  const samples = useRef({ up, down })
  const redraw = useRef<() => void>(() => {})
  samples.current = { up, down }
  useEffect(() => {
    const c = ref.current
    if (!c) return
    const ctx = c.getContext('2d', { alpha: true })
    if (!ctx) return
    const draw = () => {
      if (document.hidden) return
      const w = Math.round(c.clientWidth)
      const h = 88
      if (!w) return
      const ratio = Math.min(window.devicePixelRatio || 1, 2)
      const pixelWidth = Math.round(w * ratio)
      const pixelHeight = Math.round(h * ratio)
      if (c.width !== pixelWidth || c.height !== pixelHeight) { c.width = pixelWidth; c.height = pixelHeight }
      ctx.setTransform(ratio, 0, 0, ratio, 0, 0)
      ctx.clearRect(0, 0, w, h)
      const style = getComputedStyle(document.documentElement)
      const { up: upload, down: download } = samples.current
      let max = 0
      for (const v of upload) max = Math.max(max, clamp(v))
      for (const v of download) max = Math.max(max, clamp(v))
      ctx.strokeStyle = 'rgba(255,255,255,.06)'
      ctx.lineWidth = 1
      ctx.beginPath()
      ctx.moveTo(0, h - 6)
      ctx.lineTo(w, h - 6)
      ctx.stroke()
      if (max <= 0) return
      const paint = (data: number[], color: string, fill: boolean) => {
        ctx.beginPath()
        data.forEach((v, i) => {
          const x = (i / Math.max(1, data.length - 1)) * w
          const y = h - 6 - (clamp(v) / max) * (h - 16)
          if (i === 0) ctx.moveTo(x, y)
          else ctx.lineTo(x, y)
        })
        if (fill) {
          ctx.lineTo(w, h - 6)
          ctx.lineTo(0, h - 6)
          ctx.closePath()
          ctx.fillStyle = 'rgba(255,255,255,.05)'
          ctx.fill()
          ctx.beginPath()
          data.forEach((v, i) => {
            const x = (i / Math.max(1, data.length - 1)) * w
            const y = h - 6 - (clamp(v) / max) * (h - 16)
            if (i === 0) ctx.moveTo(x, y)
            else ctx.lineTo(x, y)
          })
        }
        ctx.strokeStyle = color
        ctx.lineWidth = 2
        ctx.lineJoin = 'round'
        ctx.lineCap = 'round'
        ctx.stroke()
      }
      paint(download, 'rgba(232,236,244,.92)', true)
      paint(upload, 'rgba(154,164,184,.7)', false)
    }
    redraw.current = draw
    const resize = new ResizeObserver(draw)
    resize.observe(c)
    const theme = new MutationObserver(draw)
    theme.observe(document.documentElement, { attributes: true, attributeFilter: ['style'] })
    document.addEventListener('visibilitychange', draw)
    return () => {
      redraw.current = () => {}
      resize.disconnect()
      theme.disconnect()
      document.removeEventListener('visibilitychange', draw)
    }
  }, [])
  useEffect(() => { redraw.current() }, [up, down])
  return <canvas ref={ref} width={520} height={88} role="img" aria-label="График скорости загрузки и отдачи" style={{ width: '100%', height: 88 }} />
})
