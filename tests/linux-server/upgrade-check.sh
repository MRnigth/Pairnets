#!/usr/bin/env bash
# Linux test box, phase B (docs/PREDEPLOY.md): the upgrade a real server gets. Installs the published
# release with the real one-line command, puts some files on it through the API, then lets its own
# self-updater install the new package from a feed (scripts/make-feed.sh) and checks that the files, the
# token and the address were kept. Needs the internet. Run as root on a fresh systemd Ubuntu:
#
#   sudo tests/linux-server/upgrade-check.sh --feed <feed-dir> [--timeout <seconds>]
#
# PAIRNETS_PUBLISHED_GET_SH overrides where the one-liner comes from (default: get.sh on GitHub's main).
set -euo pipefail
# shellcheck source=tests/linux-server/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

GET_SH="${PAIRNETS_PUBLISHED_GET_SH:-https://raw.githubusercontent.com/MRnigth/Pairnets/main/deploy/get.sh}"
FEED=""
TIMEOUT=600
while [[ $# -gt 0 ]]; do
  case "$1" in
    --feed) FEED="${2:-}"; shift 2 ;;
    --timeout) TIMEOUT="${2:-}"; shift 2 ;;
    *) echo "usage: $0 --feed <feed-dir> [--timeout <seconds>]" >&2; exit 2 ;;
  esac
done
[[ -n "$FEED" ]] || { echo "usage: $0 --feed <feed-dir>" >&2; exit 2; }
need_root
check_feed "$FEED"
FEED="$(cd "$FEED" && pwd)"
[[ ! -e "$CONF_DIR" && ! -e "$INSTALL_DIR" ]] || die "Pairnets is already installed here; this check needs a fresh machine"
NEW_VERSION="$(feed_version "$FEED")"

step "install the published release: curl -fsSL $GET_SH | bash -s -- --bind 127.0.0.1 --port $E2E_HTTP_PORT"
set +e
output="$(set -o pipefail; curl -fsSL --retry 3 "$GET_SH" | bash -s -- --bind 127.0.0.1 --port "$E2E_HTTP_PORT" 2>&1)"
rc=$?
set -e
# (without curl's download progress bar)
printf '%s\n' "$output" | tr '\r' '\n' | grep -Ev '^[-#=O ]*([0-9.,]+%)?$' | redact | sed 's/^/  | /' || true
[[ $rc -eq 0 ]] || die "the published installer exited with $rc"
OLD_VERSION="$(installed_version)"
ok "installed the published release $OLD_VERSION"
version_newer "$NEW_VERSION" "$OLD_VERSION" || die "the feed's version $NEW_VERSION is not newer than the published $OLD_VERSION (update.sh would refuse it)"
check "GET /api/health answers ok" health_ok

step "put files on the nest through the API"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
printf 'Hello from the upgrade check, %s\n' "$(date -u +%FT%TZ)" > "$work/hello.txt"
head -c 300000 /dev/urandom > "$work/random.bin"
printf 'æøå ünïcödé\n' > "$work/unicode.txt"
declare -A SEEDED=(
  ["Notes/hello.txt"]="$work/hello.txt"
  ["Photos/2026/random.bin"]="$work/random.bin"
  ["Ünïcode mappe/fil æøå.txt"]="$work/unicode.txt"
)
for path in "${!SEEDED[@]}"; do
  check "PUT $path" put_file "$path" "${SEEDED[$path]}"
done
info_before="$(api GET /api/info || echo '{}')"
server_id="$(json_field serverId <<<"$info_before")"
if [[ -n "$server_id" ]]; then ok "the nest's id is ${server_id:0:8}..."; else bad "GET /api/info gave no server id"; fi
manifest_before="$(api GET /api/manifest | python3 -c 'import json, sys; print(len(json.load(sys.stdin)))' || echo 0)"
expect "the manifest lists the 3 files" "$manifest_before" "3"
hash_before="$(token_hash)"
url_before="$(env_value ASPNETCORE_URLS)"
fingerprint="$(files_fingerprint)"

step "update to $NEW_VERSION with the server's own updater"
staged="$(stage_feed "$FEED" upgrade)"
point_updater_at "$staged"
since=$(date +%s)
rm -f "$UPDATE_DIR/status.json"
request_update
if final="$(wait_update_done "$TIMEOUT")"; then
  ok "the updater finished"
else
  bad "the updater did not finish within $TIMEOUT s"
fi
expect "status.json says the update succeeded" "$final" "succeeded|Updated from $OLD_VERSION to $NEW_VERSION."
if [[ "$final" != succeeded* ]]; then
  note "the end of update.log:"
  tail -n 25 "$UPDATE_DIR/update.log" 2>/dev/null | redact | sed 's/^/          | /' || true
fi

step "after the upgrade"
expect "VERSION is the new one" "$(installed_version)" "$NEW_VERSION"
check "GET /api/health answers ok" wait_http "$API/api/health" 120
expect "the token is unchanged" "$(token_hash)" "$hash_before"
expect "the address is unchanged" "$(env_value ASPNETCORE_URLS)" "$url_before"
expect "pairnets.env is still 600 root" "$(perms "$ENV_FILE")" "600 root root"
expect "the data folder is still 700 pairnets" "$(perms "$DATA_DIR")" "700 pairnets pairnets"
expect "the files on disk are unchanged" "$(files_fingerprint)" "$fingerprint"
expect "the nest's id is unchanged" "$(api GET /api/info 2>/dev/null | json_field serverId || true)" "$server_id"
for path in "${!SEEDED[@]}"; do
  expect "GET $path is the same file" "$(get_file_hash "$path" 2>/dev/null || true)" "$(sha256sum "${SEEDED[$path]}" | cut -d' ' -f1)"
done
expect "pairnets-server is enabled and running" "$(unit_state pairnets-server)" "enabled/active"
expect "pairnets-update.path is enabled and watching" "$(unit_state pairnets-update.path)" "enabled/active"
expect "pairnets-update.timer is enabled and running" "$(unit_state pairnets-update.timer)" "enabled/active"
t="$(token)"
if [[ -n "$t" ]] && grep -qF "$t" "$UPDATE_DIR"/update.log* 2>/dev/null; then bad "update.log contains the shared token"; else ok "update.log does not contain the shared token"; fi
check_journal_clean "$since" "no warnings or errors from the Pairnets units since the update"

finish
