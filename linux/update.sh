#!/usr/bin/env bash
# Updates CodexMonitor for Linux to a GitHub release.
#
#   update.sh          install the latest release if it is newer than the installed one
#   update.sh v2.1.0   install that release
#   update.sh --check  print {"local", "latest", "newer", "url"} as JSON and exit (no install)
#
# The widget runs this from its "Update now" notification. The log goes to
# ~/.cache/codex-monitor/update.log; the last line is shown when the update fails.
set -euo pipefail

REPO="molthun/codex-monitor"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
LOG_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/codex-monitor"
LOCAL="$(cat "$DATA/codex-monitor/VERSION" 2>/dev/null || echo unknown)"

# Is release $2 newer than installed $1? Same ranking as version_key() in codex-bridge.py: a beta
# ranks below its release, a build past a tag ("-main", "-11-gabc") above it; unknown is never older.
NEWER_PY='
import re, sys
def key(tag):
    m = re.match(r"v?(\d+(?:\.\d+){0,2})(?:-(.+))?", tag or "")
    if not m:
        return None
    numbers = tuple((list(map(int, m.group(1).split("."))) + [0, 0])[:3])
    suffix = m.group(2)
    return numbers + (1 if not suffix else 0 if re.match(r"(alpha|beta|rc|pre)", suffix, re.I) else 2,)
local, latest = key(sys.argv[1]), key(sys.argv[2])
sys.exit(0 if local and latest and latest > local else 1)
'
is_newer() { python3 -c "$NEWER_PY" "$1" "$2"; }

if [[ "${1:-}" == "--check" ]]; then
    RELEASE="$(curl -fsSL -H "User-Agent: CodexMonitor-Updater" "https://api.github.com/repos/$REPO/releases/latest")"
    LATEST="$(printf '%s' "$RELEASE" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("tag_name", ""))')"
    NEWER=false
    if is_newer "$LOCAL" "$LATEST"; then NEWER=true; fi
    printf '%s' "$RELEASE" | python3 -c '
import json, sys
release = json.load(sys.stdin)
print(json.dumps({"local": sys.argv[1], "latest": release.get("tag_name", ""), "newer": sys.argv[2] == "true",
                  "url": release.get("html_url")}))
' "$LOCAL" "$NEWER"
    exit
fi

mkdir -p "$LOG_DIR"
exec > >(tee "$LOG_DIR/update.log") 2>&1
TAG="${1:-}"
if [[ -z "$TAG" ]]; then
    TAG="$(curl -fsSL -H "User-Agent: CodexMonitor-Updater" "https://api.github.com/repos/$REPO/releases/latest" \
        | python3 -c 'import json,sys; print(json.load(sys.stdin)["tag_name"])')"
    if ! is_newer "$LOCAL" "$TAG"; then
        echo "Up to date ($LOCAL, latest release $TAG)."
        exit 0
    fi
fi
echo "==> Updating CodexMonitor $LOCAL -> $TAG"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
URL="${CODEX_MONITOR_ARCHIVE_URL:-https://github.com/$REPO/archive/refs/tags/$TAG.tar.gz}"
echo "==> Downloading $URL"
curl -fsSL "$URL" | tar -xz -C "$WORK" --strip-components=1

if [[ ! -x "$WORK/linux/install.sh" ]]; then
    echo "Error: release $TAG has no Linux version."
    exit 1
fi

CODEX_MONITOR_VERSION="$TAG" "$WORK/linux/install.sh"

# The optional root helper lives outside the user's home; refresh it only if it changed.
if [[ -f /etc/systemd/system/codex-monitor-netsplit.service ]] &&
    ! { cmp -s "$WORK/linux/netsplit/codex-netsplit.py" /usr/local/lib/codex-monitor/codex-netsplit.py &&
        cmp -s "$WORK/linux/systemd/codex-monitor-netsplit.service" /etc/systemd/system/codex-monitor-netsplit.service; }; then
    echo "==> Updating the Internet/LAN helper (asks for your password)"
    pkexec bash "$WORK/linux/install-netsplit.sh" || echo "Warning: helper not updated; run ./install-netsplit.sh later."
fi

# The widget restarts with the new code by itself; this tells it to announce the update.
echo "$TAG" > "$LOG_DIR/updated"
echo "Updated to $TAG."
