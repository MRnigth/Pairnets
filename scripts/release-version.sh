#!/usr/bin/env bash
# Prints the version, channel and build of a release run, as lines for $GITHUB_OUTPUT (docs/DECISIONS.md, "Beta channels"):
#
#   version=1.0.61.8
#   file=1.0.61.8
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
#
# A version has 3 to 5 numbers (1.0.61.10.1, docs/DECISIONS.md, "Five-number versions"). "file" is its first four: dotnet's
# Version, the Windows file version, macOS CFBundleVersion and version.json "version", the only field apps up to 1.0.61.9
# read. Stops on a version that would break the build or the update of apps already installed.
set -euo pipefail

die() { echo "::error::release-version: $*" >&2; exit 1; }

REF="${GITHUB_REF:-}"
RUN="${GITHUB_RUN_NUMBER:-}"
[[ "$RUN" =~ ^[0-9]+$ ]] || die "GITHUB_RUN_NUMBER is not a number: '${RUN}'"
NUMBERS='^[0-9]+(\.[0-9]+)*$'

# The newest release tag's version plus one in its last part; a three-part version gets a fourth part, .1.
# (1.0.61.10 -> 1.0.61.11, 1.0.61.10.3 -> 1.0.61.10.4)
next_stable() {
  local newest
  newest="$(git tag -l 'v*' --sort=-v:refname | sed 's/^v//' | grep -E '^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+){0,2}$' | head -n 1 || true)"
  [[ -n "$newest" ]] || die "no v* release tag here (a beta needs one; check out with fetch-depth: 0)"
  local IFS=.
  local -a parts
  read -r -a parts <<< "$newest"
  if (( ${#parts[@]} == 3 )); then
    echo "$newest.1"
  else
    local last=$(( ${#parts[@]} - 1 ))
    parts[last]=$(( parts[last] + 1 ))
    echo "${parts[*]}"
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
IFS=. read -r -a numbers <<< "$version"
(( ${#numbers[@]} >= 3 && ${#numbers[@]} <= 5 )) || die "a version has 3 to 5 numbers, like 1.0.61.10.1: '$version'"
for n in "${numbers[@]}"; do
  [[ ${#n} -le 5 && $((10#$n)) -le 65534 ]] || die "each number must be at most 65534, the Windows limit: '$version'"
done
file="$(printf '%s' "$version" | cut -d. -f1-4)"
# Apps up to 1.0.61.9 compare only the first four numbers with their own. A five-number version whose first four are
# 1.0.61.9 or lower would look no newer to them and they would never update again; 1.0.61.10 is the first release that
# reads all five, so it comes first.
if (( ${#numbers[@]} == 5 )); then
  lowest="$(printf '%s\n%s\n' "$file" 1.0.61.10 | sort -V | head -n1)"
  [[ "$lowest" == 1.0.61.10 ]] || die "a five-number version must start at 1.0.61.10 or higher, or older apps never see it: '$version'"
fi

echo "version=$version"
echo "file=$file"
echo "channel=$channel"
echo "build=$RUN"
