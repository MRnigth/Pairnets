#!/usr/bin/env bash
# Linux test box, phase A update (docs/PREDEPLOY.md): the server's self-update, end to end, from a local
# feed with the same build and a higher version (scripts/make-feed.sh ... next ...). Two parts:
#
#   sudo tests/linux-server/update-check.sh prepare --feed <feed-2-dir>
#       before the API tour: points the root updater at the feed (a test-only systemd drop-in), so an
#       update the tour asks for (POST /api/update) really installs the feed's version; notes what must
#       survive (token, data, a marker file).
#   sudo tests/linux-server/update-check.sh verify [--timeout <seconds>]
#       after the tour: when the tour did not update the server, asks for an update itself (like an app);
#       then checks that the update succeeded ("Updated from X to Y"), VERSION changed, the token and the
#       data were kept, the server is healthy and update.log does not contain the token.
set -euo pipefail
# shellcheck source=tests/linux-server/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

BASELINE="$E2E_DIR/update-baseline"
MARKER="$DATA_DIR/.pairnets-e2e-marker"
SEEDED_PATH="pairnets-e2e/update-check.txt"

usage() { echo "usage: $0 prepare --feed <feed-dir> | verify [--timeout <seconds>]" >&2; exit 2; }

MODE="${1:-}"
[[ -n "$MODE" ]] || usage
shift
FEED=""
TIMEOUT=600
while [[ $# -gt 0 ]]; do
  case "$1" in
    --feed) FEED="${2:-}"; shift 2 ;;
    --timeout) TIMEOUT="${2:-}"; shift 2 ;;
    *) usage ;;
  esac
done
need_root
[[ -f "$ENV_FILE" ]] || die "the server is not installed (run install-check.sh first)"

prepare() {
  [[ -n "$FEED" ]] || usage
  check_feed "$FEED"
  local old new staged
  old="$(installed_version)"
  new="$(feed_version "$FEED")"
  step "prepare: point the updater at a feed with $new (installed: $old)"
  version_newer "$new" "$old" || die "the feed's version $new is not newer than the installed $old (update.sh would refuse it)"
  staged="$(stage_feed "$FEED" update)"
  point_updater_at "$staged"
  ok "pairnets-update.service reads $staged (drop-in $UPDATE_DROPIN)"
  rm -f "$UPDATE_DIR/last-attempt"
  head -c 16 /dev/urandom | od -An -tx1 | tr -d ' \n' > "$MARKER"
  chown pairnets:pairnets "$MARKER"
  chmod 0600 "$MARKER"
  {
    echo "OLD_VERSION=$old"
    echo "NEW_VERSION=$new"
    echo "TOKEN_HASH=$(token_hash)"
    echo "MARKER=$(cat "$MARKER")"
    echo "SINCE=$(date +%s)"
  } > "$BASELINE"
  chmod 0600 "$BASELINE"
  ok "noted the token's hash and a marker in the data folder"
  finish
}

