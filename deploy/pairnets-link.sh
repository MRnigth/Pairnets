#!/usr/bin/env bash
# Links this server (the nest) to a Pairnets account, so the owner's computers reach it through
# https://sync.pairnets.app, with no domain and nothing to set up in Cloudflare (cloud/RELAY.md, section 2).
# install.sh runs it on a first install without options, and with --link:
#
#   sudo ./install.sh            (first install)
#   sudo ./install.sh --link     (link an installed server again)
#
# It prints a link and a code. Open the link on any device (a phone is fine), sign in to your Pairnets account,
# check the code and press "Add this nest". Nothing is typed into this terminal. Then it writes the tunnel token
# Pairnets made for this server to /etc/pairnets/tunnel.env, and the server's id and key at Pairnets to
# /etc/pairnets/relay.env (install.sh moves those into pairnets.env), both root only (mode 600). The device code,
# the tunnel token and the key are never printed or put on a command line (every local user can read command lines).
#
# For tests only: PAIRNETS_CONF_DIR (a folder instead of /etc/pairnets; then root is not needed) and
# PAIRNETS_SERVICE_URL (the service; plain http only to this machine).
set -euo pipefail

CONF_DIR="${PAIRNETS_CONF_DIR:-/etc/pairnets}"
TUNNEL_ENV=$CONF_DIR/tunnel.env
RELAY_ENV=$CONF_DIR/relay.env
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEFAULT_SERVICE=https://sync.pairnets.app

# The shapes of what the service sends (cloud/RELAY.md, section 1). Anything else is never written anywhere.
DEVICE_CODE_RE='^psd_[A-Za-z0-9_-]{43}$'
USER_CODE_RE='^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$'
NEST_ID_RE='^nst_[0-9a-hjkmnp-tv-z]{26}$'
# 32 bytes in base64url: 43 characters, the last one with its 2 unused bits zero.
NEST_KEY_RE='^[A-Za-z0-9_-]{42}[AEIMQUYcgkosw048]$'
TOKEN_RE='^[A-Za-z0-9+/=_-]{40,}$'
LINK_RE='^[A-Za-z0-9:/.?=&_-]+$'

die() { echo "error: $*" >&2; exit 1; }

usage() {
  cat <<'USAGE'
Usage: sudo pairnets-link.sh

Links this server to your Pairnets account (install.sh runs it for you: on a first install, or with --link).
It prints a link to open on any device; sign in there and press "Add this nest".
USAGE
}

WORK=""
cleanup() { if [[ -n "$WORK" ]]; then rm -rf "$WORK"; fi; }
trap cleanup EXIT

