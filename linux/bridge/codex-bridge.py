#!/usr/bin/env python3
"""CodexMonitor Linux bridge.

Collects hardware/system metrics from sysfs, procfs and nvidia-smi and writes
them once per interval to a JSON file that the GNOME Shell extension renders.

Modes:
  (default)  run forever
  --once     write once and exit
  --dump     print every hwmon sensor and exit
"""
import ipaddress
import json
import os
import pwd
import re
import shlex
import shutil
import subprocess
import sys
import threading
import time
import urllib.request

CONFIG_DIR = os.path.join(os.environ.get("XDG_CONFIG_HOME", os.path.expanduser("~/.config")), "codex-monitor")
CONFIG_PATH = os.path.join(CONFIG_DIR, "config.json")
RUNTIME_DIR = os.environ.get("XDG_RUNTIME_DIR", "/tmp")
DEFAULT_OUTPUT = os.path.join(RUNTIME_DIR, "codex-monitor", "sensors.json")
HERE = os.path.dirname(os.path.abspath(__file__))
# Tests point this at a fake tree to exercise AMD/Intel GPUs and other boards.
SYS = os.environ.get("CODEX_MONITOR_SYSFS", "/sys")
MAX_DISKS = 6

DEFAULT_CONFIG = {
    "bridge": {"updateSeconds": 1, "outputFile": ""},
    # list: [{"id": "nct6798/fan2", "name": "Front intake", "warn": true}], set in the settings window.
    # Without a list: chip ("auto" = the board chip with the most fans) and cpu/case/psu channels.
    "fans": {"chip": "auto", "cpu": "fan1", "case": "fan2", "psu": ""},
    # device "auto" = NVIDIA, else the AMD card with the most VRAM, else Intel; or an id from inventory.json.
    "gpu": {"device": "auto"},
    "disks": ["/"],
    "network": {
        "ignoreAdaptersContaining": ["lo", "docker", "veth", "br-", "virbr", "vnet", "tun", "tap", "wg", "tailscale", "zt"],
        "wifiPrefixes": ["wl"],
        "ethernetPrefixes": ["en", "eth"],
        "splitFile": "/run/codex-monitor/netsplit.json",
        # Extra temperatures (liquid, board, drives): [{"id": "nzxtkraken3/temp1", "name": "Liquid", "warm": 40, "hot": 50}].
    "temps": {"list": []},
    # How many applications the bridge reports; the widget shows up to widget.topProcesses of them.
        "topProcesses": 5,
    },
    # mode: "notify" (notification with an Update button), "auto" (install right away) or "off".
    "update": {"mode": "notify", "intervalHours": 6},
}


def load_config():
    cfg = json.loads(json.dumps(DEFAULT_CONFIG))
    for path in (CONFIG_PATH, os.path.join(HERE, "..", "config.example.json")):
        try:
            with open(path) as f:
                user = json.load(f)
        except (OSError, ValueError):
            continue
        for key, value in user.items():
            if isinstance(value, dict) and isinstance(cfg.get(key), dict):
                cfg[key].update(value)
            else:
                cfg[key] = value
        break
    return cfg


def read(path, default=None):
    try:
        with open(path) as f:
            return f.read().strip()
    except OSError:
        return default


def read_int(path):
    value = read(path)
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


# ---------------------------------------------------------------- hwmon

def hwmon_chips():
    base = f"{SYS}/class/hwmon"
    chips = []
    for entry in sorted(os.listdir(base)) if os.path.isdir(base) else []:
        path = os.path.join(base, entry)
        chips.append((read(os.path.join(path, "name"), entry), path))
    return chips


def cpu_temp():
    chips = dict((name, path) for name, path in hwmon_chips())
    if "coretemp" in chips:
        path = chips["coretemp"]
        for i in range(1, 64):
            if read(f"{path}/temp{i}_label", "").startswith("Package"):
                return read_int(f"{path}/temp{i}_input") / 1000
        return (read_int(f"{path}/temp1_input") or 0) / 1000
    if "k10temp" in chips:
        path = chips["k10temp"]
        for i in range(1, 16):
            if read(f"{path}/temp{i}_label", "") in ("Tctl", "Tdie"):
                return read_int(f"{path}/temp{i}_input") / 1000
    return None


# hwmon chips that belong to graphics cards, not to the board.
GPU_HWMON = {"amdgpu", "radeon", "nouveau", "i915", "xe"}


class Liquidctl:
    """AIO water coolers without a kernel driver (many Corsair, Lian Li, newer NZXT), through
    liquidctl when it is installed. Each device becomes a chip with its pump/fan speeds and
    liquid temperature. Some devices need "liquidctl initialize all" once after boot."""

    def __init__(self):
        self.chips = []
        if shutil.which("liquidctl"):
            threading.Thread(target=self._run, daemon=True).start()

    def _run(self):
        while True:
            try:
                out = subprocess.run(["liquidctl", "status", "--json"], capture_output=True, text=True,
                                     timeout=15).stdout
                self.chips = self.parse(json.loads(out or "[]"))
            except (OSError, ValueError, subprocess.TimeoutExpired):
                self.chips = []
            time.sleep(3)

    @staticmethod
    def parse(devices):
        chips = []
        for device in devices:
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
                    temps[channel] = float(value)
                else:
                    continue
                labels[channel] = key
            if fans or temps:
                name = re.sub(r"[^a-z0-9]+", "-", device.get("description", "device").lower()).strip("-")
                chips.append({"name": f"liquidctl-{name}", "fans": fans, "temps": temps, "labels": labels})
        return chips


LIQUIDCTL = None


