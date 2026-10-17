#!/usr/bin/env bash
# Signs a release (docs/HOWTO.md, "Signed releases"): writes SHA256SUMS.txt.sig, an ECDSA P-256 / SHA-256 signature
# of <release-dir>/SHA256SUMS.txt, with the private key in $RELEASE_SIGNING_KEY (PEM; the GitHub secret of the same
# name). Then checks the signature against deploy/release-signing.pub, the key the apps, update.sh and get.sh trust,
# so a release signed with any other key never goes out. No key, or the wrong one: it fails, and nothing is signed.
#
#   RELEASE_SIGNING_KEY=... scripts/sign-release.sh <release-dir>
set -euo pipefail

die() { echo "::error::sign-release: $*" >&2; exit 1; }

DIR="${1:-}"
[[ -n "$DIR" && -f "$DIR/SHA256SUMS.txt" ]] || die "usage: $0 <release-dir with SHA256SUMS.txt>"
[[ -n "${RELEASE_SIGNING_KEY:-}" ]] || die "RELEASE_SIGNING_KEY is not set (the GitHub secret); a release is never published unsigned"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PUB="$HERE/../deploy/release-signing.pub"

KEY="$(mktemp)"
trap 'rm -f "$KEY"' EXIT
chmod 600 "$KEY"
printf '%s\n' "$RELEASE_SIGNING_KEY" > "$KEY"
openssl dgst -sha256 -sign "$KEY" -out "$DIR/SHA256SUMS.txt.sig" "$DIR/SHA256SUMS.txt" || die "signing failed"
if ! openssl dgst -sha256 -verify "$PUB" -signature "$DIR/SHA256SUMS.txt.sig" "$DIR/SHA256SUMS.txt" >/dev/null; then
  rm -f "$DIR/SHA256SUMS.txt.sig"
  die "the signing key does not match deploy/release-signing.pub; the apps would refuse this release"
fi
echo "signed SHA256SUMS.txt (checked against deploy/release-signing.pub)"
