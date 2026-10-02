#!/usr/bin/env python3
"""CodexMonitor sensor plugin: AIO coolers and fan hubs supported by liquidctl.

Many Corsair iCUE coolers and Commander hubs, Lian Li, newer NZXT and others have no kernel
driver; liquidctl (https://github.com/liquidctl/liquidctl, "sudo apt install liquidctl")
talks to them over USB. Each device becomes a chip with its pump/fan speeds and liquid
temperature. Prints nothing when liquidctl is not installed. Some devices need
"liquidctl initialize all" once after boot.
"""
import json
import re
import shutil
import subprocess
import sys

if not shutil.which("liquidctl"):
    sys.exit(0)

out = subprocess.run(["liquidctl", "status", "--json"], capture_output=True, text=True, timeout=12)
if out.returncode != 0:
    sys.exit(out.stderr.strip() or "liquidctl status failed")

chips = []
for device in json.loads(out.stdout or "[]"):
    fans, temps, labels = {}, {}, {}
    for item in device.get("status", []):
        unit, key, value = str(item.get("unit", "")).lower(), item.get("key", ""), item.get("value")
        if not isinstance(value, (int, float)):
            continue
        if unit == "rpm":
            channel = f"fan{len(fans) + 1}"
            fans[channel] = round(value)
        elif unit in ("°c", "c"):
            channel = f"temp{len(temps) + 1}"
            temps[channel] = value
        else:
            continue
        labels[channel] = key
    if fans or temps:
        name = re.sub(r"[^a-z0-9]+", "-", device.get("description", "device").lower()).strip("-")
        chips.append({"name": f"liquidctl-{name}", "fans": fans, "temps": temps, "labels": labels})

print(json.dumps({"chips": chips, "interval": 3}))
