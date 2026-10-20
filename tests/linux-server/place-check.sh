#!/usr/bin/env bash
# Linux test box, phase C (docs/PREDEPLOY.md): choosing where the nest keeps its files and its program
# (deploy/place.sh). On a fresh machine it makes a few small drives (loop files), checks that the installer
# refuses the ones it must, installs onto a chosen drive with get.sh, updates through the real updater,
# checks that a missing drive keeps the server from starting on the main disk, moves the files (by command and
# the way the apps ask), moves them back to the main disk, and checks that planted links in the update
# folder are never followed by root. Run as root on a fresh systemd Ubuntu:
#
#   sudo tests/linux-server/place-check.sh --feed <feed-dir> --feed2 <next feed-dir>
set -euo pipefail
# shellcheck source=tests/linux-server/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

FEED=""
FEED2=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --feed) FEED="${2:-}"; shift 2 ;;
    --feed2) FEED2="${2:-}"; shift 2 ;;
    *) echo "usage: $0 --feed <feed-dir> --feed2 <next feed-dir>" >&2; exit 2 ;;
  esac
done
[[ -n "$FEED" && -n "$FEED2" ]] || { echo "usage: $0 --feed <feed-dir> --feed2 <next feed-dir>" >&2; exit 2; }
need_root
check_feed "$FEED"
check_feed "$FEED2"
FEED="$(cd "$FEED" && pwd)"
FEED2="$(cd "$FEED2" && pwd)"
[[ ! -e "$CONF_DIR" && ! -e "$INSTALL_DIR" ]] || die "Pairnets is already installed here; this check needs a fresh machine"
command -v mkfs.ext4 >/dev/null || die "mkfs.ext4 is needed (e2fsprogs)"

PLACE_SH="$REPO_ROOT/deploy/place.sh"
IMAGES=/root/pd-place
BIG=/mnt/pd-big        # 3 GB: fits, with a "small drive" warning
SMALL=/mnt/pd-small    # 300 MB: too small
NOEXEC=/mnt/pd-noexec  # programs can't run there
ASK=/mnt/pd-ask        # for the questions
VFAT=/mnt/pd-vfat      # no Linux permissions (only when mkfs.vfat is there)

make_drive() { # make_drive <mount> <size> <fs> [mount options]
  local img
  img="$IMAGES/$(basename "$1").img"
  truncate -s "$2" "$img"
  case "$3" in
    ext4) mkfs.ext4 -q -F -L "$(basename "$1")" "$img" ;;
    vfat) mkfs.vfat -n PDVFAT "$img" >/dev/null ;;
  esac
  mkdir -p "$1"
  mount -o "loop${4:+,$4}" "$img" "$1"
}

# install_line <args...>: runs get.sh against feed 1 without a terminal; sets RC and OUT.
install_line() {
  set +e
  OUT="$(PAIRNETS_BASE_URL="$FEED" PAIRNETS_RELEASE_KEY="$FEED/test-signing.pub" bash "$REPO_ROOT/deploy/get.sh" "$@" < /dev/null 2>&1)"
  RC=$?
  set -e
}

refused() { # refused <description> <expected exit> <expected text> <args...>
  local what="$1" code="$2" text="$3"
  shift 3
  install_line --bind 127.0.0.1 --port "$E2E_HTTP_PORT" --no-questions "$@"
  if [[ "$RC" == "$code" && "$OUT" == *"$text"* ]]; then ok "$what"
  else bad "$what (exit $RC, expected $code with \"$text\"):"; printf '%s\n' "$OUT" | tail -n 5 | sed 's/^/          /'; fi
}

what_of() { sed -n 's/^What=//p' "/etc/systemd/system/$1" 2>/dev/null | head -n1; }

place_status_field() { # place_status_field <field>
  python3 -c 'import json, sys; print(json.load(open(sys.argv[1])).get(sys.argv[2], ""))' "$UPDATE_DIR/place-status.json" "$1" 2>/dev/null || true
}

