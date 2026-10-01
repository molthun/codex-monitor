#!/usr/bin/env bash
# CodexMonitor installer for Linux (GNOME).
#
#   From GitHub:  curl -fsSL https://raw.githubusercontent.com/molthun/codex-monitor/main/install.sh | bash
#   From a clone: ./install.sh
#
# Runs linux/install.sh, downloading the project first when run from GitHub. Windows: install.ps1.
set -euo pipefail

REPO="molthun/codex-monitor"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" 2>/dev/null && pwd || true)"

if [[ -n "$HERE" && -x "$HERE/linux/install.sh" ]]; then
    exec "$HERE/linux/install.sh" "$@"
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Prefer the latest release; releases made before the Linux port have no linux/ folder,
# so fall back to main and mark it as newer than that release.
TAG="$(curl -fsSL -H "User-Agent: CodexMonitor-Installer" "https://api.github.com/repos/$REPO/releases/latest" 2>/dev/null \
    | python3 -c 'import json,sys; print(json.load(sys.stdin).get("tag_name", ""))' 2>/dev/null || true)"
if [[ -n "$TAG" ]]; then
    echo "==> Downloading CodexMonitor $TAG"
    curl -fsSL "https://github.com/$REPO/archive/refs/tags/$TAG.tar.gz" | tar -xz -C "$WORK" --strip-components=1
fi
if [[ ! -x "$WORK/linux/install.sh" ]]; then
    echo "==> Downloading CodexMonitor (main)"
    rm -rf "${WORK:?}"/*
    curl -fsSL "https://github.com/$REPO/archive/refs/heads/main.tar.gz" | tar -xz -C "$WORK" --strip-components=1
    TAG="${TAG:+$TAG-main}"
fi

CODEX_MONITOR_VERSION="${TAG:-unknown}" "$WORK/linux/install.sh" "$@"
