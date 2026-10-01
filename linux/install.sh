#!/usr/bin/env bash
# Installs CodexMonitor for the current user on GNOME (Wayland or X11).
# No root needed. Fan sensors additionally need the nct6775 kernel module (see README).
set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
UUID="codex-monitor@molthun.github.io"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
CONF="${XDG_CONFIG_HOME:-$HOME/.config}/codex-monitor"
UNIT_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"

echo "==> Bridge -> $DATA/codex-monitor"
install -Dm755 "$SRC/bridge/codex-bridge.py" "$DATA/codex-monitor/codex-bridge.py"
install -Dm755 "$SRC/update.sh" "$DATA/codex-monitor/update.sh"
# Release tag for the update check: set by update.sh, or taken from a git checkout.
VERSION="${CODEX_MONITOR_VERSION:-$(git -C "$SRC" describe --tags 2>/dev/null || echo unknown)}"
echo "$VERSION" > "$DATA/codex-monitor/VERSION"
echo "==> Version $VERSION"

if [[ ! -f "$CONF/config.json" ]]; then
    echo "==> Config -> $CONF/config.json"
    install -Dm644 "$SRC/config.example.json" "$CONF/config.json"
else
    echo "==> Keeping existing $CONF/config.json"
fi

# Internet plan speeds differ per user: ask once (also works under "curl | bash").
plan_down="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("widget", {}).get("internetDownMbps", 0) or 0)' "$CONF/config.json" 2>/dev/null || echo 0)"
if [[ "$plan_down" == "0" ]] && { true </dev/tty; } 2>/dev/null; then
    # Virtual interfaces have no speed file; that must not stop the installer (set -e).
    link="$(cat /sys/class/net/*/speed 2>/dev/null | sort -n | tail -1 || true)"
    echo
    echo "Your Internet plan speed marks the bars and highlights when it is maxed out."
    echo "LAN speed is detected from the network card${link:+ (now: ${link} Mbps)}."
    read -r -p "Internet download speed in Mbps (Enter to skip): " down </dev/tty || down=""
    read -r -p "Internet upload speed in Mbps (Enter = same as download): " up </dev/tty || up=""
    if [[ "$down" =~ ^[0-9]+$ ]]; then
        [[ "$up" =~ ^[0-9]+$ ]] || up="$down"
        python3 - "$CONF/config.json" "$down" "$up" <<'PY'
import json, sys
path, down, up = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
with open(path) as f:
    config = json.load(f)
config.setdefault("widget", {}).update({"internetDownMbps": down, "internetUpMbps": up})
with open(path, "w") as f:
    json.dump(config, f, indent=2, ensure_ascii=False)
    f.write("\n")
PY
        echo "==> Internet plan: ${down}/${up} Mbps (change it in $CONF/config.json)"
    fi
fi

echo "==> systemd user service"
install -Dm644 "$SRC/systemd/codex-monitor-bridge.service" "$UNIT_DIR/codex-monitor-bridge.service"
systemctl --user daemon-reload
systemctl --user enable --now codex-monitor-bridge.service
systemctl --user restart codex-monitor-bridge.service

echo "==> GNOME extension -> $DATA/gnome-shell/extensions/$UUID"
rm -rf "$DATA/gnome-shell/extensions/$UUID"
mkdir -p "$DATA/gnome-shell/extensions"
cp -r "$SRC/extension/$UUID" "$DATA/gnome-shell/extensions/"

# The running extension (a small loader) picks up a new widget from a fresh folder as soon
# as `current` points at it, so updates apply without logging out. Keep the previous copy.
WIDGETS="$DATA/codex-monitor/widget"
STAMP="$(printf '%s' "$VERSION" | tr -c 'A-Za-z0-9._-' '_')-$(date +%s)"
mkdir -p "$WIDGETS/$STAMP"
cp "$SRC/extension/$UUID/widget.js" "$SRC/extension/$UUID/settings.js" "$WIDGETS/$STAMP/"
previous="$(cat "$WIDGETS/current" 2>/dev/null || true)"
printf '%s\n' "$STAMP" > "$WIDGETS/current"
for dir in "$WIDGETS"/*/; do
    name="$(basename "$dir")"
    [[ "$name" == "$STAMP" || "$name" == "$previous" ]] || rm -rf "$dir"
done

if gnome-extensions enable "$UUID" 2>/dev/null; then
    echo "==> Extension enabled"
else
    # GNOME only discovers new extensions at login on Wayland.
    gsettings set org.gnome.shell enabled-extensions \
        "$(python3 -c "import ast,sys; l=ast.literal_eval(sys.argv[1].replace('@as ','')); print([*l, sys.argv[2]] if sys.argv[2] not in l else l)" \
            "$(gsettings get org.gnome.shell enabled-extensions)" "$UUID")"
    echo "==> Extension will appear after you log out and back in"
fi

if ! ls /sys/class/hwmon/*/name 2>/dev/null | xargs cat 2>/dev/null | grep -q '^nct'; then
    echo
    echo "Note: motherboard fan sensors are not available. To enable them (Nuvoton SuperIO):"
    echo "  sudo modprobe nct6775 && echo nct6775 | sudo tee /etc/modules-load.d/nct6775.conf"
fi
if [[ ! -f /etc/systemd/system/codex-monitor-netsplit.service ]]; then
    echo
    echo "Note: the Internet/LAN split is estimated from TCP connections. For exact numbers run:"
    echo "  ./install-netsplit.sh   (system service, asks for sudo)"
fi
echo "Done."
