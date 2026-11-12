import { useShallow } from 'zustand/react/shallow'
import { cloneElement, isValidElement, type ReactNode } from 'react'
import { api } from '../lib/api'
import { t } from '../lib/i18n'
import { useApp } from '../store/app'
import { MenuSelect } from '../components/MenuSelect'
import { PortSwap } from '../components/PortSwap'

function Row({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  const control = isValidElement<{ className?: string; 'aria-label'?: string; role?: string; 'aria-checked'?: boolean }>(children)
    ? cloneElement(children, { 'aria-label': label, ...(children.props.className?.includes('toggle')
      ? { role: 'switch', 'aria-checked': children.props.className.split(' ').includes('on') } : {}) })
    : children
  return (
    <div className="field row">
      <div>
        <label>{label}</label>
        {hint && <div className="hint">{hint}</div>}
      </div>
      {control}
    </div>
  )
}

export function Settings() {
  const { settings, busy, patchSettings, run, refresh } = useApp(useShallow(({ settings, busy, patchSettings, run, refresh }) => ({ settings, busy, patchSettings, run, refresh })))
  const i = t(settings?.language || 'ru')
  if (!settings) return <div className="empty"><h3>{i.settings}</h3><p>Служба приложения недоступна.</p></div>
  const s = settings
  const num = (k: keyof typeof s, v: string) => patchSettings({ [k]: Number(v) || 0 } as never)

  return (
    <div>
      <div className="page-head"><div><h1>{i.settings}</h1><p>Сетевые параметры вступают в силу после нажатия «Применить и перезапустить».</p></div>
        <button className="btn primary" disabled={busy} onClick={() => run(() => api.restart())}>Применить и перезапустить</button>
      </div>
      <div className="card" style={{ marginBottom: 16 }}>
        <h2>Служба</h2>
        <Row label="Автообновление подписок" hint="Периодически обновляет все ссылки подписки в фоне.">
          <button className={'toggle' + ((s.autoUpdateSubscriptions ?? true) ? ' on' : '')} onClick={() => patchSettings({ autoUpdateSubscriptions: !(s.autoUpdateSubscriptions ?? true) })} />
        </Row>
        <div className="field"><label>Интервал, часов</label>
          <input type="number" min={1} max={168} value={s.autoUpdateIntervalHours ?? 6} onChange={(e) => patchSettings({ autoUpdateIntervalHours: Number(e.target.value) || 6 })} />
        </div>
        <Row label="Автоперезапуск ядра" hint="Если процесс ядра завершился аварийно, Jester запускает его снова.">
          <button className={'toggle' + ((s.autoRestartCore ?? true) ? ' on' : '')} onClick={() => patchSettings({ autoRestartCore: !(s.autoRestartCore ?? true) })} />
        </Row>
        <Row label="Подсветка при подключении" hint="Обзор и интерфейс слегка меняют акцент, пока ядро активно.">
          <button className={'toggle' + ((s.connectionHighlight ?? true) ? ' on' : '')} onClick={() => patchSettings({ connectionHighlight: !(s.connectionHighlight ?? true) })} />
        </Row>
        <div className="btn-row" style={{ marginTop: 12 }}>
          <button className="btn" disabled={busy} onClick={() => run(async () => {
            const data = await api.exportSettings()
            const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' })
            const a = document.createElement('a')
          const url = URL.createObjectURL(blob)
          a.href = url
            a.download = 'jester-settings.json'
            a.click()
          setTimeout(() => URL.revokeObjectURL(url), 1000)
          })}>Экспорт настроек</button>
          <button className="btn" disabled={busy} onClick={() => {
            const input = document.createElement('input')
            input.type = 'file'
            input.accept = 'application/json,.json'
            input.onchange = () => run(async () => {
              const file = input.files?.[0]
              if (!file) return
              const text = await file.text()
              await api.importSettings(JSON.parse(text))
              await refresh()
            })
            input.click()
          }}>Импорт настроек</button>
          <button className="btn" disabled={busy} onClick={() => run(async () => {
            const r = await api.checkUpdate()
            if (r.updateAvailable && r.releaseUrl) {
              if (confirm(r.message + '\nОткрыть страницу релиза?')) window.open(r.releaseUrl, '_blank')
            } else alert(r.message)
          })}>Проверить обновления</button>
        </div>
      </div>
      <div className="card" style={{ marginBottom: 16 }}>
        <h2>Скорость и конфиденциальность</h2>
        <Row label="Адаптивный канал" hint="Переключает RUSH и AUTO по живой задержке.">
          <button className={'toggle' + ((s.adaptiveLane ?? true) ? ' on' : '')} onClick={() => patchSettings({ adaptiveLane: !(s.adaptiveLane ?? true) })} />
        </Row>
        <Row label="Warm start" hint="Прогрев лучших узлов сразу после запуска ядра.">
          <button className={'toggle' + ((s.warmStart ?? true) ? ' on' : '')} onClick={() => patchSettings({ warmStart: !(s.warmStart ?? true) })} />
        </Row>
        <Row label="Быстрый путь приложений" hint="Выбранные процессы всегда идут через PROXY.">
          <button className={'toggle' + ((s.perAppFastPath ?? true) ? ' on' : '')} onClick={() => patchSettings({ perAppFastPath: !(s.perAppFastPath ?? true) })} />
        </Row>
        <div className="field"><label>Процессы (по одному в строке)</label>
          <textarea rows={4} value={s.fastPathProcesses || ''} onChange={(e) => patchSettings({ fastPathProcesses: e.target.value })} />
        </div>
        <Row label="Раздельный UDP" hint="Игровые и VoIP порты — через группу UDP-FAST.">
          <button className={'toggle' + ((s.splitUdpPolicy ?? true) ? ' on' : '')} onClick={() => patchSettings({ splitUdpPolicy: !(s.splitUdpPolicy ?? true) })} />
        </Row>
        <div className="field"><label>UDP порты</label>
          <input value={s.udpFastPorts || ''} onChange={(e) => patchSettings({ udpFastPorts: e.target.value })} />
        </div>
        <Row label="Интервалы keep-alive" hint="Слегка меняет интервалы служебных пакетов. Это не заполнение трафика.">
          <button className={'toggle' + ((s.trafficPadding ?? true) ? ' on' : '')} onClick={() => patchSettings({ trafficPadding: !(s.trafficPadding ?? true) })} />
        </Row>
        <Row label="DNS diversity" hint="Ротация пула DoH-резолверов.">
          <button className={'toggle' + ((s.dnsDiversity ?? true) ? ' on' : '')} onClick={() => patchSettings({ dnsDiversity: !(s.dnsDiversity ?? true) })} />
        </Row>
        <Row label="DNS без fallback" hint="Строгий режим: только основной пул, без запасных.">
          <button className={'toggle' + (s.dnsStrictNoFallback ? ' on' : '')} onClick={() => patchSettings({ dnsStrictNoFallback: !s.dnsStrictNoFallback })} />
        </Row>
        <div className="field"><label>Ротация DNS, часов</label>
          <input type="number" min={1} max={168} value={s.dnsRotateHours ?? 12} onChange={(e) => patchSettings({ dnsRotateHours: Number(e.target.value) || 12 })} />
        </div>
        <Row label="Проверка exit IP" hint="Следит за сменой адреса выхода; при сбое может сменить узел или panic.">
          <button className={'toggle' + ((s.exitConsistencyCheck ?? true) ? ' on' : '')} onClick={() => patchSettings({ exitConsistencyCheck: !(s.exitConsistencyCheck ?? true) })} />
        </Row>
        <div className="field"><label>Действие при смене exit</label>
          <MenuSelect label="Действие при смене exit" value={s.exitCheckAction || 'switch'} onChange={(value) => patchSettings({ exitCheckAction: value })} options={[
            { value: 'switch', label: 'Сменить узел' },
            { value: 'warn', label: 'Только предупреждение' },
            { value: 'panic', label: 'Экстренное отключение' },
          ]} />
        </div>
        <Row label="Защита системного прокси при сбое" hint="Блокирует приложения, использующие прокси Windows. Прямые соединения других приложений не блокируются.">
          <button className={'toggle' + ((s.deadManSwitch ?? true) ? ' on' : '')} onClick={() => patchSettings({ deadManSwitch: !(s.deadManSwitch ?? true) })} />
        </Row>
        <Row label="Smart Direct" hint="Запоминает прямые маршруты к российским доменам. Автообучение при включённой защите отключено.">
          <button className={'toggle' + ((s.smartDirectMap ?? true) ? ' on' : '')} onClick={() => patchSettings({ smartDirectMap: !(s.smartDirectMap ?? true) })} />
        </Row>
        <Row label="Latency budget" hint="Если p95 выше порога — автосмена узла.">
          <button className={'toggle' + ((s.latencyBudgetEnabled ?? true) ? ' on' : '')} onClick={() => patchSettings({ latencyBudgetEnabled: !(s.latencyBudgetEnabled ?? true) })} />
        </Row>
        <div className="field"><label>Порог задержки, мс</label>
          <input type="number" min={80} max={2000} value={s.latencyBudgetMs ?? 250} onChange={(e) => patchSettings({ latencyBudgetMs: Number(e.target.value) || 250 })} />
        </div>
        {s.deadManArmed && (
          <div className="btn-row" style={{ marginTop: 12 }}>
            <button className="btn danger" disabled={busy} onClick={() => run(async () => { await api.disarmDeadMan(); await refresh() })}>Снять dead-man</button>
          </div>
        )}
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
                allowLan: next ? false : s.allowLan,
                ipv6: next ? false : s.ipv6,
                dnsHijack: next ? true : s.dnsHijack,
                strictRoute: next ? true : s.strictRoute,
                findProcessMode: next ? 'off' : s.findProcessMode,
                logLevel: next ? 'silent' : (s.logLevel === 'silent' ? 'warning' : s.logLevel),
                mtu: next ? Math.min(s.mtu || 1400, 1280) : s.mtu,
              })
              const st = useApp.getState().status
              if (st?.running) await api.restart()
            })} />
          </Row>
          <Row label="Блокировка QUIC" hint="В режиме «По правилам» отклоняет UDP/443; браузер может повторить запрос по TCP/TLS.">
            <button className={'toggle' + ((s.blockQuic ?? true) ? ' on' : '')} onClick={() => patchSettings({ blockQuic: !(s.blockQuic ?? true) })} />
          </Row>
          <Row label="Фильтр STUN" hint="В режиме «По правилам» блокирует известные STUN-домены и UDP-порты 3478/5349. Другие порты не проверяются.">
            <button className={'toggle' + ((s.blockStun ?? true) ? ' on' : '')} onClick={() => patchSettings({ blockStun: !(s.blockStun ?? true) })} />
          </Row>
          <Row label="DNS через туннель" hint="Основные DNS-запросы идут через прокси. Для начального разрешения адресов серверов используется отдельный bootstrap DNS.">
            <button className={'toggle' + ((s.dnsViaProxy ?? true) ? ' on' : '')} onClick={() => patchSettings({ dnsViaProxy: !(s.dnsViaProxy ?? true) })} />
          </Row>
          <Row label="DNS по правилам" hint="Имена из списка блокировок разрешаются через туннель.">
            <button className={'toggle' + ((s.dnsRespectRules ?? true) ? ' on' : '')} onClick={() => patchSettings({ dnsRespectRules: !(s.dnsRespectRules ?? true) })} />
          </Row>
          <Row label="Случайный отпечаток TLS" hint="Для каждого соединения используется отдельный отпечаток клиента.">
            <button className={'toggle' + ((s.randomFingerprint ?? true) ? ' on' : '')} onClick={() => patchSettings({ randomFingerprint: !(s.randomFingerprint ?? true) })} />
          </Row>
          <Row label="Журнал без записи на диск" hint="Ядро не сохраняет журнал. При остановке файл журнала очищается.">
            <button className={'toggle' + ((s.silentLogs ?? true) ? ' on' : '')} onClick={() => patchSettings({ silentLogs: !(s.silentLogs ?? true), logLevel: !(s.silentLogs ?? true) ? 'silent' : (s.logLevel === 'silent' ? 'warning' : s.logLevel) })} />
          </Row>
          <Row label="Бинарные правила" hint="Список Re:filter в формате MRS обрабатывается быстрее текстового.">
            <button className={'toggle' + ((s.useMrsRules ?? true) ? ' on' : '')} onClick={() => patchSettings({ useMrsRules: !(s.useMrsRules ?? true) })} />
          </Row>
          <p className="hint">Защита от захвата экрана недоступна для текущего движка окна.</p>
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
            <MenuSelect label="Стек" value={s.tunStack} onChange={(value) => patchSettings({ tunStack: value })} options={[
              { value: 'mixed', label: 'Смешанный — рекомендуется для Windows' },
              { value: 'system', label: 'Системный' },
              { value: 'gvisor', label: 'Изолированный' },
            ]} />
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
          <div className="field"><label>Адрес привязки</label><input value={s.bindAddress} onChange={(e) => patchSettings({ bindAddress: e.target.value })} />
            <div className="hint">Применяется, когда включён доступ из локальной сети. Пустое значение открывает все адреса.</div>
          </div>
          <div className="field"><label>Режим</label>
            <MenuSelect label="Режим" value={s.mode} onChange={(value) => patchSettings({ mode: value })} options={[
              { value: 'rule', label: 'По правилам' },
              { value: 'global', label: 'Глобальный' },
              { value: 'direct', label: 'Прямое соединение' },
            ]} />
          </div>
          <div className="field"><label>Уровень журнала</label>
            <MenuSelect label="Уровень журнала" value={s.logLevel} onChange={(value) => patchSettings({ logLevel: value, silentLogs: value === 'silent' })} options={[
              { value: 'silent', label: 'Без журнала' },
              { value: 'error', label: 'Ошибки' },
              { value: 'warning', label: 'Предупреждения' },
              { value: 'info', label: 'Сведения' },
              { value: 'debug', label: 'Отладка' },
            ]} />
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
            <MenuSelect label="Определение процесса" value={s.findProcessMode} onChange={(value) => patchSettings({ findProcessMode: value })} options={[
              { value: 'off', label: 'Отключено — быстрый путь приложений включает строгое' },
              { value: 'strict', label: 'Строгое — отображается процесс' },
              { value: 'always', label: 'Всегда' },
            ]} />
          </div>
          <div className="field"><label>Отпечаток TLS</label>
            <MenuSelect label="Отпечаток TLS" value={s.globalClientFingerprint} onChange={(value) => patchSettings({ globalClientFingerprint: value })} options={['chrome', 'firefox', 'safari', 'ios', 'android', 'edge', 'random'].map((value) => ({ value, label: value }))} />
          </div>
        </div>
        <div className="card">
          <h2>DNS</h2>
          <Row label="Встроенный DNS"><button className={'toggle' + (s.dns.enable ? ' on' : '')} onClick={() => patchSettings({ dns: { ...s.dns, enable: !s.dns.enable } })} /></Row>
          <div className="field"><label>Режим разрешения</label>
            <MenuSelect label="Режим разрешения" value={s.dns.enhancedMode} onChange={(value) => patchSettings({ dns: { ...s.dns, enhancedMode: value } })} options={[
              { value: 'fake-ip', label: 'fake-ip' },
              { value: 'redir-host', label: 'redir-host' },
            ]} />
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
            <MenuSelect label="Язык" value={s.language} onChange={(value) => patchSettings({ language: value })} options={[
              { value: 'ru', label: 'Русский' },
              { value: 'en', label: 'English' },
            ]} />
          </div>
          <div className="field"><label>Сборка ядра</label>
            <MenuSelect label="Сборка ядра" value={s.coreChannel} onChange={(value) => patchSettings({ coreChannel: value })} options={[
              { value: 'v3', label: 'Оптимизированная (AVX2)' },
              { value: 'amd64', label: 'Стандартная' },
              { value: 'compatible', label: 'Совместимая' },
            ]} />
          </div>
        </div>
      </div>
    </div>
  )
}
