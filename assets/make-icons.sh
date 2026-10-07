#!/usr/bin/env bash
# Renders assets/pairnets-logo.svg to the app icons: pairnets-icon-1024.png, pairnets-icon-256.png and pairnets.ico.
# Needs Chromium or Chrome (CHROME=/path/to/chrome) and ImageMagick (convert). Commit the generated files.
set -euo pipefail
cd "$(dirname "$0")"
CHROME="${CHROME:-chromium}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# Chrome will not shrink a window below a few hundred pixels, so draw the logo at 256 px
# on a larger page and use the device scale factor for a sharp 1024 px render; smaller sizes are scaled down from it.
sed 's/width="120" height="120"/width="256" height="256"/' pairnets-logo.svg > "$TMP/logo.svg"
printf '%s' "<!doctype html><html><body style='margin:0;background:transparent'>$(cat "$TMP/logo.svg")</body></html>" > "$TMP/logo.html"
"$CHROME" --headless --no-sandbox --disable-gpu --hide-scrollbars --default-background-color=00000000 \
  --force-device-scale-factor=4 --window-size=600,600 --screenshot="$TMP/page.png" "file://$TMP/logo.html" >/dev/null 2>&1

convert "$TMP/page.png" -crop 1024x1024+0+0 +repage "$TMP/1024.png"
cp "$TMP/1024.png" pairnets-icon-1024.png
convert "$TMP/1024.png" -filter Lanczos -resize 256x256 pairnets-icon-256.png
convert "$TMP/1024.png" -filter Lanczos -define icon:auto-resize=256,128,64,48,32,24,16 pairnets.ico
echo "wrote pairnets-icon-1024.png, pairnets-icon-256.png, pairnets.ico"
