# CodexMonitor for Linux (GNOME)

Linux port of the CodexMonitor desktop widget. Same layout, colors and health
thresholds as the Rainmeter skin, rendered by a GNOME Shell extension that sits
on the desktop under all windows (click-through, not in Alt+Tab).

Tested on Ubuntu 26.04, GNOME Shell 50 (Wayland), 2560×1440, NVIDIA RTX 4070,
ASRock B460 Phantom Gaming 4 (fans via nct6775).

## How it works

```text
hwmon (/sys/class/hwmon)   ─┐
nvidia-smi / amdgpu / i915 ─┤
/proc/stat, meminfo,       ─┼─> codex-bridge.py ──> $XDG_RUNTIME_DIR/codex-monitor/sensors.json
    net/dev, diskstats     ─┘        (systemd --user service)    + inventory.json (hardware list)
                                                                         │
                                                                         v
                                              GNOME Shell extension codex-monitor@molthun.github.io
```

The extension is a small loader (`extension.js`) plus the widget (`widget.js`) and the
settings window (`settings.js`). `install.sh` also copies the last two into a versioned
folder under `~/.local/share/codex-monitor/widget/`; the loader imports a fresh copy
whenever that changes, so updates apply **without logging out** (the widget restarts).
Only a change of the loader itself needs one new login; the update notification says so.

| Windows                               | Linux                                          |
| ------------------------------------- | ---------------------------------------------- |
| Rainmeter skin                        | GNOME Shell extension (`extension/`)           |
| CodexBridge.exe + LibreHardwareMonitor | `bridge/codex-bridge.py` (Python stdlib only) |
| Scheduled task                        | `codex-monitor-bridge.service` (systemd user)  |
| `temps.txt`                           | `sensors.json` (same key names + extras)       |
| Watch-PrimaryDisplay.ps1 / Switch-WidgetSize.ps1 | built into the extension (monitor changes, Auto profile) |
| `C:\CodexMonitor\config.json`         | `~/.config/codex-monitor/config.json`          |

## Requirements

- GNOME Shell 48–50
- Python 3
- NVIDIA driver with `nvidia-smi` (optional: GPU rows show `n/a` without it)
- `iw` (optional: Wi-Fi access-point detection)

## Install

```bash
cd linux
./install.sh
```

On Wayland, log out and back in once so GNOME picks up the new extension.

Remove with `./uninstall.sh` (your config is kept).

### Motherboard fans

Fan RPMs come from the SuperIO chip driver. For Nuvoton chips (e.g. NCT6798 on
ASRock B460 Phantom Gaming 4) load `nct6775`:

```bash
sudo modprobe nct6775
echo nct6775 | sudo tee /etc/modules-load.d/nct6775.conf
```

If the chip still does not appear, the BIOS reserves the I/O range; add
`acpi_enforce_resources=lax` to the kernel command line.

Check what the bridge sees and map channels in the config:

```bash
python3 ~/.local/share/codex-monitor/codex-bridge.py --dump
```

GPU fan speed is shown in percent: `nvidia-smi` does not report RPM on Linux.

## Settings

