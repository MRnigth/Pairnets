#!/usr/bin/env bash
# Installs the Tether desktop app for the current user (no root):
#   ~/.local/opt/tether/      program
#   ~/.local/bin/tether       command
#   ~/.local/share/applications/tether.desktop   app menu entry
# Run it from the extracted tether-desktop-linux-x64 folder.
set -euo pipefail

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PREFIX="${XDG_DATA_HOME:-$HOME/.local/share}"
OPT="$HOME/.local/opt/tether"
BIN="$HOME/.local/bin"

[[ $EUID -ne 0 ]] || { echo "error: run as your normal user, not root" >&2; exit 1; }
[[ -x "$SRC/Tether" ]] || { echo "error: Tether binary not found next to this script" >&2; exit 1; }

pkill -x Tether 2>/dev/null || true
mkdir -p "$OPT" "$BIN" "$PREFIX/applications" "$PREFIX/icons/hicolor/256x256/apps"
cp -f "$SRC/Tether" "$OPT/Tether"
chmod 755 "$OPT/Tether"
cp -f "$SRC/tether.png" "$PREFIX/icons/hicolor/256x256/apps/tether.png"
ln -sf "$OPT/Tether" "$BIN/tether"

cat > "$PREFIX/applications/tether.desktop" <<DESKTOP
[Desktop Entry]
Type=Application
Name=Tether
Comment=Keep a folder in sync through your own server
Exec="$OPT/Tether"
Icon=tether
Terminal=false
Categories=Utility;FileTools;
StartupNotify=false
DESKTOP
if command -v update-desktop-database >/dev/null; then
  update-desktop-database "$PREFIX/applications" >/dev/null 2>&1 || true
fi

echo "Tether is installed. Start it from your app menu, or run: tether"
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
  echo "(Ubuntu enables it by default). Without it, open Tether from the app menu."
fi

if [[ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]]; then
  nohup "$OPT/Tether" >/dev/null 2>&1 &
  echo "Tether is starting."
fi
