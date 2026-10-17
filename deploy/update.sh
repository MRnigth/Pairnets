#!/usr/bin/env bash
# Pairnets server self-update, run as root by pairnets-update.service when the server (which has no
# root rights) creates /var/lib/pairnets/update/request after an app asked for an update.
#
# It only ever installs the newest official release from the fixed GitHub address below, checks that
# SHA256SUMS.txt is signed by the Pairnets release key (RELEASE_KEY below, = deploy/release-signing.pub),
# verifies the download against it, refuses the same or an older version, and then runs that
# release's install.sh (which keeps the token, address and data). The request file's content is
# never read. Progress goes to /var/lib/pairnets/update/status.json for the apps to show, and every
# run is logged to /var/lib/pairnets/update/update.log, which the apps show in Debug mode.
#
# Testing: PAIRNETS_BASE_URL=<folder or url> PAIRNETS_UPDATE_DIR=<dir> PAIRNETS_INSTALL_DIR=<dir>
#          PAIRNETS_RELEASE_KEY=<public key file> (a test feed's key, scripts/make-feed.sh)
#          PAIRNETS_UPDATE_DRY_RUN=1 skips running install.sh.
set -Eeuo pipefail

BASE_URL="${PAIRNETS_BASE_URL:-https://github.com/MRnigth/Pairnets/releases/latest/download}"
UPDATE_DIR="${PAIRNETS_UPDATE_DIR:-/var/lib/pairnets/update}"
INSTALL_DIR="${PAIRNETS_INSTALL_DIR:-/opt/pairnets}"
ASSET=pairnets-server-linux-x64.tar.gz
MIN_INTERVAL=600 # seconds between attempts
# The Pairnets release public key (deploy/release-signing.pub; a test checks the copies match).
RELEASE_KEY='-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEtZRM91EOrqbsMUwpOpSY2w95wiay
sYrj9VYxXFA1adeBmWwUhKl4BOHZhdhFbdNWz05lNDRi1Od1cK7Wx0zXfg==
-----END PUBLIC KEY-----'

REQUEST="$UPDATE_DIR/request"
STATUS="$UPDATE_DIR/status.json"
LAST="$UPDATE_DIR/last-attempt"
LOG="$UPDATE_DIR/update.log"
LOG_MAX=262144 # bytes; the previous log is kept as update.log.1

# The update folder belongs to the unprivileged pairnets user, who can plant symlinks in it. This script is
# root, and chmod, chown, touch and ">" all follow symlinks, so root must never write, chmod, chown or open
# for writing anything in there. Everything that touches that folder goes through as_owner, which runs it
# as pairnets: a planted link then only reaches what pairnets could reach anyway. Without a pairnets user
# (test runs) or without root, it just runs the command.
as_owner() {
  if [[ $EUID -eq 0 ]] && id pairnets >/dev/null 2>&1; then (cd / && runuser -u pairnets -- "$@")
  else "$@"; fi
}

log() { printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"; }

# Secrets never reach the log, however many share a line: the sync token (install.sh never prints it,
# but be sure), the Cloudflare tunnel token (also bare: it is base64 JSON, so it starts with eyJ),
# one-time setup and sign-in links, the Cloudflare, mail and Google credentials install.sh may handle,
# and a linked server's key and device code from Pairnets (install.sh never prints those either).
redact() {
  sed -E \
    -e 's/((token|secret|password)[[:space:]]*:[[:space:]]*)[^[:space:]]+/\1(hidden)/gI' \
    -e 's/([?&#][A-Za-z_]*(code|token|secret)=)[^[:space:]&]+/\1(hidden)/gI' \
    -e 's/([A-Za-z0-9_]*(token|secret|password|passwd|api_?key|relay_?key|nest_?key)[A-Za-z0-9_]*[[:space:]]*=[[:space:]]*)[^[:space:]]+/\1(hidden)/gI' \
    -e 's/(Bearer[[:space:]]+)[^[:space:]"]+/\1(hidden)/gI' \
    -e 's%eyJ[A-Za-z0-9+/=_.-]{20,}%(hidden)%g' \
    -e 's%psd_[A-Za-z0-9_-]{20,}%(hidden)%g'
}

json_escape() { local s=${1//\\/\\\\}; s=${s//\"/\\\"}; printf '%s' "${s//$'\n'/ }"; }

status() { # status <state> <message>
  # Written and renamed as pairnets; 0644 so the server (and anyone) can read it.
  # shellcheck disable=SC2016 # (the $ belong to the inner bash)
  printf '{"state":"%s","message":"%s","at":"%s"}\n' "$1" "$(json_escape "$2")" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" |
    as_owner bash -c 'umask 022; tmp=$1.tmp; cat > "$tmp" && chmod 0644 "$tmp" && mv -f "$tmp" "$1"' _ "$STATUS"
  log "status: $1 - $2"
}

finish() {
  as_owner rm -f "$REQUEST"
  log "=== update.sh finished"
  # Let the log writer below drain, so the last lines are not lost.
  if [[ -n "${LOG_PID:-}" ]]; then exec >&- 2>&-; wait "$LOG_PID" 2>/dev/null || true; LOG_PID=; fi
}
trap finish EXIT

# Anything that fails unexpectedly is reported to the apps instead of leaving them waiting.
on_error() {
  trap - ERR
  status failed "update.sh stopped unexpectedly at line $1 ($2). Nothing more was changed; turn on Debug mode in the app to see the update log."
}
trap 'on_error "$LINENO" "$BASH_COMMAND"' ERR

as_owner mkdir -p "$UPDATE_DIR"
# shellcheck disable=SC2016 # (the $ belong to the inner bash)
as_owner bash -c 'umask 022; log=$1; max=$2
  if [[ -f $log ]] && (( $(wc -c < "$log") > max )); then mv -f "$log" "$log.1"; fi
  touch "$log" && chmod 0644 "$log"' _ "$LOG" "$LOG_MAX"
# The log is appended by a pairnets-owned tee, never opened by root. stdout and stderr go through it.
LOG_PID=
exec > >(as_owner tee -a "$LOG" >/dev/null) 2>&1
LOG_PID=$!

installed="$(tr -d '[:space:]' < "$INSTALL_DIR/VERSION" 2>/dev/null || echo unknown)"
log "=== update.sh started: installed version $installed, source $BASE_URL"

now=$(date +%s)
if last=$(as_owner cat "$LAST" 2>/dev/null) && [[ "$last" =~ ^[0-9]+$ ]] && (( now - last < MIN_INTERVAL )); then
  log "the last attempt was $(( now - last )) s ago (minimum $MIN_INTERVAL s)"
  status failed "An update was tried less than 10 minutes ago. Try again later."
  exit 0
fi
echo "$now" | as_owner tee "$LAST" >/dev/null

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

# SHA256SUMS.txt counts only when Pairnets signed it (SHA256SUMS.txt.sig, made by the release workflow).
if ! command -v openssl >/dev/null; then
  status failed "openssl is needed to check that the update is signed by Pairnets (sudo apt install openssl). Nothing was changed."
  exit 0
fi
if [[ -n "${PAIRNETS_RELEASE_KEY:-}" ]]; then cp "$PAIRNETS_RELEASE_KEY" "$WORK/release-signing.pub"
else printf '%s\n' "$RELEASE_KEY" > "$WORK/release-signing.pub"; fi
if ! fetch SHA256SUMS.txt.sig ||
   ! openssl dgst -sha256 -verify "$WORK/release-signing.pub" -signature "$WORK/SHA256SUMS.txt.sig" "$WORK/SHA256SUMS.txt" >/dev/null 2>&1; then
  status failed "This update isn't signed by Pairnets, so it wasn't installed. Nothing was changed."
  exit 0
fi
log "signature OK"

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
  status failed "Installing $new did not finish: install.sh reported a problem (the server may not have started again). Turn on Debug mode in the app to see the update log."
fi
