#!/usr/bin/env bash
# Builds deploy/THIRD-PARTY-NOTICES.txt: the licence texts of everything the apps and the nest ship (MIT, BSD, Apache
# and OFL ask for them to go along with the program). The release workflow puts the file in every download, and Core
# embeds it, so Settings -> About -> "Open-source licences" opens it. Run this after changing a package version in
# Directory.Packages.props (after a restore, so the packages are in the NuGet cache); a test checks the list.
# Usage: bash scripts/make-notices.sh
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
NUGET="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
pkg() { # the file $2 of NuGet package $1, at the version the apps resolve (src/Pairnets.Desktop or Core)
  local name="$1" file="$2" v
  v="$(grep -o "\"$name/[^\"]*\"" "$REPO"/src/Pairnets.Desktop/obj/project.assets.json | head -1 | tr -d '"' | cut -d/ -f2)"
  [[ -n "$v" ]] || { echo "make-notices: $name is not in src/Pairnets.Desktop/obj/project.assets.json (run dotnet restore)" >&2; exit 1; }
  local lower; lower="$(echo "$name" | tr '[:upper:]' '[:lower:]')"
  cat "$NUGET/$lower/$v/$file"
}
section() { printf '\n\n%s\n%s\n\n' "$1" "$(printf '=%.0s' $(seq 1 ${#1}))"; }
{
  cat "$REPO"/deploy/notices/head.txt
  section "A. MIT licence (the parts marked MIT above, each under its own copyright line)"
  sed -n '/^Permission is hereby granted/,$p' "$REPO"/LICENSE
  section "B. Apache License 2.0 (SQLitePCLRaw)"
  cat "$REPO"/deploy/notices/Apache-2.0.txt
  section "C. SQLite"
  echo "SQLite is in the public domain: https://sqlite.org/copyright.html"
  echo "\"The author disclaims copyright to this source code. In place of a legal notice, here is a blessing: May you do"
  echo "good and not evil. May you find forgiveness for yourself and forgive others. May you share freely, never taking"
  echo "more than you give.\""
  section "D. Notices of the SkiaSharp and HarfBuzzSharp native libraries (Skia, HarfBuzz and the parts they use)"
  pkg SkiaSharp.NativeAssets.macOS THIRD-PARTY-NOTICES.txt | tr -d '\r'
  section "E. Notices of the .NET runtime (dotnet/runtime 8.0)"
  pkg System.IO.Pipelines THIRD-PARTY-NOTICES.TXT | tr -d '\r'
  section "F. Notices of ASP.NET Core (dotnet/aspnetcore 8.0)"
  pkg Microsoft.AspNetCore.SignalR.Client THIRD-PARTY-NOTICES.TXT | tr -d '\r'
  section "G. SIL Open Font License 1.1 (Plus Jakarta Sans)"
  tr -d '\r' < "$REPO"/assets/fonts/PlusJakartaSans-OFL.txt
  section "H. Inno Setup License (the setup program inside PairnetsSetup.exe)"
  cat "$REPO"/deploy/notices/InnoSetup.txt
} > "$REPO"/deploy/THIRD-PARTY-NOTICES.txt
echo "wrote deploy/THIRD-PARTY-NOTICES.txt ($(wc -c < "$REPO"/deploy/THIRD-PARTY-NOTICES.txt) bytes)"
