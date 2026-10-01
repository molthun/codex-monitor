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
import re
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

DEFAULT_CONFIG = {
    "bridge": {"updateSeconds": 1, "outputFile": ""},
    "fans": {"chip": "nct", "cpu": "fan1", "case": "fan2", "psu": "fan7"},
    "disks": ["/"],
    "network": {
        "ignoreAdaptersContaining": ["lo", "docker", "veth", "br-", "virbr", "vnet", "tun", "tap", "wg", "tailscale", "zt"],
        "wifiPrefixes": ["wl"],
        "ethernetPrefixes": ["en", "eth"],
        "splitFile": "/run/codex-monitor/netsplit.json",
        "topProcesses": 3,
    },
    "update": {"check": True, "intervalHours": 6},
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
    base = "/sys/class/hwmon"
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


def board_fans(chip_prefix):
    """Returns {"fan1": rpm, ...} from the first SuperIO chip matching prefix."""
    for name, path in hwmon_chips():
        if name.startswith(chip_prefix):
            fans = {}
            for i in range(1, 8):
                rpm = read_int(f"{path}/fan{i}_input")
                if rpm is not None:
                    fans[f"fan{i}"] = rpm
            return fans
    return {}


def dump_sensors():
    for name, path in hwmon_chips():
        print(f"== {name} ({path})")
        for fname in sorted(os.listdir(path)):
            if fname.endswith("_input"):
                label = read(os.path.join(path, fname.replace("_input", "_label")), "")
                print(f"  {fname:<16} {read(os.path.join(path, fname)):>10}  {label}")


# ---------------------------------------------------------------- nvidia

class NvidiaReader:
    """Keeps one nvidia-smi process streaming CSV instead of spawning per tick."""
    FIELDS = "temperature.gpu,utilization.gpu,memory.used,memory.total,fan.speed"

    def __init__(self, interval_ms):
        self.latest = None
        self.interval_ms = interval_ms
        if shutil.which("nvidia-smi"):
            threading.Thread(target=self._run, daemon=True).start()

    def _run(self):
        while True:
            try:
                proc = subprocess.Popen(
                    ["nvidia-smi", f"--query-gpu={self.FIELDS}", "--format=csv,noheader,nounits",
                     f"-lms={self.interval_ms}", "-i", "0"],
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
            self.modes_at = time.monotonic()
        if iface not in self.modes:
            mode = "managed"
            if shutil.which("iw"):
                out = subprocess.run(["iw", "dev", iface, "info"], capture_output=True, text=True).stdout
                if "type AP" in out:
                    mode = "AP"
            self.modes[iface] = mode
        return self.modes[iface]

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
        speeds = [read_int(f"/sys/class/net/{i}/speed") for i, (k, _, _) in counters.items() if k == "eth"]
        speeds = [v for v in speeds if v and v > 0]
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


class ProcessTraffic:
    """Per-process TCP throughput from the kernel's per-socket byte counters (ss / tcp_info).

    Sockets owned by other users have no visible process unless the bridge runs as root;
    they are grouped as "system". UDP (QUIC, games, uTP) has no per-socket counters, so the
    bridge reports it as the difference between interface traffic and attributed TCP.
    """
    HEAD = re.compile(r'users:\(\("([^"]+)",pid=(\d+)')
    KEY = re.compile(r'\b(?:ino|sk):(\S+)')
    BYTES = re.compile(r'\b(bytes_received|bytes_acked):(\d+)')

    def __init__(self, classifier):
        self.lan = classifier
        self.prev = None
        self.prev_at = None
        self.names = {}

    def _name(self, comm, pid):
        """Full program name: comm is cut at 15 characters, argv[0] is not."""
        if pid not in self.names:
            try:
                with open(f"/proc/{pid}/cmdline", "rb") as f:
                    argv0 = os.path.basename(f.read().split(b"\0", 1)[0].decode(errors="replace").split(" ")[0])
            except OSError:
                argv0 = ""
            self.names[pid] = argv0 if argv0.startswith(comm) else comm
            if len(self.names) > 4096:
                self.names.clear()
        return self.names[pid]

    def sample(self):
        """Returns ({name: [wanDown, wanUp, lanDown, lanUp] Mbps}, ok)."""
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
            owners[key] = (self._name(*proc.groups()) if proc else "system", self.lan.is_lan(peer))
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


def traffic_split(net, split_rates, procs, procs_ok, top_n):
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
    top = [{"name": name, "wanDown": r[0], "wanUp": r[1], "lanDown": r[2], "lanUp": r[3]}
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
        if cfg.get("check", True):
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
            "UpdateAvailable": remote["tag"] if newer else None,
            "UpdateUrl": remote["url"] if newer else None,
        }


# ---------------------------------------------------------------- disks

class Disks:
    def __init__(self, mounts):
        self.mounts = mounts[:3]
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

    nvidia = NvidiaReader(int(interval * 1000))
    cpu = CpuLoad()
    net = Network(cfg["network"])
    split = NetSplit(cfg["network"].get("splitFile") or DEFAULT_CONFIG["network"]["splitFile"])
    processes = ProcessTraffic(LanClassifier())
    top_n = int(cfg["network"].get("topProcesses", 3))
    disks = Disks(cfg["disks"])
    updates = UpdateChecker(cfg.get("update", {}))
    net.sample()
    split.sample()
    processes.sample()
    disks.sample()
    time.sleep(1 if once else interval)

    fan_cfg = cfg["fans"]
    while True:
        fans = board_fans(fan_cfg.get("chip") or "nct")
        net_data = net.sample()
        data = {
            "CPU": cpu_temp(),
            "CPULoad": cpu.value(),
            **memory(),
            **nvidia.snapshot(),
            "CPUFan": fans.get(fan_cfg.get("cpu")),
            **{f"BoardFan{i}": fans.get(f"fan{i}") for i in range(1, 8)},
            "CaseFan": fans.get(fan_cfg.get("case")),
            "PSUFan": fans.get(fan_cfg.get("psu")),
            "FansAvailable": bool(fans),
            **net_data,
            **traffic_split(net_data, split.sample(), *processes.sample(), top_n),
            "Disks": disks.sample(),
            **updates.snapshot(),
            "BridgeSource": "hwmon+NvidiaSmi" if nvidia.latest else "hwmon",
            "Timestamp": time.time(),
        }
        write_atomic(out, data)
        if once:
            print(json.dumps(data, indent=2))
            return
        time.sleep(interval)


if __name__ == "__main__":
    main()
