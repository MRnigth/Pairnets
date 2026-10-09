#!/usr/bin/env bash
# A free name for the nest, like alice.pairnets.app, from the Pairnets name service (names/README.md). The service
# makes the nest's own Cloudflare Tunnel and its public hostname, so nothing has to be set up in Cloudflare.
#
#   sudo ./install.sh --name alice                runs "claim" for you, on the first install
#   sudo /opt/pairnets/pairnets-name.sh status    the name this nest has
#   sudo /opt/pairnets/pairnets-name.sh rotate    a new tunnel token (when the old one may have leaked)
#   sudo /opt/pairnets/pairnets-name.sh release   give the name back (the nest cannot be reached until it gets
#                                                 another name with install.sh --name or --public-url)
#
# "claim" asks for an email address (or takes --email) and for the 6-digit code the service sends to it. The tunnel
# token goes to /etc/pairnets/tunnel.env and the name's manage key to /etc/pairnets/name.env, both root only (mode
# 600). Neither is ever printed or put on a command line (every local user can read command lines).
#
# For tests only: PAIRNETS_CONF_DIR (a folder instead of /etc/pairnets; then root is not needed and no service is
# touched), PAIRNETS_NAMES_URL (the service; plain http only to this machine) and PAIRNETS_TTY (where answers are
# read from, instead of the terminal).
set -euo pipefail

CONF_DIR="${PAIRNETS_CONF_DIR:-/etc/pairnets}"
NAME_ENV=$CONF_DIR/name.env
TUNNEL_ENV=$CONF_DIR/tunnel.env
TTY="${PAIRNETS_TTY:-/dev/tty}"
DEFAULT_SERVICE=https://names.pairnets.app
TOOL=/opt/pairnets/pairnets-name.sh
TUNNEL_SERVICE=pairnets-tunnel

die() { echo "error: $*" >&2; exit 1; }

usage() {
  cat <<'USAGE'
Usage: sudo pairnets-name.sh <command>

  claim <name> [--email <address>]   get the free name <name>.pairnets.app for this nest
                                     (sudo ./install.sh --name <name> does this for you)
  status                             show the name this nest has
  rotate                             get a new tunnel token; the old one stops working
  release [--yes]                    give the name back
USAGE
}

# The same rule as the name service (names/src/names.ts) and install.sh.
valid_name() { [[ "$1" =~ ^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$ && "$1" != *--* ]]; }
NAME_RULE="a name is 3 to 32 lowercase letters, digits or hyphens, with no hyphen at the start or end and no two in a row (like alice or my-nest)"
TOKEN_RE='^[A-Za-z0-9+/=_-]{40,}$'
KEY_RE='^pnk_[A-Za-z0-9_-]{43}$'

conf_value() { # conf_value <file> <key>: the value of <key> in <file>, or nothing
  grep -m1 "^$2=" "$1" 2>/dev/null | cut -d= -f2- || true
}

WORK=""
cleanup() { if [[ -n "$WORK" ]]; then rm -rf "$WORK"; fi; }
trap cleanup EXIT