service_url() {
  local url="${PAIRNETS_SERVICE_URL:-$DEFAULT_SERVICE}"
  url="${url%/}"
  if [[ "$url" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ || "$url" =~ ^http://(127\.0\.0\.1|localhost)(:[0-9]+)?$ ]]; then
    printf '%s' "$url"
  else
    die "the Pairnets service must be an https address, like $DEFAULT_SERVICE (not: $url)"
  fi
}

# api <path> <json>: one POST to the service. Sets STATUS (the HTTP status, 000 when there was no answer) and ANSWER
# (the body). The JSON reaches curl on its standard input, so nothing in it is on a command line, and the answer file
# is deleted as soon as it is read.
api() {
  local proto="=https"
  [[ "$SERVICE" == https://* ]] || proto="=http"
  rm -f "$WORK/answer"
  STATUS="$(printf '%s' "$2" | curl --silent --show-error --max-time 30 --proto "$proto" --user-agent "pairnets-link/1" \
    --request POST --header "Content-Type: application/json" --data-binary @- \
    --output "$WORK/answer" --write-out "%{http_code}" "$SERVICE$1" 2>"$WORK/curl-error")" || STATUS=000
  ANSWER="$({ tr -d '\r\n' < "$WORK/answer"; } 2>/dev/null || true)"
  rm -f "$WORK/answer"
}

# json_field <key>: the string value of <key> in ANSWER. The service sends one line of JSON whose strings hold no
# quotes or backslashes, and every value read here is checked against its own pattern afterwards.
json_field() {
  printf '%s' "$ANSWER" | sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\([^\"\\\\]*\)\".*/\1/p"
}

# json_number <key>: the whole-number value of <key> in ANSWER, or nothing.
json_number() {
  printf '%s' "$ANSWER" | sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\([0-9][0-9]*\).*/\1/p"
}

# The service's message for a failed call, or what is known about it.
answer_error() {
  local message code
  message="$(json_field message)"
  code="$(json_field error)"
  if [[ -n "$message" ]]; then
    printf '%s' "$message"
  elif [[ -n "$code" ]]; then
    printf 'Pairnets answered %s (%s)' "$STATUS" "$code"
  else
    printf 'Pairnets answered %s' "$STATUS"
  fi
}

make_conf_dir() {
  mkdir -p "$CONF_DIR"
  chmod 700 "$CONF_DIR"
  if [[ $EUID -eq 0 ]]; then chown root:root "$CONF_DIR"; fi
}

write_private() { # write_private <file> <content>: replaces <file>, mode 600 (root's when run as root)
  ( umask 077; printf '%s' "$2" > "$1.tmp" )
  chmod 600 "$1.tmp"
  if [[ $EUID -eq 0 ]]; then chown root:root "$1.tmp"; fi
  mv "$1.tmp" "$1"
}

# What the approval page shows about this server: its short host name and the Pairnets version being installed.
server_facts() {
  local host version
  host="$(uname -n 2>/dev/null || true)"
  host="${host%%.*}"
  host="$(printf '%s' "$host" | tr -cd 'A-Za-z0-9_-' | cut -c1-64)"
  version="$({ tr -d '[:space:]' < "$SCRIPT_DIR/VERSION"; } 2>/dev/null || true)"
  [[ "$version" =~ ^[0-9]+(\.[0-9]+){0,3}$ ]] || version=""
  printf '{"hostname":"%s"' "${host:-server}"
  [[ -z "$version" ]] || printf ',"serverVersion":"%s"' "$version"
  printf '}'
}

link() {
  SERVICE="$(service_url)"

  # 1. Ask for a code.
  api /v1/servers/start "$(server_facts)"
  [[ "$STATUS" != 000 ]] || die "no answer from Pairnets at $SERVICE ($(head -n1 "$WORK/curl-error")). Check that this server is online, then run this again."
  [[ "$STATUS" == 200 ]] || die "$(answer_error)"
  local device_code user_code link expires interval
  device_code="$(json_field deviceCode)"
  user_code="$(json_field userCode)"
  link="$(json_field verificationUriComplete)"
  expires="$(json_number expiresIn)"
  interval="$(json_number interval)"
  if [[ ! "$device_code" =~ $DEVICE_CODE_RE || ! "$user_code" =~ $USER_CODE_RE ]]; then
    die "Pairnets gave an answer this script does not understand"
  fi
  # Only a link on the service itself is shown; anything else is replaced by the service's own page for the code.
  if [[ "$link" != "$SERVICE/"* || ! "$link" =~ $LINK_RE ]]; then
    link="$SERVICE/add?code=$user_code"
  fi
  [[ "$expires" =~ ^[0-9]+$ && "$expires" -ge 1 && "$expires" -le 3600 ]] || expires=900
  [[ "$interval" =~ ^[0-9]+$ && "$interval" -ge 1 && "$interval" -le 60 ]] || interval=3

  # 2. The person approves it on any device.
  echo
  echo "Open this link on any device and sign in to your Pairnets account:"
  echo
  echo "    $link"
  echo
  echo "Then check that the page shows the code $user_code and press \"Add this nest\"."
  local minutes=$(( (expires + 59) / 60 )) unit=minutes
  (( minutes != 1 )) || unit=minute
  echo "Waiting for you... (the link works for $minutes $unit; Ctrl+C stops)"

  # 3. Wait for the answer, as often as the service allows, until the code expires.
  local deadline=$(( SECONDS + expires )) offline=""
  while :; do
    (( SECONDS + interval <= deadline )) || die "the link expired before this computer was added. Nothing was changed; run the installer again for a new link."
    sleep "$interval"
    api /v1/servers/poll "{\"deviceCode\":\"$device_code\"}"
    case "$STATUS" in
      200)
        case "$(json_field status)" in
          pending) ;;
          approved) break ;;
          denied) die "this computer was not added (\"Not mine\" was pressed on the page). Nothing was changed; run the installer again to try once more." ;;
          *) die "Pairnets gave an answer this script does not understand" ;;
        esac ;;
      429)
        # slow_down: asked too often. Wait 5 seconds longer from now on (as in RFC 8628).
        interval=$(( interval + 5 )) ;;
      400)
        [[ "$(json_field error)" != expired ]] || die "the link expired before this computer was added. Nothing was changed; run the installer again for a new link."
        die "$(answer_error)" ;;
      000|5[0-9][0-9])
        # No answer for a moment (the network, or the service is busy): keep trying until the code expires.
        if [[ -z "$offline" ]]; then
          echo "(No answer from Pairnets just now; still trying.)"
          offline=1
        fi ;;
      *) die "$(answer_error)" ;;
    esac
  done

  # 4. Approved: the tunnel token and the key arrive once. Check their shapes, then keep them in root-only files.
  local nest_id token key service label
  nest_id="$(json_field nestId)"
  token="$(json_field tunnelToken)"
  key="$(json_field nestKey)"
  service="$(json_field serviceUrl)"
  label="$(json_field label | tr -cd '[:alnum:] ._()-' | cut -c1-64)"
  ANSWER=""
  [[ "$nest_id" =~ $NEST_ID_RE ]] || die "Pairnets answered a server id this script does not understand"
  [[ "$token" =~ $TOKEN_RE ]] || die "Pairnets answered something that is not a tunnel token"
  [[ "$key" =~ $NEST_KEY_RE ]] || die "Pairnets answered something that is not a server key"
  # The service names its own address; only an https one (or the one used here) is kept.
  if [[ "$service" != "$SERVICE" && ! "$service" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ ]]; then
    service="$SERVICE"
  fi

  make_conf_dir
  # The tunnel token first: relay.env is what tells install.sh the link is complete.
  write_private "$TUNNEL_ENV" "TUNNEL_TOKEN=$token
"
  write_private "$RELAY_ENV" "# Written by pairnets-link.sh: this server's link to your Pairnets account. Keep it private (root only,
# mode 600): the key lets Pairnets add and remove this server's computers. install.sh moves it into pairnets.env.
Sync__RelayNestId=$nest_id
Sync__RelayKey=$key
Sync__RelayServiceUrl=$service
"
  echo "Added to your Pairnets account${label:+ as \"$label\"}."
}

case "${1:-}" in
  -h|--help|help) usage; exit 0 ;;
  "") ;;
  *) echo "error: unknown option: $1" >&2; echo >&2; usage >&2; exit 2 ;;
esac
if [[ -z "${PAIRNETS_CONF_DIR:-}" && $EUID -ne 0 ]]; then die "run as root (sudo $0)"; fi
command -v curl >/dev/null || die "curl is required (sudo apt install curl)"
umask 077
WORK="$(mktemp -d)"
link
