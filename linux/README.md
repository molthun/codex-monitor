# CodexMonitor for Linux (GNOME)

Linux port of the CodexMonitor desktop widget. Same layout, colors and health
thresholds as the Rainmeter skin, rendered by a GNOME Shell extension that sits
on the desktop under all windows (click-through, not in Alt+Tab).

Tested on Ubuntu 26.04, GNOME Shell 50 (Wayland), 2560×1440, NVIDIA RTX 4070,
ASRock B460 Phantom Gaming 4 (fans via nct6775).

## How it works

```text
hwmon (/sys/class/hwmon)  ─┐
nvidia-smi (streaming)    ─┤
/proc/stat, meminfo,      ─┼─> codex-bridge.py ──> $XDG_RUNTIME_DIR/codex-monitor/sensors.json
    net/dev, diskstats    ─┘        (systemd --user service)             │
                                                                         v
                                              GNOME Shell extension codex-monitor@molthun.github.io
```

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

## Configuration

`~/.config/codex-monitor/config.json` (created from `config.example.json`).
The widget reloads it automatically; restart the bridge after bridge changes:
`systemctl --user restart codex-monitor-bridge`.

| Key | Meaning |
| --- | --- |
| `fans.chip` | hwmon chip name prefix (`nct`, `it87`, …) |
| `fans.cpu` / `case` / `psu` | hwmon channel for each row (`fan1`…`fan7`) |
| `disks` | up to three mount points for Disk I/O and Drives used |
| `network.*` | interface classification (Ethernet / Wi-Fi / ignored) |
| `widget.profile` | `Auto`, `1080p`, `2K` or `4K` |
| `widget.autoProfileThresholds` | physical screen height for `2K` / `4K` in Auto mode |
| `widget.diskLabels` | display names for mount points |
| `update.check`, `update.intervalHours` | new-release check (notification with an Update button) |
| `network.topProcesses` | how many processes the network panel lists |
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

The bridge checks the latest GitHub release every 6 hours (`update.check`,
`update.intervalHours`). When a newer version is out, GNOME shows a notification with
**Update now** and **Release notes**, and the widget subtitle says so. Update now downloads
the release and reruns `install.sh` (your config is kept); log out and back in afterwards,
since GNOME Shell loads extension code only at login. If the Internet/LAN helper is
installed and changed, the update asks for your password to refresh it.

Manual update or check: `~/.local/share/codex-monitor/update.sh` (latest) or
`update.sh v2.1.0` (a specific release). Log: `~/.cache/codex-monitor/update.log`.

## Debugging

```bash
python3 ~/.local/share/codex-monitor/codex-bridge.py --once   # one sample as JSON
systemctl --user status codex-monitor-bridge
journalctl --user -u codex-monitor-bridge -f
journalctl --user -f /usr/bin/gnome-shell | grep -i codex      # extension errors
```
