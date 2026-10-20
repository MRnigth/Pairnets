#!/usr/bin/env bash
# Where the Pairnets server keeps its files and its program: on the main disk (the default) or on a drive
# or folder the owner chose. install.sh sources this file; pairnets-place.service runs it as root when an
# app asks (Settings > Your nest > Where your nest keeps files).
#
# The server always uses /var/lib/pairnets and /opt/pairnets. A chosen place is shown there by a systemd
# bind mount (var-lib-pairnets.mount, opt-pairnets.mount), so the hardened service, the updater and every
# path in the code stay as they are. The mount units are the only record of the choice: updates run
# install.sh without options and keep them. If the chosen drive is missing, the server does not start,
# rather than filling the main disk.
#
#   sudo ./place.sh list                         the drives, with free space and whether they can be used
#   sudo ./place.sh show                         where the files and the program are now
#   sudo ./place.sh move data <folder> [--yes]    copy the files there, check the copy, switch over
#   sudo ./place.sh move program <folder> [--yes]
#   sudo ./place.sh --from-request               what pairnets-place.service runs (an app's request)
#
# <folder> /var/lib/pairnets (or /opt/pairnets) moves back to the main disk. A move never deletes the old
# copy: it says where it is and how to delete it.
#
# Testing: PAIRNETS_PLACE_NO_HEALTH=1 skips the health check after a move (no server in the test).

PLACE_DATA=/var/lib/pairnets
PLACE_PROGRAM=/opt/pairnets
PLACE_UNIT_DIR=/etc/systemd/system
PLACE_ENV_FILE=/etc/pairnets/pairnets.env
PLACE_UPDATE_DIR=$PLACE_DATA/update
PLACE_REQUEST=$PLACE_UPDATE_DIR/place-request
PLACE_STATUS=$PLACE_UPDATE_DIR/place-status.json
PLACE_DRIVES=$PLACE_UPDATE_DIR/drives.json
PLACE_LOCK=/run/pairnets-place.lock
# Kept in a chosen folder. The mount only starts when it is there: with the drive missing, an empty folder of
# the same name on the main disk must never be shown to the server as its (empty) nest.
PLACE_MARKER=.pairnets-drive
PLACE_MIN_FREE=$((1024 * 1024 * 1024))         # stop below 1 GB free
PLACE_WARN_FREE=$((10 * 1024 * 1024 * 1024))   # warn (and ask) below 10 GB free
# Filesystems a nest can live on: Linux permissions, local, kept across restarts.
PLACE_GOOD_FS=" ext2 ext3 ext4 xfs btrfs zfs f2fs bcachefs jfs reiserfs nilfs2 "
# Units that run from the program folder or use the data folder; stopped while either moves.
PLACE_SERVICES=(pairnets-update.timer pairnets-update.path pairnets-tunnel pairnets-server)

place_die() { echo "error: $*" >&2; exit 1; }

# The unit name systemd gives a mount on <path>: /var/lib/pairnets -> var-lib-pairnets.mount.
place_unit() { # place_unit data|program
  case "$1" in
    data) echo var-lib-pairnets.mount ;;
    program) echo opt-pairnets.mount ;;
  esac
}

place_link() { # place_link data|program -> the fixed path the server uses
  case "$1" in
    data) echo "$PLACE_DATA" ;;
    program) echo "$PLACE_PROGRAM" ;;
  esac
}

