# shellcheck shell=bash disable=SC2034 # (the constants are used by the scripts that source this file)
# Shared by the Linux test-box checks in this folder (sourced, not run). See docs/PREDEPLOY.md.
# The checks run as root on a fresh systemd Ubuntu: the WSL test box (scripts/wsl-testbox.ps1) or a
# GitHub runner (with sudo). They never print the shared token, only a short hash of it.

E2E_HTTP_PORT=15075
E2E_HTTPS_PORT=15443
E2E_SMTP_PORT=15025
E2E_GOOGLE_PORT=15480
E2E_DIR=/var/lib/pairnets-e2e
E2E_TLS_DIR=$E2E_DIR/tls
E2E_MAIL_DIR=$E2E_DIR/mail
E2E_FEEDS=$E2E_DIR/feeds
INSTALL_DIR=/opt/pairnets
CONF_DIR=/etc/pairnets
ENV_FILE=$CONF_DIR/pairnets.env
DATA_DIR=/var/lib/pairnets
UPDATE_DIR=$DATA_DIR/update
UPDATE_DROPIN_DIR=/etc/systemd/system/pairnets-update.service.d
UPDATE_DROPIN=$UPDATE_DROPIN_DIR/test-feed.conf
PACKAGE=pairnets-server-linux-x64
API="http://127.0.0.1:$E2E_HTTP_PORT"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

FAILURES=0

step() { printf '\n== %s\n' "$*"; }
note() { printf '        %s\n' "$*"; }
ok() { printf '  ok    %s\n' "$*"; }
bad() { printf '  FAIL  %s\n' "$*"; FAILURES=$((FAILURES + 1)); }
die() { printf '  FAIL  %s\n' "$*"; printf '\n%s: stopped\n' "$(basename "$0")"; exit 1; }

# check <description> <command...>: runs the command quietly, reports ok or FAIL.
check() {
  local what="$1"; shift
  if "$@" >/dev/null 2>&1; then ok "$what"; else bad "$what"; fi
}

# expect <description> <actual> <expected>
expect() {
  if [[ "$2" == "$3" ]]; then ok "$1"; else bad "$1 (got: ${2:-nothing}, expected: $3)"; fi
}

finish() {
  echo
  if (( FAILURES > 0 )); then
    echo "$(basename "$0"): $FAILURES check(s) failed"
    exit 1
  fi
  echo "$(basename "$0"): all checks passed"
}

need_root() { [[ $EUID -eq 0 ]] || die "run as root (sudo $0)"; }

# The value of a key in the server's env file (root only).
env_value() { sed -n "s/^$1=//p" "$ENV_FILE" 2>/dev/null | head -n1; }

token() { env_value SYNC_TOKEN; }

# A short fingerprint of the shared token, safe to print and compare.
token_hash() { token | sha256sum | cut -c1-16; }

# Hides the shared token (and anything that looks like one) in text on stdin.
redact() {
  local t
  t="$(token 2>/dev/null || true)"
  if [[ -n "$t" ]]; then
    sed -E -e "s/${t}/(token hidden)/g" -e 's/(SYNC_TOKEN=)[^[:space:]]+/\1(hidden)/g'
  else
    sed -E 's/(SYNC_TOKEN=)[^[:space:]]+/\1(hidden)/g'
  fi
}

# Replaces or adds KEY=VALUE in the env file; it stays root-owned, mode 600.
set_env() {
  local tmp
  tmp="$(mktemp "$ENV_FILE.XXXXXX")"
  { grep -v "^$1=" "$ENV_FILE" || true; printf '%s=%s\n' "$1" "$2"; } > "$tmp"
  chown root:root "$tmp"
  chmod 600 "$tmp"
  mv "$tmp" "$ENV_FILE"
}

# The version inside a feed's package.
feed_version() { tar -xOzf "$1/$PACKAGE.tar.gz" "$PACKAGE/VERSION" | tr -d '[:space:]'; }

installed_version() { tr -d '[:space:]' < "$INSTALL_DIR/VERSION" 2>/dev/null || true; }

# version_newer <a> <b>: true when a is newer than b (the comparison update.sh makes).
version_newer() { [[ "$1" != "$2" && "$(printf '%s\n%s\n' "$2" "$1" | sort -V | tail -n1)" == "$1" ]]; }

check_feed() {
  [[ -f "$1/$PACKAGE.tar.gz" && -f "$1/SHA256SUMS.txt" ]] || die "$1 is not a feed (needs $PACKAGE.tar.gz and SHA256SUMS.txt; see scripts/make-feed.sh)"
}

# Copies a feed to a folder only root can change, so the root updater never reads from a user's checkout.
stage_feed() { # stage_feed <feed> <name> -> prints the staged folder
  local dest="$E2E_FEEDS/$2"
  rm -rf "$dest"
  install -d -m 0755 "$E2E_FEEDS"
  cp -R "$1" "$dest"
  chown -R root:root "$dest"
  chmod -R go-w "$dest"
  printf '%s' "$dest"
}

# wait_http <url> <seconds> [curl options...]: true once the address answers with a 2xx.
wait_http() {
  local url="$1" limit="$2" start=$SECONDS
  shift 2
  while (( SECONDS - start < limit )); do
    if curl -fsS --max-time 5 "$@" "$url" >/dev/null 2>&1; then return 0; fi
    sleep 1
  done
  return 1
}