def sensor_chips():
    """Every sensor chip except graphics cards (the GPU reader covers those):
    [{"name", "fans": {"fan1": rpm}, "temps": {"temp1": °C}, "labels": {"fan1": "Pump speed"}}].

    Names are hwmon names, numbered when two chips share one ("nct6798#2"); liquidctl
    devices are added as "liquidctl-<model>".
    """
    chips, seen = [], {}
    for name, path in hwmon_chips():
        if name in GPU_HWMON:
            continue
        fans = {f"fan{i}": read_int(f"{path}/fan{i}_input") for i in range(1, 10)}
        temps = {f"temp{i}": read_int(f"{path}/temp{i}_input") for i in range(1, 33)}
        fans = {k: v for k, v in fans.items() if v is not None}
        temps = {k: v / 1000 for k, v in temps.items() if v is not None}
        if not fans and not temps:
            continue
        labels = {k: read(f"{path}/{k}_label") for k in [*fans, *temps]}
        seen[name] = seen.get(name, 0) + 1
        chips.append({"name": name if seen[name] == 1 else f"{name}#{seen[name]}", "fans": fans, "temps": temps,
                      "labels": {k: v for k, v in labels.items() if v}})
    return chips + (LIQUIDCTL.chips if LIQUIDCTL else [])


def fan_chips():
    """[(chip id, {"fan1": rpm, ...})] for every chip that reports fans."""
    return [(c["name"], c["fans"]) for c in sensor_chips() if c["fans"]]


def fan_readings():
    """{"nct6798/fan1": rpm, ...} for every fan channel."""
    return {f"{chip}/{channel}": rpm for chip, fans in fan_chips() for channel, rpm in fans.items()}


def temp_readings():
    """{"nct6798/temp2": °C, ...} for every temperature sensor (liquid, board, drives, …)."""
    return {f"{c['name']}/{channel}": value for c in sensor_chips() for channel, value in c["temps"].items()}


def temp_limits(label):
    """Default warm/hot thresholds: liquid runs much cooler than chips."""
    return (40, 50) if re.search(r"coolant|liquid|water", label or "", re.I) else (70, 85)


def temp_list(cfg):
    """Extra temperatures to show after CPU and GPU: [{"id", "name", "warm", "hot"}]."""
    result = []
    for t in cfg.get("list") or []:
        if isinstance(t, dict) and t.get("id"):
            warm, hot = temp_limits(t.get("name"))
            result.append({"id": t["id"], "name": t.get("name") or t["id"],
                           "warm": t.get("warm", warm), "hot": t.get("hot", hot)})
    return result


def fan_list(cfg):
    """Fans to show: [{"id", "name", "warn"}], in order.

    The settings window writes an explicit list (any number of fans, any chips). Older
    configs name a chip and the cpu/case/psu channels instead; those still work.
    """
    if isinstance(cfg.get("list"), list):
        return [{"id": f["id"], "name": f.get("name") or f["id"], "warn": bool(f.get("warn", True))}
                for f in cfg["list"] if isinstance(f, dict) and f.get("id")]
    chips = fan_chips()
    chip = cfg.get("chip") or "auto"
    if chip != "auto":
        chips = [c for c in chips if c[0].startswith(chip)]
    if not chips:
        return []
    name, fans = max(chips, key=lambda c: len(c[1]))
    roles = (("cpu", "CPU cooler", True), ("case", "Case fan", False), ("psu", "PSU fan", False))
    return [{"id": f"{name}/{cfg[key]}", "name": title, "warn": warn}
            for key, title, warn in roles if cfg.get(key) and cfg[key] in fans]


def dump_sensors():
    for name, path in hwmon_chips():
        print(f"== {name} ({path})")
        for fname in sorted(os.listdir(path)):
            if fname.endswith("_input"):
                label = read(os.path.join(path, fname.replace("_input", "_label")), "")
                print(f"  {fname:<16} {read(os.path.join(path, fname)):>10}  {label}")


# ---------------------------------------------------------------- gpu

class NvidiaReader:
    """Keeps one nvidia-smi process streaming CSV instead of spawning per tick."""
    FIELDS = "temperature.gpu,utilization.gpu,memory.used,memory.total,fan.speed"
    source = "NvidiaSmi"

    def __init__(self, interval_ms, index=0):
        self.latest = None
        self.interval_ms = interval_ms
        self.index = index
        if shutil.which("nvidia-smi"):
            threading.Thread(target=self._run, daemon=True).start()

    def _run(self):
        while True:
            try:
                proc = subprocess.Popen(
                    ["nvidia-smi", f"--query-gpu={self.FIELDS}", "--format=csv,noheader,nounits",
                     f"-lms={self.interval_ms}", "-i", str(self.index)],
                    stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True)
                for line in proc.stdout:
                    self.latest = [self._num(v) for v in line.split(",")]
                proc.wait()
            except OSError:
                pass
            self.latest = None
            time.sleep(5)

    @staticmethod
    def _num(value):
        try:
            return float(value)
        except ValueError:
            return None

    def snapshot(self):
        v = self.latest
        if not v or len(v) < 5:
            return {}
        temp, util, used, total, fan = v
        return {
            "GPUCore": temp, "GPULoad": util,
            "VRAMUsedMB": used, "VRAMTotalMB": total,
            "VRAMPct": used / total * 100 if used is not None and total else None,
            "GPUFanPct": fan,
        }


