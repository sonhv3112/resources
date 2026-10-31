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
