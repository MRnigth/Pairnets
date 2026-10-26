#!/usr/bin/env bash
# Prints the version, channel and build of a release run, as lines for $GITHUB_OUTPUT (docs/DECISIONS.md, "Beta channels"):
#
#   version=1.0.61.8
#   channel=public-beta
#   build=123
#
#   scripts/release-version.sh            reads GITHUB_REF, GITHUB_REF_NAME and GITHUB_RUN_NUMBER
#
# - a v* tag:            version from the tag, channel stable
# - branch beta/public:  channel public-beta     } version = the next stable number: the newest v* tag with its
# - branch beta/developer: channel developer-beta } last part one higher (1.0.61.7 -> 1.0.61.8, 1.0.60 -> 1.0.60.1)
# - anything else (main): 1.0.<run number>, channel main (the apps never install it)
# The build is always the run number. The beta versions need every tag: checkout with fetch-depth: 0.
set -euo pipefail

die() { echo "::error::release-version: $*" >&2; exit 1; }

REF="${GITHUB_REF:-}"
RUN="${GITHUB_RUN_NUMBER:-}"
[[ "$RUN" =~ ^[0-9]+$ ]] || die "GITHUB_RUN_NUMBER is not a number: '${RUN}'"
NUMBERS='^[0-9]+(\.[0-9]+)*$'

# The newest release tag's version plus one in its last part; a three-part version gets a fourth part, .1.
next_stable() {
  local newest
  newest="$(git tag -l 'v*' --sort=-v:refname | sed 's/^v//' | grep -E '^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$' | head -n 1 || true)"
  [[ -n "$newest" ]] || die "no v* release tag here (a beta needs one; check out with fetch-depth: 0)"
  local IFS=.
  local -a parts
  read -r -a parts <<< "$newest"
  if (( ${#parts[@]} == 3 )); then
    echo "$newest.1"
  else
    echo "${parts[0]}.${parts[1]}.${parts[2]}.$((parts[3] + 1))"
  fi
}

case "$REF" in
  refs/tags/v*)
    version="${REF#refs/tags/v}"
    channel=stable
    ;;
  refs/heads/beta/public)
    version="$(next_stable)"
    channel=public-beta
    ;;
  refs/heads/beta/developer)
    version="$(next_stable)"
    channel=developer-beta
    ;;
  refs/heads/beta/*)
    die "unknown beta branch ${REF#refs/heads/} (only beta/public and beta/developer)"
    ;;
  *)
    version="1.0.$RUN"
    channel=main
    ;;
esac
[[ "$version" =~ $NUMBERS ]] || die "not a version (numbers and dots only): '$version'"

echo "version=$version"
echo "channel=$channel"
echo "build=$RUN"