class SysfsGpu:
    """AMD (amdgpu) and Intel (i915/xe) cards: whatever their driver exposes in sysfs.

    amdgpu gives load, VRAM, temperature and fan; Intel drivers give a temperature on
    discrete cards only, so integrated Intel graphics show mostly "n/a".
    """

    def __init__(self, card):
        self.card = card
        self.source = card["driver"]
        hwmons = sorted(os.listdir(f"{card['path']}/hwmon")) if os.path.isdir(f"{card['path']}/hwmon") else []
        self.hwmon = f"{card['path']}/hwmon/{hwmons[0]}" if hwmons else None

    def _temp(self):
        if not self.hwmon:
            return None
        inputs = sorted(f for f in os.listdir(self.hwmon) if re.fullmatch(r"temp\d+_input", f))
        # Prefer the overall reading: "edge" on amdgpu, "pkg" on Intel discrete cards.
        for wanted in ("edge", "pkg"):
            for f in inputs:
                if read(f"{self.hwmon}/{f[:-6]}_label", "") == wanted:
                    return read_int(f"{self.hwmon}/{f}") / 1000
        value = read_int(f"{self.hwmon}/{inputs[0]}") if inputs else None
        return value / 1000 if value is not None else None

    def snapshot(self):
        dev = self.card["path"]
        used, total = read_int(f"{dev}/mem_info_vram_used"), read_int(f"{dev}/mem_info_vram_total")
        fan = None
        if self.hwmon:
            pwm, pwm_max = read_int(f"{self.hwmon}/pwm1"), read_int(f"{self.hwmon}/pwm1_max") or 255
            fan = pwm * 100 / pwm_max if pwm is not None else None
        data = {"GPUCore": self._temp(), "GPULoad": read_int(f"{dev}/gpu_busy_percent"), "GPUFanPct": fan}
        if used is not None and total:
            data.update(VRAMUsedMB=used / 1048576, VRAMTotalMB=total / 1048576, VRAMPct=used / total * 100)
        return data


class NoGpu:
    source = "none"

    @staticmethod
    def snapshot():
        return {}


PCI_VENDORS = {"0x1002": "AMD", "0x8086": "Intel", "0x10de": "NVIDIA"}


def drm_cards():
    """Graphics cards known to the kernel: [{"id": "card1", "driver", "vendor", "slot", "path"}]."""
    base = f"{SYS}/class/drm"
    cards = []
    for entry in sorted(os.listdir(base)) if os.path.isdir(base) else []:
        dev = f"{base}/{entry}/device"
        if not re.fullmatch(r"card\d+", entry) or not os.path.isdir(dev):
            continue
        cards.append({
            "id": entry,
            "driver": os.path.basename(os.path.realpath(f"{dev}/driver")) if os.path.exists(f"{dev}/driver") else "",
            "vendor": PCI_VENDORS.get(read(f"{dev}/vendor"), "GPU"),
            "slot": os.path.basename(os.path.realpath(dev)),
            "path": dev,
        })
    return cards


def pci_name(card):
    """"AMD Radeon RX 7800 XT"-style name from lspci, else vendor + card id."""
    if shutil.which("lspci"):
        out = subprocess.run(["lspci", "-mm", "-s", card["slot"]], capture_output=True, text=True).stdout
        fields = re.findall(r'"([^"]*)"', out)
        if len(fields) >= 3:
            model = re.search(r"\[([^\]]+)\]\s*$", fields[2])
            return f"{card['vendor']} {model.group(1) if model else fields[2]}"
    return f"{card['vendor']} GPU ({card['id']})"


def nvidia_names():
    if not shutil.which("nvidia-smi"):
        return []
    out = subprocess.run(["nvidia-smi", "-L"], capture_output=True, text=True).stdout
    return re.findall(r"^GPU \d+: (.+?) \(UUID", out, re.M)


def gpu_inventory():
    """Every GPU the bridge can read: [{"id", "name", "driver", "reader": callable}]."""
    gpus = [{"id": f"nvidia:{i}", "name": name, "driver": "nvidia-smi", "index": i}
            for i, name in enumerate(nvidia_names())]
    for card in drm_cards():
        if card["driver"] in ("amdgpu", "i915", "xe"):
            gpus.append({"id": f"drm:{card['id']}", "name": pci_name(card), "driver": card["driver"], "card": card})
    return gpus


def open_gpu(cfg, interval_ms, inventory):
    """The configured GPU reader; "auto" prefers NVIDIA, then the AMD card with the most VRAM, then Intel."""
    choice = cfg.get("device") or "auto"
    if choice == "none":
        return NoGpu()
    if choice == "auto":
        def rank(gpu):
            if gpu["driver"] == "nvidia-smi":
                return (3, 0)
            if gpu["driver"] == "amdgpu":
                return (2, read_int(f"{gpu['card']['path']}/mem_info_vram_total") or 0)
            return (1, 0)
        candidates = sorted(inventory, key=rank, reverse=True)
    else:
        candidates = [g for g in inventory if g["id"] == choice]
    if not candidates:
        return NoGpu()
    gpu = candidates[0]
    return NvidiaReader(interval_ms, gpu["index"]) if gpu["driver"] == "nvidia-smi" else SysfsGpu(gpu["card"])


# ---------------------------------------------------------------- cpu / ram

class CpuLoad:
    def __init__(self):
        self.prev = self._sample()

    @staticmethod
    def _sample():
        fields = [int(x) for x in read("/proc/stat").splitlines()[0].split()[1:]]
        idle = fields[3] + fields[4]
        return sum(fields), idle

    def value(self):
        total, idle = self._sample()
        dt, di = total - self.prev[0], idle - self.prev[1]
        self.prev = (total, idle)
        return 100 * (dt - di) / dt if dt > 0 else 0.0


def memory():
    info = {}
    for line in read("/proc/meminfo").splitlines():
        key, value = line.split(":", 1)
        info[key] = int(value.split()[0]) * 1024
    total, avail = info["MemTotal"], info["MemAvailable"]
    return {"RAMPct": (total - avail) / total * 100, "RAMUsedB": total - avail, "RAMTotalB": total}


# ---------------------------------------------------------------- network

