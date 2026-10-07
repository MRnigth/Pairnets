#!/usr/bin/env bash
# Pairnets server self-update, run as root by pairnets-update.service when the server (which has no
# root rights) creates /var/lib/pairnets/update/request after an app asked for an update.
#
# It only ever installs the newest official release from the fixed GitHub address below,
# verifies it against SHA256SUMS.txt, refuses the same or an older version, and then runs that
# release's install.sh (which keeps the token, address and data). The request file's content is
# never read. Progress goes to /var/lib/pairnets/update/status.json for the apps to show, and every
# run is logged to /var/lib/pairnets/update/update.log, which the apps show in Debug mode.
#
# Testing: PAIRNETS_BASE_URL=<folder or url> PAIRNETS_UPDATE_DIR=<dir> PAIRNETS_INSTALL_DIR=<dir>
#          PAIRNETS_UPDATE_DRY_RUN=1 skips running install.sh.
set -Eeuo pipefail

BASE_URL="${PAIRNETS_BASE_URL:-https://github.com/MRnigth/Pairnets/releases/latest/download}"
UPDATE_DIR="${PAIRNETS_UPDATE_DIR:-/var/lib/pairnets/update}"
INSTALL_DIR="${PAIRNETS_INSTALL_DIR:-/opt/pairnets}"
ASSET=pairnets-server-linux-x64.tar.gz
MIN_INTERVAL=600 # seconds between attempts

REQUEST="$UPDATE_DIR/request"
STATUS="$UPDATE_DIR/status.json"
LAST="$UPDATE_DIR/last-attempt"
LOG="$UPDATE_DIR/update.log"
LOG_MAX=262144 # bytes; the previous log is kept as update.log.1

log() { printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"; }

# Secrets never reach the log: the sync token (install.sh prints it only on a first install, but be
# sure), setup links, and the Cloudflare, mail and Google credentials install.sh may handle.
redact() {
  sed -E 's/(Token:[[:space:]]*)[^[:space:]]+/\1(hidden)/I; s/(SYNC_TOKEN=)[^[:space:]]+/\1(hidden)/; s/(api_token[[:space:]]*=[[:space:]]*)[^[:space:]]+/\1(hidden)/I;
    s/(CLOUDFLARE_API_TOKEN=)[^[:space:]]+/\1(hidden)/; s/(Bearer[[:space:]]+)[^[:space:]"]+/\1(hidden)/I; s/((SECRET|PASSWORD|API_KEY)[A-Za-z_]*=)[^[:space:]]+/\1(hidden)/I;
    s/([?&#](code|token)=)[^[:space:]&]+/\1(hidden)/I'
}

json_escape() { local s=${1//\\/\\\\}; s=${s//\"/\\\"}; printf '%s' "${s//$'\n'/ }"; }

status() { # status <state> <message>
  local tmp="$STATUS.tmp"
  printf '{"state":"%s","message":"%s","at":"%s"}\n' "$1" "$(json_escape "$2")" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$tmp"
  chmod 0644 "$tmp"
  if id pairnets >/dev/null 2>&1; then chown pairnets:pairnets "$tmp" 2>/dev/null || true; fi
  mv -f "$tmp" "$STATUS"
  log "status: $1 - $2"
}

finish() {
  rm -f "$REQUEST"
  log "=== update.sh finished"
}
trap finish EXIT

# Anything that fails unexpectedly is reported to the apps instead of leaving them waiting.
on_error() {
  trap - ERR
  status failed "update.sh stopped unexpectedly at line $1 ($2). Nothing more was changed; turn on Debug mode in the app to see the update log."
}
trap 'on_error "$LINENO" "$BASH_COMMAND"' ERR

mkdir -p "$UPDATE_DIR"
if [[ -f "$LOG" ]] && (( $(wc -c < "$LOG") > LOG_MAX )); then mv -f "$LOG" "$LOG.1"; fi
touch "$LOG"
chmod 0644 "$LOG"
if id pairnets >/dev/null 2>&1; then chown pairnets:pairnets "$LOG" 2>/dev/null || true; fi
exec >>"$LOG" 2>&1

installed="$(tr -d '[:space:]' < "$INSTALL_DIR/VERSION" 2>/dev/null || echo unknown)"
log "=== update.sh started: installed version $installed, source $BASE_URL"

now=$(date +%s)
if [[ -f "$LAST" ]] && last=$(cat "$LAST" 2>/dev/null) && [[ "$last" =~ ^[0-9]+$ ]] && (( now - last < MIN_INTERVAL )); then
  log "the last attempt was $(( now - last )) s ago (minimum $MIN_INTERVAL s)"
  status failed "An update was tried less than 10 minutes ago. Try again later."
  exit 0
fi
echo "$now" > "$LAST"

status running "Downloading the newest release"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"; finish' EXIT

fetch() {
  log "downloading $BASE_URL/$1"
  if [[ -d "$BASE_URL" ]]; then cp "$BASE_URL/$1" "$WORK/$1" || return 1
  else curl -fsSL --retry 3 --connect-timeout 30 -o "$WORK/$1" "$BASE_URL/$1" || return 1; fi
  log "downloaded $1 ($(wc -c < "$WORK/$1") bytes)"
}

if ! fetch SHA256SUMS.txt || ! fetch "$ASSET"; then
  status failed "Could not download the release. Is the repository public and the server online?"
  exit 0
fi

line="$(grep -E "[[:space:]]\*?$ASSET\$" "$WORK/SHA256SUMS.txt" || true)"
if [[ -z "$line" ]] || ! (cd "$WORK" && echo "$line" | sha256sum --check --status); then
  log "checksum line: ${line:-<missing>}"
  status failed "The download did not match its checksum. Nothing was changed."
  exit 0
fi
log "checksum OK"

tar -xzf "$WORK/$ASSET" -C "$WORK"
SRC="$WORK/pairnets-server-linux-x64"
new="$(tr -d '[:space:]' < "$SRC/VERSION" 2>/dev/null || true)"
old="$(tr -d '[:space:]' < "$INSTALL_DIR/VERSION" 2>/dev/null || echo 0)"
log "installed version $old, newest release ${new:-<none>}"
if [[ ! "$new" =~ ^[0-9]+(\.[0-9]+)*$ ]]; then
  status failed "The release has no valid version number. Nothing was changed."
  exit 0
fi
if [[ "$new" == "$old" ]] || [[ "$(printf '%s\n%s\n' "$old" "$new" | sort -V | tail -n1)" != "$new" ]]; then
  status succeeded "Already up to date ($old)."
  exit 0
fi

status running "Installing $new"
if [[ -n "${PAIRNETS_UPDATE_DRY_RUN:-}" ]]; then
  status succeeded "Would install $new (dry run)."
  exit 0
fi
log "running install.sh from the new release"
if "$SRC/install.sh" 2>&1 | redact; then
  status succeeded "Updated from $old to $new."
else
  status failed "install.sh failed. Nothing more was changed; turn on Debug mode in the app to see the update log."
fi
