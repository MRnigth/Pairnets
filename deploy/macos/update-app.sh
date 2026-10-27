#!/bin/bash
# Swaps the installed Pairnets.app for a new one the app already unpacked and checked, after the old app has quit.
# Started by the Mac app itself (Core/Client/MacAppUpdate.cs) and then it quits; the app carries this file inside.
#   update-app.sh <old-pid> <installed-app> <new-app> <backup-app> <ready-file> <failed-file> <log-file> <version>
# Good update: the new app takes the place, starts, writes <ready-file>; the backup is deleted.
# Anything else: the old app is put back, opened again, and <failed-file> says what happened (the app shows it).
# Tests put stand-ins for open/pkill in front of PATH (PAIRNETS_UPDATE_WAIT / PAIRNETS_UPDATE_START scale the waits).
set -u
OLD_PID="$1"; APP="$2"; NEW="$3"; BACKUP="$4"; READY="$5"; FAILED="$6"; LOG="$7"; VERSION="$8"
STAGING="$(dirname "$NEW")"
QUIT_WAIT="${PAIRNETS_UPDATE_WAIT:-60}"
START_WAIT="${PAIRNETS_UPDATE_START:-90}"

mkdir -p "$(dirname "$LOG")"
log() { printf '%s %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" >> "$LOG"; }

log "=== update to $VERSION: $APP"

# 1. Wait for the old Pairnets to be gone. If it never quits, nothing is changed.
waited=0
while kill -0 "$OLD_PID" 2>/dev/null; do
  if [ "$waited" -ge "$QUIT_WAIT" ]; then
    log "the old Pairnets did not quit in ${QUIT_WAIT}s: nothing changed"
    printf '%s' "Pairnets $VERSION could not be installed because the running Pairnets did not quit. Nothing was changed." > "$FAILED"
    rm -rf "$NEW"
    exit 1
  fi
  sleep 1; waited=$((waited + 1))
done
log "old Pairnets has quit"

# 2. Old app aside, new app in its place. A failed move puts the old app straight back.
rm -rf "$BACKUP"
if ! mv "$APP" "$BACKUP"; then
  log "could not move the old app aside: nothing changed"
  printf '%s' "Pairnets $VERSION could not be installed: the old app could not be moved aside. Nothing was changed." > "$FAILED"
  rm -rf "$NEW"
  open "$APP" --args --after-failed-update 2>>"$LOG" || true
  exit 1
fi
if ! mv "$NEW" "$APP"; then
  log "could not move the new app in: putting the old one back"
  mv "$BACKUP" "$APP"
  printf '%s' "Pairnets $VERSION could not be installed: the new app could not be put in place. Your old version was put back." > "$FAILED"
  rm -rf "$NEW"
  open "$APP" --args --after-failed-update 2>>"$LOG" || true
  exit 1
fi
xattr -dr com.apple.quarantine "$APP" 2>/dev/null || true
log "new app in place"

# 3. Start it and wait for it to say it is running.
rm -f "$READY"
open "$APP" --args --after-update 2>>"$LOG" || log "open failed"
waited=0
while [ ! -f "$READY" ] && [ "$waited" -lt "$START_WAIT" ]; do
  sleep 1; waited=$((waited + 1))
done

if [ -f "$READY" ]; then
  log "new Pairnets is running: deleting the backup"
  rm -f "$READY"
  rm -rf "$BACKUP" "$STAGING"
  exit 0
fi

# 4. It did not start: stop it, put the old app back and open it.
log "new Pairnets did not start in ${START_WAIT}s: putting the old version back"
pkill -f "$APP/Contents/MacOS" 2>/dev/null || true
sleep 1
rm -rf "$APP.failed"
mv "$APP" "$APP.failed"
if mv "$BACKUP" "$APP"; then
  rm -rf "$APP.failed"
  printf '%s' "Pairnets $VERSION didn't start, so your old version was put back." > "$FAILED"
  open "$APP" --args --after-failed-update 2>>"$LOG" || true
  log "old version is back"
  exit 1
fi
log "could not put the old version back: the backup stays at $BACKUP"
mv "$APP.failed" "$APP"
exit 1