class Network:
    def __init__(self, cfg):
        self.cfg = cfg
        self.prev = None
        self.prev_at = None
        self.modes = {}
        self.bitrates = {}
        self.modes_at = 0

    def _kind(self, iface):
        low = iface.lower()
        if any(term in low for term in self.cfg["ignoreAdaptersContaining"]):
            return None
        if any(low.startswith(p) for p in self.cfg["wifiPrefixes"]):
            return "wifi"
        if any(low.startswith(p) for p in self.cfg["ethernetPrefixes"]):
            return "eth"
        return None

    def _wifi_mode(self, iface):
        # Refresh "managed"/"AP" every 5 s; iw is cheap but not free.
        if time.monotonic() - self.modes_at > 5:
            self.modes = {}
            self.bitrates = {}
            self.modes_at = time.monotonic()
        if iface not in self.modes:
            mode = "managed"
            if shutil.which("iw"):
                out = subprocess.run(["iw", "dev", iface, "info"], capture_output=True, text=True).stdout
                if "type AP" in out:
                    mode = "AP"
            self.modes[iface] = mode
        return self.modes[iface]

    def _wifi_bitrate(self, iface):
        """Wi-Fi link rate in Mbps ("tx bitrate: 866.7 MBit/s"); sysfs has no speed for Wi-Fi."""
        self._wifi_mode(iface)  # shares the 5 s refresh
        if iface not in self.bitrates:
            rate = None
            if shutil.which("iw"):
                out = subprocess.run(["iw", "dev", iface, "link"], capture_output=True, text=True).stdout
                match = re.search(r"tx bitrate:\s*([\d.]+)\s*MBit/s", out)
                rate = float(match.group(1)) if match else None
            self.bitrates[iface] = rate
        return self.bitrates[iface]

    def sample(self):
        now = time.monotonic()
        counters = {}
        for line in read("/proc/net/dev").splitlines()[2:]:
            iface, data = line.split(":", 1)
            iface = iface.strip()
            if read(f"/sys/class/net/{iface}/operstate") not in ("up", "unknown"):
                continue
            kind = self._kind(iface)
            if kind:
                cols = data.split()
                counters[iface] = (kind, int(cols[0]), int(cols[8]))

        rates = {"eth": [0.0, 0.0], "wifi": [0.0, 0.0], "ap": [0.0, 0.0]}
        if self.prev is not None:
            dt = now - self.prev_at
            for iface, (kind, rx, tx) in counters.items():
                if iface not in self.prev or dt <= 0:
                    continue
                _, prx, ptx = self.prev[iface]
                bucket = kind
                if kind == "wifi" and self._wifi_mode(iface) == "AP":
                    bucket = "ap"
                rates[bucket][0] += max(rx - prx, 0) * 8 / dt / 1e6
                rates[bucket][1] += max(tx - ptx, 0) * 8 / dt / 1e6
        self.prev, self.prev_at = counters, now

        eth, wifi, ap = rates["eth"], rates["wifi"], rates["ap"]
        # AP semantics: clients download what the PC transmits.
        if ap[0] + ap[1] > 0 or (wifi[0] + wifi[1] == 0 and any(
                k == "wifi" and self._wifi_mode(i) == "AP" for i, (k, _, _) in counters.items())):
            mode, a_in, a_out, dl, ul = "AP", ap[0], ap[1], ap[1], ap[0]
        elif any(k == "wifi" for k, _, _ in counters.values()):
            mode, a_in, a_out, dl, ul = "WiFi", wifi[0], wifi[1], wifi[0], wifi[1]
        else:
            mode, a_in, a_out, dl, ul = "Off", 0.0, 0.0, 0.0, 0.0
        speeds = [read_int(f"/sys/class/net/{i}/speed") if k == "eth" else self._wifi_bitrate(i)
                  for i, (k, _, _) in counters.items()]
        speeds = [round(v) for v in speeds if v and v > 0]
        return {
            "NetLinkMbps": max(speeds) if speeds else None,
            "NetEthInMbps": eth[0], "NetEthOutMbps": eth[1],
            "NetWifiInMbps": wifi[0], "NetWifiOutMbps": wifi[1],
            "NetWifiApInMbps": ap[0], "NetWifiApOutMbps": ap[1],
            "NetWifiActiveMode": mode,
            "NetWifiActiveInMbps": a_in, "NetWifiActiveOutMbps": a_out,
            "NetWifiActiveDlMbps": dl, "NetWifiActiveUlMbps": ul,
            "NetDownMbps": eth[0] + wifi[0] + ap[0],
            "NetUpMbps": eth[1] + wifi[1] + ap[1],
        }


# ---------------------------------------------------------------- Internet / LAN

class LanClassifier:
    """LAN = private, link-local and multicast ranges plus every on-link (gateway-less) route."""

    def __init__(self):
        self.nets = []
        self.at = -1e9

    def _refresh(self):
        if time.monotonic() - self.at < 30:
            return
        self.at = time.monotonic()
        nets = []
        for family in ("-4", "-6"):
            out = subprocess.run(["ip", "-j", family, "route", "show", "table", "main"],
                                 capture_output=True, text=True).stdout
            try:
                routes = json.loads(out or "[]")
            except ValueError:
                routes = []
            for route in routes:
                dst = route.get("dst")
                if (route.get("gateway") or route.get("nexthops") or dst in (None, "default")
                        or route.get("type", "unicast") != "unicast"):
                    continue
                try:
                    nets.append(ipaddress.ip_network(dst, strict=False))
                except ValueError:
                    pass
        self.nets = nets

    def is_lan(self, ip):
        self._refresh()
        return ip.is_private or ip.is_link_local or ip.is_multicast or any(ip in net for net in self.nets)


