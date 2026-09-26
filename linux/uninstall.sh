#!/usr/bin/env bash
# Removes CodexMonitor for the current user. Keeps ~/.config/codex-monitor/config.json.
set -uo pipefail

UUID="codex-monitor@molthun.github.io"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
UNIT_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"

gnome-extensions disable "$UUID" 2>/dev/null
rm -rf "$DATA/gnome-shell/extensions/$UUID"

systemctl --user disable --now codex-monitor-bridge.service 2>/dev/null
rm -f "$UNIT_DIR/codex-monitor-bridge.service"
systemctl --user daemon-reload

rm -rf "$DATA/codex-monitor"
echo "Removed. Config kept in ${XDG_CONFIG_HOME:-$HOME/.config}/codex-monitor/"
