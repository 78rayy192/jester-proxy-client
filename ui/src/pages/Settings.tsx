import type { ReactNode } from 'react'
import { api } from '../lib/api'
import { t } from '../lib/i18n'
import { useApp } from '../store/app'
import { PortSwap } from '../components/PortSwap'

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
  if (!settings) return <div className="empty"><h3>{i.settings}</h3><p>Служба приложения недоступна.</p></div>
  const s = settings
  const num = (k: keyof typeof s, v: string) => patchSettings({ [k]: Number(v) || 0 } as never)

  return (
    <div>
      <div className="page-head"><div><h1>{i.settings}</h1><p>Параметры клиента. При изменении сетевых настроек ядро перезапускается.</p></div>
        <button className="btn primary" disabled={busy} onClick={() => run(() => api.restart())}>Применить и перезапустить</button>
      </div>
      <div className="grid cols-2">
        <div className="card">
          <h2>Защита</h2>
          <Row label="Режим защиты" hint="Маршрутизация DNS через туннель, случайный отпечаток TLS, блокировка QUIC и STUN, отключение журналов и доступа из локальной сети.">
            <button className={'toggle' + ((s.stealthMode ?? true) ? ' on' : '')} onClick={() => run(async () => {
              const next = !(s.stealthMode ?? true)
              await patchSettings({
                stealthMode: next,
                blockQuic: next,
                blockStun: next,
                dnsViaProxy: next,
                dnsRespectRules: next,
                randomFingerprint: next,
                silentLogs: next,
                useMrsRules: next,
                hideFromCapture: next,
              })
              const st = useApp.getState().status
              if (st?.running) await api.restart()
            })} />
          </Row>
          <Row label="Блокировка QUIC" hint="Трафик UDP/443 передаётся по TLS через туннель. Это снижает вероятность анализа средствами DPI.">
            <button className={'toggle' + ((s.blockQuic ?? true) ? ' on' : '')} onClick={() => patchSettings({ blockQuic: !(s.blockQuic ?? true) })} />
          </Row>
          <Row label="Блокировка STUN и WebRTC" hint="Предотвращает раскрытие внешнего адреса через браузер.">
            <button className={'toggle' + ((s.blockStun ?? true) ? ' on' : '')} onClick={() => patchSettings({ blockStun: !(s.blockStun ?? true) })} />
          </Row>
          <Row label="DNS через туннель" hint="Запросы DNS-over-HTTPS направляются через прокси. Поставщик связи не видит разрешаемые имена.">
            <button className={'toggle' + ((s.dnsViaProxy ?? true) ? ' on' : '')} onClick={() => patchSettings({ dnsViaProxy: !(s.dnsViaProxy ?? true) })} />
          </Row>
          <Row label="DNS по правилам" hint="Имена из списка блокировок разрешаются через туннель.">
            <button className={'toggle' + ((s.dnsRespectRules ?? true) ? ' on' : '')} onClick={() => patchSettings({ dnsRespectRules: !(s.dnsRespectRules ?? true) })} />
          </Row>
          <Row label="Случайный отпечаток TLS" hint="Для каждого соединения используется отдельный отпечаток клиента.">
            <button className={'toggle' + ((s.randomFingerprint ?? true) ? ' on' : '')} onClick={() => patchSettings({ randomFingerprint: !(s.randomFingerprint ?? true) })} />
          </Row>
          <Row label="Журнал без записи на диск" hint="Ядро не сохраняет журнал. При остановке файл журнала очищается.">
            <button className={'toggle' + ((s.silentLogs ?? true) ? ' on' : '')} onClick={() => patchSettings({ silentLogs: !(s.silentLogs ?? true) })} />
          </Row>
          <Row label="Бинарные правила" hint="Список Re:filter в формате MRS обрабатывается быстрее текстового.">
            <button className={'toggle' + ((s.useMrsRules ?? true) ? ' on' : '')} onClick={() => patchSettings({ useMrsRules: !(s.useMrsRules ?? true) })} />
          </Row>
          <Row label="Исключить из захвата экрана" hint="Окно не попадает в снимки и запись экрана Windows.">
            <button className={'toggle' + ((s.hideFromCapture ?? true) ? ' on' : '')} onClick={() => patchSettings({ hideFromCapture: !(s.hideFromCapture ?? true) })} />
          </Row>
          <Row label="Ускоренный канал" hint="Приоритет Hysteria2, TUIC и VLESS. Сети Российской Федерации направляются напрямую. Для применения требуется перезапуск ядра.">
            <button className={'toggle' + ((s.rfRushMode ?? true) ? ' on' : '')} onClick={() => run(async () => {
              await patchSettings({ rfRushMode: !(s.rfRushMode ?? true), preferRushLane: true })
              const st = useApp.getState().status
              if (st?.running) await api.restart()
            })} />
          </Row>
        </div>
        <div className="card">
          <h2>Туннель</h2>
          <Row label="Включить туннель" hint="Создаётся системный сетевой адаптер. Трафик не ограничивается портами HTTP и SOCKS.">
            <button className={'toggle' + (s.tunEnabled ? ' on' : '')} onClick={() => patchSettings({ tunEnabled: !s.tunEnabled })} />
          </Row>
          <div className="field"><label>Стек</label>
            <select value={s.tunStack} onChange={(e) => patchSettings({ tunStack: e.target.value })}>
              <option value="mixed">Смешанный — рекомендуется для Windows</option>
              <option value="system">Системный</option>
              <option value="gvisor">Изолированный</option>
            </select>
          </div>
          <Row label="Строгая маршрутизация" hint="Исключает передачу трафика в обход туннеля.">
            <button className={'toggle' + (s.strictRoute ? ' on' : '')} onClick={() => patchSettings({ strictRoute: !s.strictRoute })} />
          </Row>
          <Row label="Автоматическая маршрутизация"><button className={'toggle' + (s.autoRoute ? ' on' : '')} onClick={() => patchSettings({ autoRoute: !s.autoRoute })} /></Row>
          <Row label="Автоматическое перенаправление"><button className={'toggle' + (s.autoRedirect ? ' on' : '')} onClick={() => patchSettings({ autoRedirect: !s.autoRedirect })} /></Row>
          <Row label="Определение интерфейса"><button className={'toggle' + (s.autoDetectInterface ? ' on' : '')} onClick={() => patchSettings({ autoDetectInterface: !s.autoDetectInterface })} /></Row>
          <Row label="Перехват DNS, порт 53"><button className={'toggle' + (s.dnsHijack ? ' on' : '')} onClick={() => patchSettings({ dnsHijack: !s.dnsHijack })} /></Row>
          <Row label="IPv6"><button className={'toggle' + (s.ipv6 ? ' on' : '')} onClick={() => patchSettings({ ipv6: !s.ipv6 })} /></Row>
          <Row label="Независимый NAT"><button className={'toggle' + (s.endpointIndependentNat ? ' on' : '')} onClick={() => patchSettings({ endpointIndependentNat: !s.endpointIndependentNat })} /></Row>
          <div className="field"><label>MTU</label><input type="number" value={s.mtu} onChange={(e) => num('mtu', e.target.value)} />
            <div className="hint">Для туннеля рекомендуется значение 1400.</div>
          </div>
          <div className="field"><label>Адрес туннеля IPv4</label><input value={s.inet4Address} onChange={(e) => patchSettings({ inet4Address: e.target.value })} /></div>
        </div>
        <div className="card">
          <h2>Прокси и порты</h2>
          <PortSwap />
          <div style={{ height: 8 }} />
          <Row label="Доступ из локальной сети"><button className={'toggle' + (s.allowLan ? ' on' : '')} onClick={() => patchSettings({ allowLan: !s.allowLan })} /></Row>
          <div className="field"><label>Адрес привязки</label><input value={s.bindAddress} onChange={(e) => patchSettings({ bindAddress: e.target.value })} /></div>
          <div className="field"><label>Режим</label>
            <select value={s.mode} onChange={(e) => patchSettings({ mode: e.target.value })}>
              <option value="rule">По правилам</option>
              <option value="global">Глобальный</option>
              <option value="direct">Прямое соединение</option>
            </select>
          </div>
          <div className="field"><label>Уровень журнала</label>
            <select value={s.logLevel} onChange={(e) => patchSettings({ logLevel: e.target.value })}>
              <option value="silent">Без журнала</option>
              <option value="error">Ошибки</option>
              <option value="warning">Предупреждения</option>
              <option value="info">Сведения</option>
              <option value="debug">Отладка</option>
            </select>
          </div>
          <Row label="Параллельные TCP-соединения"><button className={'toggle' + (s.tcpConcurrent ? ' on' : '')} onClick={() => patchSettings({ tcpConcurrent: !s.tcpConcurrent })} /></Row>
          <Row label="Единая задержка"><button className={'toggle' + (s.unifiedDelay ? ' on' : '')} onClick={() => patchSettings({ unifiedDelay: !s.unifiedDelay })} /></Row>
          <Row label="Поддержание соединения"><button className={'toggle' + (s.keepAlive ? ' on' : '')} onClick={() => patchSettings({ keepAlive: !s.keepAlive })} /></Row>
          <Row label="Анализ имён" hint="Разбор SNI и HTTP повышает точность правил и немного увеличивает нагрузку на процессор.">
            <button className={'toggle' + ((s.snifferEnabled ?? true) ? ' on' : '')} onClick={() => patchSettings({ snifferEnabled: !(s.snifferEnabled ?? true) })} />
          </Row>
          <Row label="Запоминать выбранный сервер"><button className={'toggle' + ((s.storeSelected ?? true) ? ' on' : '')} onClick={() => patchSettings({ storeSelected: !(s.storeSelected ?? true) })} /></Row>
          <Row label="Повышенный приоритет процесса" hint="Операционная система выделяет ядру больше процессорного времени.">
            <button className={'toggle' + ((s.highPriority ?? true) ? ' on' : '')} onClick={() => patchSettings({ highPriority: !(s.highPriority ?? true) })} />
          </Row>
          <div className="field"><label>Определение процесса</label>
            <select value={s.findProcessMode} onChange={(e) => patchSettings({ findProcessMode: e.target.value })}>
              <option value="off">Отключено — рекомендуется для туннеля</option>
              <option value="strict">Строгое — отображается процесс</option>
              <option value="always">Всегда</option>
            </select>
          </div>
          <div className="field"><label>Отпечаток TLS</label>
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
          <Row label="Встроенный DNS"><button className={'toggle' + (s.dns.enable ? ' on' : '')} onClick={() => patchSettings({ dns: { ...s.dns, enable: !s.dns.enable } })} /></Row>
          <div className="field"><label>Режим разрешения</label>
            <select value={s.dns.enhancedMode} onChange={(e) => patchSettings({ dns: { ...s.dns, enhancedMode: e.target.value } })}>
              <option value="fake-ip">fake-ip</option>
              <option value="redir-host">redir-host</option>
            </select>
          </div>
          <div className="field"><label>Диапазон фиктивных адресов</label><input value={s.dns.fakeIpRange} onChange={(e) => patchSettings({ dns: { ...s.dns, fakeIpRange: e.target.value } })} /></div>
          <div className="field"><label>Основные серверы имён</label>
            <textarea rows={3} value={s.dns.nameserver.join('\n')} onChange={(e) => patchSettings({ dns: { ...s.dns, nameserver: e.target.value.split('\n').map((x) => x.trim()).filter(Boolean) } })} />
          </div>
          <div className="field"><label>Резервные серверы имён</label>
            <textarea rows={3} value={s.dns.fallback.join('\n')} onChange={(e) => patchSettings({ dns: { ...s.dns, fallback: e.target.value.split('\n').map((x) => x.trim()).filter(Boolean) } })} />
          </div>
          <div className="field"><label>Серверы имён по умолчанию</label>
            <textarea rows={2} value={s.dns.defaultNameserver.join('\n')} onChange={(e) => patchSettings({ dns: { ...s.dns, defaultNameserver: e.target.value.split('\n').map((x) => x.trim()).filter(Boolean) } })} />
          </div>
        </div>
        <div className="card">
          <h2>Система</h2>
          <Row label={i.sysProxy} hint="Для приложений, которые не используют туннель. При включённом туннеле системный прокси следует отключить.">
            <button className={'toggle' + (s.systemProxy ? ' on' : '')} onClick={() => run(async () => { await api.sysproxy(!s.systemProxy); await refresh() })} />
          </Row>
          <Row label="Автозапуск Windows"><button className={'toggle' + (s.autoStart ? ' on' : '')} onClick={() => patchSettings({ autoStart: !s.autoStart })} /></Row>
          <Row label="Закрывать в область уведомлений"><button className={'toggle' + (s.closeToTray ? ' on' : '')} onClick={() => patchSettings({ closeToTray: !s.closeToTray })} /></Row>
          <Row label="Запускать свёрнутым"><button className={'toggle' + (s.startHidden ? ' on' : '')} onClick={() => patchSettings({ startHidden: !s.startHidden })} /></Row>
          <div className="field"><label>Адрес проверки задержки</label><input value={s.latencyUrl} onChange={(e) => patchSettings({ latencyUrl: e.target.value })} /></div>
          <div className="field"><label>Таймаут проверки, мс</label><input type="number" value={s.latencyTimeoutMs} onChange={(e) => num('latencyTimeoutMs', e.target.value)} /></div>
          <div className="field"><label>Язык</label>
            <select value={s.language} onChange={(e) => patchSettings({ language: e.target.value })}>
              <option value="ru">Русский</option>
              <option value="en">English</option>
            </select>
          </div>
          <div className="field"><label>Сборка ядра</label>
            <select value={s.coreChannel} onChange={(e) => patchSettings({ coreChannel: e.target.value })}>
              <option value="v3">Оптимизированная (AVX2)</option>
              <option value="amd64">Стандартная</option>
              <option value="compatible">Совместимая</option>
            </select>
          </div>
        </div>
      </div>
    </div>
  )
}
