#!/usr/bin/env bash
# One-line installer for Tether. Downloads the latest release from GitHub, verifies its SHA-256
# checksum, and installs it.
#
#   Ubuntu server:        curl -fsSL https://raw.githubusercontent.com/MRnigth/Tether/main/deploy/get.sh | sudo bash
#     (options for install.sh go after "-s --":  ... | sudo bash -s -- --bind 100.x.y.z)
#     No Tailscale, no open ports (Cloudflare Tunnel, see docs/HOWTO.md):
#                         ... | sudo bash -s -- --cloudflare-tunnel --public-url https://tether.example.com
#   Linux desktop app:    curl -fsSL https://raw.githubusercontent.com/MRnigth/Tether/main/deploy/get.sh | bash -s -- --desktop
#   macOS app:            curl -fsSL https://raw.githubusercontent.com/MRnigth/Tether/main/deploy/get.sh | bash -s -- --mac
#
# Environment overrides: TETHER_VERSION=v1.2.3 (default: latest), TETHER_REPO=owner/name,
# TETHER_BASE_URL=<url or local folder with the release files> (for testing/mirrors).
set -euo pipefail

REPO="${TETHER_REPO:-MRnigth/Tether}"
VERSION="${TETHER_VERSION:-latest}"
MODE=server
ARGS=()
for a in "$@"; do
  case "$a" in
    --desktop) MODE=desktop ;;
    --mac|--macos) MODE=mac ;;
    --server) MODE=server ;;
    *) ARGS+=("$a") ;;
  esac
done

die() { echo "error: $*" >&2; exit 1; }
info() { echo "==> $*"; }

base_url() {
  if [[ -n "${TETHER_BASE_URL:-}" ]]; then echo "$TETHER_BASE_URL"
  elif [[ "$VERSION" == latest ]]; then echo "https://github.com/$REPO/releases/latest/download"
  else echo "https://github.com/$REPO/releases/download/$VERSION"; fi
}

fetch() { # fetch <asset> <dest-dir>
  local src; src="$(base_url)/$1"
  if [[ -d "$(base_url)" ]]; then cp "$src" "$2/$1"
  else curl -fL --retry 3 --progress-bar -o "$2/$1" "$src" || die "download failed: $src"; fi
}

verify() { # verify <asset> <dir>
  local line
  line="$(grep -E "[[:space:]]\*?$1\$" "$2/SHA256SUMS.txt" || true)"
  [[ -n "$line" ]] || die "$1 is not listed in SHA256SUMS.txt"
  if command -v sha256sum >/dev/null; then
    (cd "$2" && echo "$line" | sha256sum --check --status) || die "checksum mismatch for $1"
  else
    local expected actual
    expected="${line%% *}"
    actual="$(shasum -a 256 "$2/$1" | cut -d' ' -f1)"
    [[ "$expected" == "$actual" ]] || die "checksum mismatch for $1"
  fi
  info "checksum OK: $1"
}

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
command -v curl >/dev/null || [[ -n "${TETHER_BASE_URL:-}" ]] || die "curl is required"

case "$MODE" in
  server)
    [[ "$(uname -s)" == Linux ]] || die "the server runs on Linux (Ubuntu)"
    [[ $EUID -eq 0 ]] || die "run with sudo:  curl -fsSL .../get.sh | sudo bash"
    [[ "$(uname -m)" == x86_64 ]] || die "only x86_64 (amd64) servers are supported"
    ASSET=tether-server-linux-x64.tar.gz
    info "downloading Tether server ($VERSION)"
    fetch SHA256SUMS.txt "$WORK"; fetch "$ASSET" "$WORK"; verify "$ASSET" "$WORK"
    tar -xzf "$WORK/$ASSET" -C "$WORK"
    info "running install.sh"
    "$WORK/tether-server-linux-x64/install.sh" "${ARGS[@]}"
    ;;

  desktop)
    [[ "$(uname -s)" == Linux ]] || die "--desktop is for Linux; on a Mac use --mac"
    [[ $EUID -ne 0 ]] || die "install the desktop app as your normal user (without sudo)"
    [[ "$(uname -m)" == x86_64 ]] || die "only x86_64 desktops are supported"
    ASSET=tether-desktop-linux-x64.tar.gz
    info "downloading Tether for Linux ($VERSION)"
    fetch SHA256SUMS.txt "$WORK"; fetch "$ASSET" "$WORK"; verify "$ASSET" "$WORK"
    tar -xzf "$WORK/$ASSET" -C "$WORK"
    "$WORK/tether-desktop-linux-x64/install-desktop.sh" "${ARGS[@]}"
    ;;

  mac)
    [[ "$(uname -s)" == Darwin ]] || die "--mac must be run on a Mac"
    case "$(uname -m)" in
      arm64) ASSET=Tether-macos-arm64.zip ;;
      x86_64) ASSET=Tether-macos-x64.zip ;;
      *) die "unsupported Mac architecture $(uname -m)" ;;
    esac
    info "downloading Tether for macOS ($VERSION)"
    fetch SHA256SUMS.txt "$WORK"; fetch "$ASSET" "$WORK"; verify "$ASSET" "$WORK"
    DEST=/Applications
    [[ -w "$DEST" ]] || DEST="$HOME/Applications"
    mkdir -p "$DEST"
    pkill -x Tether 2>/dev/null || true
    rm -rf "$DEST/Tether.app"
    ditto -x -k "$WORK/$ASSET" "$DEST"
    # Files fetched with curl are not quarantined; clear the flag anyway in case of a mirror.
    xattr -dr com.apple.quarantine "$DEST/Tether.app" 2>/dev/null || true
    info "installed $DEST/Tether.app"
    open "$DEST/Tether.app"
    echo "Tether is starting: look for its icon in the menu bar (top right)."
    ;;
esac
