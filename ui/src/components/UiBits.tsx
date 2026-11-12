import type { ReactNode } from 'react'

export function EmptyState({ title, text, action }: { title: string; text: string; action?: ReactNode }) {
  return (
    <div className="empty">
      <h3>{title}</h3>
      <p>{text}</p>
      {action ? <div className="btn-row" style={{ justifyContent: 'center', marginTop: 16 }}>{action}</div> : null}
    </div>
  )
}

const flagUrls = import.meta.glob('../../node_modules/country-flag-icons/3x2/*.svg', {
  eager: true,
  query: '?url',
  import: 'default',
}) as Record<string, string>

const flagByCode: Record<string, string> = {}
for (const [path, url] of Object.entries(flagUrls)) {
  const code = path.split('/').pop()?.replace(/\.svg$/i, '').toUpperCase() || ''
  if (code) flagByCode[code] = url
}

const COUNTRIES: [string, string][] = [
  ['нидерланд', 'NL'], ['netherlands', 'NL'], ['holland', 'NL'], ['amsterdam', 'NL'],
  ['герман', 'DE'], ['germany', 'DE'], ['frankfurt', 'DE'], ['berlin', 'DE'],
  ['польш', 'PL'], ['poland', 'PL'], ['warsaw', 'PL'],
  ['финлянд', 'FI'], ['finland', 'FI'], ['helsinki', 'FI'],
  ['сша', 'US'], ['usa', 'US'], ['united states', 'US'], ['флорид', 'US'], ['florida', 'US'], ['нью-йорк', 'US'], ['new york', 'US'],
  ['великобритан', 'GB'], ['англия', 'GB'], ['britain', 'GB'], ['england', 'GB'], ['london', 'GB'], ['лондон', 'GB'],
  ['франц', 'FR'], ['france', 'FR'], ['paris', 'FR'], ['париж', 'FR'],
  ['швеци', 'SE'], ['sweden', 'SE'], ['stockholm', 'SE'],
  ['норвег', 'NO'], ['norway', 'NO'], ['oslo', 'NO'],
  ['дани', 'DK'], ['denmark', 'DK'], ['copenhagen', 'DK'],
  ['эстон', 'EE'], ['estonia', 'EE'], ['tallinn', 'EE'],
  ['латви', 'LV'], ['latvia', 'LV'], ['riga', 'LV'], ['рига', 'LV'],
  ['литв', 'LT'], ['lithuania', 'LT'], ['vilnius', 'LT'],
  ['турци', 'TR'], ['turkey', 'TR'], ['istanbul', 'TR'], ['стамбул', 'TR'],
  ['япони', 'JP'], ['japan', 'JP'], ['tokyo', 'JP'], ['токио', 'JP'],
  ['коре', 'KR'], ['korea', 'KR'], ['seoul', 'KR'],
  ['сингапур', 'SG'], ['singapore', 'SG'],
  ['канад', 'CA'], ['canada', 'CA'],
  ['австрал', 'AU'], ['australia', 'AU'],
  ['казахстан', 'KZ'], ['kazakhstan', 'KZ'],
  ['украин', 'UA'], ['ukraine', 'UA'],
  ['швейцар', 'CH'], ['switzerland', 'CH'],
  ['австри', 'AT'], ['austria', 'AT'],
  ['итали', 'IT'], ['italy', 'IT'],
  ['испан', 'ES'], ['spain', 'ES'],
  ['чехи', 'CZ'], ['czech', 'CZ'],
  ['румын', 'RO'], ['romania', 'RO'],
  ['болгар', 'BG'], ['bulgaria', 'BG'],
  ['венгр', 'HU'], ['hungary', 'HU'],
  ['грузи', 'GE'], ['georgia', 'GE'],
  ['армен', 'AM'], ['armenia', 'AM'],
  ['росси', 'RU'], ['russia', 'RU'],
  ['беларус', 'BY'], ['belarus', 'BY'],
  ['молдав', 'MD'], ['moldova', 'MD'],
  ['серби', 'RS'], ['serbia', 'RS'],
  ['хорват', 'HR'], ['croatia', 'HR'],
  ['словен', 'SI'], ['slovenia', 'SI'],
  ['словац', 'SK'], ['slovakia', 'SK'],
  ['португал', 'PT'], ['portugal', 'PT'],
  ['грец', 'GR'], ['greece', 'GR'],
  ['ирланд', 'IE'], ['ireland', 'IE'],
  ['исланд', 'IS'], ['iceland', 'IS'],
  ['люксембург', 'LU'], ['luxembourg', 'LU'],
  ['бельг', 'BE'], ['belgium', 'BE'],
  ['гонконг', 'HK'], ['hong kong', 'HK'], ['hongkong', 'HK'],
  ['тайван', 'TW'], ['taiwan', 'TW'],
  ['индонез', 'ID'], ['indonesia', 'ID'],
  ['инди', 'IN'], ['india', 'IN'],
  ['бразил', 'BR'], ['brazil', 'BR'],
  ['аргентин', 'AR'], ['argentina', 'AR'],
  ['мексик', 'MX'], ['mexico', 'MX'],
  ['израил', 'IL'], ['israel', 'IL'],
  ['оаэ', 'AE'], ['эмират', 'AE'], ['dubai', 'AE'],
  ['саудов', 'SA'],
  ['юар', 'ZA'], ['south africa', 'ZA'],
  ['егип', 'EG'], ['egypt', 'EG'],
  ['тайланд', 'TH'], ['thailand', 'TH'],
  ['вьетнам', 'VN'], ['vietnam', 'VN'],
  ['малайз', 'MY'], ['malaysia', 'MY'],
  ['филиппин', 'PH'], ['philippines', 'PH'],
]

