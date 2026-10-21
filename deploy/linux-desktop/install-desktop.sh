#!/usr/bin/env bash
# Installs the Pairnets desktop app for the current user (no root):
#   ~/.local/opt/pairnets/      program (or the folder you choose, on any drive)
#   ~/.local/bin/pairnets       command
#   ~/.local/share/applications/pairnets.desktop   app menu entry
# Run it from the extracted pairnets-desktop-linux-x64 folder.
#
#   ./install-desktop.sh                          a first install in a terminal asks where the program goes
#   ./install-desktop.sh --program-dir <folder>   put it there (moves an installed app there)
#   ./install-desktop.sh --no-questions           don't ask; keep the place it has (or the default)
#
# Installing again keeps the place chosen before: ~/.local/bin/pairnets points at it. The app's own updates
# replace the program wherever it is, and Settings > This computer > Program folder moves it later.
set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PREFIX="${XDG_DATA_HOME:-$HOME/.local/share}"
DEFAULT_OPT="$HOME/.local/opt/pairnets"
BIN="$HOME/.local/bin"
NEED_FREE=$((300 * 1024 * 1024)) # the program is about 100 MB; room for an update next to it

die() { echo "error: $*" >&2; exit 1; }

CHOICE=""
NO_QUESTIONS=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --program-dir)
      [[ $# -ge 2 && -n "$2" && "$2" != -* ]] || { echo "error: --program-dir needs a folder, for example: --program-dir /mnt/data/pairnets" >&2; exit 2; }
      CHOICE="${2%/}"; shift 2 ;;
    --program-dir=*) CHOICE="${1#*=}"; CHOICE="${CHOICE%/}"; [[ -n "$CHOICE" ]] || { echo "error: --program-dir needs a folder" >&2; exit 2; }; shift ;;
    --no-questions) NO_QUESTIONS=1; shift ;;
    -h|--help) sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "error: unknown option: $1 (options: --program-dir <folder>, --no-questions)" >&2; exit 2 ;;
  esac
done

[[ $EUID -ne 0 ]] || { echo "error: run as your normal user, not root" >&2; exit 1; }
[[ -x "$SRC/Pairnets" ]] || { echo "error: Pairnets binary not found next to this script" >&2; exit 1; }

# Where it is now: the command's link says (an older install, or one moved in Settings, may be anywhere).
CURRENT=""
if [[ -L "$BIN/pairnets" ]]; then
  target="$(readlink -f "$BIN/pairnets" 2>/dev/null || true)"
  if [[ -n "$target" && "$(basename "$target")" == Pairnets ]]; then CURRENT="$(dirname "$target")"; fi
fi

