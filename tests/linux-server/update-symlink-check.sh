#!/usr/bin/env bash
# Regression test for deploy/update.sh: root must never follow a symlink planted in the update folder.
# That folder belongs to the unprivileged pairnets user, so it can plant update.log, status.json.tmp,
# last-attempt and update.log.1 as links to a root-only file. Before the fix, the root updater chmod'ed,
# chown'ed or overwrote the target. This runs update.sh (dry run, local feed, scratch folders) and checks the
# targets keep their content, mode and owner, then that a normal run still works.
#
#   sudo tests/linux-server/update-symlink-check.sh
#
# Needs root and a throwaway box (the WSL test box or a CI runner): it creates the pairnets user when missing.
set -euo pipefail
# shellcheck source=tests/linux-server/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
need_root

SCRIPT="${PAIRNETS_UPDATE_SCRIPT:-$REPO_ROOT/deploy/update.sh}" # (override: to prove the test fails on an old script)
id pairnets >/dev/null 2>&1 || useradd --system --no-create-home --shell /usr/sbin/nologin pairnets

T="$(mktemp -d)"
trap 'rm -rf "$T"' EXIT
chmod 0755 "$T"
mkdir "$T/install" "$T/feed" "$T/pkg" "$T/victims"
echo 1.0.0 > "$T/install/VERSION"
mkdir "$T/pkg/pairnets-server-linux-x64"
echo 1.0.1 > "$T/pkg/pairnets-server-linux-x64/VERSION"
tar -czf "$T/feed/pairnets-server-linux-x64.tar.gz" -C "$T/pkg" pairnets-server-linux-x64
(cd "$T/feed" && sha256sum pairnets-server-linux-x64.tar.gz > SHA256SUMS.txt)
# Signed with a throwaway key (TEST ONLY), which the updater is given as PAIRNETS_RELEASE_KEY.
openssl ecparam -name prime256v1 -genkey -noout -out "$T/test.key"
openssl pkey -in "$T/test.key" -pubout -out "$T/test.pub"
openssl dgst -sha256 -sign "$T/test.key" -out "$T/feed/SHA256SUMS.txt.sig" "$T/feed/SHA256SUMS.txt"

run_update() {
  PAIRNETS_BASE_URL="$T/feed" PAIRNETS_UPDATE_DIR="$T/update" PAIRNETS_INSTALL_DIR="$T/install" \
    PAIRNETS_RELEASE_KEY="$T/test.pub" PAIRNETS_UPDATE_DRY_RUN=1 bash "$SCRIPT" >"$T/run.out" 2>&1 || true
}
new_update_dir() {
  rm -rf "$T/update"
  install -d -m 0750 -o pairnets -g pairnets "$T/update"
  chmod 0755 "$T/update" # (the real folder is 0750; the test user needs no extra rights, root enters anyway)
}
stat_of() { stat -c '%U:%G %a' "$1"; }

step "planted symlinks are not followed by the root updater"
new_update_dir
declare -A BEFORE
for n in log status last log1; do
  echo "root secret $n" > "$T/victims/$n"
  chmod 0600 "$T/victims/$n"
  BEFORE[$n]="$(stat_of "$T/victims/$n")|$(cat "$T/victims/$n")"
done
runuser -u pairnets -- ln -s "$T/victims/log" "$T/update/update.log"
runuser -u pairnets -- ln -s "$T/victims/status" "$T/update/status.json.tmp"
runuser -u pairnets -- ln -s "$T/victims/last" "$T/update/last-attempt"
runuser -u pairnets -- ln -s "$T/victims/log1" "$T/update/update.log.1"
run_update
for n in log status last log1; do
  expect "victim '$n' keeps its owner, mode and content" "$(stat_of "$T/victims/$n")|$(cat "$T/victims/$n")" "${BEFORE[$n]}"
done

step "a normal run still works"
new_update_dir
touch "$T/update/request"
run_update
check "status.json says succeeded (dry run)" grep -q '"state":"succeeded"' "$T/update/status.json"
expect "status.json is pairnets 644" "$(stat_of "$T/update/status.json")" "pairnets:pairnets 644"
expect "update.log is pairnets 644" "$(stat_of "$T/update/update.log")" "pairnets:pairnets 644"
check "update.log has the first and the last line" bash -c "grep -q 'update.sh started' '$T/update/update.log' && grep -q 'update.sh finished' '$T/update/update.log'"
check "the request file is gone" test ! -e "$T/update/request"
check "last-attempt holds a number" grep -qE '^[0-9]+$' "$T/update/last-attempt"
run_update
check "a second run inside 10 minutes is refused" grep -q 'less than 10 minutes' "$T/update/status.json"
finish