def parse_addr(text):
    """'1.2.3.4:443', '[2a00::1]:443', '[::ffff:1.2.3.4]:443', 'fe80::1%eno1:22' -> ip_address."""
    host = text.rsplit(":", 1)[0].strip("[]").split("%")[0]
    try:
        ip = ipaddress.ip_address(host)
    except ValueError:
        return None
    return ip.ipv4_mapped or ip if ip.version == 6 else ip


def unescape_unit(name):
    """systemd escapes '-' inside unit name parts as \\x2d."""
    return re.sub(r"\\x([0-9a-fA-F]{2})", lambda m: chr(int(m.group(1), 16)), name)


class AppResolver:
    """Turns a process (or a systemd unit) into a readable application name and icon.

    1. The executable is matched against installed .desktop files: exact path, snap
       package, or the application's own directory (/opt/yandex/browser/...).
    2. Otherwise the app whose cgroup the process runs in names it: "claude · Visual Studio Code".
    3. Sockets without a visible process (other users, services) use the systemd unit description.
    """
    # Directories shared by many programs: an executable there says nothing about its app.
    SHARED_DIRS = {"/", "/bin", "/sbin", "/usr", "/usr/bin", "/usr/sbin", "/usr/local", "/usr/local/bin",
                   "/usr/local/sbin", "/usr/lib", "/usr/lib64", "/usr/libexec", "/usr/share", "/opt",
                   "/snap", "/snap/bin", os.path.expanduser("~"), os.path.expanduser("~/.local/bin")}
    INTERPRETERS = re.compile(r"^(python[\d.]*|node|nodejs|java|perl|ruby|bash|sh|dash|gjs|electron\d*)$")
    SNAP_DESKTOP_DIR = "/var/lib/snapd/desktop/applications"

    def __init__(self):
        lang = (os.environ.get("LC_MESSAGES") or os.environ.get("LANG") or "C").split(".")[0]
        self.name_keys = [f"Name[{lang}]", f"Name[{lang.split('_')[0]}]", "Name"]
        self.indexed_at = -1e9
        self.cache = {}
        self.units = {}

    # ---------------------------------------------------------- .desktop index

    def _desktop_dirs(self):
        data_dirs = [os.environ.get("XDG_DATA_HOME") or os.path.expanduser("~/.local/share")]
        data_dirs += (os.environ.get("XDG_DATA_DIRS") or "/usr/local/share:/usr/share").split(":")
        data_dirs += ["/var/lib/snapd/desktop", "/var/lib/flatpak/exports/share",
                      os.path.expanduser("~/.local/share/flatpak/exports/share")]
        seen = []
        for d in data_dirs:
            d = os.path.join(d, "applications")
            if d not in seen and os.path.isdir(d):
                seen.append(d)
        return seen

    @staticmethod
    def _parse(path):
        entry, inside = {}, False
        try:
            with open(path, encoding="utf-8", errors="replace") as f:
                for line in f:
                    line = line.strip()
                    if line.startswith("["):
                        if inside:
                            break
                        inside = line == "[Desktop Entry]"
                    elif inside and "=" in line and not line.startswith("#"):
                        key, value = line.split("=", 1)
                        entry[key.strip()] = value.strip()
        except OSError:
            return None
        return entry

    def _program(self, command, depth=0):
        """First real program of an Exec line, looking through env, VAR=value and sh -c '...'."""
        try:
            tokens = shlex.split(command)
        except ValueError:
            tokens = command.split()
        i = 0
        while i < len(tokens):
            token, base = tokens[i], os.path.basename(tokens[i])
            if base == "env" or ("=" in token and not token.startswith("/")):
                i += 1
            elif base in ("sh", "bash", "dash") and tokens[i + 1:i + 2] == ["-c"] and len(tokens) > i + 2:
                return self._program(tokens[i + 2], depth + 1) if depth < 2 else None
            else:
                token = token.replace("${HOME}", "$HOME").replace("$HOME", os.path.expanduser("~"))
                path = os.path.expanduser(token) if token.startswith(("/", "~")) else shutil.which(token)
                return os.path.realpath(path) if path and os.path.exists(path) else None
        return None

    def _index(self):
        if time.monotonic() - self.indexed_at < 300:
            return
        self.indexed_at = time.monotonic()
        self.cache.clear()
        self.by_id, self.by_path, self.by_dir, self.snaps = {}, {}, {}, {}
        for directory in self._desktop_dirs():
            for root, _, files in os.walk(directory):
                for file in files:
                    if not file.endswith(".desktop"):
                        continue
                    path = os.path.join(root, file)
                    desktop_id = os.path.relpath(path, directory).replace("/", "-")
                    entry = self._parse(path)
                    if (not entry or entry.get("Type", "Application") != "Application"
                            or entry.get("Hidden") == "true" or desktop_id in self.by_id):
                        continue
                    app = {"name": next((entry[k] for k in self.name_keys if entry.get(k)), file[:-8]),
                           "icon": entry.get("Icon"), "visible": entry.get("NoDisplay") != "true"}
                    self.by_id[desktop_id] = app
                    if directory == self.SNAP_DESKTOP_DIR:
                        self._prefer(self.snaps, desktop_id.split("_", 1)[0], app)
                    for command in (entry.get("TryExec"), entry.get("Exec")):
                        program = self._program(command) if command else None
                        if program:
                            self._prefer(self.by_path, program, app)
                            if os.path.dirname(program) not in self.SHARED_DIRS:
                                self._prefer(self.by_dir, os.path.dirname(program), app)

    @staticmethod
    def _prefer(table, key, app):
        # Menu entries beat hidden helpers (URL handlers, "open file" entries).
        if key not in table or (app["visible"] and not table[key]["visible"]):
            table[key] = app

    def _match(self, path):
        if not path:
            return None
        if path in self.by_path:
            return self.by_path[path]
        snap = re.match(r"/snap/([^/]+)/", path)
        if snap and snap.group(1) in self.snaps:
            return self.snaps[snap.group(1)]
        directory = os.path.dirname(path)
        while directory not in self.SHARED_DIRS and len(directory) > 1:
            if directory in self.by_dir:
                return self.by_dir[directory]
            directory = os.path.dirname(directory)
        return None

    def _app_of_cgroup(self, cgroup):
        unit = unescape_unit(cgroup.rsplit("/", 1)[-1]) if cgroup else ""
        snap = re.match(r"snap\.([^.]+)\.([^.]+?)(?:-[0-9a-f-]{8,})?\.(?:scope|service)$", unit)
        if snap:
            return self.by_id.get(f"{snap.group(1)}_{snap.group(2)}.desktop") or self.snaps.get(snap.group(1))
        app = re.match(r"app-(?:(?:gnome|flatpak|kde|xfce|dbus-[^-]+)-)?(.+?)(?:-\d+|-[0-9a-f]{32}|@[0-9a-f]+)?\.(?:scope|service)$", unit)
        return self.by_id.get(f"{app.group(1)}.desktop") if app else None

    # ---------------------------------------------------------- resolving

    def _unit_description(self, cgroup):
        """Sockets without a visible process: name them after their systemd unit or user."""
        unit = unescape_unit(cgroup.rsplit("/", 1)[-1]) if cgroup else ""
        if not re.search(r"\.(service|socket|scope)$", unit):
            user = re.search(r"/user-(\d+)\.slice", cgroup or "")
            if user:
                try:
                    return f"user {pwd.getpwuid(int(user.group(1))).pw_name}"
                except KeyError:
                    pass
            return "system"
        if unit not in self.units:
            scope = ["--user"] if "/user@" in cgroup else []
            description = subprocess.run(["systemctl", *scope, "show", "-p", "Description", "--value", unit],
                                         capture_output=True, text=True).stdout.strip()
            self.units[unit] = description or re.sub(r"\.(service|socket|scope)$", "", unit)
        return self.units[unit]

    def resolve(self, comm, pid, cgroup):
        """Returns (label, icon); icon is a theme icon name, a file path or None."""
        self._index()
        key = pid or cgroup
        if key in self.cache:
            return self.cache[key]
        if not pid:
            result = (self._unit_description(cgroup), "applications-system-symbolic")
        else:
            try:
                exe = os.path.realpath(f"/proc/{pid}/exe")
                with open(f"/proc/{pid}/cmdline", "rb") as f:
                    argv = [a.decode(errors="replace") for a in f.read().split(b"\0") if a]
            except OSError:
                exe, argv = None, []
            # comm is cut at 15 characters; argv[0] is the full name unless the program rewrote it.
            argv0 = os.path.basename(argv[0].split(" ")[0]) if argv else ""
            name = argv0 if argv0.startswith(comm) else comm
            if self.INTERPRETERS.match(name):
                script = next((a for a in argv[1:] if not a.startswith("-")), None)
                if script:
                    name = os.path.basename(script)
                    exe = self._program(script) or exe
            app = self._match(exe)
            if app:
                result = (app["name"], app["icon"])
            else:
                owner = self._app_of_cgroup(cgroup)
                if owner and owner["name"].lower() != name.lower():
                    result = (f"{name} · {owner['name']}", owner["icon"])
                else:
                    result = (owner["name"], owner["icon"]) if owner else (name, None)
        if len(self.cache) > 4096:
            self.cache.clear()
        self.cache[key] = result
        return result


