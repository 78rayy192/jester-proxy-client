export type CoreStatus = {
  running: boolean
  coreInstalled: boolean
  wintunInstalled: boolean
  isAdmin: boolean
  needsAdmin: boolean
  pid: number
  version: string
  appVersion?: string
  message: string
  downloadProgress: number
  activeProfileId: string
  mode: string
}

export type Appearance = {
  theme: string
  accent: string
  accentMuted: string
  background: string
  surface: string
  text: string
  backgroundStyle: string
  backgroundImage?: string | null
  glass: boolean
  glassBlur: number
  glassTintOpacity?: number
  radius: number
  windowOpacity: number
  fontUi: string
  fontDisplay: string
  sidebar: string
  animations: boolean
  trafficGraph: boolean
  customCss: string
}

export type DnsSettings = {
  enable: boolean
  ipv6: boolean
  useHosts: boolean
  enhancedMode: string
  fakeIpRange: string
  nameserver: string[]
  fallback: string[]
  defaultNameserver: string[]
  fakeIpFilter: string[]
}

export type AppSettings = {
  tunEnabled: boolean
  tunStack: string
  strictRoute: boolean
  autoRoute: boolean
  autoDetectInterface: boolean
  autoRedirect: boolean
  ipv6: boolean
  endpointIndependentNat: boolean
  mtu: number
  dnsHijack: boolean
  inet4Address: string
  mixedPort: number
  socksPort: number
  externalPort: number
  allowLan: boolean
  bindAddress: string
  mode: string
  logLevel: string
  systemProxy: boolean
  autoStart: boolean
  closeToTray: boolean
  startHidden: boolean
  unifiedDelay: boolean
  tcpConcurrent: boolean
  keepAlive: boolean
  findProcessMode: string
  globalClientFingerprint: string
  latencyUrl: string
  latencyTimeoutMs: number
  dns: DnsSettings
  appearance: Appearance
  language: string
  activeProfileId: string
  coreChannel: string
  snifferEnabled?: boolean
  storeSelected?: boolean
  highPriority?: boolean
  refilterEnabled?: boolean
  refilterUseIp?: boolean
  refilterDomainsUrl?: string
  refilterIpUrl?: string
  customProxyRules?: string
  customDirectRules?: string
  finalOutbound?: string
  stealthMode?: boolean
  blockQuic?: boolean
  dnsRespectRules?: boolean
  blockStun?: boolean
  dnsViaProxy?: boolean
  randomFingerprint?: boolean
  silentLogs?: boolean
  useMrsRules?: boolean
  hideFromCapture?: boolean
  rfRushMode?: boolean
  preferRushLane?: boolean
  autoUpdateSubscriptions?: boolean
  autoUpdateIntervalHours?: number
  lastSubscriptionUpdateAt?: string | null
  favoriteNodes?: string[]
  lastSelectedNode?: string
  firstRunCompleted?: boolean
  connectionHighlight?: boolean
  autoRestartCore?: boolean
  performancePreset?: string
  networkPersona?: string
  adaptiveLane?: boolean
  adaptiveLaneBadRttMs?: number
  adaptiveLaneGoodRttMs?: number
  adaptiveLaneIntervalSec?: number
  perAppFastPath?: boolean
  fastPathProcesses?: string
  warmStart?: boolean
  splitUdpPolicy?: boolean
  udpFastPorts?: string
  trafficPadding?: boolean
  dnsDiversity?: boolean
  dnsStrictNoFallback?: boolean
  dnsRotateHours?: number
  dnsPoolIndex?: number
  exitConsistencyCheck?: boolean
  exitCheckIntervalSec?: number
  exitCheckAction?: string
  lastKnownExitIp?: string | null
  lastKnownExitOrg?: string | null
  deadManSwitch?: boolean
  deadManArmed?: boolean
  smartDirectMap?: boolean
  smartDirectMinHits?: number
  smartDirectDomains?: string[]
  latencyBudgetEnabled?: boolean
  latencyBudgetMs?: number
  latencyBudgetAction?: string
  adaptiveLaneState?: string
  lastMeasuredRttMs?: number
  guardianMessage?: string
}

export type RuleListStatus = {
  domainsPath: string
  ipsPath: string
  format?: string
  domainsCount: number
  ipsCount: number
  domainsBytes?: number
  ipsBytes?: number
  domainsUpdatedAt?: string | null
  ipsUpdatedAt?: string | null
}

export type Profile = {
  id: string
  name: string
  kind: string
  source: string
  updatedAt: string
  bytes: number
  upload?: number
  download?: number
  total?: number
  expireUnix?: number
  hwidLimited?: boolean
  updateIntervalHours?: number
  fingerprintOverride?: string | null
  nameserverOverride?: string[] | null
}

async function req<T>(path: string, init?: RequestInit, timeoutMs = 30000): Promise<T> {
  const res = await fetch(path, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...(init?.headers || {}) },
    signal: init?.signal
      ? AbortSignal.any([init.signal, AbortSignal.timeout(timeoutMs)])
      : AbortSignal.timeout(timeoutMs),
  })
  if (!res.ok) {
    let msg = res.statusText
    try {
      const j = await res.json()
      msg = j.error || j.message || msg
    } catch { /* text */ }
    throw new Error(msg)
  }
  if (res.status === 204) return undefined as T
  return res.json() as Promise<T>
}

