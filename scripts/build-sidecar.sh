#!/usr/bin/env bash
# Freezes the Camoufox sidecar into a standalone binary (no Python needed at runtime).
# Output: <repo>/dist-sidecar/gpmlite-camoufox-sidecar/  (bundled into the app by package-macos.sh)
#
# Requires a Python venv with: pip install "camoufox[geoip]" pyinstaller
# Set VENV to that venv (default: <repo>/.camoufox-venv).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"          # gpm-lite/
REPO="$(cd "$ROOT/.." && pwd)"                      # repo root (holds dist-sidecar/)
VENV="${VENV:-$REPO/.camoufox-venv}"
PY="$VENV/bin/python"
[ -x "$PY" ] || { echo "No venv python at $PY. Create it + pip install 'camoufox[geoip]' pyinstaller."; exit 1; }

"$PY" -m PyInstaller --noconfirm --clean \
  --name gpmlite-camoufox-sidecar \
  --distpath "$REPO/dist-sidecar" --workpath "$REPO/build/sidecar-build" --specpath "$REPO/build/sidecar-build" \
  --collect-all camoufox --collect-all browserforge --collect-all apify_fingerprint_datapoints \
  --collect-all language_tags --collect-submodules playwright \
  "$ROOT/sidecar/camoufox_sidecar.py"

echo "Frozen sidecar: $REPO/dist-sidecar/gpmlite-camoufox-sidecar"