class ProcessTraffic:
    """Per-application TCP throughput from the kernel's per-socket byte counters (ss / tcp_info).

    Sockets are named by AppResolver and grouped by that name, so a browser's many
    processes add up to one row. UDP (QUIC, games, uTP) has no per-socket counters, so the
    bridge reports it as the difference between interface traffic and attributed TCP.
    """
    HEAD = re.compile(r'users:\(\("([^"]+)",pid=(\d+)')
    CGROUP = re.compile(r'\bcgroup:(\S+)')
    KEY = re.compile(r'\b(?:ino|sk):(\S+)')
    BYTES = re.compile(r'\b(bytes_received|bytes_acked):(\d+)')

    def __init__(self, classifier):
        self.lan = classifier
        self.apps = AppResolver()
        self.icons = {}
        self.prev = None
        self.prev_at = None

    def sample(self):
        """Returns ({app label: [wanDown, wanUp, lanDown, lanUp] Mbps}, ok); icons are in self.icons."""
        if not shutil.which("ss"):
            return {}, False
        out = subprocess.run(["ss", "-tinpHe", "state", "established"],
                             capture_output=True, text=True).stdout
        now = time.monotonic()
        current, owners = {}, {}
        head = None
        for line in out.splitlines():
            if not line[:1].isspace():
                head = line
                continue
            if head is None:
                continue
            cols = head.split()
            peer = parse_addr(cols[3]) if len(cols) > 3 else None
            if peer is None or peer.is_loopback:
                continue
            key = tuple(self.KEY.findall(head)) or (cols[2], cols[3])
            counters = dict(self.BYTES.findall(line))
            current[key] = (int(counters.get("bytes_received", 0)), int(counters.get("bytes_acked", 0)))
            proc = self.HEAD.search(head)
            cgroup = self.CGROUP.search(head)
            label, icon = self.apps.resolve(*(proc.groups() if proc else ("", None)), cgroup and cgroup.group(1))
            self.icons[label] = icon
            owners[key] = (label, self.lan.is_lan(peer))
            head = None

        procs = {}
        if self.prev is not None and now > self.prev_at:
            scale = 8 / (now - self.prev_at) / 1e6
            for key, (rx, tx) in current.items():
                # New sockets were opened during this interval: count everything they moved.
                prx, ptx = self.prev.get(key, (0, 0))
                name, lan = owners[key]
                row = procs.setdefault(name, [0.0, 0.0, 0.0, 0.0])
                base = 2 if lan else 0
                row[base] += max(rx - prx, 0) * scale
                row[base + 1] += max(tx - ptx, 0) * scale
        self.prev, self.prev_at = current, now
        return procs, True


