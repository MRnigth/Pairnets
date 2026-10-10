#!/usr/bin/env bash
# Writes a "feed": a folder that looks like a GitHub release to deploy/get.sh and the server's self-updater
# (deploy/update.sh), for testing them without GitHub:  PAIRNETS_BASE_URL=<feed-dir>
#
#   scripts/make-feed.sh <publish-dir|pairnets-server-linux-x64.tar.gz> <version|next> <feed-dir> [<commit>]
#
# The feed holds pairnets-server-linux-x64.tar.gz (made by scripts/package-server.sh), version.json and
# SHA256SUMS.txt, like the real release. A package is copied as it is when its VERSION already matches;
# "next" is the package's own version with its last number one higher (1.0.65000 -> 1.0.65001), which is
# what an update needs: update.sh refuses the same or an older version.
set -euo pipefail

die() { echo "make-feed: $*" >&2; exit 1; }

[[ $# -ge 3 && $# -le 4 ]] || die "usage: $0 <publish-dir|package.tar.gz> <version|next> <feed-dir> [<commit>]"
SOURCE="$1"
VERSION="$2"
FEED="$3"
COMMIT="${4:-}"
NAME=pairnets-server-linux-x64
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

[[ -e "$SOURCE" ]] || die "not found: $SOURCE"
current=""
if [[ -f "$SOURCE" ]]; then
  current="$(tar -xOzf "$SOURCE" "$NAME/VERSION" 2>/dev/null | tr -d '[:space:]')" || true
fi
if [[ "$VERSION" == next ]]; then
  [[ "$current" =~ ^[0-9]+(\.[0-9]+)*$ ]] || die "\"next\" needs a package with a VERSION (got: ${current:-none})"
  last="${current##*.}"
  if [[ "$current" == *.* ]]; then VERSION="${current%.*}.$((last + 1))"; else VERSION="$((last + 1))"; fi
fi
if [[ -z "$COMMIT" ]]; then
  COMMIT="$(git -C "$HERE" rev-parse HEAD 2>/dev/null || echo unknown)"
fi

rm -rf "$FEED"
mkdir -p "$FEED"
if [[ -f "$SOURCE" && "$current" == "$VERSION" ]]; then
  cp "$SOURCE" "$FEED/$NAME.tar.gz"
else
  bash "$HERE/package-server.sh" "$SOURCE" "$VERSION" "$FEED" >/dev/null
fi
printf '{"version":"%s","commit":"%s"}\n' "$VERSION" "$COMMIT" > "$FEED/version.json"
(cd "$FEED" && sha256sum "$NAME.tar.gz" version.json > SHA256SUMS.txt)
echo "feed $FEED: version $VERSION"
