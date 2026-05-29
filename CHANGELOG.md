# Changelog

## 2026-05-29

- Stopped the size switcher from rewriting tracked `CodexMonitor.ini` during runtime profile changes.
- Refactored CodexBridge and Rainmeter setup to fix hardcoded paths, hardware-specific dependencies, and configuration mismatches:
  - Translated all hardcoded absolute paths `file://C:/CodexMonitor/@Resources/temps.txt` to the relative `#@#temps.txt` path in all `.ini` skins and size presets.
  - Updated installer to copy `@Resources` payload directly into the active Rainmeter skin target, and dynamically configure `outputFile` path in `config.json`.
  - Parsed `"network"` adapters and `"boardFanIdentifierPrefix"` configurations in the C# bridge.
  - Implemented dynamic fan mapping fallback in `CodexBridge` that auto-detects motherboard RPM fan sensors on different hardware.
  - Added IPv6 support by utilizing interface-level `GetIPStatistics` to prevent traffic underreporting.
  - Enabled automatic IPC connection recovery in the C# bridge loop when FanControl is closed/reopened.
  - Cleaned up obsolete `UpdateTemps.ps1` and duplicate `Watch-PrimaryDisplay.ps1` files.
- Decided GitHub should become the primary source of truth for collaboration; local reinstall archives become optional staging after GitHub is live.
- Split public project documentation from ignored local machine setup.
- Changed config model to one local working `config.json`, created from committed `config.example.json`.
- Installer, size switcher, display watcher, and bridge now read JSON config.
- Installer can build `CodexBridge.exe` from source when the binary is missing.
- Added developer documentation set:
  - `README.md`
  - `docs\PROJECT_CONTEXT.md`
  - `docs\ARCHITECTURE.md`
  - `docs\SENSOR_CONTRACT.md`
  - `docs\DEVELOPMENT.md`
  - `docs\GITHUB.md`
- Documented active paths, reinstall kit, runtime tasks, sensor contract, and collaboration rules.
- Current 4K profile documented as approximately `720 x 1400`.
- Fixed header health background logic to use `HealthY`, `HealthH`, and `HealthR` variables instead of hard-coded 1080p coordinates in dynamic Rainmeter actions.
- Rebuilt reinstall kit archive after the header fix.

## 2026-05-28

- Added local reinstall/staging kit.
- Added automatic installer, backup, uninstall, profile switcher, and display watcher scripts.
- Added automatic 1080p/4K profile selection by primary display height.
- Added primary-monitor watcher so the widget follows the Windows primary display.
- Added per-disk I/O display for C:, D:, and E:.
- Added Ethernet, Wi-Fi, and Wi-Fi AP traffic split.
- Improved GPU fan handling with `nvidia-smi` fallback.
- Grouped widget values into performance, temperatures, cooling, network, disk I/O, and disk usage sections.

## Earlier

- Replaced direct LibreHardwareMonitor/CoreTemp approach with FanControl IPC bridge.
- Built .NET 10 `CodexBridge`.
- Mapped CPU fan to board fan channel.
- Configured Rainmeter to behave as a desktop widget:
  - `AlwaysOnTop=-2`
  - `Draggable=0`
  - `ClickThrough=1`
  - `SavePosition=0`
