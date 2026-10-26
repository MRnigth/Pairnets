#!/usr/bin/env bash
# The version numbers of a release build, for release.yml:
#   scripts/release-version.sh <git ref, e.g. refs/tags/v1.0.61.8.1 or refs/heads/main> <run number>
# Prints two lines (GITHUB_OUTPUT format):
#   version=1.0.61.8.1   the release number: tag, app's About line, version.json "release", the server's VERSION file
#   file=1.0.61.8        its first four numbers: dotnet's Version, the Windows file version, macOS CFBundleVersion and
#                        version.json "version", the only field apps up to 1.0.61.7 read (they cannot read five numbers)
# A tag (v + 3 to 5 numbers) gives its own number, anything else 1.0.<run number>. Stops on a tag that would break
# the build or the update of apps already installed.
set -euo pipefail
die() { echo "release-version.sh: $*" >&2; exit 1; }
[[ $# -eq 2 ]] || die "usage: $0 <git ref> <run number>"
REF="$1"; RUN="$2"

if [[ "$REF" == refs/tags/v* ]]; then
  version="${REF#refs/tags/v}"
else
  [[ "$RUN" =~ ^[0-9]+$ ]] || die "the run number must be a number (got: $RUN)"
  version="1.0.$RUN"
fi

[[ "$version" =~ ^[0-9]+(\.[0-9]+){2,4}$ ]] || die "a release number has 3 to 5 numbers, like 1.0.61.8.1 (got: $version)"
IFS=. read -r -a parts <<< "$version"
for p in "${parts[@]}"; do
  [[ ${#p} -le 5 && $((10#$p)) -le 65534 ]] || die "each number must be at most 65534, the Windows limit (got: $version)"
done
file="$(printf '%s' "$version" | cut -d. -f1-4)"

# Apps up to 1.0.61.7 compare only the first four numbers with their own. A five-number release whose first four are
# 1.0.61.7 or lower would look no newer to them and they would never update again; 1.0.61.8 is the first release
# that reads all five, so it has to come out first.
if [[ ${#parts[@]} -eq 5 ]]; then
  lowest="$(printf '%s\n%s\n' "$file" 1.0.61.8 | sort -V | head -n1)"
  [[ "$lowest" == 1.0.61.8 ]] || die "a five-number release must start at 1.0.61.8 or higher, or older apps never see it (got: $version)"
fi

echo "version=$version"
echo "file=$file"
