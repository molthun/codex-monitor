# Sensor plugins

The bridge reads board sensors (hwmon), graphics cards and the drivers the kernel has for
water coolers. For anything else — a USB fan hub, an RGB/fan controller, a cooler without a
kernel driver, a vendor command-line tool — add a **plugin**: a small program that prints
what it measures. Its fans and temperatures appear in Settings → Hardware next to the
board's, where you name them and add them to the widget.

## Where plugins live

| Folder | What |
| --- | --- |
| `~/.local/share/codex-monitor/plugins/` | bundled with CodexMonitor (replaced on updates) |
| `~/.config/codex-monitor/plugins/` | **yours**; a plugin here replaces a bundled one with the same name |

Any **executable** file counts (`chmod +x`): Python, shell, a compiled program. The file name
without its extension is the plugin's name. Settings → Hardware → Sensor plugins lists every
plugin with what it found or its last error, and switches each one on or off.

## What a plugin prints

The bridge runs the plugin every few seconds and reads JSON from its standard output:

```json
{
  "chips": [
    {
      "name": "uni-hub",
      "fans":   {"fan1": 1180, "fan2": 1210},
      "temps":  {"temp1": 33.5},
      "labels": {"fan1": "Top fans", "fan2": "Bottom fans", "temp1": "Hub"}
    }
  ],
  "interval": 3
}
```

- `chips` — one entry per device. `name` must be unique; sensor ids become `uni-hub/fan1`.
- `fans` — speed in RPM, `temps` — °C; any keys, but `fan1…`, `temp1…` read best.
- `labels` — optional, shown next to each sensor and used as its default name.
- `interval` — optional, seconds until the next run (default 3).
- **Print nothing** (exit 0) when there is nothing to report, e.g. the device is unplugged.
- Exit with a non-zero code on errors; the last line of standard error is shown in the settings.
- Answer within 15 seconds. Non-numbers are ignored, so a sloppy plugin cannot break the widget.

## Example

```python
#!/usr/bin/env python3
"""Fan hub that exposes its speeds as text files (adapt to your device or vendor tool)."""
import json
import pathlib

hub = pathlib.Path("/sys/bus/usb/devices/1-4:1.0/fan_hub")  # example path
if not hub.exists():
    raise SystemExit(0)  # unplugged: report nothing

fans = {f"fan{i}": int((hub / f"fan{i}_rpm").read_text()) for i in range(1, 4)}
print(json.dumps({"chips": [{"name": "fan-hub", "fans": fans}]}))
```

Save it as `~/.config/codex-monitor/plugins/fan-hub.py`, `chmod +x` it, and open
Settings → Hardware: the plugin shows up with its fans within a few seconds.

## Bundled plugins

- `liquidctl` — AIO coolers and fan hubs supported by [liquidctl](https://github.com/liquidctl/liquidctl)
  (many Corsair iCUE coolers and Commander hubs, Lian Li, newer NZXT). Install liquidctl
  (`sudo apt install liquidctl`); some devices need `liquidctl initialize all` once after boot.

The Windows version uses the same format (a `plugins` folder next to the bridge); it is added
there when someone needs it.
