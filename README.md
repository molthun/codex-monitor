# CodexMonitor

Desktop hardware monitoring widget: CPU, GPU, RAM, fans, network and disks in one panel that
sits on the desktop under your windows.

The network panel splits traffic into **Internet** (blue) and **LAN** (mint), scales the bars to
your network card's link speed, marks your Internet plan and lists **which applications use the
network right now**. When a new release is out you get a notification with an **Update now** button.

Settings live behind an icon — the tray icon on Windows, the top bar icon on Linux: size (automatic
by screen or your own), which sections to show, graphics card (NVIDIA / AMD / Intel), any number of
fans and temperatures (water coolers included), up to six drives, your speeds, updates. Updates
apply without signing out.

| Platform | Widget | Folder |
| --- | --- | --- |
| Windows 10/11 | Rainmeter skin + `CodexBridge.exe` (LibreHardwareMonitor) | [`windows/`](windows/README.md) |
| Linux (GNOME 48–50) | GNOME Shell extension + `codex-bridge.py` | [`linux/`](linux/README.md) |

## Install

**Windows** — in PowerShell opened as Administrator:

```powershell
Set-ExecutionPolicy Bypass -Scope Process -Force; irm https://raw.githubusercontent.com/molthun/codex-monitor/main/install.ps1 | iex
```

**Linux**:

```bash
curl -fsSL https://raw.githubusercontent.com/molthun/codex-monitor/main/install.sh | bash
```

From a clone, run `install.ps1` (Windows) or `./install.sh` (Linux) in the repository root.

## Your speeds

Internet plans and LAN speeds differ, so nothing is hard-coded:

- **LAN**: detected from the network card (Ethernet link speed, or the Wi-Fi bitrate). Override
  it with `lanMbps` if you want a different full scale.
- **Internet plan**: the installer asks for it (the Windows settings wizard has fields for it,
  Linux asks in the terminal). Leave it empty and the widget simply shows no plan mark.

Where the settings live: `C:\CodexMonitor\config.json` → `network` on Windows,
`~/.config/codex-monitor/config.json` → `widget` on Linux. See the platform READMEs for all options.

## Repository layout

```text
install.ps1     Windows installer (downloads windows/ when run from GitHub)
install.sh      Linux installer (downloads linux/ when run from GitHub)
windows/        Rainmeter skin, CodexBridge (C#), PowerShell deploy scripts, docs
linux/          GNOME Shell extension, Python bridge, systemd units
CHANGELOG.md    changes for both platforms
```

Releases are GitHub tags (`v*`); the release workflow builds `CodexBridge.exe` and both
platforms update themselves from the latest release.
