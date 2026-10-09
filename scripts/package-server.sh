#!/usr/bin/env bash
# Makes the server package pairnets-server-linux-x64.tar.gz, the same way for a release (release.yml)
# and for the pre-deploy check (scripts/pre-deploy.ps1, the linux-install CI job).
#
#   scripts/package-server.sh <publish-dir> <version> <out-dir>
#       <publish-dir>: the output of "dotnet publish src/Pairnets.Server ... -r linux-x64" (holds pairnets-server)
#   scripts/package-server.sh <pairnets-server-linux-x64.tar.gz> <version> <out-dir>
#       the same package again with another VERSION (nothing else changes)
#
# The package is one folder, pairnets-server-linux-x64/, with the server, install.sh, update.sh, the systemd
# units, pairnets.env.example, DEPLOY.md and VERSION. Prints the path of the package it wrote.
# Runs on Linux and in Git Bash on Windows (where it sets the executable bits itself).
set -euo pipefail

die() { echo "package-server: $*" >&2; exit 1; }

[[ $# -eq 3 ]] || die "usage: $0 <publish-dir|package.tar.gz> <version> <out-dir>"
SOURCE="$1"
VERSION="$2"
OUT_DIR="$3"
NAME=pairnets-server-linux-x64
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# update.sh only installs a version made of numbers and dots.
[[ "$VERSION" =~ ^[0-9]+(\.[0-9]+)*$ ]] || die "the version must look like 1.0.123 (got: $VERSION)"
[[ -e "$SOURCE" ]] || die "not found: $SOURCE"

mkdir -p "$OUT_DIR"
OUT_DIR="$(cd "$OUT_DIR" && pwd)"
STAGE="$(mktemp -d "$OUT_DIR/.package.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT
PKG="$STAGE/$NAME"

if [[ -d "$SOURCE" ]]; then
  [[ -f "$SOURCE/pairnets-server" ]] || die "$SOURCE has no pairnets-server (publish for linux-x64 first)"
  mkdir -p "$PKG"
  cp -R "$SOURCE"/. "$PKG"/
  cp "$REPO"/deploy/install.sh "$REPO"/deploy/update.sh "$REPO"/deploy/pairnets-server.service "$REPO"/deploy/pairnets-update.service \
     "$REPO"/deploy/pairnets-update.path "$REPO"/deploy/pairnets-update.timer "$REPO"/deploy/pairnets-tunnel.service \
     "$REPO"/deploy/pairnets.env.example "$PKG"/
  cp "$REPO"/docs/DEPLOY.md "$PKG"/DEPLOY.md
else
  tar -xzf "$SOURCE" -C "$STAGE"
  [[ -f "$PKG/pairnets-server" && -f "$PKG/install.sh" ]] || die "$SOURCE is not a server package"
fi
echo "$VERSION" > "$PKG"/VERSION
chmod +x "$PKG"/install.sh "$PKG"/update.sh "$PKG"/pairnets-server

TARBALL="$OUT_DIR/$NAME.tar.gz"
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*)
    # Windows file systems have no executable bit: Git Bash guesses it (only files starting with #!), so the
    # server binary would arrive without it. Set the modes in the archive instead.
    TAR="$STAGE/$NAME.tar"
    tar -C "$STAGE" -cf "$TAR" --owner=0 --group=0 --mode='u+rwX,go+rX,go-w' --exclude="$NAME/pairnets-server" "$NAME"
    tar -C "$STAGE" -rf "$TAR" --owner=0 --group=0 --mode=0755 "$NAME/pairnets-server"
    gzip -c "$TAR" > "$TARBALL"
    ;;
  *)
    tar -C "$STAGE" -czf "$TARBALL" "$NAME"
    ;;
esac

# The installer refuses a server it cannot run, and get.sh and update.sh run install.sh directly.
listing="$(tar -tvzf "$TARBALL")"
for f in pairnets-server install.sh update.sh; do
  grep -Eq "^-rwx.* $NAME/$f\$" <<<"$listing" || die "$f is not executable in $TARBALL"
done
for f in VERSION DEPLOY.md pairnets-server.service pairnets-update.service pairnets-update.path pairnets-update.timer pairnets-tunnel.service pairnets.env.example; do
  grep -Eq " $NAME/$f\$" <<<"$listing" || die "$f is missing from $TARBALL"
done
echo "$TARBALL"