# ask_place <request lines>: drops a request the way the server does, and waits for the root helper.
ask_place() { # ask_place <request text> <seconds>
  rm -f "$UPDATE_DIR/place-status.json"
  printf '%s\n' "$1" | runuser -u pairnets -- tee "$UPDATE_DIR/place-request" >/dev/null
  local start=$SECONDS
  while (( SECONDS - start < $2 )); do
    if [[ ! -e "$UPDATE_DIR/place-request" ]] && ! systemctl is-active --quiet pairnets-place.service; then
      case "$1" in
        *action=move*) [[ "$(place_status_field state)" == succeeded || "$(place_status_field state)" == failed ]] && return 0 ;;
        *) [[ -f "$UPDATE_DIR/drives.json" ]] && return 0 ;;
      esac
    fi
    sleep 1
  done
  return 1
}

step "small drives to choose from"
mkdir -p "$IMAGES"
make_drive "$BIG" 3G ext4
make_drive "$SMALL" 300M ext4
make_drive "$NOEXEC" 300M ext4 noexec
make_drive "$ASK" 1500M ext4
HAVE_VFAT=""
if command -v mkfs.vfat >/dev/null; then make_drive "$VFAT" 300M vfat; HAVE_VFAT=1; fi
for m in "$BIG" "$SMALL" "$NOEXEC" "$ASK"; do findmnt -rn -o TARGET,FSTYPE,SIZE,OPTIONS --mountpoint "$m" | sed 's/^/        /'; done
mkdir -p "$BIG/full" && echo something > "$BIG/full/file.txt"
mkdir -p /srv/pd-real && ln -sfn /srv/pd-real /srv/pd-link
mkdir -p /mnt/pd-nothing

step "the installer refuses a place it can't use, and changes nothing"
refused "a folder that isn't a full path" 2 "must be a full path" --data-dir pairnets-files
refused "a system folder" 2 "is a system folder" --data-dir /etc/pairnets-files
refused "a top folder" 2 "is a top folder" --data-dir /mnt
refused "a folder inside Pairnets' own" 2 "inside Pairnets' own folders" --program-dir /var/lib/pairnets/program
refused "a drive with too little space" 1 "it needs at least" --data-dir "$SMALL/pairnets"
refused "a drive that isn't mounted" 1 "no drive is mounted at /mnt/pd-nothing" --data-dir /mnt/pd-nothing/pairnets
refused "a folder that isn't empty" 1 "is not empty" --data-dir "$BIG/full"
refused "a folder through a symbolic link" 1 "goes through a symbolic link" --data-dir /srv/pd-link/pairnets
refused "a drive that can't run programs, for the program" 1 "doesn't allow running programs" --program-dir "$NOEXEC/prog"
if [[ -n "$HAVE_VFAT" ]]; then
  refused "a drive without Linux permissions" 1 "no Linux file permissions" --data-dir "$VFAT/pairnets"
else
  note "mkfs.vfat is not installed: the FAT drive refusal is not tried here"
fi
check "nothing was installed by the refused runs" test ! -e "$CONF_DIR" -a ! -e /etc/systemd/system/pairnets-server.service
check "no mount was made" test ! -e /etc/systemd/system/var-lib-pairnets.mount -a ! -e /etc/systemd/system/opt-pairnets.mount

step "the questions: a list of drives, Enter keeps the main disk"
# script gives the questions a terminal; the answers come in on it (it never passes on the end of the input,
# so a question asked once too often would wait: timeout).
ask_number="$(bash -c "source $PLACE_SH; place_drives" | cut -d$'\037' -f1 | grep -nx "$ASK" | cut -d: -f1)"
note "$ASK is number ${ask_number:-?} in the list"
menu="$(printf '%s\nn\n' "$ask_number" | timeout 60 script -qec "bash -c 'source $PLACE_SH; place_ask_install; echo; echo CHOICE=\$PLACE_DATA_CHOICE PROGRAM=\$PLACE_PROGRAM_CHOICE'" /dev/null 2>&1 | tr -d '\r' || true)"
check "it asks where to keep the files" grep -q "Where should Pairnets keep your files?" <<<"$menu"
check "the main disk is the default" grep -Eq "1\) Main disk .*\(default\)" <<<"$menu"
check "the drives show their free space" grep -Eq "$ASK/pairnets +[0-9.]+[KMGT]?B free" <<<"$menu"
expect "picking that number chooses $ASK/pairnets" "$(sed -n 's/.*CHOICE=\([^ ]*\) .*/\1/p' <<<"$menu" | tail -n1)" "$ASK/pairnets"
menu="$(printf '\nn\n' | timeout 60 script -qec "bash -c 'source $PLACE_SH; place_ask_install; echo; echo CHOICE=[\$PLACE_DATA_CHOICE]'" /dev/null 2>&1 | tr -d '\r')"
check "Enter keeps the main disk" grep -q "CHOICE=\[\]" <<<"$menu"
check "the questions changed nothing" test ! -e /etc/systemd/system/var-lib-pairnets.mount

