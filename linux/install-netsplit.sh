#!/usr/bin/env bash
# Optional: exact Internet/LAN split for the network panel (system service, needs sudo).
# Without it the widget estimates the split from TCP connections.
set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if ! command -v nft >/dev/null; then
    echo "nftables is required: sudo apt install nftables" >&2
    exit 1
fi

echo "==> Helper -> /usr/local/lib/codex-monitor/codex-netsplit.py"
sudo install -Dm755 "$SRC/netsplit/codex-netsplit.py" /usr/local/lib/codex-monitor/codex-netsplit.py
echo "==> systemd system service"
sudo install -Dm644 "$SRC/systemd/codex-monitor-netsplit.service" /etc/systemd/system/codex-monitor-netsplit.service
sudo systemctl daemon-reload
sudo systemctl enable --now codex-monitor-netsplit.service
sudo systemctl restart codex-monitor-netsplit.service
echo "Done. Counters: /run/codex-monitor/netsplit.json"
