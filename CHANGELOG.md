# Changelog

## 2026-10-02

- Linux: top bar icon (GNOME's tray) with Settings, Check for updates, Restart widget and a Show widget switch.
- Linux: settings window (Extensions → CodexMonitor): widget size (automatic, 1080p/2K/4K or a custom percentage), fit to screen, position, which sections to show; graphics card, fan chip and channels with live RPM, up to 6 drives with names; Internet plan, LAN scale, top-process rows; update mode with Check now / Install. Changes apply immediately (the bridge restarts itself when the config changes).
- Linux: updates apply without logging out. The extension is now a small loader that imports the widget from a versioned folder and restarts it when an update lands; only a change of the loader needs one new login, and the update notification says so.
- Linux: AMD graphics (load, temperature, VRAM, fan via `amdgpu`) and Intel discrete graphics (temperature); "auto" picks NVIDIA, then the AMD card with the most VRAM, then Intel. Board fans: the "auto" chip is the one with the most fans (ITE `it87` boards work without configuration). Up to 6 drives. The bridge writes `inventory.json` (GPUs, fan chips, drives, link speed) for the settings window.
- Linux: the example config no longer contains the author's drives and fan channels.
- Linux: version checks rank a beta below its release (a beta is offered the final release, never an older one) and offer nothing when the installed version is unknown (a copy installed from an archive used to be offered v2.0.0). `install.sh --version <tag>` installs a given release, beta included.
- Linux: any number of fans from any sensor chip, each with its own name, order and "warn when it stops" switch, edited in Settings → Hardware → Fans with live RPM (add, rename, reorder, remove). A graphics card in its 0 RPM mode shows "idle" and only raises FANS LOW when the card is hot. Older configs (`fans.chip` with cpu/case/psu channels) keep working.
- Linux: water cooling. Extra temperatures (liquid, board, drives) can be added to the widget with their own amber/red thresholds (liquid defaults to 40/50 °C). AIO coolers with a kernel driver (NZXT Kraken, Corsair Commander, Aquacomputer) show their pump, fans and coolant temperature with driver labels; others work through liquidctl when it is installed. Fan bars scale per fan, so pumps do not peg the bar.
- Linux: sensor plugins for hardware the bridge does not know (USB fan hubs, coolers without a kernel driver): any executable in `~/.config/codex-monitor/plugins/` that prints sensor chips as JSON; its fans and temperatures join the lists in the settings, which also show each plugin's status and switch it on or off. liquidctl support moved into the first bundled plugin. Format documented in `linux/plugins/README.md`, shared with a future Windows implementation.

- Windows: the Rainmeter skin is generated from the settings (`CodexBridge.exe --build-skin`) instead of the fixed 1080p/4K presets: any size (automatic by screen height, 1080p / 2K / 4K, or a custom percentage), shrunk to fit the screen, sections can be hidden, 1–6 drives with names, any number of fans and extra temperatures, 0–5 top-process rows. It is rebuilt when the screen height changes. Bar alert colors work now (the old skin set `BarColor` on Shape meters).
- Windows: tray icon (`--tray`, started at sign-in, not elevated): show/hide the widget, settings, check for updates, install an update, restart the widget.
- Windows: new settings window with the Linux sections — Widget, Hardware (graphics card, fans and temperatures with live values, add/remove/reorder/rename, warn flags and thresholds, drives), Network, Updates (Check now / Install).
- Windows: graphics card choice ("auto": NVIDIA, then the AMD card with the most memory, then Intel) with VRAM for AMD and Intel; fans from any LibreHardwareMonitor source including AIO coolers; a graphics card in 0 RPM mode shows "idle"; the bridge writes `inventory.json` for the settings.
- Windows: updates apply without signing out — after installing, the watcher restarts the tray icon and itself on the new version.
- Windows: the example config no longer carries the author's fan chip prefix and drives.
- Windows: CPU temperatures and board fans need the PawnIO driver (LibreHardwareMonitor 0.9.5+ dropped WinRing0). Setup now installs it with winget, and Settings → Hardware has a Sensor access card that says what is missing (administrator rights, PawnIO, LibreHardwareMonitor errors), lists the devices found, and offers an Install PawnIO button.
- Windows: a missing sensor shows as N/A / n/a instead of a believable 0 (the bridge writes `-1`, as it already did for fans and extra temperatures): health strip, CPU/GPU/extra temperatures, VRAM and GPU fans. A graphics card without fan sensors no longer raises FANS LOW.
- Windows: the generated skin is saved as UTF-16 LE, the Unicode encoding Rainmeter reads; UTF-8 showed "°C" as "Â°C" and would garble non-ASCII names.
- Windows: the settings no longer show a plain "Ethernet" adapter as Ignore (the ignore word "vethernet" contains its name); saving then used to stop counting its traffic.
- Windows and Linux: the wireless half of the network legend is hidden while no Wi-Fi is in use; an adapter that is up but has carried no traffic for a minute counts as unused.
- Windows: the widget only shows what this PC has. No graphics card: no GPU load, VRAM, GPU temperature or GPU fan rows; no CPU temperature sensor: no CPU temperature row; no fans: no Cooling section; the health strip keeps only the cells it can fill and disappears when only RAM is left. The bridge reports the sensors it found and the display watcher rebuilds the skin when that changes (first run, after installing PawnIO). N/A remains for a sensor that drops out while running.
- Windows: in a virtual machine (Parallels, VMware, Hyper-V, VirtualBox, QEMU) the Sensor access card says that the VM has no temperature or fan sensors instead of suggesting a driver, and the health strip shows FANS N/A when no fan reports at all.
## 2026-10-01

- Linux: "Top processes" shows application names and icons from `.desktop` files instead of raw process names (`Yandex Browser` instead of `yandex_browser`), groups an app's processes into one row, names helpers by their host app (`claude · Visual Studio Code`) and system services by their systemd description.
- Linux: smart network panel. Download and Upload are each one bar split into Internet (blue) and LAN (mint), scaled to the NIC link speed with a tick at the Internet plan speed (`widget.internetDownMbps` / `internetUpMbps`, replaces `netMaxMbps`). The graph is mirrored (download up, upload down), stacked by the same colors, and snaps its scale to round values and the plan speed.
- Linux: "Top processes" shows which programs use the network now and whether they talk to the Internet or the LAN, from per-socket TCP counters (`ss`); UDP traffic is shown as `UDP / other`.
- Linux: update notifications. The bridge checks the latest GitHub release every 6 hours; a newer one shows a GNOME notification with "Update now" (downloads the release and reruns `install.sh`, keeping the config) and "Release notes". `install.sh` records the installed version; `update.sh` can also be run by hand.
- Linux: optional root helper `install-netsplit.sh` (nftables counters) for an exact Internet/LAN split; without it the split is estimated from TCP connections.
- Windows: smart network panel like the Linux one. Download and Upload are each one bar split into Internet (blue) and LAN (mint), scaled to the link speed, with a tick at the Internet plan (`network.internetDownMbps` / `internetUpMbps` / `lanMbps`) that turns amber when the plan is maxed out; the graph is mirrored (download up, upload down) and stacked in the same colors. The Disk I/O divider no longer crosses the Upload row.
- Windows: "Top processes" shows which applications use the network, with readable names and icons (file description like Task Manager, service names for `svchost`, `helper · host app` for runtimes, `Windows file sharing (SMB)` for kernel SMB traffic), from per-connection TCP counters (`GetPerTcpConnectionEStats`, needs the elevated bridge). `temps.txt` is now UTF-8.
- Windows: update notifications with buttons. `display.autoUpdate: "notify"` (new default) shows a Windows notification with "Update now" (installs through the display watcher via the `codexmonitor:` URL protocol) and "Release notes", and the widget subtitle shows the available version; `true` keeps the silent install, `false` disables the check. The settings wizard offers the three modes.

- Repository layout: the Windows version moved to `windows/` (next to `linux/`); the root holds `install.ps1` (Windows) and `install.sh` (Linux), both usable from a clone or straight from GitHub. The Windows one-liner is now `irm .../main/install.ps1 | iex`. The display watcher installs releases from either layout, so existing installs update across the move.
- Per-user speeds instead of the author's: the example configs no longer contain a 500 Mbps plan. LAN full scale comes from the network card (Linux now also reads the Wi-Fi bitrate); the Internet plan is asked by the Linux installer and set in the Windows settings wizard (new fields), and stays unset (no plan mark) if skipped.
- Fixed the Windows bootstrap copy step (`Copy-Item -LiteralPath "...\*"` does not expand the wildcard).
## 2026-06-11

- Routed 2560x1440 (2K) screens to the compact (1080p) profile by raising the default `autoProfileHeightThreshold` from 1440 to 1600. The 4K preset (720 px wide) was oversized on a 2K monitor (~28% of the screen); the compact preset reads closer to how it looks on native FullHD. Only true 4K-height screens (>= 1600) now get the large profile.
- Applied the same physical-resolution detection (`GetDeviceCaps(DESKTOPHORZRES/DESKTOPVERTRES)`) to `Install-CodexMonitor.ps1`, so the initial widget position is correct even when the installer runs in a DPI-virtualized context.

## 2026-06-10

- Fixed the widget rendering at the wrong size and floating away from the corner on a 4K screen after roaming from an RDP/FullHD session. The display watcher is a long-lived System-DPI-aware `powershell.exe`, so `[Screen]::PrimaryScreen.Bounds` stayed virtualized against the DPI context captured at process start (a 4K@100% screen reported as 1920x1080), making the watcher repeatedly force the 1080p preset and a `WindowX` computed for 1920 px. `Watch-PrimaryDisplay.ps1` and `Switch-WidgetSize.ps1` now read the true physical resolution via `GetDeviceCaps(DESKTOPHORZRES/DESKTOPVERTRES)`, which is immune to the per-process DPI virtualization.

## 2026-06-06

- Fixed widget corner pinning and auto-size: anchor the skin by its right edge (`AnchorX=100%`) so positioning no longer depends on the dynamic widget width, and re-pin the widget when it drifts (Rainmeter refresh/restart, manual drag, DPI change) instead of only on a resolution change. Removed the duplicate move that caused a visible jump on profile switches.
- Made the hardware bridge more reliable: `temps.txt` is now written atomically (temp file + replace) so Rainmeter never reads a truncated file and flashes all-zeros, and `nvidia-smi` is queried with a hard timeout that kills a hung process instead of freezing the update loop.
- Switched binary distribution to GitHub Releases. `CodexBridge.exe` is no longer committed to the repository; pushing a version tag (`v*`) builds it via GitHub Actions (`.github/workflows/release.yml`) and attaches it as a release asset. The installer and the in-app auto-updater download the binary from the latest release, and the updater now tracks release tags instead of `main` commits.
- The auto-updater installs the downloaded binary into the bridge run location, and the bridge scheduled-task name is read from `config.json`.

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