step "install onto a chosen drive (files on $BIG, program in /srv/pd-prog)"
mark="$(journal_mark)"
install_line --bind 127.0.0.1 --port "$E2E_HTTP_PORT" --data-dir "$BIG/pairnets" --program-dir /srv/pd-prog --no-questions
printf '%s\n' "$OUT" | redact | sed 's/^/  | /'
if [[ $RC -ne 0 ]]; then
  # Show why a mount would not start (a drive's own unit, or what it depends on).
  { systemctl --failed --no-pager; systemctl status --no-pager -l var-lib-pairnets.mount opt-pairnets.mount "$(systemd-escape -p --suffix=mount "$BIG")" mnt.mount srv.mount
    systemctl list-dependencies --no-pager var-lib-pairnets.mount; journalctl -xe --no-pager -n 80; } 2>&1 | redact | sed 's/^/  ! /'
  die "get.sh / install.sh exited with $RC"
fi
ok "get.sh and install.sh finished"
check "it warned that the drive is small" grep -q "will have only" <<<"$OUT"
check "the summary names the chosen drive" grep -q "Data:    $BIG/pairnets (shown at $DATA_DIR)" <<<"$OUT"
expect "the files' mount shows $BIG/pairnets" "$(what_of var-lib-pairnets.mount)" "$BIG/pairnets"
expect "the program's mount shows /srv/pd-prog" "$(what_of opt-pairnets.mount)" "/srv/pd-prog"
expect "$DATA_DIR is mounted from the drive" "$(findmnt -rn -o SOURCE --mountpoint "$DATA_DIR")" "/dev/$(basename "$(findmnt -rn -o SOURCE --mountpoint "$BIG")")[/pairnets]"
expect "the chosen files folder is 700 pairnets" "$(perms "$BIG/pairnets")" "700 pairnets pairnets"
expect "the chosen program folder is 755 root" "$(perms /srv/pd-prog)" "755 root root"
check "the server's data is on the drive" test -f "$BIG/pairnets/manifest.db"
check "the program is in the chosen folder" test -x /srv/pd-prog/pairnets-server
check "place.sh is installed next to the program" test -x "$INSTALL_DIR/place.sh"
check "the drive marker is there" test -f "$BIG/pairnets/.pairnets-drive"
check "the server needs the mounts" grep -q "RequiresMountsFor=$DATA_DIR $INSTALL_DIR" /etc/systemd/system/pairnets-server.service.d/place.conf
expect "the mounts are enabled and mounted" "$(unit_state var-lib-pairnets.mount) $(unit_state opt-pairnets.mount)" "enabled/active enabled/active"
expect "pairnets-place.path is enabled and watching" "$(unit_state pairnets-place.path)" "enabled/active"
check "the server answers" health_ok
check_journal_clean "$mark" "no warnings or errors from the Pairnets units during the install"

