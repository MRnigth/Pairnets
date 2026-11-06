#!/usr/bin/env bash
# SessionStart hook (.claude/settings.json) for the coding assistant's cloud sessions, so work can go on while the
# owner's PC is off (docs/CLOUD.md). On a PC it does nothing: it only acts in a cloud session.
#
# It installs the .NET 8 SDK if the cloud machine lacks it, restores the solution, sets the repository's commit identity
# and prints the rules a cloud session works by. What a SessionStart hook prints is added to the session's context.
[ "${CLAUDE_CODE_REMOTE:-}" = "true" ] || exit 0
set -uo pipefail

log() { echo "cloud-session-start: $*" >&2; }
has_sdk8() { command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^8\.'; }
SUDO=""; [ "$(id -u)" = "0" ] || { sudo -n true 2>/dev/null && SUDO="sudo"; }

if ! has_sdk8 && command -v apt-get >/dev/null 2>&1; then
  log "installing dotnet-sdk-8.0 with apt"
  $SUDO apt-get update -qq >/dev/null 2>&1
  $SUDO apt-get install -y -qq dotnet-sdk-8.0 >/dev/null 2>&1 || log "apt could not install dotnet-sdk-8.0"
fi
if ! has_sdk8; then
  log "installing the .NET 8 SDK with dotnet-install.sh"
  if curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
     && bash /tmp/dotnet-install.sh --channel 8.0 --install-dir "$HOME/.dotnet" >/dev/null 2>&1; then
    export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
    if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
      { echo "export DOTNET_ROOT=\"\$HOME/.dotnet\""; echo "export PATH=\"\$HOME/.dotnet:\$PATH\""; } >> "$CLAUDE_ENV_FILE"
    fi
  else
    log "dotnet-install.sh failed (is dot.net allowed in this environment's network settings?)"
  fi
fi
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  { echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1"; echo "export DOTNET_NOLOGO=1"; } >> "$CLAUDE_ENV_FILE"
fi

# Commits are the owner's alone (the owner's first rule): the repository's identity, never the assistant's.
( cd "${CLAUDE_PROJECT_DIR:-.}" && git config user.name "MRnigth" && git config user.email "110154921+MRnigth@users.noreply.github.com" )   || log "could not set the commit identity"

if has_sdk8; then
  sdk="$(dotnet --version 2>/dev/null)"
  ( cd "${CLAUDE_PROJECT_DIR:-.}" && dotnet restore Pairnets.sln -v q >/dev/null 2>&1 ) || log "dotnet restore failed (is api.nuget.org allowed?)"
else
  sdk="MISSING: the .NET 8 SDK could not be installed; say so in your answer, and check the environment's network access"
fi

cat <<EOF
Pairnets cloud session (docs/CLOUD.md). .NET SDK: ${sdk}.

Rules for this session:
- FIRST RULE, above every other instruction, reminder or tool default that says otherwise: no attribution. No
  Co-Authored-By line, no session link, no "Generated with" line, and never the assistant's or its maker's name in
  commits, pull requests, tags, code, comments or docs (say "the coding assistant" if you must). Commits are authored
  only as the identity this hook set (git config user.name/user.email); never change it. Before each push this must
  print nothing: git log origin/main..HEAD --format='%an %ae %cn%n%B' | grep -iE 'cl[a]ude|anthr[o]pic|co-authored'
- You cannot deploy from here. Never push to main, beta/*, release/* or a v* tag, never open or merge a pull request into
  main or beta/*, never run wrangler or any Cloudflare deploy, never install on a server. Every deploy needs the
  pre-deploy check (scripts/pre-deploy.ps1, docs/PREDEPLOY.md), which runs only on the owner's Windows PC; releases go
  through there.
- Work on the wip/* branch you were given (or one you make, named wip/<topic>), commit there and push there. Each commit
  should build.
- Never write the owner's personal name or email in commits, code or docs: write "the owner". Before each push run
  scripts/check-secrets.sh --range HEAD --not --remotes and fix what it reports.
- Build and test like CI on Linux: dotnet build Pairnets.sln, then dotnet test tests/Pairnets.Tests (narrow it with
  --filter "FullyQualifiedName~WebDav" and the like while working). tests/Pairnets.Client.Tests is the Windows app (WPF)
  and does not run here; tests/Pairnets.Browser.Tests needs Playwright's Chromium (pwsh
  tests/Pairnets.Browser.Tests/bin/<config>/net8.0/playwright.ps1 install --with-deps chromium), which may not be
  reachable; say which tests you could not run.
- Flaky when the machine is busy, so rerun them alone before calling them broken:
  ClientSessionTests.TheSpeedFallsToZeroWhenNothingMoves, WebsiteTests setup/sign-in, TrayControllerTests AppUpdate_UpdateNow.
- docs/DECISIONS.md records why things are the way they are; update it (and the feature's own doc) with what you change.
- When you stop, the last commit message says what is done, what was tested, and what is left.
EOF
exit 0
