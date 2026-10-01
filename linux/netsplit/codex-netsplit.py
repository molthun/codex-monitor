#!/usr/bin/env python3
"""CodexMonitor Internet/LAN traffic split (optional, runs as root).

Interface counters cannot tell Internet traffic from LAN traffic when both go
through the same NIC. This helper installs an nftables table that counts every
packet on physical interfaces and classifies it by the remote address: private
ranges and on-link prefixes are LAN, everything else is Internet. Cumulative
byte counters are written once per second to /run/codex-monitor/netsplit.json,
which the per-user bridge turns into rates.

Usage: codex-netsplit.py [--ifaces en,eth,wl] [--output PATH] [--interval 1]
"""
import argparse
import ipaddress
import json
import os
import signal
import subprocess
import sys
import time

TABLE = "codex_monitor"
COUNTERS = ("wan_in", "wan_out", "lan_in", "lan_out")
STATIC_LAN = {
    4: ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "224.0.0.0/4", "255.255.255.255/32"],
    6: ["fc00::/7", "fe80::/10", "ff00::/8"],
}
ROUTE_REFRESH_SECONDS = 30


def nft(script):
    subprocess.run(["nft", "-f", "-"], input=script, text=True, check=True)


def ruleset(prefixes):
    match_in = "\n".join(f'        iifname "{p}*" jump count_in' for p in prefixes)
    match_out = "\n".join(f'        oifname "{p}*" jump count_out' for p in prefixes)
    counters = "\n".join(f"    counter {name} {{}}" for name in COUNTERS)
    # "table; delete table; table {...}" recreates it atomically whether or not it existed.
    return f"""
table inet {TABLE}
delete table inet {TABLE}
table inet {TABLE} {{
    set lan4 {{ type ipv4_addr; flags interval; }}
    set lan6 {{ type ipv6_addr; flags interval; }}
{counters}

    # Before DNAT: the source is still the real remote peer.
    chain pre {{
        type filter hook prerouting priority -300; policy accept;
{match_in}
    }}
    # After SNAT: the destination is the real remote peer.
    chain post {{
        type filter hook postrouting priority 300; policy accept;
{match_out}
    }}
    chain count_in {{
        ip saddr @lan4 counter name lan_in return
        ip6 saddr @lan6 counter name lan_in return
        counter name wan_in
    }}
    chain count_out {{
        ip daddr @lan4 counter name lan_out return
        ip6 daddr @lan6 counter name lan_out return
        counter name wan_out
    }}
}}
"""


def lan_networks(prefixes):
    """Static private ranges plus every on-link (gateway-less) route on a counted interface."""
    nets = {4: [ipaddress.ip_network(n) for n in STATIC_LAN[4]],
            6: [ipaddress.ip_network(n) for n in STATIC_LAN[6]]}
    for family in (4, 6):
        out = subprocess.run(["ip", "-j", f"-{family}", "route", "show", "table", "main"],
                             capture_output=True, text=True).stdout
        try:
            routes = json.loads(out or "[]")
        except ValueError:
            routes = []
        for route in routes:
            dst = route.get("dst")
            if (route.get("gateway") or route.get("nexthops") or dst in (None, "default")
                    or route.get("type", "unicast") != "unicast"
                    or not route.get("dev", "").startswith(tuple(prefixes))):
                continue
            try:
                nets[family].append(ipaddress.ip_network(dst, strict=False))
            except ValueError:
                pass
    return {family: list(ipaddress.collapse_addresses(n)) for family, n in nets.items()}


def refresh_sets(prefixes):
    nets = lan_networks(prefixes)
    script = []
    for family, name in ((4, "lan4"), (6, "lan6")):
        script.append(f"flush set inet {TABLE} {name}")
        script.append(f"add element inet {TABLE} {name} {{ {', '.join(map(str, nets[family]))} }}")
    nft("\n".join(script) + "\n")


def read_counters():
    out = subprocess.run(["nft", "-j", "list", "table", "inet", TABLE],
                         capture_output=True, text=True, check=True).stdout
    values = {}
    for item in json.loads(out).get("nftables", []):
        counter = item.get("counter")
        if counter and counter.get("name") in COUNTERS:
            values[counter["name"]] = counter.get("bytes", 0)
    return values


def write_atomic(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + ".tmp"
    with open(tmp, "w") as f:
        json.dump(data, f)
    os.chmod(tmp, 0o644)
    os.replace(tmp, path)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--ifaces", default="en,eth,wl", help="comma-separated interface name prefixes")
    parser.add_argument("--output", default="/run/codex-monitor/netsplit.json")
    parser.add_argument("--interval", type=float, default=1.0)
    args = parser.parse_args()
    prefixes = [p for p in args.ifaces.split(",") if p]

    def cleanup(*_):
        subprocess.run(["nft", "delete", "table", "inet", TABLE], capture_output=True)
        try:
            os.unlink(args.output)
        except OSError:
            pass
        sys.exit(0)

    signal.signal(signal.SIGTERM, cleanup)
    signal.signal(signal.SIGINT, cleanup)

    nft(ruleset(prefixes))
    routes_at = 0.0
    while True:
        now = time.monotonic()
        if now - routes_at > ROUTE_REFRESH_SECONDS:
            refresh_sets(prefixes)
            routes_at = now
        counters = read_counters()
        # CLOCK_MONOTONIC is system-wide, so the bridge can compare it with its own clock.
        write_atomic(args.output, {
            "wanIn": counters.get("wan_in", 0), "wanOut": counters.get("wan_out", 0),
            "lanIn": counters.get("lan_in", 0), "lanOut": counters.get("lan_out", 0),
            "mono": time.monotonic(), "ts": time.time(),
        })
        time.sleep(args.interval)


if __name__ == "__main__":
    main()