step "some files"
TMPF="$(mktemp -d)"
trap 'rm -rf "$TMPF"' EXIT
head -c 300000 /dev/urandom > "$TMPF/a.bin"
echo "hello from the place check" > "$TMPF/b.txt"
put_file "photos/a.bin" "$TMPF/a.bin"
put_file "notes/b.txt" "$TMPF/b.txt"
FP="$(files_fingerprint)"
expect "the nest has the files" "$(get_file_hash notes/b.txt)" "$(sha256sum "$TMPF/b.txt" | cut -d' ' -f1)"
TOKEN_HASH="$(token_hash)"
# install.sh from a release package, as the updater runs it (the installed folder has no install.sh).
tar -xzf "$FEED2/$PACKAGE.tar.gz" -C "$TMPF"
PKG_INSTALL="$TMPF/$PACKAGE/install.sh"

step "an update keeps the chosen places (the real updater, feed 2)"
FEED2_STAGED="$(stage_feed "$FEED2" feed2)"
point_updater_at "$FEED2_STAGED"
request_update
result="$(wait_update_done 600 || true)"
expect "the updater finished" "${result%%|*}" "succeeded"
expect "the new version is installed" "$(installed_version)" "$(feed_version "$FEED2")"
expect "the files' mount still shows $BIG/pairnets" "$(what_of var-lib-pairnets.mount)" "$BIG/pairnets"
expect "the program's mount still shows /srv/pd-prog" "$(what_of opt-pairnets.mount)" "/srv/pd-prog"
check "the new program went to the chosen folder" grep -qx "$(feed_version "$FEED2")" /srv/pd-prog/VERSION
expect "the files are unchanged" "$(files_fingerprint)" "$FP"
expect "the token is unchanged" "$(token_hash)" "$TOKEN_HASH"
check "the server answers" health_ok

step "a missing drive: the server does not start on the main disk"
systemctl stop pairnets-server var-lib-pairnets.mount
umount "$BIG"
check "the main disk's $DATA_DIR is empty" test -z "$(ls -A "$DATA_DIR")"
# A leftover folder with the same name on the main disk must not be shown as the (empty) nest either.
mkdir -p "$BIG/pairnets"
set +e
systemctl start pairnets-server 2>/dev/null
set -e
sleep 2
check "the server is not running" bash -c '! systemctl is-active --quiet pairnets-server'
check "$DATA_DIR is not mounted" bash -c "! findmnt -rn --mountpoint $DATA_DIR >/dev/null"
set +e
out="$(cd /root && bash "$PKG_INSTALL" 2>&1 < /dev/null)"
rc=$?
set -e
expect "install.sh (as the updater runs it) stops" "$rc" "1"
check "and says the drive is not there" grep -q "is not there. Plug it in or mount it" <<<"$out"
check "nothing was made on the main disk" test -z "$(ls -A "$DATA_DIR")"
rmdir "$BIG/pairnets"
mount -o loop "$IMAGES/pd-big.img" "$BIG"
systemctl reset-failed var-lib-pairnets.mount pairnets-server 2>/dev/null || true
systemctl start var-lib-pairnets.mount pairnets-server
check "with the drive back, the server answers" wait_http "$API/api/health" 120
expect "and has its files" "$(files_fingerprint)" "$FP"
expect "the watchers are back too" "$(unit_state pairnets-update.path) $(unit_state pairnets-place.path)" "enabled/active enabled/active"

step "move the files to another folder (place.sh move, as the install command does)"
set +e
out="$(bash "$INSTALL_DIR/place.sh" move data /srv/pd-data --yes 2>&1)"
rc=$?
set -e
printf '%s\n' "$out" | sed 's/^/  | /'
expect "the move finished" "$rc" "0"
expect "the files' mount shows /srv/pd-data" "$(what_of var-lib-pairnets.mount)" "/srv/pd-data"
expect "the files are unchanged" "$(files_fingerprint)" "$FP"
check "the server answers" health_ok
check "the old copy is kept" test -f "$BIG/pairnets/manifest.db"
check "it says how to delete the old copy" grep -q "sudo rm -rf '$BIG/pairnets'" <<<"$out"
expect "the moved folder is 700 pairnets" "$(perms /srv/pd-data)" "700 pairnets pairnets"