# The name service: PAIRNETS_NAMES_URL, else the one this name came from, else Pairnets' own.
service_url() {
  local url="${PAIRNETS_NAMES_URL:-}"
  [[ -n "$url" ]] || url="$(conf_value "$NAME_ENV" PAIRNETS_NAMES_URL)"
  url="${url:-$DEFAULT_SERVICE}"
  url="${url%/}"
  if [[ "$url" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ || "$url" =~ ^http://(127\.0\.0\.1|localhost)(:[0-9]+)?$ ]]; then
    printf '%s' "$url"
  else
    die "the name service must be an https address, like $DEFAULT_SERVICE (not: $url)"
  fi
}

# api <method> <path> <json> [manage key]: one call to the name service. Sets STATUS (the HTTP status, 000 when there
# was no answer) and ANSWER (the body). The JSON reaches curl on its standard input and the key through a root-only
# config file, so neither is on a command line.
api() {
  local proto="=https"
  [[ "$SERVICE" == https://* ]] || proto="=http"
  local -a args=(--silent --show-error --max-time 90 --proto "$proto" --user-agent "pairnets-name/1"
    --request "$1" --header "Content-Type: application/json" --data-binary @-
    --output "$WORK/answer" --write-out "%{http_code}")
  if [[ -n "${4:-}" ]]; then
    printf 'header = "Authorization: Bearer %s"\n' "$4" > "$WORK/auth"
    args+=(--config "$WORK/auth")
  fi
  rm -f "$WORK/answer"
  STATUS="$(printf '%s' "$3" | curl "${args[@]}" "$SERVICE$2" 2>"$WORK/curl-error")" || STATUS=000
  rm -f "$WORK/auth"
  ANSWER="$(tr -d '\r\n' < "$WORK/answer" 2>/dev/null || true)"
  [[ "$STATUS" != 000 ]] || die "no answer from the name service $SERVICE ($(head -n1 "$WORK/curl-error"))"
}

# json_field <key>: the string value of <key> in ANSWER. The service sends one line of JSON whose strings hold no
# quotes or backslashes, and every value read here is checked against its own pattern afterwards.
json_field() {
  printf '%s' "$ANSWER" | sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\([^\"\\\\]*\)\".*/\1/p"
}

# The service's message for a failed call.
answer_error() {
  local message
  message="$(json_field message)"
  printf '%s' "${message:-the name service answered $STATUS}"
}

open_tty() { # open_tty <message when there is no terminal>
  [[ -z "${TTY_OPEN:-}" ]] || return 0
  { exec 3<"$TTY"; } 2>/dev/null || die "$1"
  TTY_OPEN=1
}

ask() { # ask <prompt>: one line from the terminal into REPLY
  printf '%s ' "$1"
  IFS= read -r -u 3 REPLY || { echo; die "no answer"; }
  REPLY="${REPLY%$'\r'}"
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

# The running tunnel only picks up a new token when it restarts (never with PAIRNETS_CONF_DIR: tests).
restart_tunnel() {
  if [[ -z "${PAIRNETS_CONF_DIR:-}" && -f /etc/systemd/system/$TUNNEL_SERVICE.service ]] && command -v systemctl >/dev/null; then
    systemctl restart "$TUNNEL_SERVICE"
  fi
}

load_name() {
  [[ -f "$NAME_ENV" ]] || die "this nest has no Pairnets name (give it one with: sudo ./install.sh --name <name>)"
  NAME="$(conf_value "$NAME_ENV" PAIRNETS_NAME)"
  NAME_URL="$(conf_value "$NAME_ENV" PAIRNETS_NAME_URL)"
  KEY="$(conf_value "$NAME_ENV" PAIRNETS_NAME_KEY)"
  if ! valid_name "$NAME" || [[ ! "$KEY" =~ $KEY_RE ]]; then
    die "$NAME_ENV is damaged: it needs PAIRNETS_NAME and PAIRNETS_NAME_KEY (restore it from your copy)"
  fi
  SERVICE="$(service_url)"
}

cmd_claim() {
  local name="" email=""
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --email)
        if [[ $# -lt 2 || -z "$2" || "$2" == -* ]]; then die "--email needs a value, for example: --email you@example.com"; fi
        email="$2"; shift 2 ;;
      --email=*) email="${1#*=}"; [[ -n "$email" ]] || die "--email needs a value, for example: --email you@example.com"; shift ;;
      -*) die "unknown option: $1" ;;
      *) [[ -z "$name" ]] || die "give one name"; name="$1"; shift ;;
    esac
  done
  [[ -n "$name" ]] || die "which name? For example: claim alice"
  name="${name,,}"
  valid_name "$name" || die "$NAME_RULE"
  [[ ! -f "$NAME_ENV" ]] || die "this nest already has the name $(conf_value "$NAME_ENV" PAIRNETS_NAME_URL); give it back first with: sudo $TOOL release"
  SERVICE="$(service_url)"

  open_tty "no terminal to type the emailed code into; run this in a terminal"
  if [[ -z "$email" ]]; then
    echo "The name service sends a 6-digit code to your email address, to check that it is yours."
    ask "Your email address:"
    email="$REPLY"
  fi
  email="${email//[[:space:]]/}"
  local email_re='^[^[:space:]"\@<>,;]+@[^[:space:]"\@<>,;]+\.[^[:space:]"\@<>,;]+$'
  [[ "$email" =~ $email_re ]] || die "that does not look like an email address: $email"

  api POST /v1/claim/start "{\"name\":\"$name\",\"email\":\"$email\"}"
  [[ "$STATUS" == 202 ]] || die "$(answer_error)"
  local claim_id
  claim_id="$(json_field claim_id)"
  [[ "$claim_id" =~ ^pnc_[A-Za-z0-9_-]{24}$ ]] || die "the name service gave an answer this script does not understand"
  echo "A 6-digit code is on its way to $email (look in the spam folder too). It works for 15 minutes."
  local code
  while :; do
    ask "Code:"
    code="${REPLY//[[:space:]]/}"
    if [[ ! "$code" =~ ^[0-9]{6}$ ]]; then
      echo "The code is 6 digits; type it again."
      continue
    fi
    api POST /v1/claim "{\"claim_id\":\"$claim_id\",\"code\":\"$code\"}"
    [[ "$STATUS" == 400 && "$(json_field error)" == wrong_code ]] || break
    echo "$(answer_error) Type it again."
  done
  [[ "$STATUS" == 201 ]] || die "$(answer_error)"

  local url token key
  url="$(json_field public_url)"
  token="$(json_field tunnel_token)"
  key="$(json_field manage_key)"
  [[ "$url" =~ ^https://$name\.[a-z0-9.-]+$ ]] || die "the name service answered an address this script does not expect: $url"
  [[ "$token" =~ $TOKEN_RE ]] || die "the name service answered something that is not a tunnel token"
  [[ "$key" =~ $KEY_RE ]] || die "the name service answered something that is not a manage key"

  make_conf_dir
  # The name's file first: if anything stops after it, install.sh --name picks up from there (it asks for a new token).
  write_private "$NAME_ENV" "# Written by pairnets-name.sh: this nest's free name. Keep this file private (root only, mode 600):
# its key lets whoever has it get new tunnel tokens for the name, or give the name away.
PAIRNETS_NAME=$name
PAIRNETS_NAME_URL=$url
PAIRNETS_NAMES_URL=$SERVICE
PAIRNETS_NAME_KEY=$key
"
  write_private "$TUNNEL_ENV" "TUNNEL_TOKEN=$token
"
  echo "This nest's name is $url"
  echo "The name's key is kept in $NAME_ENV (root only). Keep a copy somewhere safe, like your password"
  echo "manager: without it the name cannot get a new tunnel token or be given back. Show it with: sudo cat $NAME_ENV"
}

cmd_status() {
  [[ $# -eq 0 ]] || die "status takes no options"
  if [[ ! -f "$NAME_ENV" ]]; then
    echo "This nest has no Pairnets name."
    return 0
  fi
  load_name
  echo "Name:     $NAME_URL"
  echo "Service:  $SERVICE"
  echo "Key:      in $NAME_ENV (root only)"
  if [[ -z "${PAIRNETS_CONF_DIR:-}" && -f /etc/systemd/system/$TUNNEL_SERVICE.service ]] && command -v systemctl >/dev/null; then
    echo "Tunnel:   $TUNNEL_SERVICE is $(systemctl is-active "$TUNNEL_SERVICE" 2>/dev/null || true) (should be: active)"
  fi
}

cmd_rotate() {
  [[ $# -eq 0 ]] || die "rotate takes no options"
  load_name
  api POST /v1/rotate "{\"name\":\"$NAME\"}" "$KEY"
  [[ "$STATUS" == 200 ]] || die "$(answer_error)"
  local token
  token="$(json_field tunnel_token)"
  [[ "$token" =~ $TOKEN_RE ]] || die "the name service answered something that is not a tunnel token"
  make_conf_dir
  write_private "$TUNNEL_ENV" "TUNNEL_TOKEN=$token
"
  restart_tunnel
  echo "The tunnel for $NAME_URL has a new token; the old one no longer works."
}

cmd_release() {
  local yes=""
  case "${1:-}" in
    --yes) yes=1 ;;
    "") ;;
    *) die "unknown option: $1" ;;
  esac
  load_name
  if [[ -z "$yes" ]]; then
    open_tty "no terminal to confirm on; run it with --yes to give the name back without asking"
    echo "Give back the name $NAME_URL? This nest can then not be reached by that name, and anyone may take it."
    ask "Type the name ($NAME) to confirm:"
    [[ "$REPLY" == "$NAME" ]] || die "nothing was changed (what you typed is not $NAME)"
  fi
  api DELETE /v1/name "{\"name\":\"$NAME\"}" "$KEY"
  case "$STATUS" in
    204|202) ;;
    401) die "$(answer_error) If the name is already gone, forget it on this server with: sudo rm $NAME_ENV $TUNNEL_ENV" ;;
    *) die "$(answer_error)" ;;
  esac
  if [[ -z "${PAIRNETS_CONF_DIR:-}" ]] && command -v systemctl >/dev/null; then
    systemctl disable --now "$TUNNEL_SERVICE" >/dev/null 2>&1 || true
  fi
  rm -f "$TUNNEL_ENV" "$NAME_ENV"
  if [[ "$STATUS" == 202 ]]; then
    echo "The name $NAME_URL is given back. Cloudflare is still finishing; it is free for others within the hour."
  else
    echo "The name $NAME_URL is given back."
  fi
  echo "To reach this nest again, give it a new name: sudo ./install.sh --name <name> (or --public-url for your own domain)."
}

[[ $# -gt 0 ]] || { usage >&2; exit 2; }
COMMAND="$1"
shift
case "$COMMAND" in
  -h|--help|help) usage; exit 0 ;;
esac
if [[ -z "${PAIRNETS_CONF_DIR:-}" && $EUID -ne 0 ]]; then die "run as root (sudo $0 $COMMAND)"; fi
command -v curl >/dev/null || die "curl is required (sudo apt install curl)"
umask 077
WORK="$(mktemp -d)"
case "$COMMAND" in
  claim) cmd_claim "$@" ;;
  status) cmd_status "$@" ;;
  rotate) cmd_rotate "$@" ;;
  release) cmd_release "$@" ;;
  *) echo "error: unknown command: $COMMAND" >&2; echo >&2; usage >&2; exit 2 ;;
esac
