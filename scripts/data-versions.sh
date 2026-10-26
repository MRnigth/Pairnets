#!/usr/bin/env bash
# Prints the data versions of the code in this checkout as compact JSON, for version.json's "data":
#
#   {"settings":2,"state":2,"sealed":1}
#
#   scripts/data-versions.sh [<repo-root>]
#
# settings = SettingsUpgrades.Current (src/Pairnets.Core/Settings/ClientSettings.cs)
# state    = StateDb.SchemaVersion    (src/Pairnets.Core/State/StateDb.cs)
# sealed   = the sealed-file Version  (src/Pairnets.Core/Crypto/SealedFile.cs, the first "const byte Version")
# An app never installs a build whose numbers are lower than its own. DataVersionsScriptTests pins the same patterns
# against Pairnets.Core.DataVersions.Current; if one of these lines changes shape, this fails instead of guessing.
set -euo pipefail

die() { echo "::error::data-versions: $*" >&2; exit 1; }

ROOT="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"
CORE="$ROOT/src/Pairnets.Core"

# The number in the first line of <file> (after a line matching <after>, if given) that matches <pattern>.
number() {
  local file="$1" pattern="$2" after="${3:-}"
  [[ -f "$file" ]] || die "not found: $file"
  awk -v pat="$pattern" -v after="$after" '
    after != "" && !seen && $0 ~ after { seen = 1; next }
    (after == "" || seen) && match($0, pat) {
      s = substr($0, RSTART, RLENGTH); gsub(/[^0-9]/, "", s); print s; exit
    }' "$file"
}

settings="$(number "$CORE/Settings/ClientSettings.cs" '(^|[[:space:]])const int Current = [0-9]+;' 'static class SettingsUpgrades([^A-Za-z0-9_]|$)')"
state="$(number "$CORE/State/StateDb.cs" '(^|[[:space:]])const int SchemaVersion = [0-9]+;')"
sealed="$(number "$CORE/Crypto/SealedFile.cs" '(^|[[:space:]])const byte Version = [0-9]+;')"

[[ -n "$settings" ]] || die "no 'const int Current = N;' in class SettingsUpgrades (src/Pairnets.Core/Settings/ClientSettings.cs)"
[[ -n "$state" ]] || die "no 'const int SchemaVersion = N;' in src/Pairnets.Core/State/StateDb.cs"
[[ -n "$sealed" ]] || die "no 'const byte Version = N;' in src/Pairnets.Core/Crypto/SealedFile.cs"

printf '{"settings":%d,"state":%d,"sealed":%d}\n' "$settings" "$state" "$sealed"