class NetSplit:
    """Exact Internet/LAN rates from codex-netsplit (optional root service, see linux/netsplit)."""
    KEYS = ("wanIn", "wanOut", "lanIn", "lanOut")

    def __init__(self, path):
        self.path = path
        self.prev = None
        self.rates = [0.0] * 4

    def sample(self):
        try:
            with open(self.path) as f:
                cur = json.load(f)
        except (OSError, ValueError):
            cur = None
        if not cur or time.monotonic() - cur.get("mono", 0) > 3:
            self.prev = None
            return None
        prev, self.prev = self.prev, cur
        # The helper and the bridge tick independently; reuse the last rates for a repeated snapshot.
        if prev and cur["mono"] > prev["mono"]:
            dt = cur["mono"] - prev["mono"]
            self.rates = [max(cur[k] - prev[k], 0) * 8 / dt / 1e6 for k in self.KEYS]
        return self.rates


def traffic_split(net, split_rates, procs, procs_ok, top_n, icons):
    """Internet/LAN rates (exact from codex-netsplit, else estimated from TCP) and the top processes."""
    down, up = net["NetDownMbps"], net["NetUpMbps"]
    tcp = [sum(p[i] for p in procs.values()) for i in range(4)]
    if split_rates is not None:
        mode = "exact"
        wan_down, wan_up, lan_down, lan_up = split_rates
    else:
        # Scale the interface totals by the TCP Internet/LAN ratio; unattributed traffic counts as Internet.
        mode = "estimate" if procs_ok else "none"
        lan_down = min(down * tcp[2] / (tcp[0] + tcp[2]), down) if tcp[0] + tcp[2] > 0 else 0.0
        lan_up = min(up * tcp[3] / (tcp[1] + tcp[3]), up) if tcp[1] + tcp[3] > 0 else 0.0
        wan_down, wan_up = down - lan_down, up - lan_up

    top = sorted(procs.items(), key=lambda kv: -sum(kv[1]))
    top = [{"name": name, "icon": icons.get(name), "wanDown": r[0], "wanUp": r[1], "lanDown": r[2], "lanUp": r[3]}
           for name, r in top[:top_n] if sum(r) >= 0.05]
    return {
        "NetSplitMode": mode,
        "NetWanDownMbps": wan_down, "NetWanUpMbps": wan_up,
        "NetLanDownMbps": lan_down, "NetLanUpMbps": lan_up,
        "NetTopProcesses": top,
        # UDP/QUIC and sockets that closed between samples.
        "NetOtherDownMbps": max(down - tcp[0] - tcp[2], 0.0),
        "NetOtherUpMbps": max(up - tcp[1] - tcp[3], 0.0),
    }


# ---------------------------------------------------------------- updates

def version_key(tag):
    """'v2.1.0' / 'v2.0.0-11-g5724a8a' -> (2, 1, 0); None if it is not a version."""
    match = re.match(r"v?(\d+(?:\.\d+)*)", tag or "")
    return tuple(int(x) for x in match.group(1).split(".")) if match else None


class UpdateChecker:
    """Polls the latest GitHub release in the background, like the Windows display watcher."""
    URL = "https://api.github.com/repos/molthun/codex-monitor/releases/latest"

    def __init__(self, cfg):
        self.local = read(os.path.join(HERE, "VERSION"), "unknown")
        self.release = None
        # Older configs only had "check": false.
        self.mode = "off" if cfg.get("check") is False else cfg.get("mode") or "notify"
        if self.mode != "off":
            self.interval = max(float(cfg.get("intervalHours") or 6), 1) * 3600
            threading.Thread(target=self._run, daemon=True).start()

    def _run(self):
        time.sleep(15)  # let the session settle (network, shell) after login
        while True:
            try:
                request = urllib.request.Request(self.URL, headers={"User-Agent": "CodexMonitor-Updater"})
                with urllib.request.urlopen(request, timeout=20) as response:
                    release = json.load(response)
                self.release = {"tag": release["tag_name"], "url": release.get("html_url")}
            except (OSError, ValueError, KeyError):
                pass  # offline or rate-limited: keep the last answer, retry next round
            time.sleep(self.interval)

    def snapshot(self):
        remote = self.release
        local_key = version_key(self.local)
        newer = remote and version_key(remote["tag"]) and (local_key is None or version_key(remote["tag"]) > local_key)
        return {
            "Version": self.local,
            "UpdateMode": self.mode,
            "UpdateAvailable": remote["tag"] if newer else None,
            "UpdateUrl": remote["url"] if newer else None,
        }


# ---------------------------------------------------------------- disks

