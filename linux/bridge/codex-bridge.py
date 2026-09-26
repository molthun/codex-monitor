#!/usr/bin/env python3
"""CodexMonitor Linux bridge.

Collects hardware/system metrics from sysfs, procfs and nvidia-smi and writes
them once per interval to a JSON file that the GNOME Shell extension renders.

Modes:
  (default)  run forever
  --once     write once and exit
  --dump     print every hwmon sensor and exit
"""
import json
import os
import shutil
import subprocess
import sys
import threading
import time

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
    },
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
        return {
            "NetEthInMbps": eth[0], "NetEthOutMbps": eth[1],
            "NetWifiInMbps": wifi[0], "NetWifiOutMbps": wifi[1],
            "NetWifiApInMbps": ap[0], "NetWifiApOutMbps": ap[1],
            "NetWifiActiveMode": mode,
            "NetWifiActiveInMbps": a_in, "NetWifiActiveOutMbps": a_out,
            "NetWifiActiveDlMbps": dl, "NetWifiActiveUlMbps": ul,
            "NetDownMbps": eth[0] + wifi[0] + ap[0],
            "NetUpMbps": eth[1] + wifi[1] + ap[1],
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
    disks = Disks(cfg["disks"])
    net.sample()
    disks.sample()
    time.sleep(1 if once else interval)

    fan_cfg = cfg["fans"]
    while True:
        fans = board_fans(fan_cfg.get("chip") or "nct")
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
            **net.sample(),
            "Disks": disks.sample(),
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
