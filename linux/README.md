# CodexMonitor for Linux (GNOME)

Linux port of the CodexMonitor desktop widget. Same layout, colors and health
thresholds as the Rainmeter skin, rendered by a GNOME Shell extension that sits
on the desktop under all windows (click-through, not in Alt+Tab).

Tested on Ubuntu 26.04, GNOME Shell 50 (Wayland), NVIDIA RTX 4070.

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

Fan RPMs come from the SuperIO chip driver. For Nuvoton chips (e.g. NCT6796D on
ASRock B460) load `nct6775`:

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
| `widget.netMaxMbps`, `diskIOMaxMBs`, `fanMaxRpm` | full-scale values for bars |

Size profiles: 1080p ≈ 430 px wide, 2K ≈ 540 px, 4K ≈ 720 px. In Auto mode the
widget also shrinks if it would not fit the screen height.

## Debugging

```bash
python3 ~/.local/share/codex-monitor/codex-bridge.py --once   # one sample as JSON
systemctl --user status codex-monitor-bridge
journalctl --user -u codex-monitor-bridge -f
journalctl --user -f /usr/bin/gnome-shell | grep -i codex      # extension errors
```
