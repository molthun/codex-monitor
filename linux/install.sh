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

if [[ ! -f "$CONF/config.json" ]]; then
    echo "==> Config -> $CONF/config.json"
    install -Dm644 "$SRC/config.example.json" "$CONF/config.json"
else
    echo "==> Keeping existing $CONF/config.json"
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
echo "Done."