export const api = {
  status: () => req<CoreStatus>('/api/status'),
  settings: () => req<AppSettings>('/api/settings'),
  saveSettings: (s: AppSettings) => req<AppSettings>('/api/settings', { method: 'PUT', body: JSON.stringify(s) }),
  appearance: () => req<Appearance>('/api/appearance'),
  saveAppearance: (a: Appearance) => req<Appearance>('/api/appearance', { method: 'PUT', body: JSON.stringify(a) }),
  pickWallpaper: () => req<Appearance | { cancelled: true }>('/api/appearance/pick-wallpaper', { method: 'POST' }),
  profiles: () => req<Profile[]>('/api/profiles'),
  createProfile: (body: { name: string; kind: string; payload?: string; source?: string }) =>
    req<Profile>('/api/profiles', { method: 'POST', body: JSON.stringify(body) }, 45000),
  activateProfile: (id: string) => req('/api/profiles/' + id + '/activate', { method: 'POST' }),
  updateProfile: (id: string) => req<Profile>('/api/profiles/' + id + '/update', { method: 'POST' }, 60000),
  updateAllProfiles: () => req<{ updated: number; failed: number; errors: string[] }>('/api/profiles/update-all', { method: 'POST' }, 180000),
  renameProfile: (id: string, name: string) => req<Profile>('/api/profiles/' + id, { method: 'PUT', body: JSON.stringify({ name }) }),
  patchProfile: (id: string, body: { name?: string; fingerprintOverride?: string | null; nameserverOverride?: string | string[] | null }) =>
    req<Profile>('/api/profiles/' + id, { method: 'PUT', body: JSON.stringify(body) }),
  deleteProfile: (id: string) => req('/api/profiles/' + id, { method: 'DELETE' }),
  exportSettings: () => req<AppSettings>('/api/settings/export'),
  importSettings: (s: AppSettings) => req<AppSettings>('/api/settings/import', { method: 'POST', body: JSON.stringify(s) }),
  checkUpdate: () => req<{ current: string; latest?: string; updateAvailable: boolean; releaseUrl?: string; downloadUrl?: string; message: string }>('/api/app/update'),
  applyPreset: (name: 'speed' | 'stability' | 'protection') => req<AppSettings>('/api/presets/' + name, { method: 'POST' }),
  applyPersona: (name: 'auto' | 'home' | 'mobile' | 'corporate') => req<AppSettings>('/api/persona/' + name, { method: 'POST' }),
  guardian: () => req<{
    message: string
    lane: string
    rttMs: number
    deadManArmed: boolean
    exitIp?: string | null
    exitOrg?: string | null
    smartDirect: string[]
    dnsPoolIndex: number
  }>('/api/guardian'),
  disarmDeadMan: () => req<{ ok: boolean; deadManArmed: boolean }>('/api/guardian/disarm', { method: 'POST' }),
  warmStart: () => req<{ ok: boolean }>('/api/guardian/warm', { method: 'POST' }),
  learnDirect: () => req<{ ok: boolean; message: string; domains?: string[] }>('/api/guardian/learn-direct', { method: 'POST' }),
  start: () => req<CoreStatus>('/api/core/start', { method: 'POST' }, 90000),
  stop: () => req<CoreStatus>('/api/core/stop', { method: 'POST' }),
  restart: () => req<CoreStatus>('/api/core/restart', { method: 'POST' }, 90000),
  download: () => req<CoreStatus>('/api/core/download', { method: 'POST' }, 180000),
  elevate: () => req('/api/core/elevate', { method: 'POST' }),
  logs: () => req<{ text: string }>('/api/logs'),
  sysproxy: (enable: boolean) => req<{ enable: boolean; tunEnabled?: boolean; windowsProxy?: boolean; mixedPort?: number }>('/api/sysproxy', { method: 'POST', body: JSON.stringify({ enable }) }),
  sysproxyStatus: () => req<{ enable: boolean; windowsProxy: boolean; mixedPort: number }>('/api/sysproxy'),
  testDelays: (url?: string, timeout?: number) => {
    const q = new URLSearchParams()
    if (url) q.set('url', url)
    if (timeout) q.set('timeout', String(timeout))
    const s = q.toString()
    return req<Record<string, number>>('/api/proxies/test' + (s ? '?' + s : ''), { method: 'POST' }, 120000)
  },
  rulesStatus: () => req<RuleListStatus>('/api/rules/status'),
  rulesUpdate: () => req<RuleListStatus>('/api/rules/update', { method: 'POST' }, 180000),
  leakCheck: () => req<LeakCheckResult>('/api/leak-check'),
  ghostPanic: () => req<GhostPanicResult>('/api/ghost/panic', { method: 'POST' }),
  ghostRush: () => req<RushResult>('/api/ghost/rush', { method: 'POST' }),
  ghostLearn: () => req<LearnResult>('/api/ghost/learn', { method: 'POST' }),
  applyPorts: (mixedPort: number, socksPort: number, externalPort: number) =>
    req<PortApplyResult>('/api/ports', { method: 'POST', body: JSON.stringify({ mixedPort, socksPort, externalPort }) }),
}

