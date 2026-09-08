# Jester

Лёгкий Windows-клиент прокси с режимом **TUN**. Не свой движок трафика — готовое ядро **mihomo** (Clash Meta). Оболочка тонкая, интерфейс свой.

## Почему такая связка

| Слой | Технология | Зачем |
|---|---|---|
| Ядро TUN / протоколы | **mihomo** (Go) | Уже быстрый, мало RAM, VLESS / SS / Trojan / Hysteria2 / TUIC, Clash YAML-подписки |
| Хост | **C# / .NET 10 + WebView2** | ~десятки МБ, трей, админ-права для TUN, без Electron |
| UI | **React + TypeScript** | Кастомизация, много настроек, понятная навигация |

Переписывать ядро на Rust/C++ нет смысла: sing-box и mihomo уже закрывают скорость. Electron отброшен из‑за RAM. Tauri был бы ещё легче, но на этой машине нет Rust; WebView2 даёт почти тот же профиль памяти.

Ориентир по памяти: UI WebView2 + процесс Jester + mihomo обычно заметно меньше Clash for Windows / Electron-клиентов.

## Скачать приложение

Готовые сборки — на странице [Releases](https://github.com/78rayy192/jester-proxy-client/releases): скачай **Jester-windows-x64.zip**, распакуй и запусти `Jester.exe`.

.NET отдельно ставить не нужно. Нужны Windows 10/11 x64 и WebView2 (в Windows 11 обычно уже есть). Для TUN запускай от администратора; при первом старте нажми **Скачать ядро**.

## Сборка из исходников

Нужны **Node.js**, **.NET 10** и (для TUN) запуск **от администратора**.

```powershell
cd ui
npm install
npm run build
cd ..\host
dotnet run -c Release
```

Собрать zip для релиза:

```powershell
.\release.ps1 v0.1.0
# или сразу создать GitHub Release:
.\release.ps1 v0.1.0 -Release
```

Для живой вёрстки UI (хост уже запущен):

```powershell
cd ui
npm run dev
```

Окно само подхватит `http://127.0.0.1:5173`, если Vite отвечает.

При первом старте нажмите **Скачать ядро** — подтянутся `mihomo.exe` и `wintun.dll` в `%AppData%\Jester\cores`.

1. Добавьте Clash YAML профиль (ссылка подписки или вставка).
2. Включите TUN в настройках.
3. Если Windows спросит UAC — согласитесь.
4. Запустите.

## Данные

`%AppData%\Jester\` — настройки, профили, журнал, ядро.

## Замечание

TUN на Windows требует драйвер WinTUN и повышенные права. Без админа остаётся mixed/SOCKS на локальных портах.