const SKIP_CODES = new Set(['WS', 'SS', 'VM', 'OB', 'CF'])

function codeFromEmoji(pair: string) {
  const chars = Array.from(pair)
  if (chars.length < 2) return ''
  const code = chars.slice(0, 2).map((ch) => String.fromCharCode((ch.codePointAt(0) || 0) - 0x1F1E6 + 65)).join('')
  return flagByCode[code] ? code : ''
}

function codeFromText(name: string) {
  const lower = name.toLowerCase()
  for (const [key, code] of COUNTRIES) {
    if (lower.includes(key) && flagByCode[code]) return code
  }
  const token = name.match(/(?:^|[^\p{L}\p{N}])([A-Z]{2})(?=[^\p{L}\p{N}]|$)/u)
  const code = token?.[1] || ''
  if (code && flagByCode[code] && !SKIP_CODES.has(code)) return code
  return ''
}

export function splitServerName(name: string) {
  const mark = name.match(/\p{Regional_Indicator}{2}/u)
  let code = ''
  let title = name.trim()
  if (mark && mark.index != null) {
    code = codeFromEmoji(mark[0])
    title = `${name.slice(0, mark.index)} ${name.slice(mark.index + mark[0].length)}`.replace(/\s+/g, ' ').trim()
  }
  title = title.replace(/^[\s•·∙●⟳↻↺\-–—|]+/, '').trim()
  if (!code) code = codeFromText(title)
  return { code, title: title || name }
}

export function Flag({ code }: { code: string }) {
  const src = flagByCode[code]
  if (!src) return null
  return <img className="flag-img" src={src} alt="" draggable={false} />
}

export function formatQuota(used: number, total: number) {
  const u = formatData(used)
  if (!total || total <= 0) return `${u} / ∞`
  return `${u} / ${formatData(total)}`
}

export function formatData(n: number) {
  if (!Number.isFinite(n) || n <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let v = n
  let i = 0
  while (v >= 1024 && i < units.length - 1) { v /= 1024; i++ }
  return `${v < 10 && i > 0 ? v.toFixed(1) : Math.round(v)} ${units[i]}`
}

export function formatExpire(unix?: number) {
  if (!unix || unix <= 0) return ''
  const d = new Date(unix * 1000)
  if (Number.isNaN(d.getTime())) return ''
  return d.toLocaleDateString('ru-RU')
}

export function routeLabel(settings?: { tunEnabled?: boolean; systemProxy?: boolean; mode?: string } | null) {
  if (!settings) return 'Не задано'
  if (settings.tunEnabled) return 'Системный туннель'
  if (settings.systemProxy) return 'Системный прокси'
  if (settings.mode === 'direct') return 'Прямое соединение'
  return 'Локальный прокси'
}
