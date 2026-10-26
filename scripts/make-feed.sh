#!/usr/bin/env bash
# Writes a "feed": a folder that looks like a GitHub release to deploy/get.sh and the server's self-updater
# (deploy/update.sh), for testing them without GitHub:  PAIRNETS_BASE_URL=<feed-dir>
#
#   scripts/make-feed.sh <publish-dir|pairnets-server-linux-x64.tar.gz> <version|next> <feed-dir> [<commit> [<channel> [<build>]]]
#
# The feed holds pairnets-server-linux-x64.tar.gz (made by scripts/package-server.sh), version.json,
# SHA256SUMS.txt and SHA256SUMS.txt.sig, like the real release. TEST ONLY: it is signed with a throwaway key, never
# the Pairnets one: feed-signing.key next to the feed folder (made once, so feeds side by side share it; or
# PAIRNETS_FEED_KEY=<private key file>). Its public half is test-signing.pub in the feed; give it to get.sh and
# update.sh as PAIRNETS_RELEASE_KEY. A package is copied as it is when its VERSION already matches;
# "next" is the package's own version with its last number one higher (1.0.65000 -> 1.0.65001), which is
# what an update needs: update.sh refuses the same or an older version.
# version.json has the release's fields (docs/DECISIONS.md, "Beta channels"): channel (default stable), build (default
# 0) and data. data comes from scripts/data-versions.sh, or, without the sources (the Linux test box has only scripts/),
# from the version.json next to a package taken from another feed.
set -euo pipefail

die() { echo "make-feed: $*" >&2; exit 1; }

[[ $# -ge 3 && $# -le 6 ]] || die "usage: $0 <publish-dir|package.tar.gz> <version|next> <feed-dir> [<commit> [<channel> [<build>]]]"
SOURCE="$1"
VERSION="$2"
FEED="$3"
COMMIT="${4:-}"
CHANNEL="${5:-stable}"
BUILD="${6:-0}"
NAME=pairnets-server-linux-x64
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

[[ -e "$SOURCE" ]] || die "not found: $SOURCE"
[[ "$CHANNEL" =~ ^(stable|public-beta|developer-beta|main)$ ]] || die "unknown channel: $CHANNEL"
[[ "$BUILD" =~ ^[0-9]+$ ]] || die "the build is not a number: $BUILD"
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

if [[ -d "$HERE/../src/Pairnets.Core" ]]; then
  DATA="$(bash "$HERE/data-versions.sh" "$HERE/..")" || die "could not read the data versions"
else
  DATA="$(grep -o '"data":{[^}]*}' "$(dirname "$SOURCE")/version.json" 2>/dev/null | head -n 1 | sed 's/^"data"://' || true)"
  [[ -n "$DATA" ]] || die "no sources here for scripts/data-versions.sh, and no version.json with data next to $SOURCE"
fi

rm -rf "$FEED"
mkdir -p "$FEED"
if [[ -f "$SOURCE" && "$current" == "$VERSION" ]]; then
  cp "$SOURCE" "$FEED/$NAME.tar.gz"
else
  bash "$HERE/package-server.sh" "$SOURCE" "$VERSION" "$FEED" >/dev/null
fi
# As release.yml writes it: "version" at most four numbers (all that apps up to 1.0.61.9 read), "release" the full number.
printf '{"version":"%s","release":"%s","commit":"%s","channel":"%s","build":%d,"data":%s}\n' \
  "$(printf '%s' "$VERSION" | cut -d. -f1-4)" "$VERSION" "$COMMIT" "$CHANNEL" "$BUILD" "$DATA" > "$FEED/version.json"
(cd "$FEED" && sha256sum "$NAME.tar.gz" version.json > SHA256SUMS.txt)
KEY="${PAIRNETS_FEED_KEY:-$(dirname "$FEED")/feed-signing.key}"
if [[ ! -f "$KEY" ]]; then
  (umask 077 && openssl ecparam -name prime256v1 -genkey -noout -out "$KEY") || die "could not make a test signing key"
fi
openssl pkey -in "$KEY" -pubout -out "$FEED/test-signing.pub"
openssl dgst -sha256 -sign "$KEY" -out "$FEED/SHA256SUMS.txt.sig" "$FEED/SHA256SUMS.txt"
echo "feed $FEED: version $VERSION"