Click the CodexMonitor icon in the top bar (GNOME's equivalent of a tray icon):
**Settings**, **Check for updates**, **Restart widget** and a **Show widget** switch.
The settings window (also under Extensions → CodexMonitor) has:

- **Widget**: size (automatic by screen, 1080p / 2K / 4K, or a custom percentage),
  fit to screen height, position, which sections to show, the top bar icon;
- **Hardware**: which graphics card to show (NVIDIA, AMD, Intel, none), the fans to show
  (any number, from any chip, named and ordered as you like, with live RPM to tell them
  apart and a per-fan "warn when it stops"), up to 6 drives and their names;
- **Network**: Internet plan speeds, LAN full scale, number of top-process rows;
- **Updates**: notify / install automatically / off, and **Check now** with **Install**.

Changes apply immediately: the widget and the bridge both watch the config file.

## Hardware support

| | Load | Temperature | VRAM | Fan |
| --- | --- | --- | --- | --- |
| NVIDIA (`nvidia-smi`) | ✓ | ✓ | ✓ | % |
| AMD (`amdgpu`) | ✓ | ✓ (edge) | ✓ | % |
| Intel discrete (`xe` / `i915`) | – | ✓ | – | – |
| Intel / other integrated | – | – | – | – |

CPU temperature comes from `coretemp` (Intel) or `k10temp` (AMD). Board fans come from
the SuperIO chips (`nct67xx`, `it87xx`, …); some boards need the kernel module first
(`sudo modprobe nct6775` or `it87`). Which channel is which fan: in Settings → Hardware →
Fans the speeds update live — load the CPU and watch its cooler speed up; on most boards
`fan1…fan7` follow the header order in the BIOS. Channels at 0 RPM are usually empty headers.
A graphics card at 0 RPM while cool is in its normal "0 RPM" mode and shown as idle; it only
counts as a problem when the card is hot (60 °C and up).

## Configuration

`~/.config/codex-monitor/config.json` (created from `config.example.json`), normally
edited through the settings window.

| Key | Meaning |
| --- | --- |
| `fans.list` | fans to show, in order: `[{"id": "nct6798/fan2", "name": "Front intake", "warn": true}]` (set in the settings window) |
| `fans.chip`, `fans.cpu` / `case` / `psu` | used while there is no `fans.list`: chip (`auto` or a name prefix) and its CPU / case / PSU channels |
| `gpu.device` | `auto`, `none` or an id from `inventory.json` (`nvidia:0`, `drm:card1`) |
| `disks` | up to six mount points for Disk I/O and Drives used |
| `network.*` | interface classification (Ethernet / Wi-Fi / ignored) |
| `widget.profile` | `Auto`, `1080p`, `2K` or `4K` |
| `widget.autoProfileThresholds` | physical screen height for `2K` / `4K` in Auto mode |
| `widget.diskLabels` | display names for mount points |
| `update.mode`, `update.intervalHours` | `notify` (notification with an Update button), `auto` or `off` |
| `network.topProcesses` | how many applications the bridge reports (the widget shows `widget.topProcesses` of them) |
| `widget.scale` | custom size in percent of the 1080p layout; `0` = by `profile` |
| `widget.show.*` | `health`, `performance`, `temperatures`, `cooling`, `network`, `diskIO`, `drives` |
| `widget.fitToScreen`, `visible`, `panelIcon`, `topProcesses`, `marginRight`, `marginTop` | as in the settings window |
| `widget.internetDownMbps` / `internetUpMbps` | your Internet plan (asked by the installer; `0` = unknown, no tick): tick on the bars, graph scale step, amber when ≥ 90% used |
| `widget.lanMbps` | full scale of the Download/Upload bars; `0` = link speed of the network card (Ethernet, or the Wi-Fi bitrate) |
| `widget.diskIOMaxMBs`, `fanMaxRpm` | full-scale values for bars |

Size profiles: 1080p ≈ 430 px wide, 2K ≈ 540 px, 4K ≈ 720 px. In Auto mode the
widget also shrinks if it would not fit the screen height.

## Network panel

Download and Upload are each one bar split by color: **blue = Internet**, **mint = LAN**.
The bars span the NIC link speed; the white tick marks the Internet plan and turns amber
when the plan is ≥ 90% used. The graph is mirrored (download above the middle line,
upload below) with the same colors, and its scale snaps to round values and to your
plan speed. A dashed line shows the plan when LAN traffic pushes the scale past it.

LAN means private, link-local and multicast addresses plus on-link subnets; everything
else is Internet.

**Top processes** lists who is using the network right now, from the kernel's per-socket
TCP counters (`ss`); the dot shows whether the app talks mostly to the Internet or the LAN.
Rows show application names and icons from installed `.desktop` files (matched by the
executable, its snap package or its install directory), so a browser's many processes add
up to one row. Helpers inside another app are shown with it (`claude · Visual Studio Code`),
and system services by their systemd description. UDP (QUIC in browsers, games) has no
per-process counters and shows up as `UDP / other` when it is a noticeable share.

By default the Internet/LAN split is **estimated** from those TCP connections (`est.` next
to the graph scale). For exact numbers install the optional root helper, which counts every
packet with nftables:

```bash
./install-netsplit.sh   # system service codex-monitor-netsplit, asks for sudo
```

## Updates

The bridge checks the latest GitHub release every 6 hours (`update.mode`,
`update.intervalHours`); **Check for updates** in the top bar menu or the settings window
asks right away. When a newer version is out, GNOME shows a notification with
**Update now** and **Release notes** (or installs it directly in `auto` mode). The update
downloads the release, reruns `install.sh` (your config is kept) and the widget restarts
with the new version by itself. If the Internet/LAN helper is installed and changed, the
update asks for your password to refresh it.

Manual: `~/.local/share/codex-monitor/update.sh` (latest), `update.sh v2.1.0` (a specific
release) or `update.sh --check`. Log: `~/.cache/codex-monitor/update.log`.

## Debugging

```bash
python3 ~/.local/share/codex-monitor/codex-bridge.py --once   # one sample as JSON
systemctl --user status codex-monitor-bridge
journalctl --user -u codex-monitor-bridge -f
journalctl --user -f /usr/bin/gnome-shell | grep -i codex      # extension errors
```
