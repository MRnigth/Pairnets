#!/usr/bin/env bash
# Linux test box, phase A (docs/PREDEPLOY.md): installs the server the real way, with deploy/get.sh from a
# feed folder (scripts/make-feed.sh), and checks what the installer made. Then runs install.sh again, as
# an upgrade does, and checks that nothing was lost. Run as root on a fresh systemd Ubuntu:
#
#   sudo tests/linux-server/install-check.sh --feed <feed-dir>
set -euo pipefail
# shellcheck source=tests/linux-server/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

FEED=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --feed) FEED="${2:-}"; shift 2 ;;
    *) echo "usage: $0 --feed <feed-dir>" >&2; exit 2 ;;
  esac
done
[[ -n "$FEED" ]] || { echo "usage: $0 --feed <feed-dir>" >&2; exit 2; }
need_root
check_feed "$FEED"
FEED="$(cd "$FEED" && pwd)"
[[ ! -e "$CONF_DIR" && ! -e "$INSTALL_DIR" ]] || die "Pairnets is already installed here; this check needs a fresh machine"
EXPECTED_VERSION="$(feed_version "$FEED")"

step "install $EXPECTED_VERSION with get.sh (--bind 127.0.0.1 --port $E2E_HTTP_PORT)"
mark="$(journal_mark)"
set +e
output="$(PAIRNETS_BASE_URL="$FEED" bash "$REPO_ROOT/deploy/get.sh" --bind 127.0.0.1 --port "$E2E_HTTP_PORT" 2>&1)"
rc=$?
set -e
printf '%s\n' "$output" | redact | sed 's/^/  | /'
[[ $rc -eq 0 ]] || die "get.sh / install.sh exited with $rc"
ok "get.sh and install.sh finished"

step "files, owners and permissions"
expect "/opt/pairnets is 755 root" "$(perms "$INSTALL_DIR")" "755 root root"
expect "the server binary is 755 root" "$(perms "$INSTALL_DIR/pairnets-server")" "755 root root"
expect "update.sh is 755 root" "$(perms "$INSTALL_DIR/update.sh")" "755 root root"
expect "VERSION is the feed's" "$(installed_version)" "$EXPECTED_VERSION"
expect "/etc/pairnets is 700 root" "$(perms "$CONF_DIR")" "700 root root"
expect "pairnets.env is 600 root" "$(perms "$ENV_FILE")" "600 root root"
check "pairnets.env has a SYNC_TOKEN of 64 hex characters" grep -Eq '^SYNC_TOKEN=[0-9a-f]{64}$' "$ENV_FILE"
expect "pairnets.env listens on 127.0.0.1:$E2E_HTTP_PORT" "$(env_value ASPNETCORE_URLS)" "http://127.0.0.1:$E2E_HTTP_PORT"
expect "/var/lib/pairnets is 700 pairnets" "$(perms "$DATA_DIR")" "700 pairnets pairnets"
expect "/var/lib/pairnets/update is 750 pairnets" "$(perms "$UPDATE_DIR")" "750 pairnets pairnets"
expect "the pairnets user cannot log in" "$(getent passwd pairnets | cut -d: -f7)" "/usr/sbin/nologin"

step "services"
expect "pairnets-server is enabled and running" "$(unit_state pairnets-server)" "enabled/active"
expect "pairnets-update.path is enabled and watching" "$(unit_state pairnets-update.path)" "enabled/active"
expect "pairnets-update.timer is enabled and running" "$(unit_state pairnets-update.timer)" "enabled/active"
check "no tunnel unit was installed" test ! -e /etc/systemd/system/pairnets-tunnel.service
check "no tunnel token file" test ! -e "$CONF_DIR/tunnel.env"
check "cloudflared was not installed" test ! -e /usr/bin/cloudflared
main_pid="$(systemctl show -p MainPID --value pairnets-server)"
expect "the server runs as pairnets" "$(ps -o user= -p "$main_pid" 2>/dev/null | tr -d ' ')" "pairnets"
expect "the server has no capabilities" "$(grep -E '^CapEff:' "/proc/$main_pid/status" 2>/dev/null | awk '{print $2}')" "0000000000000000"
expect "NoNewPrivileges is on" "$(grep -E '^NoNewPrivs:' "/proc/$main_pid/status" 2>/dev/null | awk '{print $2}')" "1"
if command -v ss >/dev/null; then
  listening="$(ss -Hltn "sport = :$E2E_HTTP_PORT" | awk '{print $4}' | sort -u | paste -sd' ' -)"
  expect "it listens on 127.0.0.1 only" "$listening" "127.0.0.1:$E2E_HTTP_PORT"
fi
if command -v systemd-analyze >/dev/null; then
  exposure="$(systemd-analyze security pairnets-server --no-pager 2>/dev/null | tail -n1 || true)"
  [[ -z "$exposure" ]] || note "systemd-analyze security: ${exposure#*: }"
fi

step "health and logs"
check "GET /api/health answers ok" health_ok
check "GET /api/hello answers without a token" curl -fsS --max-time 10 "$API/api/hello"
expect "the API takes the shared token" "$(api_status GET /api/info)" "200"
check_journal_clean "$mark" "no warnings or errors from the Pairnets units in the journal"
# (After the journal check: the server rightly logs a warning for this one.)
expect "the API refuses a request without the token" "$(curl -s -o /dev/null -w '%{http_code}' "$API/api/info")" "401"

step "install.sh again (an upgrade with the same package)"
hash_before="$(token_hash)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
tar -xzf "$FEED/$PACKAGE.tar.gz" -C "$work"
mark="$(journal_mark)"
set +e
output="$("$work/$PACKAGE/install.sh" 2>&1)"
rc=$?
set -e
printf '%s\n' "$output" | redact | sed 's/^/  | /'
expect "install.sh exited with 0" "$rc" "0"
expect "the token is unchanged" "$(token_hash)" "$hash_before"
expect "the address is unchanged" "$(env_value ASPNETCORE_URLS)" "http://127.0.0.1:$E2E_HTTP_PORT"
expect "pairnets.env is still 600 root" "$(perms "$ENV_FILE")" "600 root root"
expect "pairnets-server is enabled and running" "$(unit_state pairnets-server)" "enabled/active"
expect "pairnets-update.path is enabled and watching" "$(unit_state pairnets-update.path)" "enabled/active"
check "GET /api/health answers ok" health_ok
check_journal_clean "$mark" "no warnings or errors in the journal after the second install"

finish
