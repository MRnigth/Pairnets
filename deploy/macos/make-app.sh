#!/usr/bin/env bash
# Builds Pairnets.app from a `dotnet publish` output folder, then a .zip and a .dmg.
#   deploy/macos/make-app.sh <publish-dir> <version> <arch: arm64|x64> <out-dir>
# Runs on macOS (uses sips, iconutil, codesign, ditto, hdiutil). The zip and dmg keep their
# Tether-macos-* names because the apps look for those; release.yml adds Pairnets-macos-* copies.
set -euo pipefail
PUB="$1"; VERSION="$2"; ARCH="$3"; OUT="$4"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
APP="$OUT/Pairnets.app"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$PUB"/. "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/Pairnets"

# Icon: render all iconset sizes from the 1024px master.
ICONSET="$(mktemp -d)/Pairnets.iconset"
mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s "$ROOT/assets/pairnets-icon-1024.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$ROOT/assets/pairnets-icon-1024.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Pairnets.icns"

sed -e "s/__VERSION__/$VERSION/g" "$ROOT/deploy/macos/Info.plist" > "$APP/Contents/Info.plist"

# Ad-hoc signature: required for Apple Silicon to run the binary at all. Not notarized.
codesign --force --deep --sign - "$APP"

ZIP="$OUT/Pairnets-macos-$ARCH.zip"
rm -f "$ZIP"
ditto -c -k --keepParent "$APP" "$ZIP"

STAGE="$(mktemp -d)"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
DMG="$OUT/Pairnets-macos-$ARCH.dmg"
rm -f "$DMG"
hdiutil create -volname "Pairnets" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$APP"
echo "built $ZIP and $DMG"