export type GhostCheck = {
  id: string
  label: string
  ok: boolean
  weight: number
  detail?: string
}

export type LeakCheckResult = {
  realIp?: string | null
  exitIp?: string | null
  masked: boolean
  running: boolean
  tunMode?: boolean
  score?: number
  message: string
  checks?: GhostCheck[]
}

export type GhostPanicResult = {
  ok: boolean
  message: string
}

export type RushResult = {
  enabled: boolean
  lane?: string
  node?: string | null
  delayMs?: number
  message: string
}

export type LearnResult = {
  ok: boolean
  added: number
  domains: string[]
  message: string
}

export type PortApplyResult = {
  ok: boolean
  mixedPort: number
  socksPort: number
  externalPort: number
  message: string
}

export const clash = {
  version: () => req<{ version?: string; meta?: boolean }>('/clash/version'),
  proxies: () => req<{ proxies: Record<string, ClashProxy> }>('/clash/proxies'),
  rules: () => req<{ rules: ClashRule[] }>('/clash/rules'),
  connections: () => req<{ connections: ClashConn[]; downloadTotal: number; uploadTotal: number }>('/clash/connections'),
  closeAll: () => req<void>('/clash/connections', { method: 'DELETE' }),
  select: async (group: string, name: string) => {
    const res = await fetch('/clash/proxies/' + encodeURIComponent(group), {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name }),
    })
    if (!res.ok && res.status !== 204) {
      let msg = res.statusText
      try { msg = ((await res.json()) as { message?: string; error?: string }).message || msg } catch { /* ignore */ }
      throw new Error(msg || 'Не удалось выбрать узел')
    }
  },
  delay: async (name: string, url: string, timeout: number) => {
    const res = await fetch(
      `/clash/proxies/${encodeURIComponent(name)}/delay?url=${encodeURIComponent(url)}&timeout=${timeout}`,
    )
    try {
      const j = (await res.json()) as { delay?: number; message?: string }
      if (typeof j.delay === 'number') return { delay: j.delay }
    } catch { /* ignore */ }
    return { delay: 0 }
  },
  groupDelay: async (name: string, url: string, timeout: number) => {
    const res = await fetch(
      `/clash/group/${encodeURIComponent(name)}/delay?url=${encodeURIComponent(url)}&timeout=${timeout}`,
    )
    if (!res.ok) {
      let msg = res.statusText
      try { msg = ((await res.json()) as { message?: string }).message || msg } catch { /* ignore */ }
      throw new Error(msg || 'Тест группы не удался')
    }
    return (await res.json()) as Record<string, number>
  },
  patchMode: (mode: string) =>
    req<void>('/clash/configs', { method: 'PATCH', body: JSON.stringify({ mode }) }),
}

export type ClashProxy = {
  name: string
  type: string
  now?: string
  all?: string[]
  udp?: boolean
  history?: { delay: number }[]
}

export type ClashRule = { type: string; payload: string; proxy: string; size?: number }
export type ClashConn = {
  id: string
  metadata?: {
    network: string
    type: string
    sourceIP: string
    destinationIP: string
    host: string
    process?: string
  }
  upload: number
  download: number
  chains: string[]
  rule: string
  rulePayload: string
}

/** Cap absurd spikes (SSE/poll races, reconnects) so UI never shows "PB/s". */
export const MAX_BPS = 2 * 1024 * 1024 * 1024

export function sanitizeBps(n: unknown, prev = 0): number {
  const v = typeof n === 'number' ? n : Number(n)
  if (!Number.isFinite(v) || v < 0) return 0
  if (v > MAX_BPS) return prev > 0 && prev <= MAX_BPS ? prev : 0
  return v
}

export function formatBytes(n: number) {
  if (!Number.isFinite(n) || n < 0) return '0 B'
  if (n < 1024) return Math.round(n) + ' B'
  const u = ['KB', 'MB', 'GB', 'TB']
  let v = n
  let i = -1
  do { v /= 1024; i++ } while (v >= 1024 && i < u.length - 1)
  return (v >= 10 ? v.toFixed(1) : v.toFixed(2)) + ' ' + u[i]
}

export function formatSpeed(n: number) {
  return formatBytes(sanitizeBps(n)) + '/s'
}

export function delayTone(ms?: number) {
  if (ms == null || ms <= 0 || ms >= 10000) return 'dead'
  if (ms < 180) return 'fast'
  if (ms < 450) return 'ok'
  return 'slow'
}

export function hostMsg(msg: 'drag' | 'minimize' | 'maximize' | 'close' | 'material-ready') {
  const ipc = (window as unknown as { ipc?: { postMessage?: (value: string) => void } }).ipc
  if (ipc?.postMessage) {
    ipc.postMessage(msg)
    return
  }
  window.chrome?.webview?.postMessage(msg)
}