verify() {
  [[ -f "$BASELINE" ]] || die "run \"$0 prepare --feed <feed>\" first"
  local OLD_VERSION NEW_VERSION TOKEN_HASH MARKER_VALUE SINCE
  OLD_VERSION="$(sed -n 's/^OLD_VERSION=//p' "$BASELINE")"
  NEW_VERSION="$(sed -n 's/^NEW_VERSION=//p' "$BASELINE")"
  TOKEN_HASH="$(sed -n 's/^TOKEN_HASH=//p' "$BASELINE")"
  MARKER_VALUE="$(sed -n 's/^MARKER=//p' "$BASELINE")"
  SINCE="$(sed -n 's/^SINCE=//p' "$BASELINE")"

  step "verify: update from $OLD_VERSION to $NEW_VERSION"
  # A run the tour started may still be going.
  if [[ -e "$UPDATE_DIR/request" ]] || systemctl is-active --quiet pairnets-update.service; then
    note "an update is in progress; waiting for it"
    wait_update_done "$TIMEOUT" >/dev/null || true
  fi

  local fingerprint="" seeded_hash="" by_tour=1 final
  if [[ "$(installed_version)" == "$OLD_VERSION" ]]; then
    by_tour=0
    note "the tour did not update the server; asking for an update now"
    health_ok || die "the server does not answer before the update"
    fingerprint="$(files_fingerprint)"
    if [[ "$(api_status GET /api/info)" == 200 ]]; then
      local sample
      sample="$(mktemp)"
      printf 'kept across the update %s\n' "$(date -u +%FT%TZ)" > "$sample"
      if put_file "$SEEDED_PATH" "$sample"; then
        seeded_hash="$(sha256sum "$sample" | cut -d' ' -f1)"
        fingerprint="$(files_fingerprint)"
        ok "uploaded $SEEDED_PATH through the API"
      else
        bad "could not upload $SEEDED_PATH through the API before the update"
      fi
      rm -f "$sample"
    else
      note "the shared token is not accepted any more (the tour may have turned it off): checking the files on disk only"
    fi
    rm -f "$UPDATE_DIR/status.json"
    request_update
    if final="$(wait_update_done "$TIMEOUT")"; then :; else bad "the updater did not finish within $TIMEOUT s (status: ${final:-none})"; fi
  else
    note "the tour's update request already installed $(installed_version)"
  fi

  # update.log keeps every run's result; the last status.json may belong to a later, refused request.
  local expected_line="status: succeeded - Updated from $OLD_VERSION to $NEW_VERSION."
  if grep -qF "$expected_line" "$UPDATE_DIR/update.log" 2>/dev/null; then
    ok "update.log says: Updated from $OLD_VERSION to $NEW_VERSION"
  else
    bad "update.log does not say \"Updated from $OLD_VERSION to $NEW_VERSION\" (status.json: $(update_status))"
    note "the end of update.log:"
    tail -n 25 "$UPDATE_DIR/update.log" 2>/dev/null | redact | sed 's/^/          | /' || true
  fi
  if (( by_tour == 0 )); then
    final="$(update_status)"
    expect "status.json reports success" "${final%%|*}" "succeeded"
  fi
  expect "VERSION is the new one" "$(installed_version)" "$NEW_VERSION"
  expect "the token is unchanged" "$(token_hash)" "$TOKEN_HASH"
  expect "the marker in the data folder is still there" "$(cat "$MARKER" 2>/dev/null || true)" "$MARKER_VALUE"
  check "manifest.db is still there" test -s "$DATA_DIR/manifest.db"
  check "auth.db is still there" test -s "$DATA_DIR/auth.db"
  expect "the data folder is still 700 pairnets" "$(perms "$DATA_DIR")" "700 pairnets pairnets"
  if [[ -n "$fingerprint" ]]; then
    expect "every synced file is unchanged" "$(files_fingerprint)" "$fingerprint"
  fi
  check "GET /api/health answers ok" wait_http "$API/api/health" 120
  if [[ -n "$seeded_hash" ]]; then
    expect "a file uploaded before the update downloads the same" "$(get_file_hash "$SEEDED_PATH" 2>/dev/null || true)" "$seeded_hash"
  fi
  if [[ -n "$(env_value Sync__HttpsUrl)" ]]; then
    check "the website still answers over HTTPS" wait_http "https://localhost:$E2E_HTTPS_PORT/api/health" 60 --cacert "$E2E_TLS_DIR/fullchain.pem"
  fi
  expect "pairnets-server is enabled and running" "$(unit_state pairnets-server)" "enabled/active"
  expect "pairnets-update.path is enabled and watching" "$(unit_state pairnets-update.path)" "enabled/active"
  local t
  t="$(token)"
  if [[ -n "$t" ]] && grep -qF "$t" "$UPDATE_DIR"/update.log* 2>/dev/null; then
    bad "update.log contains the shared token"
  else
    ok "update.log does not contain the shared token"
  fi
  local errors
  errors="$(journalctl --no-pager -q -p err --since "@$SINCE" -u pairnets-update.service 2>/dev/null | redact || true)"
  if [[ -z "$errors" ]]; then ok "no errors from pairnets-update.service in the journal"; else bad "pairnets-update.service logged errors:"; printf '%s\n' "$errors" | head -n 20 | sed 's/^/          /'; fi
  finish
}

case "$MODE" in
  prepare) prepare ;;
  verify) verify ;;
  *) usage ;;
esac
