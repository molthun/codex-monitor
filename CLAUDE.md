# CodexMonitor — контекст для Claude

## Владелец и правила работы
- Отвечать по-русски. Интерфейс самого проекта (настройки, README) — на английском.
- Проект публичный: никаких личных значений владельца в настройках по умолчанию (его тариф 500 Мбит/с,
  LAN 1000, диски /srv/ssd, вентилятор fan7, NVIDIA). Всё определять автоматически, спрашивать в
  установщике или давать выбрать в настройках.
- На Windows-ПК владелец не может вставить команду (присылает фото экрана). Установка там только
  двойным кликом по `Install-CodexMonitor.cmd` из релиза на GitHub.
- Работа через ветки и PR; сливать — по команде владельца. Финальные релизы (теги без «-») ставить только
  по его команде: они автообновляют все Windows-установки. Бета-теги `vX.Y.Z-beta.N` — pre-release,
  их автообновление не видит (смотрит releases/latest), их можно делать для тестов.
- Экзотическое железо (USB-хабы вентиляторов, водянки без драйвера) — по запросу, через плагины.
- Отложено: macOS, плагины на Windows, перевод интерфейса.

## Устройство репозитория
- `install.ps1` (Windows) и `install.sh` (Linux) в корне; `windows/` и `linux/` — платформы; `CHANGELOG.md`.
- **Linux (GNOME 48–50):** `linux/bridge/codex-bridge.py` пишет `$XDG_RUNTIME_DIR/codex-monitor/sensors.json`
  и `inventory.json` (железо для настроек). Расширение: `extension.js` — маленький стабильный загрузчик
  (LOADER_API), `widget.js` (виджет, значок в панели, уведомления) и `settings.js` (окно настроек через
  `prefs.js`) грузятся из версионной папки `~/.local/share/codex-monitor/widget/<версия>/` → обновления
  без перезахода. Плагины датчиков: `linux/plugins/` (формат в README там), встроенный `liquidctl.py`.
  Root-хелпер точного Internet/LAN: `linux/netsplit/` + `install-netsplit.sh`.
- **Windows:** `windows/CodexBridge/` (C#, .NET 10, LibreHardwareMonitor, работает с правами админа через
  задачу «CodexMonitor Bridge Elevated») пишет `temps.txt` в `@Resources` скина. Скин Rainmeter
  ГЕНЕРИРУЕТСЯ: `CodexBridge.exe --build-skin --out <ini> --screen-height <px>` (`SkinBuilder.cs`);
  порядок ключей temps.txt — один источник `TempsFile.cs` (и для бриджа, и для RegExp скина).
  `--tray` — значок в трее (`TrayApp.cs`), `--settings` — окно настроек (`SettingsForm.cs`), общие
  действия — `WidgetControl.cs`, конфиг — `AppConfig.cs`, датчики/инвентарь — `HardwareSensors.cs`.
  Watcher `Watch-PrimaryDisplay.ps1`: позиция, пересборка скина при смене разрешения, обновления
  (режим `display.autoUpdate`: notify / true / false; кнопка Update now → протокол
  `codexmonitor:update/<tag>` → `update-request.txt`). `Deploy/Payload/` — зеркало исходников, синхронизировать.
- Релизы: `.github/workflows/release.yml` на теги `v*` собирает `CodexBridge.exe` и `Install-CodexMonitor.cmd`.

## Как проверять без Windows (на Linux)
- C#: .NET 10 SDK, `dotnet build -c Release -p:EnableWindowsTargeting=true` (0 ошибок и предупреждений).
- PowerShell: парсер из пакета Microsoft.PowerShell.SDK (маленькая утилита Parser.ParseFile) и
  выполнение функций через PowerShell.Create().
- Скин: `python3 windows/tools/rainmeter-emulator.py skin.ini temps.txt out.png`.
- GNOME: `dbus-run-session -- gnome-shell --headless --virtual-monitor 1920x1080` в изолированных XDG-папках
  + тестовое расширение-наблюдатель (скриншоты через Shell.Screenshot). Короткий XDG_RUNTIME_DIR
  (путь к wayland-сокету ≤ 108 байт).
- Чужое железо (AMD/Intel, платы ITE, водянки): поддельное дерево sysfs через `CODEX_MONITOR_SYSFS`.

## Текущее состояние (2026-10-02)
- В main: PR #3–#6 (сетевая панель Internet/LAN, топ процессов, уведомления об обновлениях,
  папки windows/ и linux/, установщики в корне).
- **Открыт PR #7** (ветка `linux-tray-settings-live-update`) — Linux: меню в панели, окно настроек,
  обновления на лету, AMD/Intel GPU, понятный список видеокарт, вентиляторы списком с живыми
  оборотами, температуры (водянки), плагины, до 6 дисков. Стоит у владельца на Linux, ждёт его отзыва.
- **Открыт PR #8** (ветка `windows-tray-settings-skin-builder`, основан на #7) — Windows: генерируемый
  скин, трей, новое окно настроек, исправленный установщик (ставит скрипты+скин+exe одного релиза,
  настройки после установки, один пункт в «Пуске», без ярлыка на рабочем столе).
- **Владелец тестирует на Windows бету v2.2.0-beta.2**
  (https://github.com/molthun/codex-monitor/releases/tag/v2.2.0-beta.2) через `Install-CodexMonitor.cmd`.
  Следующий шаг: разобрать его фото (трей, окно настроек, виджет), исправить, при необходимости beta.3.
- Порядок финальных релизов: сначала тег `v2.1.0` на коммит `2e5a30c` (старая структура + обновлятор,
  понимающий windows/), потом слить #7/#8 и тег `v2.2.0`.

Этот раздел устаревает: обновлять его, когда меняется состояние (слит PR, вышел релиз, новая бета).

## С чего начать новую сессию
`gh pr list`, `gh release list`, затем спросить владельца о результатах теста беты.