class Disks:
    def __init__(self, mounts):
        self.mounts = mounts[:MAX_DISKS]
        self.prev = {}
        self.prev_at = None

    @staticmethod
    def _device_for(mount):
        """Resolves a mount point to its /proc/diskstats device name via major:minor."""
        try:
            dev = os.stat(mount).st_dev
        except OSError:
            return None
        return os.path.basename(os.path.realpath(f"/sys/dev/block/{os.major(dev)}:{os.minor(dev)}"))

    def sample(self):
        now = time.monotonic()
        stats = {}
        for line in read("/proc/diskstats").splitlines():
            cols = line.split()
            stats[cols[2]] = (int(cols[5]) * 512, int(cols[9]) * 512)  # sectors read / written

        result = []
        current = {}
        for mount in self.mounts:
            dev = self._device_for(mount)
            entry = {"mount": mount, "label": mount, "device": dev,
                     "readMBs": 0.0, "writeMBs": 0.0, "usedB": 0, "totalB": 0}
            try:
                st = os.statvfs(mount)
                entry["totalB"] = st.f_blocks * st.f_frsize
                entry["usedB"] = (st.f_blocks - st.f_bfree) * st.f_frsize
            except OSError:
                pass
            if dev in stats:
                current[dev] = stats[dev]
                if dev in self.prev and self.prev_at:
                    dt = now - self.prev_at
                    entry["readMBs"] = max(stats[dev][0] - self.prev[dev][0], 0) / dt / 1e6
                    entry["writeMBs"] = max(stats[dev][1] - self.prev[dev][1], 0) / dt / 1e6
            result.append(entry)
        self.prev, self.prev_at = current, now
        return result


# ---------------------------------------------------------------- main

REAL_FILESYSTEMS = {"ext2", "ext3", "ext4", "btrfs", "xfs", "f2fs", "vfat", "exfat", "ntfs", "ntfs3",
                    "fuseblk", "zfs", "bcachefs"}


def mount_inventory():
    """Mounted real filesystems, one per device (its shortest mount point), for the settings window."""
    by_device = {}
    for line in (read("/proc/mounts") or "").splitlines():
        device, mount, fs = line.split()[:3]
        mount = mount.replace("\\040", " ")
        if fs not in REAL_FILESYSTEMS or mount.startswith(("/snap/", "/var/snap/")):
            continue
        if device not in by_device or len(mount) < len(by_device[device]["mount"]):
            try:
                st = os.statvfs(mount)
                size = st.f_blocks * st.f_frsize
            except OSError:
                size = 0
            by_device[device] = {"mount": mount, "device": device, "fs": fs, "sizeB": size}
    return sorted(by_device.values(), key=lambda m: m["mount"])


def inventory(gpus, net_data, fan_cfg):
    """What this PC has, for the settings window: GPUs, sensor chips with live values, drives, link speed."""
    chips = sensor_chips()
    return {
        "gpus": [{"id": g["id"], "name": g["name"], "driver": g["driver"]} for g in gpus],
        "fanChips": [{"name": c["name"], "fans": c["fans"], "labels": {k: v for k, v in c["labels"].items() if k in c["fans"]}}
                     for c in chips if c["fans"]],
        "tempChips": [{"name": c["name"], "temps": c["temps"], "labels": {k: v for k, v in c["labels"].items() if k in c["temps"]}}
                      for c in chips if c["temps"]],
        # The fans shown now, so the settings window can start from them.
        "fanList": fan_list(fan_cfg),
        "mounts": mount_inventory(),
        "linkMbps": net_data.get("NetLinkMbps"),
        "Timestamp": time.time(),
    }


def config_stamp():
    try:
        return os.stat(CONFIG_PATH).st_mtime_ns
    except OSError:
        return None


def write_atomic(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w") as f:
        json.dump(data, f)
    os.replace(tmp, path)


def main():
    if "--dump" in sys.argv:
        dump_sensors()
        return
    once = "--once" in sys.argv
    cfg = load_config()
    interval = max(float(cfg["bridge"].get("updateSeconds") or 1), 0.5)
    out = os.path.expanduser(cfg["bridge"].get("outputFile") or DEFAULT_OUTPUT)

    gpus = gpu_inventory()
    gpu = open_gpu(cfg.get("gpu", {}), int(interval * 1000), gpus)
    inventory_path = os.path.join(os.path.dirname(out), "inventory.json")
    inventory_at = 0
    started_with = config_stamp()
    cpu = CpuLoad()
    net = Network(cfg["network"])
    split = NetSplit(cfg["network"].get("splitFile") or DEFAULT_CONFIG["network"]["splitFile"])
    processes = ProcessTraffic(LanClassifier())
    top_n = int(cfg["network"].get("topProcesses", 5))
    disks = Disks(cfg["disks"])
    updates = UpdateChecker(cfg.get("update", {}))
    net.sample()
    split.sample()
    processes.sample()
    disks.sample()
    time.sleep(1 if once else interval)

    fan_cfg = cfg["fans"]
    temp_cfg = cfg.get("temps", {})
    global LIQUIDCTL
    LIQUIDCTL = Liquidctl()
    while True:
        # Settings changed (settings window or by hand): restart cleanly with the new config.
        if not once and config_stamp() != started_with:
            os.execv(sys.executable, [sys.executable, *sys.argv])
        readings = fan_readings()
        temps = temp_readings() if temp_cfg.get("list") else {}
        fans = [{**fan, "rpm": readings.get(fan["id"])} for fan in fan_list(fan_cfg)]
        net_data = net.sample()
        data = {
            "CPU": cpu_temp(),
            "CPULoad": cpu.value(),
            **memory(),
            **gpu.snapshot(),
            "Fans": fans,
            "Temps": [{**t, "value": temps.get(t["id"])} for t in temp_list(temp_cfg)],
            "FansAvailable": bool(readings),
            **net_data,
            **traffic_split(net_data, split.sample(), *processes.sample(), top_n, processes.icons),
            "Disks": disks.sample(),
            **updates.snapshot(),
            "BridgeSource": f"hwmon+{gpu.source}",
            "Timestamp": time.time(),
        }
        write_atomic(out, data)
        # Often enough for the settings window to show live fan speeds.
        if time.monotonic() - inventory_at > 2:
            write_atomic(inventory_path, inventory(gpus, net_data, fan_cfg))
            inventory_at = time.monotonic()
        if once:
            print(json.dumps(data, indent=2))
            return
        time.sleep(interval)


if __name__ == "__main__":
    main()
