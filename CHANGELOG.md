# Changelog

## 2026-05-29

- Added a graphical WinForms settings wizard hosted by `CodexBridge.exe --settings`; `Configure-CodexMonitor.ps1` now launches that GUI instead of running the older console prompts.
- Fixed `--once` bridge mode after the settings wizard integration so one-shot runs write `temps.txt` instead of entering sensor dump mode.
- Bundled `CodexBridge.exe` as a self-contained single-file executable so end users no longer need Git or the .NET SDK/runtime to install or update CodexMonitor.
- Removed the end-user Git dependency from the bootstrap/update path by using GitHub API version checks and ZIP downloads, while preserving local `config.json`.
- Updated installer, documentation, sensor contract, skin metadata, and public config to reflect direct LibreHardwareMonitor sensor telemetry; CodexMonitor now clearly presents itself as display-only and does not manage fan behavior.
- Integrated `LibreHardwareMonitorLib` directly into the C# project to query CPU, GPU, and motherboard sensors natively. The bridge is now fully autonomous and reads hardware telemetry itself.
- Updated `Install-CodexMonitor.ps1` and `Watch-PrimaryDisplay.ps1` to deploy the bundled bridge executable from the GitHub ZIP without rebuilding on the client.
- Created `docs/DEPENDENCY_ANALYSIS.md` evaluating third-party dependencies and documenting simplification strategies.
- Made background Git auto-update failures visible to users with Windows notifications instead of silently swallowing failed fetch, pull, build, copy, task restart, or Rainmeter refresh steps.
- Created a dependency upgrade helper script `Deploy/Upgrade-Prerequisites-And-Apps.ps1` to stop active services safely, perform `winget` upgrades, and restore executing widgets. Added an `-Auto` switch to close the console automatically when done.
- Added daily winget upgrade checks in `Watch-PrimaryDisplay.ps1` that display an interactive GUI prompt to automatically run updates without manual terminal commands.
- Created a GitHub bootstrap script `Deploy/Bootstrap-CodexMonitor.ps1` to clone the repository and run the setup cleanly from GitHub using a single PowerShell one-liner.
- Added Git as a core prerequisite dependency in `Setup-CodexMonitor.ps1` and implemented session PATH updating to ensure commands run immediately upon install.
- Added a unified `Setup-CodexMonitor.ps1` self-elevating setup manager script to automatically check and install prerequisites via `winget`.
- Created an early interactive `Configure-CodexMonitor.ps1` configuration wizard, later replaced by the graphical settings wizard.
- Integrated a background Git auto-updater loop inside `Watch-PrimaryDisplay.ps1` to pull remote Git updates, automatically rebuild the C# bridge, and reload the widget every 6 hours.
- Synchronized the root and payload default skins with the 1080p preset, preserving variable-based health background sizing, and made 4K section icons use the committed PNG assets consistently.
- Updated the display watcher to switch automatically between 1080p and 4K profiles when the primary display height changes.
- Increased the 4K Rainmeter profile height to `720 x 1500`, normalized section icons, and re-spaced the cooling, network, disk I/O, and drive usage rows to prevent text/bar overlaps.
- Replaced missing SVG icon references with built-in Rainmeter shape icons and spaced 4K drive bars away from drive labels.
- Stopped the size switcher from rewriting tracked `CodexMonitor.ini` during runtime profile changes.
- Refactored CodexBridge and Rainmeter setup to fix hardcoded paths, hardware-specific dependencies, and configuration mismatches:
  - Translated all hardcoded absolute paths `file://C:/CodexMonitor/@Resources/temps.txt` to the relative `#@#temps.txt` path in all `.ini` skins and size presets.
  - Updated installer to copy `@Resources` payload directly into the active Rainmeter skin target, and dynamically configure `outputFile` path in `config.json`.
  - Parsed `"network"` adapters and `"boardFanIdentifierPrefix"` configurations in the C# bridge.
  - Implemented dynamic fan mapping fallback in `CodexBridge` that auto-detects motherboard RPM fan sensors on different hardware.
  - Added IPv6 support by utilizing interface-level `GetIPStatistics` to prevent traffic underreporting.
  - Enabled automatic IPC connection recovery in the C# bridge loop used by the earlier sensor-provider design.
  - Cleaned up obsolete `UpdateTemps.ps1` and duplicate `Watch-PrimaryDisplay.ps1` files.
- Decided GitHub should become the primary source of truth for collaboration; local reinstall archives become optional staging after GitHub is live.
- Split public project documentation from ignored local machine setup.
- Changed config model to one local working `config.json`, created from committed `config.example.json`.
- Installer, size switcher, display watcher, and bridge now read JSON config.
- Installer deploys the bundled `CodexBridge.exe`.
- Added developer documentation set:
  - `README.md`
  - `docs\PROJECT_CONTEXT.md`
  - `docs\ARCHITECTURE.md`
  - `docs\SENSOR_CONTRACT.md`
  - `docs\DEVELOPMENT.md`
  - `docs\GITHUB.md`
- Documented active paths, reinstall kit, runtime tasks, sensor contract, and collaboration rules.
- Current 4K profile documented as approximately `720 x 1500`.
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

- Tested an earlier IPC-based sensor bridge before returning to direct LibreHardwareMonitor telemetry.
- Built .NET 10 `CodexBridge`.
- Mapped CPU fan to board fan channel.
- Configured Rainmeter to behave as a desktop widget:
  - `AlwaysOnTop=-2`
  - `Draggable=0`
  - `ClickThrough=1`
  - `SavePosition=0`