step "the apps' way: list the drives, then move (pairnets-place.path)"
check "the drive list arrives" ask_place "action=list" 60
check "drives.json names the current place" grep -q '"current":"/srv/pd-data"' "$UPDATE_DIR/drives.json"
check "drives.json lists $ASK" grep -q "\"mount\":\"$ASK\"" "$UPDATE_DIR/drives.json"
expect "drives.json is the pairnets user's, readable" "$(perms "$UPDATE_DIR/drives.json")" "644 pairnets pairnets"
check "a move to a drive that's too small is answered" ask_place $'action=move\ndata_dir='"$SMALL/pairnets" 120
expect "it failed" "$(place_status_field state)" "failed"
check "with a plain reason" grep -q "needs at least" <<<"$(place_status_field message)"
expect "and nothing changed" "$(what_of var-lib-pairnets.mount)" "/srv/pd-data"
check "a move to $ASK is answered" ask_place $'action=move\ndata_dir='"$ASK/pairnets" 300
expect "it succeeded" "$(place_status_field state)" "succeeded"
expect "it names the old copy" "$(place_status_field oldCopy)" "/srv/pd-data"
expect "the files' mount shows $ASK/pairnets" "$(what_of var-lib-pairnets.mount)" "$ASK/pairnets"
expect "the files are unchanged" "$(files_fingerprint)" "$FP"
check "the server answers" wait_http "$API/api/health" 120

step "planted links in the update folder are never followed by root"
echo "root only" > /root/pd-victim
chmod 600 /root/pd-victim
victim_before="$(sha256sum /root/pd-victim | cut -d' ' -f1) $(perms /root/pd-victim)"
runuser -u pairnets -- ln -sfn /root/pd-victim "$UPDATE_DIR/place-status.json"
runuser -u pairnets -- ln -sfn /root/pd-victim "$UPDATE_DIR/drives.json"
runuser -u pairnets -- ln -sfn /root/pd-victim "$UPDATE_DIR/place-request"
systemctl start pairnets-place.service 2>/dev/null || true
sleep 2
check "the planted request was not read as root (no move happened)" test "$(what_of var-lib-pairnets.mount)" = "$ASK/pairnets"
expect "the root-only file is unchanged" "$(sha256sum /root/pd-victim | cut -d' ' -f1) $(perms /root/pd-victim)" "$victim_before"
rm -f "$UPDATE_DIR/place-status.json" "$UPDATE_DIR/drives.json" "$UPDATE_DIR/place-request"
ask_place "action=list" 60 || true
check "drives.json is a file again, not the link" test -f "$UPDATE_DIR/drives.json" -a ! -L "$UPDATE_DIR/drives.json"
expect "the root-only file is still unchanged" "$(sha256sum /root/pd-victim | cut -d' ' -f1) $(perms /root/pd-victim)" "$victim_before"

step "back to the main disk (files and program, with the install command)"
set +e
out="$(cd /root && bash "$PKG_INSTALL" --data-dir "$DATA_DIR" --program-dir "$INSTALL_DIR" --no-questions 2>&1 < /dev/null)"
rc=$?
set -e
printf '%s\n' "$out" | redact | tail -n 12 | sed 's/^/  | /'
expect "install.sh finished" "$rc" "0"
check "no files mount any more" test ! -e /etc/systemd/system/var-lib-pairnets.mount
check "no program mount any more" test ! -e /etc/systemd/system/opt-pairnets.mount
check "no drop-in any more" test ! -e /etc/systemd/system/pairnets-server.service.d/place.conf
check "$DATA_DIR is a plain folder" bash -c "! findmnt -rn --mountpoint $DATA_DIR >/dev/null"
check "$INSTALL_DIR is a plain folder" bash -c "! findmnt -rn --mountpoint $INSTALL_DIR >/dev/null"
expect "$DATA_DIR is 700 pairnets" "$(perms "$DATA_DIR")" "700 pairnets pairnets"
check "the program is on the main disk" test -x "$INSTALL_DIR/pairnets-server"
expect "the files are unchanged" "$(files_fingerprint)" "$FP"
expect "the token is unchanged" "$(token_hash)" "$TOKEN_HASH"
check "the server answers" health_ok
check "the old copies are kept" test -f "$ASK/pairnets/manifest.db" -a -x /srv/pd-prog/pairnets-server

finish
