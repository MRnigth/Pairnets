#!/bin/bash
# On a real Mac: the release zip unpacks the way the app's updater unpacks it (ditto), holds the right app, and its
# signature verifies (codesign): what MacAppUpdate.Stage checks before the swap.
#   tests/macos/zip-check.sh <Pairnets-macos-arch.zip> <expected version>
set -eu
ZIP="$1"; VERSION="$2"
OUT="$(mktemp -d)"; trap 'rm -rf "$OUT"' EXIT
/usr/bin/ditto -x -k "$ZIP" "$OUT"
APP="$OUT/Pairnets.app"
test -x "$APP/Contents/MacOS/Pairnets"
ID="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$APP/Contents/Info.plist")"
GOT="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP/Contents/Info.plist")"
[ "$ID" = "app.pairnets.client" ] || { echo "wrong app id: $ID"; exit 1; }
[ "$GOT" = "$VERSION" ] || { echo "wrong version: $GOT (wanted $VERSION)"; exit 1; }
/usr/bin/codesign --verify --deep --strict "$APP"
echo "zip OK: $ID $GOT"
