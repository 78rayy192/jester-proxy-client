import type { ReactNode } from 'react'
import { api } from '../lib/api'
import { t } from '../lib/i18n'
import { useApp } from '../store/app'

function Row({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="field row">
      <div>
        <label>{label}</label>
        {hint && <div className="hint">{hint}</div>}
      </div>
      {children}
    </div>
  )
}

export function Settings() {
  const { settings, busy, patchSettings, run, refresh } = useApp()
  const i = t(settings?.language || 'ru')
  if (!settings) return <div className="empty"><h3>{i.settings}</h3><p>Нет связи с хостом — настройки недоступны.</p></div>
  const s = settings
  const num = (k: keyof typeof s, v: string) => patchSettings({ [k]: Number(v) || 0 } as never)

  return (
    <div>
      <div className="page-head"><div><h1>{i.settings}</h1><p>Поведение клиента. Ядро перезапускается, когда меняется сеть.</p></div>
        <button className="btn primary" disabled={busy} onClick={() => run(() => api.restart())}>Применить и перезапустить</button>
      </div>
      <div className="grid cols-2">
        <div className="card">
          <h2>Щит Jester</h2>
          <Row label="Stealth-режим" hint="Максимальная маскировка: DNS через VPN, random TLS, anti-QUIC/STUN, silent logs, без LAN.">
            <button className={'toggle' + ((s.stealthMode ?? true) ? ' on' : '')} onClick={() => patchSettings({
              stealthMode: !(s.stealthMode ?? true),
              blockQuic: !(s.stealthMode ?? true),
              blockStun: !(s.stealthMode ?? true),
              dnsViaProxy: !(s.stealthMode ?? true),
              dnsRespectRules: !(s.stealthMode ?? true),
              randomFingerprint: !(s.stealthMode ?? true),
              silentLogs: !(s.stealthMode ?? true),
            })} />
          </Row>
          <Row label="Блок QUIC (UDP/443)" hint="Сайты идут по обычному TLS через туннель — сложнее для DPI.">
            <button className={'toggle' + ((s.blockQuic ?? true) ? ' on' : '')} onClick={() => patchSettings({ blockQuic: !(s.blockQuic ?? true) })} />
          </Row>
          <Row label="Блок STUN / WebRTC" hint="Закрывает утечку реального IP через браузер.">
            <button className={'toggle' + ((s.blockStun ?? true) ? ' on' : '')} onClick={() => patchSettings({ blockStun: !(s.blockStun ?? true) })} />
          </Row>
          <Row label="DNS через VPN" hint="DoH-запросы идут через PROXY — провайдер не видит, что вы резолвите.">
            <button className={'toggle' + ((s.dnsViaProxy ?? true) ? ' on' : '')} onClick={() => patchSettings({ dnsViaProxy: !(s.dnsViaProxy ?? true) })} />
          </Row>
          <Row label="DNS по правилам" hint="Заблокированные домены резолвятся через VPN.">
            <button className={'toggle' + ((s.dnsRespectRules ?? true) ? ' on' : '')} onClick={() => patchSettings({ dnsRespectRules: !(s.dnsRespectRules ?? true) })} />
          </Row>
          <Row label="Random TLS fingerprint" hint="Каждый коннект — случайный отпечаток клиента.">
            <button className={'toggle' + ((s.randomFingerprint ?? true) ? ' on' : '')} onClick={() => patchSettings({ randomFingerprint: !(s.randomFingerprint ?? true) })} />
          </Row>
          <Row label="Silent logs" hint="Ядро не пишет логи на диск; при стопе журнал очищается.">
            <button className={'toggle' + ((s.silentLogs ?? true) ? ' on' : '')} onClick={() => patchSettings({ silentLogs: !(s.silentLogs ?? true) })} />
          </Row>
          <Row label="Быстрые правила (.mrs)" hint="Бинарный Re:filter — заметно быстрее текстового списка.">
            <button className={'toggle' + ((s.useMrsRules ?? true) ? ' on' : '')} onClick={() => patchSettings({ useMrsRules: !(s.useMrsRules ?? true) })} />
          </Row>
          <Row label="Скрыть от скриншотов" hint="Окно не попадает в захват экрана Windows.">
            <button className={'toggle' + ((s.hideFromCapture ?? true) ? ' on' : '')} onClick={() => patchSettings({ hideFromCapture: !(s.hideFromCapture ?? true) })} />
          </Row>
        </div>
        <div className="card">
          <h2>TUN</h2>
          <Row label="Включить TUN" hint="Системный VPN-адаптер, не только HTTP/SOCKS.">
            <button className={'toggle' + (s.tunEnabled ? ' on' : '')} onClick={() => patchSettings({ tunEnabled: !s.tunEnabled })} />
          </Row>
          <div className="field"><label>Стек</label>
            <select value={s.tunStack} onChange={(e) => patchSettings({ tunStack: e.target.value })}>
              <option value="mixed">mixed — быстрее на Windows</option>
              <option value="system">system</option>
              <option value="gvisor">gvisor — изолированнее</option>
            </select>
          </div>
          <Row label="Strict route" hint="Не пускать утечки в обход TUN.">
            <button className={'toggle' + (s.strictRoute ? ' on' : '')} onClick={() => patchSettings({ strictRoute: !s.strictRoute })} />
          </Row>
          <Row label="Auto route"><button className={'toggle' + (s.autoRoute ? ' on' : '')} onClick={() => patchSettings({ autoRoute: !s.autoRoute })} /></Row>
          <Row label="Auto redirect"><button className={'toggle' + (s.autoRedirect ? ' on' : '')} onClick={() => patchSettings({ autoRedirect: !s.autoRedirect })} /></Row>
          <Row label="Auto detect interface"><button className={'toggle' + (s.autoDetectInterface ? ' on' : '')} onClick={() => patchSettings({ autoDetectInterface: !s.autoDetectInterface })} /></Row>
          <Row label="DNS hijack :53"><button className={'toggle' + (s.dnsHijack ? ' on' : '')} onClick={() => patchSettings({ dnsHijack: !s.dnsHijack })} /></Row>
          <Row label="IPv6"><button className={'toggle' + (s.ipv6 ? ' on' : '')} onClick={() => patchSettings({ ipv6: !s.ipv6 })} /></Row>
          <Row label="Endpoint-independent NAT"><button className={'toggle' + (s.endpointIndependentNat ? ' on' : '')} onClick={() => patchSettings({ endpointIndependentNat: !s.endpointIndependentNat })} /></Row>
          <div className="field"><label>MTU</label><input type="number" value={s.mtu} onChange={(e) => num('mtu', e.target.value)} />
            <div className="hint">1400 быстрее для VPN, чем 1500</div>
          </div>
          <div className="field"><label>IPv4 TUN</label><input value={s.inet4Address} onChange={(e) => patchSettings({ inet4Address: e.target.value })} /></div>
        </div>
        <div className="card">
          <h2>Прокси и порты</h2>
          <div className="field"><label>Mixed port</label><input type="number" value={s.mixedPort} onChange={(e) => num('mixedPort', e.target.value)} /></div>
          <div className="field"><label>SOCKS port</label><input type="number" value={s.socksPort} onChange={(e) => num('socksPort', e.target.value)} /></div>
          <div className="field"><label>External controller</label><input type="number" value={s.externalPort} onChange={(e) => num('externalPort', e.target.value)} /></div>
          <Row label="Allow LAN"><button className={'toggle' + (s.allowLan ? ' on' : '')} onClick={() => patchSettings({ allowLan: !s.allowLan })} /></Row>
          <div className="field"><label>Bind</label><input value={s.bindAddress} onChange={(e) => patchSettings({ bindAddress: e.target.value })} /></div>
          <div className="field"><label>Режим</label>
            <select value={s.mode} onChange={(e) => patchSettings({ mode: e.target.value })}>
              <option value="rule">rule</option>
              <option value="global">global</option>
              <option value="direct">direct</option>
            </select>
          </div>
          <div className="field"><label>Лог</label>
            <select value={s.logLevel} onChange={(e) => patchSettings({ logLevel: e.target.value })}>
              <option value="silent">silent</option>
              <option value="error">error</option>
              <option value="warning">warning</option>
              <option value="info">info</option>
              <option value="debug">debug</option>
            </select>
          </div>
          <Row label="TCP concurrent"><button className={'toggle' + (s.tcpConcurrent ? ' on' : '')} onClick={() => patchSettings({ tcpConcurrent: !s.tcpConcurrent })} /></Row>
          <Row label="Unified delay"><button className={'toggle' + (s.unifiedDelay ? ' on' : '')} onClick={() => patchSettings({ unifiedDelay: !s.unifiedDelay })} /></Row>
          <Row label="Keep-alive"><button className={'toggle' + (s.keepAlive ? ' on' : '')} onClick={() => patchSettings({ keepAlive: !s.keepAlive })} /></Row>
          <Row label="Sniffer" hint="Разбор SNI/HTTP — точнее правила, чуть больше CPU.">
            <button className={'toggle' + ((s.snifferEnabled ?? true) ? ' on' : '')} onClick={() => patchSettings({ snifferEnabled: !(s.snifferEnabled ?? true) })} />
          </Row>
          <Row label="Запоминать узел"><button className={'toggle' + ((s.storeSelected ?? true) ? ' on' : '')} onClick={() => patchSettings({ storeSelected: !(s.storeSelected ?? true) })} /></Row>
          <Row label="Высокий приоритет процесса" hint="Windows даёт mihomo больше CPU.">
            <button className={'toggle' + ((s.highPriority ?? true) ? ' on' : '')} onClick={() => patchSettings({ highPriority: !(s.highPriority ?? true) })} />
          </Row>
          <div className="field"><label>Find process</label>
            <select value={s.findProcessMode} onChange={(e) => patchSettings({ findProcessMode: e.target.value })}>
              <option value="off">off — быстрее (рекомендуется с TUN)</option>
              <option value="strict">strict — видно процесс</option>
              <option value="always">always</option>
            </select>
          </div>
          <div className="field"><label>TLS fingerprint</label>
            <select value={s.globalClientFingerprint} onChange={(e) => patchSettings({ globalClientFingerprint: e.target.value })}>
              <option value="chrome">chrome</option>
              <option value="firefox">firefox</option>
              <option value="safari">safari</option>
              <option value="ios">ios</option>
              <option value="android">android</option>
              <option value="edge">edge</option>
              <option value="random">random</option>
            </select>
          </div>
        </div>
        <div className="card">
          <h2>DNS</h2>
          <Row label="Включить DNS ядра"><button className={'toggle' + (s.dns.enable ? ' on' : '')} onClick={() => patchSettings({ dns: { ...s.dns, enable: !s.dns.enable } })} /></Row>
          <div className="field"><label>Режим</label>
            <select value={s.dns.enhancedMode} onChange={(e) => patchSettings({ dns: { ...s.dns, enhancedMode: e.target.value } })}>
              <option value="fake-ip">fake-ip</option>
              <option value="redir-host">redir-host</option>
            </select>
          </div>
          <div className="field"><label>Fake-IP range</label><input value={s.dns.fakeIpRange} onChange={(e) => patchSettings({ dns: { ...s.dns, fakeIpRange: e.target.value } })} /></div>
          <div className="field"><label>Nameserver (по строке)</label>
            <textarea rows={3} value={s.dns.nameserver.join('\n')} onChange={(e) => patchSettings({ dns: { ...s.dns, nameserver: e.target.value.split('\n').map((x) => x.trim()).filter(Boolean) } })} />
          </div>
          <div className="field"><label>Fallback</label>
            <textarea rows={3} value={s.dns.fallback.join('\n')} onChange={(e) => patchSettings({ dns: { ...s.dns, fallback: e.target.value.split('\n').map((x) => x.trim()).filter(Boolean) } })} />
          </div>
          <div className="field"><label>Default nameserver</label>
            <textarea rows={2} value={s.dns.defaultNameserver.join('\n')} onChange={(e) => patchSettings({ dns: { ...s.dns, defaultNameserver: e.target.value.split('\n').map((x) => x.trim()).filter(Boolean) } })} />
          </div>
        </div>
        <div className="card">
          <h2>Система</h2>
          <Row label={i.sysProxy} hint="Для приложений без TUN. С TUN лучше выключить — иначе двойной прокси и тормоза.">
            <button className={'toggle' + (s.systemProxy ? ' on' : '')} onClick={() => run(async () => { await api.sysproxy(!s.systemProxy); await refresh() })} />
          </Row>
          <Row label="Автозапуск Windows"><button className={'toggle' + (s.autoStart ? ' on' : '')} onClick={() => patchSettings({ autoStart: !s.autoStart })} /></Row>
          <Row label="Закрытие в трей"><button className={'toggle' + (s.closeToTray ? ' on' : '')} onClick={() => patchSettings({ closeToTray: !s.closeToTray })} /></Row>
          <Row label="Стартовать свёрнутым"><button className={'toggle' + (s.startHidden ? ' on' : '')} onClick={() => patchSettings({ startHidden: !s.startHidden })} /></Row>
          <div className="field"><label>URL теста задержки</label><input value={s.latencyUrl} onChange={(e) => patchSettings({ latencyUrl: e.target.value })} /></div>
          <div className="field"><label>Таймаут теста, мс</label><input type="number" value={s.latencyTimeoutMs} onChange={(e) => num('latencyTimeoutMs', e.target.value)} /></div>
          <div className="field"><label>Язык</label>
            <select value={s.language} onChange={(e) => patchSettings({ language: e.target.value })}>
              <option value="ru">Русский</option>
              <option value="en">English</option>
            </select>
          </div>
          <div className="field"><label>Канал ядра</label>
            <select value={s.coreChannel} onChange={(e) => patchSettings({ coreChannel: e.target.value })}>
              <option value="v3">amd64 v3 — AVX2, быстрее (этот ПК)</option>
              <option value="amd64">amd64 — обычная сборка</option>
              <option value="compatible">compatible — старые CPU</option>
            </select>
          </div>
        </div>
      </div>
    </div>
  )
}