# place_path_problem <kind> <folder>: why <folder> can never be a place (prints a sentence), or nothing.
# Only looks at the text, so install.sh can check its options before it needs root.
place_path_problem() {
  local kind="$1" dir="$2" link
  link="$(place_link "$kind")"
  [[ "$dir" == /* ]] || { echo "the folder must be a full path starting with /, for example /mnt/data/pairnets"; return; }
  [[ "$dir" != *$'\n'* && "$dir" != *[[:cntrl:]]* ]] || { echo "the folder name has a line break or another control character"; return; }
  [[ "/$dir/" != */../* && "/$dir/" != */./* ]] || { echo "the folder must not contain . or .. parts"; return; }
  dir="${dir%/}"
  [[ -n "$dir" ]] || { echo "the whole disk (/) can't be the folder; choose a folder on it, for example /srv/pairnets"; return; }
  [[ "$dir" == "$link" ]] && return 0 # back to the main disk
  case "$dir" in
    /bin|/bin/*|/boot|/boot/*|/dev|/dev/*|/etc|/etc/*|/lib|/lib/*|/lib32|/lib32/*|/lib64|/lib64/*|/proc|/proc/*|/root|/root/*|/run|/run/*|/sbin|/sbin/*|/sys|/sys/*|/usr|/usr/*|/tmp|/tmp/*|/var/tmp|/var/tmp/*)
      echo "$dir is a system folder; choose a folder on a data drive, for example /mnt/data/pairnets"; return ;;
    "$PLACE_DATA"/*|"$PLACE_PROGRAM"/*|"$PLACE_DATA"|"$PLACE_PROGRAM")
      echo "$dir is inside Pairnets' own folders; choose a folder outside them"; return ;;
    /home|/media|/mnt|/srv|/opt|/var|/var/lib)
      echo "$dir is a top folder; choose a folder inside it, for example $dir/pairnets"; return ;;
  esac
  return 0
}

# --- Facts about the machine ---------------------------------------------------------------------------

# place_current <kind>: the folder the files (or program) really are in now: the chosen folder when our
# mount unit shows it at the fixed path, else the fixed path itself.
place_current() {
  local unit what
  unit="$PLACE_UNIT_DIR/$(place_unit "$1")"
  if [[ -f "$unit" ]]; then
    what="$(sed -n 's/^What=//p' "$unit" | head -n1)"
    if [[ -n "$what" ]]; then printf '%s' "$what"; return; fi
  fi
  place_link "$1"
}

# True when something other than our unit is mounted on the fixed path (the manual way in DEPLOY.md).
place_mounted_by_hand() { # place_mounted_by_hand <kind>
  local link
  link="$(place_link "$1")"
  findmnt -rn --mountpoint "$link" >/dev/null 2>&1 && [[ ! -f "$PLACE_UNIT_DIR/$(place_unit "$1")" ]]
}

# The nearest folder of <path> that exists (a folder about to be made lives on that one's drive).
place_existing_parent() {
  local p="$1"
  while [[ ! -e "$p" && "$p" != / ]]; do p="$(dirname "$p")"; done
  printf '%s' "$p"
}

# place_mount_of <path>: "target fstype options avail-bytes source" of the filesystem holding <path>.
place_mount_of() {
  local p
  p="$(place_existing_parent "$1")"
  findmnt -rn -b -o TARGET,FSTYPE,OPTIONS,AVAIL,SOURCE --target "$p" 2>/dev/null | head -n1
}

place_human() { # place_human <bytes>
  numfmt --to=iec --suffix=B --format='%.1f' "$1" 2>/dev/null | sed 's/\.0B$/B/' || echo "$1 bytes"
}

# Why a filesystem type can't hold a nest, or nothing.
place_fs_problem() { # place_fs_problem <fstype>
  case "$1" in
    vfat|msdos|exfat|ntfs|ntfs3|fuseblk|hfsplus) echo "this drive has no Linux file permissions ($1); format it as ext4 to use it" ;;
    nfs|nfs4|cifs|smb3|smbfs|sshfs|fuse.sshfs|9p|virtiofs|drvfs|fuse.*) echo "this is a network or shared drive ($1), which can lose files when the connection drops" ;;
    tmpfs|ramfs) echo "this drive is in memory ($1) and is emptied on every restart" ;;
    overlay|squashfs|iso9660|udf) echo "this drive can't be written to ($1)" ;;
    *) [[ "$PLACE_GOOD_FS" == *" $1 "* ]] || echo "Pairnets doesn't know this kind of drive ($1)" ;;
  esac
}

# Bytes in use under <folder> (what a move has to copy).
place_used() { du -sxB1 "$1" 2>/dev/null | awk '{print $1}' || echo 0; }

# --- The drive list (installer questions, and drives.json for the apps) -----------------------------------

# Prints one line per drive: mount<TAB>label<TAB>fstype<TAB>avail<TAB>size<TAB>problem (empty when usable).
# The main disk (the one holding /var/lib) comes first.
place_drives() {
  local main seen=" " target source fstype avail size label problem
  main="$(findmnt -rn -o TARGET --target /var/lib 2>/dev/null | head -n1)"
  while IFS=$'\t' read -r target source fstype avail size; do
    target="$(printf '%b' "$target")" # findmnt -r escapes spaces as \x20
    case "$target" in
      /boot|/boot/*|/proc*|/sys*|/dev*|/run/user*|/run/credentials*|/snap/*|/var/snap/*|/var/lib/docker/*|/var/lib/containers/*|"$PLACE_DATA"|"$PLACE_DATA"/*|"$PLACE_PROGRAM"|"$PLACE_PROGRAM"/*) continue ;;
      /run/*) [[ "$target" == /run/media/* ]] || continue ;;
      /usr*|/etc*|/var/log*|/tmp*|/var/tmp*) continue ;;
    esac
    case "$fstype" in
      rootfs|proc|sysfs|devtmpfs|devpts|cgroup*|securityfs|pstore|bpf|debugfs|tracefs|mqueue|hugetlbfs|configfs|fusectl|autofs|binfmt_misc|efivarfs|nsfs|rpc_pipefs|squashfs|overlay|iso9660|ramfs|tmpfs) continue ;;
    esac
    [[ "$seen" != *" $source "* ]] || continue # the same drive mounted twice: listed once
    seen+="$source "
    label=""
    if [[ "$source" == /dev/* ]]; then label="$(lsblk -no LABEL "$source" 2>/dev/null | head -n1 || true)"; fi
    if [[ "$target" == "$main" ]]; then label="Main disk"; fi
    problem="$(place_fs_problem "$fstype")"
    if [[ -z "$problem" ]] && findmnt -rn -o OPTIONS --mountpoint "$target" 2>/dev/null | grep -qE '(^|,)ro(,|$)'; then
      problem="this drive is read-only"
    fi
    if [[ "$target" == "$main" ]]; then
      printf '0\037%s\037%s\037%s\037%s\037%s\037%s\n' "$target" "$label" "$fstype" "$avail" "$size" "$problem"
    else
      printf '1\037%s\037%s\037%s\037%s\037%s\037%s\n' "$target" "$label" "$fstype" "$avail" "$size" "$problem"
    fi
  done < <(findmnt -rn -b -o TARGET,SOURCE,FSTYPE,AVAIL,SIZE 2>/dev/null | awk -v OFS='\t' '{print $1,$2,$3,$4,$5}') |
    sort -t$'\037' -k1,1 -k2,2 | cut -d$'\037' -f2-
}

# The folder a place on a drive gets: the main disk keeps the fixed path, another drive gets a folder on it.
place_folder_on() { # place_folder_on <kind> <mount> <main-disk mount>
  local kind="$1" mount="$2" main="$3"
  if [[ "$mount" == "$main" ]]; then place_link "$kind"; return; fi
  case "$kind" in
    data) printf '%s/pairnets' "${mount%/}" ;;
    program) printf '%s/pairnets-program' "${mount%/}" ;;
  esac
}

place_json_escape() { local s=${1//\\/\\\\}; s=${s//\"/\\\"}; s=${s//$'\t'/ }; printf '%s' "${s//$'\n'/ }"; }

# drives.json for the apps: the drives and where the files are now.
place_drives_json() {
  local first=1 mount label fstype avail size problem main current
  main="$(findmnt -rn -o TARGET --target /var/lib 2>/dev/null | head -n1)"
  current="$(place_current data)"
  printf '{"current":"%s","mainDisk":"%s","byHand":%s,"listedAt":"%s","drives":[' \
    "$(place_json_escape "$current")" "$(place_json_escape "$PLACE_DATA")" \
    "$(place_mounted_by_hand data && echo true || echo false)" "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  while IFS=$'\037' read -r mount label fstype avail size problem; do
    (( first )) || printf ','
    first=0
    printf '{"mount":"%s","label":"%s","fs":"%s","free":%s,"total":%s,"folder":"%s","problem":"%s","main":%s}' \
      "$(place_json_escape "$mount")" "$(place_json_escape "$label")" "$(place_json_escape "$fstype")" \
      "${avail:-0}" "${size:-0}" "$(place_json_escape "$(place_folder_on data "$mount" "$main")")" \
      "$(place_json_escape "$problem")" "$([[ "$mount" == "$main" ]] && echo true || echo false)"
  done < <(place_drives)
  printf ']}\n'
}

# --- Checks before anything is changed --------------------------------------------------------------------

# place_check <kind> <folder> <bytes to copy>: stops (exit 1) with a plain reason when the folder can't be
# used. Sets PLACE_WARNING when it can, but the drive is small.
PLACE_WARNING=""
place_check() {
  local kind="$1" dir="${2%/}" need="$3" problem mount fstype options avail parent link
  link="$(place_link "$kind")"
  PLACE_WARNING=""
  problem="$(place_path_problem "$kind" "$dir")"
  [[ -z "$problem" ]] || place_die "$problem"
  [[ "$dir" != "$link" ]] || return 0 # the main disk: checked as it is used today
  parent="$(place_existing_parent "$dir")"
  # A link anywhere on the way could point somewhere else later (or into a system folder now).
  [[ "$(realpath -m "$dir")" == "$dir" ]] || place_die "$dir goes through a symbolic link; give the real folder: $(realpath -m "$dir")"
  [[ -d "$parent" ]] || place_die "$parent is a file, not a folder"
  if [[ -e "$dir" ]]; then
    [[ -d "$dir" ]] || place_die "$dir is a file, not a folder"
    [[ -z "$(ls -A "$dir" 2>/dev/null)" ]] || place_die "$dir is not empty; choose an empty or new folder, so nothing in it is mixed up with your nest"
  fi
  read -r mount fstype options avail _ <<<"$(place_mount_of "$dir")"
  [[ -n "$mount" ]] || place_die "could not tell which drive $dir is on"
  problem="$(place_fs_problem "$fstype")"
  [[ -z "$problem" ]] || place_die "$dir can't be used: $problem"
  [[ ",$options," != *,ro,* ]] || place_die "$dir can't be used: the drive $mount is read-only"
  if [[ "$kind" == program && ",$options," == *,noexec,* ]]; then
    place_die "$dir can't hold the program: the drive $mount doesn't allow running programs (mounted noexec)"
  fi
  # Not mounted: a folder under /mnt or /media with no drive in it would fill the main disk instead.
  case "$dir" in
    /mnt/*|/media/*|/run/media/*)
      if [[ "$mount" == / || "$mount" == "$(findmnt -rn -o TARGET --target /var/lib | head -n1)" ]]; then
        place_die "no drive is mounted at ${dir%/*}; plug it in or mount it first (sudo mount ...), then run this again"
      fi ;;
  esac
  avail="${avail:-0}"
  if (( avail < PLACE_MIN_FREE || avail < need + PLACE_MIN_FREE )); then
    place_die "$mount has $(place_human "$avail") free; it needs at least $(place_human $((need + PLACE_MIN_FREE)))"
  fi
  if (( avail - need < PLACE_WARN_FREE )); then
    PLACE_WARNING="$mount will have only $(place_human $((avail - need))) free; your nest needs room to grow"
  fi
  # Can the one who will use it write there? Made now, removed again if it was not there.
  local made=""
  [[ -e "$dir" ]] || { mkdir -p "$dir" && made=1; }
  # As the user who will write there (before the first install made that user: as root).
  local writer=root
  if [[ "$kind" == data ]] && id pairnets >/dev/null 2>&1; then writer=pairnets; chown pairnets:pairnets "$dir"; fi
  # shellcheck disable=SC2016 # (the $ belong to the inner bash)
  if ! (cd / && runuser -u "$writer" -- bash -c 'f="$1/.pairnets-write-test"; : > "$f" && rm -f "$f"' _ "$dir") 2>/dev/null; then
    [[ -z "$made" ]] || rmdir "$dir" 2>/dev/null || true
    place_die "$dir can't be written to"
  fi
  if [[ "$kind" == program ]]; then
    # Some drives say exec but still refuse it (fuse, mount namespaces): run something there to be sure.
    if ! { cp /bin/true "$dir/.pairnets-run-test" && "$dir/.pairnets-run-test"; } 2>/dev/null; then
      rm -f "$dir/.pairnets-run-test"
      [[ -z "$made" ]] || rmdir "$dir" 2>/dev/null || true
      place_die "$dir can't hold the program: programs can't run from that drive"
    fi
    rm -f "$dir/.pairnets-run-test"
  fi
  [[ -z "$made" ]] || rmdir "$dir" 2>/dev/null || true
}

# --- Making and changing the mount -----------------------------------------------------------------------

# place_write_unit <kind> <folder>: the bind mount that shows <folder> at the fixed path.
place_write_unit() {
  local kind="$1" dir="$2" link unit
  link="$(place_link "$kind")"
  unit="$PLACE_UNIT_DIR/$(place_unit "$kind")"
  cat > "$unit.tmp" <<UNIT
# Written by Pairnets (deploy/place.sh): the folder you chose for Pairnets, shown at $link.
# Change it with the install command (--$([[ "$kind" == data ]] && echo data || echo program)-dir) or, for the files, in the app's Settings.
[Unit]
Description=Pairnets $([[ "$kind" == data ]] && echo files || echo program) on $dir
RequiresMountsFor=$dir
AssertPathExists=$dir/$PLACE_MARKER

[Mount]
What=$dir
Where=$link
Type=none
Options=bind

[Install]
WantedBy=local-fs.target
UNIT
  chmod 0644 "$unit.tmp"
  mv -f "$unit.tmp" "$unit"
}

# The server and the updater need the mounts: without the drive they don't start.
place_write_dropins() {
  local svc dir paths=""
  [[ -f "$PLACE_UNIT_DIR/$(place_unit data)" ]] && paths+=" $PLACE_DATA"
  [[ -f "$PLACE_UNIT_DIR/$(place_unit program)" ]] && paths+=" $PLACE_PROGRAM"
  for svc in pairnets-server pairnets-update pairnets-tunnel; do
    dir="$PLACE_UNIT_DIR/$svc.service.d"
    if [[ -z "$paths" ]]; then
      rm -f "$dir/place.conf"
      rmdir "$dir" 2>/dev/null || true
      continue
    fi
    install -d -m 0755 "$dir"
    {
      printf '# Written by Pairnets (deploy/place.sh): needs the drive you chose for Pairnets.\n[Unit]\nRequiresMountsFor=%s\n' "${paths# }"
      # The watchers stop with the mount (a drive that was missing); the server brings them back when it starts.
      if [[ "$svc" == pairnets-server ]]; then printf 'Wants=pairnets-update.path pairnets-place.path\n'; fi
    } > "$dir/place.conf"
    chmod 0644 "$dir/place.conf"
  done
}

place_mount_on() { # place_mount_on <kind>
  local unit
  unit="$(place_unit "$1")"
  systemctl daemon-reload
  systemctl enable "$unit" >/dev/null 2>&1
  systemctl restart "$unit"
  findmnt -rn --mountpoint "$(place_link "$1")" >/dev/null || place_die "the chosen folder could not be mounted at $(place_link "$1") (see: systemctl status $unit)"
}

place_unmount() { # place_unmount <kind>: removes our mount (the files stay in the chosen folder)
  local unit
  unit="$(place_unit "$1")"
  systemctl disable --now "$unit" >/dev/null 2>&1 || true
  if findmnt -rn --mountpoint "$(place_link "$1")" >/dev/null 2>&1; then umount "$(place_link "$1")"; fi
  rm -f "$PLACE_UNIT_DIR/$unit"
  systemctl daemon-reload
}

# place_prepare <kind> <folder>: a fresh install into a chosen place: make the folder and mount it, before
# install.sh puts anything there. Only when the fixed path is still empty (else install.sh moves instead).
place_prepare() {
  local kind="$1" dir="${2%/}" link
  link="$(place_link "$kind")"
  [[ "$dir" != "$link" ]] || return 0
  place_check "$kind" "$dir" 0
  if [[ "$kind" == data ]]; then install -d -m 0700 -o pairnets -g pairnets "$dir"; else install -d -m 0755 -o root -g root "$dir"; fi
  install -m 0644 -o root -g root /dev/null "$dir/$PLACE_MARKER"
  install -d -m 0755 "$link"
  place_write_unit "$kind" "$dir"
  place_write_dropins
  place_mount_on "$kind"
  echo "Pairnets $([[ "$kind" == data ]] && echo "keeps its files" || echo "program goes") in $dir (shown at $link)."
}

# True when the fixed path holds nothing yet (a first install into a chosen place can just mount it).
place_is_empty() { # place_is_empty <kind>
  local link
  link="$(place_link "$1")"
  [[ ! -e "$link" ]] || [[ -d "$link" && -z "$(ls -A "$link" 2>/dev/null)" ]]
}

# --- Status for the apps (the update folder belongs to pairnets: never write there as root) ---------------

place_as_owner() {
  if [[ $EUID -eq 0 ]] && id pairnets >/dev/null 2>&1; then (cd / && runuser -u pairnets -- "$@")
  else "$@"; fi
}

# Writes stdin to <file> in the update folder: written and renamed as pairnets, readable by the server.
place_write_owned() { # place_write_owned <file>
  # shellcheck disable=SC2016 # (the $ belong to the inner bash)
  place_as_owner bash -c 'umask 022; tmp=$1.tmp; cat > "$tmp" && chmod 0644 "$tmp" && mv -f "$tmp" "$1"' _ "$1"
}

PLACE_REPORT=""   # set by --from-request: write place-status.json for the apps
place_status() { # place_status <state> <percent> <message> [old copy]
  [[ -n "$PLACE_REPORT" ]] || return 0
  [[ -d "$PLACE_UPDATE_DIR" ]] || return 0
  printf '{"state":"%s","percent":%s,"message":"%s","oldCopy":"%s","at":"%s"}\n' "$1" "$2" \
    "$(place_json_escape "$3")" "$(place_json_escape "${4:-}")" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" |
    place_write_owned "$PLACE_STATUS" || true
}

place_say() { # place_say <percent> <message>: to the terminal, and to the apps
  echo "$2"
  place_status running "$1" "$2"
}

# --- Moving ----------------------------------------------------------------------------------------------

place_stop_services() {
  local u
  for u in "${PLACE_SERVICES[@]}"; do systemctl stop "$u" 2>/dev/null || true; done
}

place_start_services() {
  local u
  # The path units watch folders on the moved mount, so systemd stopped them with it.
  for u in pairnets-server pairnets-tunnel pairnets-update.path pairnets-update.timer pairnets-place.path; do
    if systemctl is-enabled --quiet "$u" 2>/dev/null; then systemctl start "$u" 2>/dev/null || true; fi
  done
}

# Does the server answer again? (Up to 5 minutes: it checks the data folder before it answers.)
place_healthy() {
  [[ -z "${PAIRNETS_PLACE_NO_HEALTH:-}" ]] || return 0
  local url
  url="$(sed -n 's/^ASPNETCORE_URLS=//p' "$PLACE_ENV_FILE" 2>/dev/null | head -n1 | cut -d';' -f1)"
  url="${url%/}"
  [[ -n "$url" ]] || return 0
  SECONDS=0
  while (( SECONDS < 300 )); do
    if curl -fsS --max-time 5 "$url/api/health" >/dev/null 2>&1; then return 0; fi
    case "$(systemctl is-active pairnets-server 2>/dev/null || true)" in
      active|activating|reloading) sleep 1 ;;
      *) return 1 ;;
    esac
  done
  return 1
}

# Copies with rsync, reporting progress (as a percent between <from> and <to>) to the apps. With "exact" it
# also checks that the copy is the same as the source (when nothing writes to it any more).
place_copy() { # place_copy <source> <target> <from%> <to%> exact|live [extra rsync options]
  local src="$1" dst="$2" from="$3" to="$4" mode="$5" rc tr_cmd=(tr '\r' '\n')
  shift 5
  # The apps' status files (and their .tmp while being written) change during the move: they are not the nest's files.
  local -a opts=(-aHAX --numeric-ids --exclude=/update/place-status.json --exclude=/update/place-status.json.tmp
    --exclude=/update/place-request --exclude=/update/drives.json --exclude=/update/drives.json.tmp "$@")
  if command -v stdbuf >/dev/null; then tr_cmd=(stdbuf -oL tr '\r' '\n'); fi
  set +e
  rsync "${opts[@]}" --info=progress2 --no-inc-recursive "$src/" "$dst/" 2>"$PLACE_WORK/rsync.err" | "${tr_cmd[@]}" |
    { local line pct last=-1
      while IFS= read -r line; do
        if [[ "$line" =~ ([0-9]+)%[[:space:]] ]]; then
          pct=$(( from + BASH_REMATCH[1] * (to - from) / 100 ))
          if (( pct != last )); then last=$pct; place_status running "$pct" "Copying your files to the new drive"; fi
        fi
      done; }
  rc=${PIPESTATUS[0]}
  set -e
  # 24: files vanished while copying (the nest was running). The last pass, with the nest stopped, gets them.
  if (( rc != 0 )) && ! [[ "$mode" == live && $rc -eq 24 ]]; then
    echo "rsync stopped ($rc): $(tail -n 3 "$PLACE_WORK/rsync.err" | tr '\n' ' ')" >&2
    return 1
  fi
  [[ "$mode" == exact ]] || return 0
  # Nothing may differ any more (rsync already checked every file it copied against its checksum). Folder times are
  # left out: writing the status file for the apps touches the update folder's time while this runs.
  [[ -z "$(rsync "${opts[@]}" -n --omit-dir-times --itemize-changes "$src/" "$dst/" 2>&1)" ]]
}

# place_move <kind> <folder> [yes]: copy, check, switch over, start, check health; rolls back on failure.
place_move() {
  local kind="$1" target="${2%/}" yes="${3:-}" link old used stamp staging backup="" answer
  link="$(place_link "$kind")"
  old="$(place_current "$kind")"
  command -v rsync >/dev/null || place_die "rsync is needed to move Pairnets (sudo apt install rsync)"
  ! place_mounted_by_hand "$kind" || place_die "$link is already on another drive, set up by hand (in /etc/fstab). Change it there, or remove that mount and run this again"
  [[ "$target" != "$old" ]] || { echo "Pairnets $([[ "$kind" == data ]] && echo "files are" || echo "program is") already in $target."; place_status succeeded 100 "Your nest already keeps its files there."; return 0; }
  used="$(place_used "$link")"
  if [[ "$target" == "$link" ]]; then
    # Back to the main disk: copy next to the fixed path first, it becomes the folder once the mount is gone.
    staging="${link}-moving-in"
    [[ ! -e "$staging" ]] || place_die "$staging is left over from a move that did not finish; look inside, delete it, and run this again"
    place_check "$kind" "$staging" "$used"
  else
    place_check "$kind" "$target" "$used"
    staging="$target"
  fi
  echo "Moving Pairnets $([[ "$kind" == data ]] && echo files || echo program) ($(place_human "$used")) from $old to $target."
  [[ -z "$PLACE_WARNING" ]] || echo "Note: $PLACE_WARNING."
  echo "Your nest keeps running while the files are copied, then stops for a moment to switch over."
  echo "The old copy is kept until you delete it."
  if [[ -z "$yes" ]]; then
    if { exec 3</dev/tty; } 2>/dev/null; then
      read -r -p "Move? [y/N] " answer <&3 || answer=""
      exec 3<&-
      [[ "$answer" =~ ^[Yy] ]] || { echo "Nothing was changed."; return 1; }
    else
      place_die "no terminal to ask; add --yes to move without asking"
    fi
  fi

  local made=""
  [[ -e "$staging" ]] || made=1
  PLACE_WORK="$(mktemp -d)"
  if [[ "$kind" == data ]]; then install -d -m 0700 -o pairnets -g pairnets "$staging"; else install -d -m 0755 -o root -g root "$staging"; fi
  place_cleanup_staging() {
    if [[ -n "$made" ]]; then rm -rf --one-file-system "$staging"
    else find "$staging" -mindepth 1 -delete 2>/dev/null || true; fi
  }

  place_say 1 "Copying your files to the new drive"
  if ! place_copy "$link" "$staging" 1 85 live; then
    place_cleanup_staging
    place_status failed 0 "Copying to the new drive did not finish. Nothing was changed; your nest keeps its files where they were."
    place_die "copying to $target did not finish; nothing was changed"
  fi
  place_say 86 "Pausing your nest to copy the last changes"
  place_stop_services
  if ! place_copy "$link" "$staging" 86 95 exact --delete; then
    place_start_services
    place_cleanup_staging
    place_status failed 0 "Copying to the new drive did not finish. Nothing was changed; your nest keeps its files where they were."
    place_die "copying to $target did not finish; nothing was changed"
  fi
  place_say 96 "Switching your nest over to the new drive"
  [[ "$target" == "$link" ]] || install -m 0644 -o root -g root /dev/null "$staging/$PLACE_MARKER"

  stamp="$(date +%Y-%m-%d-%H%M%S)"
  if [[ "$old" == "$link" ]]; then
    # Main disk -> drive: the old folder is renamed (kept), an empty one becomes the mount point.
    backup="${link}-before-move-$stamp"
    mv "$link" "$backup"
    install -d -m 0755 "$link"
    place_write_unit "$kind" "$target"
    place_write_dropins
    place_mount_on "$kind"
  elif [[ "$target" == "$link" ]]; then
    # Drive -> main disk: drop the mount, the copy becomes the folder.
    backup="$old"
    place_unmount "$kind"
    place_write_dropins
    rmdir "$link"
    mv "$staging" "$link"
  else
    # Drive -> another drive.
    backup="$old"
    place_unmount "$kind"
    place_write_unit "$kind" "$target"
    place_write_dropins
    place_mount_on "$kind"
  fi
  systemctl daemon-reload
  place_start_services

  if ! place_healthy; then
    echo "The server did not start from the new place: going back to $old." >&2
    place_stop_services
    if [[ "$old" == "$link" ]]; then
      place_unmount "$kind"; place_write_dropins
      rmdir "$link"; mv "$backup" "$link"
    elif [[ "$target" == "$link" ]]; then
      mv "$link" "$staging"; install -d -m 0755 "$link"
      place_write_unit "$kind" "$old"; place_write_dropins; place_mount_on "$kind"
    else
      place_unmount "$kind"; place_write_unit "$kind" "$old"; place_write_dropins; place_mount_on "$kind"
    fi
    systemctl daemon-reload
    place_start_services
    place_status failed 0 "Your nest did not start from the new drive, so it went back to where it was. The copy is left in $target."
    place_die "the server did not start from $target, so it went back to $old (the copy is left in $target)"
  fi

  local done_msg
  done_msg="Done. Your nest now keeps its $([[ "$kind" == data ]] && echo files || echo program) in $target."
  echo "$done_msg"
  echo "The old copy is still in $backup. When everything works, delete it with:"
  echo "  sudo rm -rf '$backup'"
  place_status succeeded 100 "$done_msg The old copy is kept in $backup until you delete it." "$backup"
}

# --- Questions at install time ---------------------------------------------------------------------------

# place_ask <kind>: shows the drives and asks; prints the chosen folder (or nothing for the default).
place_ask() {
  local kind="$1" main i=0 choice answer folder
  local -a mounts=() labels=() frees=() problems=()
  main="$(findmnt -rn -o TARGET --target /var/lib 2>/dev/null | head -n1)"
  while IFS=$'\037' read -r mount label _ avail _ problem; do
    mounts+=("$mount"); labels+=("$label"); frees+=("$avail"); problems+=("$problem")
  done < <(place_drives)
  {
    echo
    if [[ "$kind" == data ]]; then echo "Where should Pairnets keep your files?"; else echo "Where should the Pairnets program go?"; fi
    echo
    for i in "${!mounts[@]}"; do
      folder="$(place_folder_on "$kind" "${mounts[$i]}" "$main")"
      if [[ -n "${problems[$i]}" ]]; then
        printf '  %d) %-12s %-28s %s free  (can'"'"'t use: %s)\n' $((i + 1)) "${labels[$i]:-drive}" "$folder" "$(place_human "${frees[$i]}")" "${problems[$i]}"
      else
        printf '  %d) %-12s %-28s %s free%s\n' $((i + 1)) "${labels[$i]:-drive}" "$folder" "$(place_human "${frees[$i]}")" "$([[ "${mounts[$i]}" == "$main" ]] && echo "  (default)")"
      fi
    done
    printf '  %d) Type a folder...\n' $(( ${#mounts[@]} + 1 ))
    echo
  } >&2
  while true; do
    read -r -p "Pick 1-$(( ${#mounts[@]} + 1 )) [1]: " answer <&3 || answer=""
    answer="${answer:-1}"
    if [[ "$answer" =~ ^[0-9]+$ ]] && (( answer >= 1 && answer <= ${#mounts[@]} )); then
      choice=$((answer - 1))
      if [[ -n "${problems[$choice]}" ]]; then echo "That drive can't be used: ${problems[$choice]}." >&2; continue; fi
      folder="$(place_folder_on "$kind" "${mounts[$choice]}" "$main")"
    elif [[ "$answer" == "$(( ${#mounts[@]} + 1 ))" ]]; then
      read -r -p "Folder (full path, for example /mnt/data/pairnets): " folder <&3 || folder=""
      folder="${folder%/}"
      local problem
      problem="$(place_path_problem "$kind" "$folder")"
      if [[ -n "$problem" ]]; then echo "That folder can't be used: $problem." >&2; continue; fi
    else
      echo "Type a number from the list." >&2; continue
    fi
    if [[ "$folder" == "$(place_link "$kind")" ]]; then printf ''; return 0; fi
    # The full check (free space, writable, empty), without stopping the questions on a no.
    if ! (place_check "$kind" "$folder" 0) >/dev/null; then continue; fi
    printf '%s' "$folder"
    return 0
  done
}

# place_ask_install: the questions on a first install in a terminal. Sets PLACE_DATA_CHOICE and
# PLACE_PROGRAM_CHOICE (empty = the default).
# shellcheck disable=SC2034 # (install.sh reads them)
PLACE_DATA_CHOICE=""
# shellcheck disable=SC2034
PLACE_PROGRAM_CHOICE=""
# shellcheck disable=SC2034
place_ask_install() {
  { exec 3</dev/tty; } 2>/dev/null || return 0
  PLACE_DATA_CHOICE="$(place_ask data)"
  local answer=""
  read -r -p "Put the program somewhere else too? (it is small; most people keep /opt/pairnets) [y/N] " answer <&3 || answer=""
  if [[ "$answer" =~ ^[Yy] ]]; then PLACE_PROGRAM_CHOICE="$(place_ask program)"; fi
  exec 3<&-
}

# --- Requests from the apps (run by pairnets-place.service as root) --------------------------------------

place_from_request() {
  PLACE_REPORT=1
  local req action="" folder=""
  # Read as pairnets: a link planted in its own folder then only reaches what pairnets may read anyway.
  req="$(place_as_owner head -c 4096 "$PLACE_REQUEST" 2>/dev/null || true)"
  place_as_owner rm -f "$PLACE_REQUEST"
  while IFS= read -r line; do
    case "$line" in
      action=*) action="${line#action=}" ;;
      data_dir=*) folder="${line#data_dir=}" ;;
    esac
  done <<<"$req"
  case "$action" in
    list|"")
      place_drives_json | place_write_owned "$PLACE_DRIVES"
      ;;
    move)
      local problem
      problem="$(place_path_problem data "$folder")"
      if [[ -n "$problem" ]]; then place_status failed 0 "That folder can't be used: $problem."; return 0; fi
      # A check that refuses stops before place_move reports anything: tell the apps why.
      place_status running 0 "Checking the drive"
      local out rc=0 why
      out="$( (place_move data "$folder" yes) 2>&1)" || rc=$?
      printf '%s
' "$out"
      if (( rc != 0 )) && place_as_owner cat "$PLACE_STATUS" 2>/dev/null | grep -q '"state":"running"'; then
        why="$(grep '^error: ' <<<"$out" | tail -n1 | sed 's/^error: //')"
        place_status failed 0 "That drive can't be used: ${why:-the move did not start}. Nothing was changed."
      fi
      place_drives_json | place_write_owned "$PLACE_DRIVES" || true
      ;;
    *) place_status failed 0 "Pairnets did not understand the request from the app." ;;
  esac
}

place_main() {
  [[ $EUID -eq 0 ]] || place_die "run as root (sudo $0)"
  case "${1:-}" in
    list)
      local main
      main="$(findmnt -rn -o TARGET --target /var/lib 2>/dev/null | head -n1)"
      while IFS=$'\037' read -r mount label fstype avail size problem; do
        printf '%-28s %-12s %-6s %10s free of %-10s %s\n' "$mount" "${label:-}" "$fstype" "$(place_human "$avail")" "$(place_human "$size")" "${problem:+(cannot use: $problem)}"
      done < <(place_drives)
      ;;
    show)
      echo "Files:   $(place_current data)$(place_mounted_by_hand data && echo " (mounted by hand)")"
      echo "Program: $(place_current program)$(place_mounted_by_hand program && echo " (mounted by hand)")"
      ;;
    move)
      local kind="${2:-}" dir="${3:-}" yes=""
      [[ ( "$kind" == data || "$kind" == program ) && -n "$dir" ]] || place_die "usage: $0 move data|program <folder> [--yes]"
      [[ "${4:-}" == --yes ]] && yes=1
      exec 9>"$PLACE_LOCK"
      flock -n 9 || place_die "another move is running"
      place_move "$kind" "$dir" "$yes"
      ;;
    --from-request)
      exec 9>"$PLACE_LOCK"
      flock -n 9 || exit 0
      place_from_request
      ;;
    *) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
  esac
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  set -Eeuo pipefail
  place_main "$@"
fi
