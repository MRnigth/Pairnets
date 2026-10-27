#!/bin/bash
# Runs the real deploy/macos/update-app.sh (the Mac app's swap script) against fake apps in a temp folder, with stand-ins for
# `open` and `pkill`, so it runs on Linux, a Mac and Git Bash. Nothing outside the temp folder is touched.
#   tests/macos/update-check.sh
# Cases: a good update, a new app that never starts (old one put back), an old app that never quits, a failed move.
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/../../deploy/macos/update-app.sh"
ROOT="$(mktemp -d)"
trap 'rm -rf "$ROOT"' EXIT
failures=0

check() { # check <description> <condition-exit-code>
  if [ "$2" -eq 0 ]; then echo "ok   $1"; else echo "FAIL $1"; failures=$((failures + 1)); fi
}

# Stand-ins first in PATH. The fake `open` "starts" an app: it reports ready (like the new Pairnets does) when the app's
# program says "good"; an old app just records that it was opened again.
BIN="$ROOT/bin"; mkdir -p "$BIN"
cat > "$BIN/open" <<'EOF'
#!/bin/bash
app="$1"; flag="${3:-}"
echo "$flag" >> "$FAKE_OPENED"
if [ "$flag" = "--after-update" ] && grep -q good "$app/Contents/MacOS/Pairnets"; then
  touch "$FAKE_READY"
fi
EOF
printf '#!/bin/bash\nexit 0\n' > "$BIN/pkill"
printf '#!/bin/bash\nexit 0\n' > "$BIN/xattr"
chmod +x "$BIN"/*
export PATH="$BIN:$PATH"

make_app() { # make_app <folder> <what the program says>
  mkdir -p "$1/Contents/MacOS"; echo "$2" > "$1/Contents/MacOS/Pairnets"
}

scenario() { # scenario <name>: a fresh Applications folder with the old app and a staged new one
  S="$ROOT/$1"; mkdir -p "$S/Applications" "$S/local" "$S/logs"
  APP="$S/Applications/Pairnets.app"
  NEW="$S/Applications/.Pairnets-update/Pairnets.app"
  BACKUP="$S/Applications/.Pairnets-previous"
  export FAKE_READY="$S/local/mac-update.ready" FAKE_OPENED="$S/opened.txt"
  READY="$FAKE_READY"; FAILED="$S/local/mac-update.failed"; LOG="$S/logs/mac-update.log"
  : > "$FAKE_OPENED"
  make_app "$APP" old
}

run_script() { # run_script <pid>
  bash "$SCRIPT" "$1" "$APP" "$NEW" "$BACKUP" "$READY" "$FAILED" "$LOG" 1.0.62
}

# 1. A good update.
scenario good; make_app "$NEW" good
PAIRNETS_UPDATE_WAIT=2 PAIRNETS_UPDATE_START=5 run_script 999999
check "good: the exit code is 0" $?
check "good: the new app is in place" "$(grep -q good "$APP/Contents/MacOS/Pairnets"; echo $?)"
check "good: it was started with --after-update" "$(grep -q -- --after-update "$FAKE_OPENED"; echo $?)"
check "good: the backup and the staging folder are gone" "$([ ! -e "$BACKUP" ] && [ ! -e "$S/Applications/.Pairnets-update" ]; echo $?)"
check "good: nothing is reported as failed" "$([ ! -e "$FAILED" ]; echo $?)"
check "good: the log says what happened" "$(grep -q 'new Pairnets is running' "$LOG"; echo $?)"

# 2. The new app never says it runs: the old one comes back and is opened.
scenario nostart; make_app "$NEW" broken
PAIRNETS_UPDATE_WAIT=2 PAIRNETS_UPDATE_START=2 run_script 999999
check "no start: the script reports failure" "$([ $? -ne 0 ]; echo $?)"
check "no start: the old app is back" "$(grep -q old "$APP/Contents/MacOS/Pairnets"; echo $?)"
check "no start: the broken copy and the backup are gone" "$([ ! -e "$BACKUP" ] && [ ! -e "$APP.failed" ]; echo $?)"
check "no start: the old app was opened again" "$(grep -q -- --after-failed-update "$FAKE_OPENED"; echo $?)"
check "no start: the note says the old version was put back" "$(grep -q 'old version was put back' "$FAILED"; echo $?)"

# 3. The old app never quits: nothing is changed.
scenario stuck; make_app "$NEW" good
sleep 30 & stuck=$!
PAIRNETS_UPDATE_WAIT=2 PAIRNETS_UPDATE_START=2 run_script "$stuck"
rc=$?
kill "$stuck" 2>/dev/null
check "stuck: the script reports failure" "$([ $rc -ne 0 ]; echo $?)"
check "stuck: the old app is untouched" "$(grep -q old "$APP/Contents/MacOS/Pairnets"; echo $?)"
check "stuck: the note says why" "$(grep -q 'did not quit' "$FAILED"; echo $?)"

# 4. The new app is missing when it should be moved in: the old one is put straight back.
scenario move; rm -rf "$NEW"
PAIRNETS_UPDATE_WAIT=2 PAIRNETS_UPDATE_START=2 run_script 999999
check "move: the script reports failure" "$([ $? -ne 0 ]; echo $?)"
check "move: the old app is back" "$(grep -q old "$APP/Contents/MacOS/Pairnets"; echo $?)"
check "move: the note says the old version was put back" "$(grep -q 'put back' "$FAILED"; echo $?)"

if [ "$failures" -ne 0 ]; then echo "$failures check(s) failed"; exit 1; fi
echo "all update-app.sh checks passed"
