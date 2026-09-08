import { useEffect, useRef } from 'react'

const CAP = 2 * 1024 * 1024 * 1024

function clamp(v: number) {
  if (!Number.isFinite(v) || v <= 0) return 0
  return Math.min(v, CAP)
}

export function Sparkline({ up, down }: { up: number[]; down: number[] }) {
  const ref = useRef<HTMLCanvasElement>(null)
  useEffect(() => {
    const c = ref.current
    if (!c) return
    const ctx = c.getContext('2d')
    if (!ctx) return
    const w = c.width
    const h = c.height
    ctx.clearRect(0, 0, w, h)
    const upC = up.map(clamp)
    const downC = down.map(clamp)
    const max = Math.max(1, ...upC, ...downC)
    const paint = (data: number[], color: string) => {
      ctx.beginPath()
      data.forEach((v, i) => {
        const x = (i / Math.max(1, data.length - 1)) * w
        const y = h - (v / max) * (h - 6) - 3
        if (i === 0) ctx.moveTo(x, y)
        else ctx.lineTo(x, y)
      })
      ctx.strokeStyle = color
      ctx.lineWidth = 1.8
      ctx.stroke()
    }
    paint(downC, getComputedStyle(document.documentElement).getPropertyValue('--accent').trim() || '#e2b93b')
    paint(upC, getComputedStyle(document.documentElement).getPropertyValue('--accent-2').trim() || '#c45c5c')
  }, [up, down])
  return <canvas ref={ref} width={520} height={88} style={{ width: '100%', height: 88 }} />
}