# wait_port <port> <seconds>: true once something listens on 127.0.0.1:<port>.
wait_port() {
  local limit="$2" start=$SECONDS
  while (( SECONDS - start < limit )); do
    if (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; then return 0; fi
    sleep 0.5
  done
  return 1
}

health_ok() { [[ "$(curl -fsS --max-time 10 "$API/api/health" 2>/dev/null)" == ok ]]; }

# api <method> <path-and-query> [curl options...]: the plain-HTTP API with the shared token (sent from a
# file descriptor, so it never shows in the process list). Prints the body; fails on a non-2xx answer.
api() {
  local method="$1" path="$2"
  shift 2
  curl -fsS --max-time 60 -X "$method" -H @<(printf 'X-Sync-Token: %s\n' "$(token)") "$@" "$API$path"
}

# api_status <method> <path> [curl options...]: only the HTTP status code.
api_status() {
  local method="$1" path="$2"
  shift 2
  curl -sS --max-time 60 -o /dev/null -w '%{http_code}' -X "$method" -H @<(printf 'X-Sync-Token: %s\n' "$(token)") "$@" "$API$path" || true
}

urlencode() { python3 -c 'import sys, urllib.parse; print(urllib.parse.quote(sys.argv[1], safe=""))' "$1"; }

# put_file <path in the nest> <local file>: a new file (base=none, as the apps send for a file the nest
# does not have yet).
put_file() { api PUT "/api/file?path=$(urlencode "$1")&base=none" --data-binary "@$2" -H 'Content-Type: application/octet-stream' >/dev/null; }

# get_file_hash <path in the nest>: SHA-256 of the file as the server sends it.
get_file_hash() { api GET "/api/file?path=$(urlencode "$1")" | sha256sum | cut -d' ' -f1; }

# JSON field from stdin: json_field <name>
json_field() { python3 -c 'import json, sys; v = json.load(sys.stdin).get(sys.argv[1]); print("" if v is None else v)' "$1"; }

# A mark in the journal (the cursor of its newest entry): later checks look only at what came after it.
journal_mark() { journalctl -q -n 1 -o export 2>/dev/null | sed -n 's/^__CURSOR=//p' | head -n1; }

# Warnings and errors the Pairnets units logged after a journal mark.
journal_problems() {
  if [[ -n "$1" ]]; then
    journalctl --no-pager -q -o short-iso -p warning --after-cursor "$1" -u 'pairnets-*' 2>/dev/null || true
  else
    journalctl --no-pager -q -o short-iso -p warning -u 'pairnets-*' 2>/dev/null || true
  fi
}

check_journal_clean() { # check_journal_clean <journal mark> <description> [<regex of known, harmless lines>]
  local problems ignored=""
  problems="$(journal_problems "$1" | redact)"
  if [[ -n "${3:-}" && -n "$problems" ]]; then
    ignored="$(grep -E -- "$3" <<<"$problems" || true)"
    problems="$(grep -Ev -- "$3" <<<"$problems" || true)"
  fi
  [[ -z "$ignored" ]] || note "known and harmless: $(head -n1 <<<"$ignored" | sed -E 's/^[^]]*\]: //')"
  if [[ -z "$problems" ]]; then
    ok "$2"
  else
    bad "$2:"
    printf '%s\n' "$problems" | head -n 30 | sed 's/^/          /'
  fi
}

# mode, owner and group of a path, e.g. "755 root root"
perms() { stat -c '%a %U %G' "$1" 2>/dev/null || echo missing; }

unit_state() { printf '%s/%s' "$(systemctl is-enabled "$1" 2>/dev/null || true)" "$(systemctl is-active "$1" 2>/dev/null || true)"; }

# status.json as "state|message", or nothing.
update_status() {
  [[ -f "$UPDATE_DIR/status.json" ]] || return 0
  python3 -c 'import json, sys; d = json.load(open(sys.argv[1])); print("%s|%s" % (d.get("state", ""), d.get("message", "")))' "$UPDATE_DIR/status.json" 2>/dev/null || true
}

# Waits until the updater finished a run that started after <since> (unix seconds). Prints the final status.
wait_update_done() { # wait_update_done <seconds>
  local limit="$1" start=$SECONDS s
  while (( SECONDS - start < limit )); do
    s="$(update_status)"
    if [[ ! -e "$UPDATE_DIR/request" && "$s" != running\|* && -n "$s" ]] && ! systemctl is-active --quiet pairnets-update.service; then
      printf '%s' "$s"
      return 0
    fi
    sleep 2
  done
  printf '%s' "$(update_status)"
  return 1
}

# Points the updater at a local feed (a test-only drop-in; the unit itself stays as installed).
point_updater_at() { # point_updater_at <staged feed>
  install -d -m 0755 "$UPDATE_DROPIN_DIR"
  cat > "$UPDATE_DROPIN" <<CONF
# Pre-deploy test only (tests/linux-server): install updates from a local feed instead of GitHub.
[Service]
Environment=PAIRNETS_BASE_URL=$1
CONF
  chmod 0644 "$UPDATE_DROPIN"
  systemctl daemon-reload
}

# Asks for an update like an app does (POST /api/update with the shared token); when the API refuses
# (shared token turned off, asked less than a minute ago), drops the request file the way the server does.
request_update() {
  rm -f "$UPDATE_DIR/last-attempt" # the updater's 10-minute pause between attempts is not under test here
  local code
  code="$(api_status POST /api/update)"
  if [[ "$code" == 202 ]]; then
    note "POST /api/update answered 202"
  else
    note "POST /api/update answered ${code:-nothing}; dropping the request file instead"
    install -o pairnets -g pairnets -m 0640 /dev/null "$UPDATE_DIR/request"
  fi
}

# Fingerprint of the synced files: every path with its content hash.
files_fingerprint() {
  if [[ -d "$DATA_DIR/files" ]]; then
    (cd "$DATA_DIR/files" && find . -type f -print0 | sort -z | xargs -0 -r sha256sum) | sha256sum | cut -c1-16
  else
    echo none
  fi
}
