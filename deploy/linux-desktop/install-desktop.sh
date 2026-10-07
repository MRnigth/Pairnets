#!/usr/bin/env bash
# Installs the Pairnets desktop app for the current user (no root):
#   ~/.local/opt/pairnets/      program
#   ~/.local/bin/pairnets       command
#   ~/.local/share/applications/pairnets.desktop   app menu entry
# Run it from the extracted pairnets-desktop-linux-x64 folder.
set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PREFIX="${XDG_DATA_HOME:-$HOME/.local/share}"
OPT="$HOME/.local/opt/pairnets"
BIN="$HOME/.local/bin"

[[ $EUID -ne 0 ]] || { echo "error: run as your normal user, not root" >&2; exit 1; }
[[ -x "$SRC/Pairnets" ]] || { echo "error: Pairnets binary not found next to this script" >&2; exit 1; }

pkill -x Pairnets 2>/dev/null || true

# Pairnets used to be called Tether: stop and remove the old program (not its settings and sync state,
# which Pairnets takes over when it first starts, together with "start at login").
pkill -x Tether 2>/dev/null || true
rm -rf "$HOME/.local/opt/tether"
rm -f "$BIN/tether" "$PREFIX/applications/tether.desktop" "$PREFIX/icons/hicolor/256x256/apps/tether.png"

mkdir -p "$OPT" "$BIN" "$PREFIX/applications" "$PREFIX/icons/hicolor/256x256/apps"
cp -f "$SRC/Pairnets" "$OPT/Pairnets"
chmod 755 "$OPT/Pairnets"
cp -f "$SRC/pairnets.png" "$PREFIX/icons/hicolor/256x256/apps/pairnets.png"
ln -sf "$OPT/Pairnets" "$BIN/pairnets"

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

echo "Pairnets is installed. Start it from your app menu, or run: pairnets"
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