# Why <folder> can't hold the program (a sentence), or nothing.
folder_problem() {
  local dir="$1" parent mount fstype options avail
  [[ "$dir" == /* ]] || { echo "the folder must be a full path starting with /"; return; }
  [[ "/$dir/" != */../* && "/$dir/" != */./* ]] || { echo "the folder must not contain . or .. parts"; return; }
  case "$dir" in
    /|/bin|/bin/*|/boot|/boot/*|/dev|/dev/*|/etc|/etc/*|/lib|/lib/*|/lib64|/lib64/*|/proc|/proc/*|/run|/sbin|/sbin/*|/sys|/sys/*|/usr|/usr/*|/var|/var/*)
      echo "$dir is a system folder"; return ;;
  esac
  if [[ -e "$dir" ]]; then
    [[ -d "$dir" ]] || { echo "$dir is a file, not a folder"; return; }
    # Empty, or a Pairnets program folder (installing again there).
    if [[ -n "$(ls -A "$dir" 2>/dev/null)" && ! -f "$dir/Pairnets" ]]; then echo "$dir is not empty; choose an empty or new folder"; return; fi
  fi
  parent="$dir"
  while [[ ! -e "$parent" && "$parent" != / ]]; do parent="$(dirname "$parent")"; done
  [[ -w "$parent" ]] || { echo "you can't write to $parent"; return; }
  read -r mount fstype options avail <<<"$(findmnt -rn -b -o TARGET,FSTYPE,OPTIONS,AVAIL --target "$parent" 2>/dev/null | head -n1)"
  case "$fstype" in
    vfat|msdos|exfat|ntfs|ntfs3|fuseblk) echo "the drive $mount has no Linux file permissions ($fstype), so the program can't run from it"; return ;;
    nfs|nfs4|cifs|smb3|sshfs|fuse.sshfs|9p) echo "the drive $mount is a network drive ($fstype)"; return ;;
    tmpfs) echo "the drive $mount is emptied on every restart"; return ;;
  esac
  [[ ",$options," != *,noexec,* ]] || { echo "the drive $mount doesn't allow running programs (mounted noexec)"; return; }
  if [[ -n "$avail" ]] && (( avail < NEED_FREE )); then echo "the drive $mount has too little free space"; return; fi
}

# The places to offer: your home folder, then drives you can write to.
ask_place() {
  local -a folders=() labels=()
  local mount avail fstype folder i answer problem
  folders+=("$DEFAULT_OPT"); labels+=("Home folder")
  while IFS=' ' read -r mount fstype avail; do
    mount="$(printf '%b' "$mount")"
    case "$mount" in /media/*|/run/media/*|/mnt/*) ;; *) continue ;; esac
    [[ -w "$mount" ]] || continue
    folder="${mount%/}/pairnets"
    [[ -z "$(folder_problem "$folder")" ]] || continue
    folders+=("$folder"); labels+=("$(basename "$mount") ($(numfmt --to=iec --suffix=B "$avail" 2>/dev/null || echo "$avail bytes") free)")
  done < <(findmnt -rn -b -o TARGET,FSTYPE,AVAIL 2>/dev/null)
  {
    echo
    echo "Where should the Pairnets program go?"
    echo
    for i in "${!folders[@]}"; do
      printf '  %d) %-36s %s%s\n' $((i + 1)) "${folders[$i]}" "${labels[$i]}" "$([[ $i -eq 0 ]] && echo "  (default)")"
    done
    printf '  %d) Type a folder...\n' $(( ${#folders[@]} + 1 ))
    echo
  } >&2
  while true; do
    read -r -p "Pick 1-$(( ${#folders[@]} + 1 )) [1]: " answer <&3 || answer=""
    answer="${answer:-1}"
    if [[ "$answer" =~ ^[0-9]+$ ]] && (( answer >= 1 && answer <= ${#folders[@]} )); then
      folder="${folders[$((answer - 1))]}"
    elif [[ "$answer" == "$(( ${#folders[@]} + 1 ))" ]]; then
      read -r -p "Folder (full path): " folder <&3 || folder=""
      folder="${folder%/}"
    else
      echo "Type a number from the list." >&2; continue
    fi
    problem="$(folder_problem "$folder")"
    if [[ -n "$problem" ]]; then echo "That folder can't be used: $problem." >&2; continue; fi
    printf '%s' "$folder"
    return 0
  done
}

if [[ -z "$CHOICE" && -z "$CURRENT" && -z "$NO_QUESTIONS" && -t 1 ]] && { exec 3</dev/tty; } 2>/dev/null; then
  CHOICE="$(ask_place)"
  exec 3<&-
fi
OPT="${CHOICE:-${CURRENT:-$DEFAULT_OPT}}"
if [[ "$OPT" != "$DEFAULT_OPT" && "$OPT" != "$CURRENT" ]]; then
  problem="$(folder_problem "$OPT")"
  [[ -z "$problem" ]] || die "$OPT can't be used: $problem"
fi

pkill -x Pairnets 2>/dev/null || true

# Pairnets used to be called Tether: stop and remove the old program (not its settings and sync state,
# which Pairnets takes over when it first starts, together with "start at login").
pkill -x Tether 2>/dev/null || true
rm -rf "$HOME/.local/opt/tether"
rm -f "$BIN/tether" "$PREFIX/applications/tether.desktop" "$PREFIX/icons/hicolor/256x256/apps/tether.png"

mkdir -p "$OPT" "$BIN" "$PREFIX/applications" "$PREFIX/icons/hicolor/256x256/apps"
cp -f "$SRC/Pairnets" "$OPT/Pairnets"
chmod 755 "$OPT/Pairnets"
# The drive might still refuse to run programs: try a harmless one before pointing anything at it.
if cp /bin/true "$OPT/.pairnets-run-test" 2>/dev/null; then
  "$OPT/.pairnets-run-test" 2>/dev/null || { rm -f "$OPT/.pairnets-run-test"; die "programs can't run from $OPT; choose another folder with --program-dir"; }
  rm -f "$OPT/.pairnets-run-test"
fi
cp -f "$SRC/pairnets.png" "$PREFIX/icons/hicolor/256x256/apps/pairnets.png"
ln -sfn "$OPT/Pairnets" "$BIN/pairnets"

cat > "$PREFIX/applications/pairnets.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Pairnets
Comment=Keep a folder in sync through your own server
Exec="$OPT/Pairnets" %u
Icon=pairnets
Terminal=false
Categories=Utility;FileTools;
# The nest's website opens pairnets:// after approving this computer, to bring Pairnets back
# to the front. The link carries nothing: the app fetches its key through its own secret poll.
MimeType=x-scheme-handler/pairnets;
StartupNotify=false
DESKTOP
if command -v update-desktop-database >/dev/null; then
  update-desktop-database "$PREFIX/applications" >/dev/null 2>&1 || true
fi
# "Start Pairnets when I log in" names the program's place: point it at the new one.
AUTOSTART="${XDG_CONFIG_HOME:-$HOME/.config}/autostart/pairnets.desktop"
if [[ -f "$AUTOSTART" ]]; then
  sed -i "s#^Exec=.*#Exec=\"$OPT/Pairnets\"#" "$AUTOSTART"
fi

# Moved: the old program goes (only Pairnets' own files; the folder too once it is empty).
if [[ -n "$CURRENT" && "$CURRENT" != "$OPT" && -f "$CURRENT/Pairnets" ]]; then
  rm -f "$CURRENT/Pairnets" "$CURRENT/Pairnets.old" "$CURRENT/Pairnets.new"
  rmdir "$CURRENT" 2>/dev/null || true
  echo "Moved Pairnets from $CURRENT to $OPT."
fi

echo "Pairnets is installed in $OPT. Start it from your app menu, or run: pairnets"
missing=()
command -v secret-tool >/dev/null || missing+=("libsecret-tools (keeps the token in your keyring; without it the token is stored in a private file)")
command -v notify-send >/dev/null || missing+=("libnotify-bin (desktop notifications)")
command -v gio >/dev/null || missing+=("libglib2.0-bin (move deleted files to the Trash)")
if [[ ${#missing[@]} -gt 0 ]]; then
  echo
  echo "Recommended packages (sudo apt install ...):"
  printf '  - %s\n' "${missing[@]}"
fi
if [[ "${XDG_CURRENT_DESKTOP:-}" == *GNOME* ]]; then
  echo
  echo "GNOME shows tray icons only with the 'AppIndicator and KStatusNotifierItem Support' extension"
  echo "(Ubuntu enables it by default). Without it, open Pairnets from the app menu."
fi

if [[ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]]; then
  nohup "$OPT/Pairnets" >/dev/null 2>&1 &
  echo "Pairnets is starting."
fi
