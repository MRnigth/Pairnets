#!/usr/bin/env bash
# Tether server self-update, run as root by tether-update.service when the server (which has no
# root rights) creates /var/lib/tether/update/request after an app asked for an update.
#
# It only ever installs the newest official release from the fixed GitHub address below,
# verifies it against SHA256SUMS.txt, refuses the same or an older version, and then runs that
# release's install.sh (which keeps the token, address and data). The request file's content is
# never read. Progress goes to /var/lib/tether/update/status.json for the apps to show.
#
# Testing: TETHER_BASE_URL=<folder or url> TETHER_UPDATE_DIR=<dir> TETHER_INSTALL_DIR=<dir>
#          TETHER_UPDATE_DRY_RUN=1 skips running install.sh.
set -euo pipefail

BASE_URL="${TETHER_BASE_URL:-https://github.com/MRnigth/Tether/releases/latest/download}"
UPDATE_DIR="${TETHER_UPDATE_DIR:-/var/lib/tether/update}"
INSTALL_DIR="${TETHER_INSTALL_DIR:-/opt/tether}"
ASSET=tether-server-linux-x64.tar.gz
MIN_INTERVAL=600 # seconds between attempts

REQUEST="$UPDATE_DIR/request"
STATUS="$UPDATE_DIR/status.json"
LAST="$UPDATE_DIR/last-attempt"

json_escape() { local s=${1//\\/\\\\}; s=${s//\"/\\\"}; printf '%s' "${s//$'\n'/ }"; }

status() { # status <state> <message>
  local tmp="$STATUS.tmp"
  printf '{"state":"%s","message":"%s","at":"%s"}\n' "$1" "$(json_escape "$2")" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$tmp"
  chmod 0644 "$tmp"
  if id tether >/dev/null 2>&1; then chown tether:tether "$tmp" 2>/dev/null || true; fi
  mv -f "$tmp" "$STATUS"
}

finish() { rm -f "$REQUEST"; }
trap finish EXIT

mkdir -p "$UPDATE_DIR"

now=$(date +%s)
if [[ -f "$LAST" ]] && last=$(cat "$LAST" 2>/dev/null) && [[ "$last" =~ ^[0-9]+$ ]] && (( now - last < MIN_INTERVAL )); then
  status failed "An update was tried less than 10 minutes ago. Try again later."
  exit 0
fi
echo "$now" > "$LAST"

status running "Downloading the newest release"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"; finish' EXIT

fetch() {
  if [[ -d "$BASE_URL" ]]; then cp "$BASE_URL/$1" "$WORK/$1"
  else curl -fsSL --retry 3 -o "$WORK/$1" "$BASE_URL/$1"; fi
}

if ! fetch SHA256SUMS.txt || ! fetch "$ASSET"; then
  status failed "Could not download the release. Is the repository public and the server online?"
  exit 0
fi

line="$(grep -E "[[:space:]]\*?$ASSET\$" "$WORK/SHA256SUMS.txt" || true)"
if [[ -z "$line" ]] || ! (cd "$WORK" && echo "$line" | sha256sum --check --status); then
  status failed "The download did not match its checksum. Nothing was changed."
  exit 0
fi

tar -xzf "$WORK/$ASSET" -C "$WORK"
SRC="$WORK/tether-server-linux-x64"
new="$(tr -d '[:space:]' < "$SRC/VERSION" 2>/dev/null || true)"
old="$(tr -d '[:space:]' < "$INSTALL_DIR/VERSION" 2>/dev/null || echo 0)"
if [[ ! "$new" =~ ^[0-9]+(\.[0-9]+)*$ ]]; then
  status failed "The release has no valid version number. Nothing was changed."
  exit 0
fi
if [[ "$new" == "$old" ]] || [[ "$(printf '%s\n%s\n' "$old" "$new" | sort -V | tail -n1)" != "$new" ]]; then
  status succeeded "Already up to date ($old)."
  exit 0
fi

status running "Installing $new"
if [[ -n "${TETHER_UPDATE_DRY_RUN:-}" ]]; then
  status succeeded "Would install $new (dry run)."
  exit 0
fi
if "$SRC/install.sh" >"$WORK/install.log" 2>&1; then
  status succeeded "Updated from $old to $new."
else
  status failed "install.sh failed; see: sudo journalctl -u tether-update -n 50"
  cat "$WORK/install.log" >&2
fi
