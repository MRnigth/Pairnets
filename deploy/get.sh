#!/usr/bin/env bash
# One-line installer for Pairnets. Downloads the latest release from GitHub, verifies its SHA-256
# checksum, and installs it.
#
#   Ubuntu server:        curl -fsSL https://raw.githubusercontent.com/MRnigth/Pairnets/main/deploy/get.sh | sudo bash -s -- --public-url https://sync.example.com
#     (options for install.sh go after "-s --"; the server is reached through a Cloudflare Tunnel, see docs/HOWTO.md;
#      to upgrade, run it without options)
#   Linux desktop app:    curl -fsSL https://raw.githubusercontent.com/MRnigth/Pairnets/main/deploy/get.sh | bash -s -- --desktop
#   macOS app:            curl -fsSL https://raw.githubusercontent.com/MRnigth/Pairnets/main/deploy/get.sh | bash -s -- --mac
#
# Environment overrides: PAIRNETS_VERSION=v1.2.3 (default: latest), PAIRNETS_REPO=owner/name,
# PAIRNETS_BASE_URL=<url or local folder with the release files> (for testing/mirrors).
set -euo pipefail

REPO="${PAIRNETS_REPO:-MRnigth/Pairnets}"
VERSION="${PAIRNETS_VERSION:-latest}"
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
  if [[ -n "${PAIRNETS_BASE_URL:-}" ]]; then echo "$PAIRNETS_BASE_URL"
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
command -v curl >/dev/null || [[ -n "${PAIRNETS_BASE_URL:-}" ]] || die "curl is required"

case "$MODE" in
  server)
    [[ "$(uname -s)" == Linux ]] || die "the server runs on Linux (Ubuntu)"
    [[ $EUID -eq 0 ]] || die "run with sudo:  curl -fsSL .../get.sh | sudo bash"
    [[ "$(uname -m)" == x86_64 ]] || die "only x86_64 (amd64) servers are supported"
    ASSET=pairnets-server-linux-x64.tar.gz
    info "downloading Pairnets server ($VERSION)"
    fetch SHA256SUMS.txt "$WORK"; fetch "$ASSET" "$WORK"; verify "$ASSET" "$WORK"
    tar -xzf "$WORK/$ASSET" -C "$WORK"
    info "running install.sh"
    "$WORK/pairnets-server-linux-x64/install.sh" "${ARGS[@]}"
    ;;

  desktop)
    [[ "$(uname -s)" == Linux ]] || die "--desktop is for Linux; on a Mac use --mac"
    [[ $EUID -ne 0 ]] || die "install the desktop app as your normal user (without sudo)"
    [[ "$(uname -m)" == x86_64 ]] || die "only x86_64 desktops are supported"
    ASSET=pairnets-desktop-linux-x64.tar.gz
    info "downloading Pairnets for Linux ($VERSION)"
    fetch SHA256SUMS.txt "$WORK"; fetch "$ASSET" "$WORK"; verify "$ASSET" "$WORK"
    tar -xzf "$WORK/$ASSET" -C "$WORK"
    "$WORK/pairnets-desktop-linux-x64/install-desktop.sh" "${ARGS[@]}"
    ;;

  mac)
    [[ "$(uname -s)" == Darwin ]] || die "--mac must be run on a Mac"
    case "$(uname -m)" in
      arm64) ASSET=Pairnets-macos-arm64.zip ;;
      x86_64) ASSET=Pairnets-macos-x64.zip ;;
      *) die "unsupported Mac architecture $(uname -m)" ;;
    esac
    info "downloading Pairnets for macOS ($VERSION)"
    fetch SHA256SUMS.txt "$WORK"; fetch "$ASSET" "$WORK"; verify "$ASSET" "$WORK"
    DEST=/Applications
    [[ -w "$DEST" ]] || DEST="$HOME/Applications"
    mkdir -p "$DEST"
    pkill -x Pairnets 2>/dev/null || true
    rm -rf "$DEST/Pairnets.app"
    ditto -x -k "$WORK/$ASSET" "$DEST"
    # Files fetched with curl are not quarantined; clear the flag anyway in case of a mirror.
    xattr -dr com.apple.quarantine "$DEST/Pairnets.app" 2>/dev/null || true
    info "installed $DEST/Pairnets.app"
    open "$DEST/Pairnets.app"
    echo "Pairnets is starting: look for its icon in the menu bar (top right)."
    ;;
esac
