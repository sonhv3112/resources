#!/usr/bin/env bash
# Builds GpmLite.app (self-contained, no .NET install needed) and a GpmLite.dmg into ./dist.
# Usage: scripts/package-macos.sh [osx-arm64|osx-x64]
set -euo pipefail

RID="${1:-osx-arm64}"
VERSION="0.1.0"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIST="$ROOT/dist"
APP="$DIST/GpmLite.app"
PUBLISH="$DIST/publish-$RID"

# --- Engine payloads ---
# Camoufox (default engine): the Camoufox.app and the frozen Python sidecar binary. Both are bundled so
# the installed app needs no Python and no separate download.
CAMOUFOX_APP="${CAMOUFOX_APP:-$HOME/Library/Caches/camoufox/Camoufox.app}"
SIDECAR_DIST="${SIDECAR_DIST:-$ROOT/../dist-sidecar/gpmlite-camoufox-sidecar}"

# Patched Chromium core (optional, OFF by default to keep the app small). Set INCLUDE_CHROMIUM_CORE=1 to
# also bundle it. CORE_OUT must contain Chromium.app + its component *.dylib files.
INCLUDE_CHROMIUM_CORE="${INCLUDE_CHROMIUM_CORE:-0}"
CORE_OUT="${CORE_OUT:-$ROOT/../chromium-build/chromium/src/out/GpmLite}"

rm -rf "$APP" "$PUBLISH" "$DIST/GpmLite.dmg"
mkdir -p "$DIST"

dotnet publish "$ROOT/src/GpmLite.App/GpmLite.App.csproj" -c Release -r "$RID" --self-contained true \
  -p:PublishTrimmed=false -o "$PUBLISH"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUBLISH/." "$APP/Contents/MacOS/"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>GpmLite</string>
  <key>CFBundleDisplayName</key><string>GpmLite</string>
  <key>CFBundleIdentifier</key><string>local.gpmlite.app</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleExecutable</key><string>GpmLite</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSCameraUsageDescription</key><string>Browser profiles may use the camera for websites you allow.</string>
  <key>NSMicrophoneUsageDescription</key><string>Browser profiles may use the microphone for websites you allow.</string>
</dict>
</plist>
PLIST

# --- Bundle Camoufox (default engine) ---
if [ -d "$CAMOUFOX_APP" ]; then
  echo "Bundling Camoufox from: $CAMOUFOX_APP"
  mkdir -p "$APP/Contents/Resources/camoufox"
  cp -R "$CAMOUFOX_APP" "$APP/Contents/Resources/camoufox/"
  echo "  Camoufox bundled ($(du -sh "$APP/Contents/Resources/camoufox/Camoufox.app" | cut -f1))"
else
  echo "WARNING: Camoufox not found at $CAMOUFOX_APP — run 'python -m camoufox fetch' or set CAMOUFOX_APP."
fi

# --- Bundle the frozen Camoufox sidecar (no Python needed at runtime) ---
if [ -d "$SIDECAR_DIST" ]; then
  echo "Bundling sidecar from: $SIDECAR_DIST"
  mkdir -p "$APP/Contents/Resources/sidecar"
  cp -R "$SIDECAR_DIST" "$APP/Contents/Resources/sidecar/"
  chmod +x "$APP/Contents/Resources/sidecar/gpmlite-camoufox-sidecar/gpmlite-camoufox-sidecar" 2>/dev/null || true
  echo "  sidecar bundled ($(du -sh "$APP/Contents/Resources/sidecar/gpmlite-camoufox-sidecar" | cut -f1))"
else
  echo "WARNING: frozen sidecar not found at $SIDECAR_DIST — build it with scripts/build-sidecar.sh first."
fi

# --- Bundle the patched Chromium core (optional, self-contained from a component build) ---
if [ "$INCLUDE_CHROMIUM_CORE" = "1" ] && [ -d "$CORE_OUT/Chromium.app" ]; then
  echo "Bundling patched core from: $CORE_OUT"
  CORE_DST="$APP/Contents/Resources/core"
  mkdir -p "$CORE_DST"
  cp -R "$CORE_OUT/Chromium.app" "$CORE_DST/"
  FW="$CORE_DST/Chromium.app/Contents/Frameworks"
  # Fold the loose component dylibs in (if any) so the core runs outside the build tree.
  shopt -s nullglob
  dylibs=("$CORE_OUT"/*.dylib)
  if [ ${#dylibs[@]} -gt 0 ]; then
    cp "${dylibs[@]}" "$FW"/
    echo "  folded ${#dylibs[@]} component dylibs into the core bundle"
  fi
  shopt -u nullglob
  xattr -cr "$CORE_DST/Chromium.app" 2>/dev/null || true
  codesign --force --deep --sign - "$CORE_DST/Chromium.app"
  echo "  core bundled ($(du -sh "$CORE_DST/Chromium.app" | cut -f1))"
else
  echo "Chromium core: not bundled (Camoufox is the default engine; set INCLUDE_CHROMIUM_CORE=1 to add it)."
fi

# Ad-hoc signature so Apple Silicon will run the bundle (not notarized).
codesign --force --deep --sign - "$APP"

STAGE="$DIST/dmg-stage"
rm -rf "$STAGE" && mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "GpmLite" -srcfolder "$STAGE" -ov -format UDZO "$DIST/GpmLite.dmg" >/dev/null
rm -rf "$STAGE" "$PUBLISH"

echo "Built: $APP"
echo "Built: $DIST/GpmLite.dmg ($(du -h "$DIST/GpmLite.dmg" | cut -f1))"
