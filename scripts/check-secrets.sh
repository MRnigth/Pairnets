#!/usr/bin/env bash
# Scans for things that must never be committed: keys and tokens of the services Pairnets uses, private
# (carrier-grade NAT / VPN) IPs, personal Windows paths. Run before every push (and in CI).
#
#   scripts/check-secrets.sh                    # staged changes + working tree diff
#   scripts/check-secrets.sh --tree             # every tracked file
#   scripts/check-secrets.sh --history          # every commit in the history
#   scripts/check-secrets.sh --range <revs...>  # the commits git rev-list <revs...> lists, e.g.
#                                               # "--range HEAD --not --remotes" (what a push would send)
#
# A line can opt out with the marker "check-secrets: allow" (use sparingly,
# only for documented test vectors).
#
# Personal details (a real name, email address or IP) can't be listed here, because this file is public. Put them,
# one per line, in .git/info/secret-denylist: that file never leaves this computer. --diff and --range also refuse
# any line that contains one of them (any case). --tree and --history don't, because old commits keep what they had.
set -euo pipefail

mode="${1:---diff}"
range=("${@:2}")
cd "$(git rev-parse --show-toplevel)"

# This script (its patterns look like what they catch) and golden test vectors (JSON has no comments for the
# marker) are not scanned.
excludes=(':(exclude)scripts/check-secrets.sh' ':(exclude)cloud/test/vectors/**')

# Known public test vectors (SHA-256 of "abc" and of the empty string).
# Plus the 100.64.0.0/10 range itself, which is public documentation,
# and made-up logins on the reserved example domains.
allowlist='ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad|e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855|100\.64\.0\.0/10|://[^/\s:@]+:[^/\s@]*@[A-Za-z0-9.-]*example\.(com|org|net)\b'

patterns=(
  # 64 hex chars: the shape of "openssl rand -hex 32" tokens.
  '(?<![0-9a-fA-F])[0-9a-fA-F]{64}(?![0-9a-fA-F])'
  # Carrier-grade NAT range 100.64.0.0/10 (also used by VPNs): a real address of someone's machine.
  '(?<![0-9.])100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.[0-9]{1,3}\.[0-9]{1,3}(?![0-9])'
  # Token assignments with a concrete-looking value (not a placeholder).
  '(?i)(sync_token|sync__token|x-sync-token|"token")\s*[:=]\s*"?[A-Za-z0-9+/_\-]{16,}'
  # Personal Windows profile paths.
  '(?i)[a-z]:\\\\?users\\\\?(?!<|%|\{|public|default)[a-z0-9._-]+'
  # Private keys.
  '-----BEGIN [A-Z ]*PRIVATE KEY-----'
  # Cloudflare Tunnel tokens: base64 of {"a":"<account>","t":"<tunnel>","s":"<secret>"}.
  'eyJhIjoi[A-Za-z0-9+/=_-]{20,}'
  # Keys of the services the Worker uses (cloud/): Resend, a Google OAuth client secret, a real Turnstile key.
  '(?<![A-Za-z0-9_])re_[A-Za-z0-9]{8,}_[A-Za-z0-9]{16,}'
  'GOCSPX-[A-Za-z0-9_-]{20,}'
  '0x4AAAAAAA[A-Za-z0-9_-]{8,}'
  # The private part of a JSON Web Key.
  '"d"\s*:\s*"[A-Za-z0-9_-]{40,}"'
  # A Pairnets computer key: "pn_" and 32 random bytes, base64url (43 characters).
  '(?<![A-Za-z0-9_])pn_[A-Za-z0-9_-]{40,}'
  # GitHub tokens (personal, OAuth, app, refresh) and fine-grained tokens.
  '(?<![A-Za-z0-9_])gh[pousr]_[A-Za-z0-9]{36,}'
  'github_pat_[A-Za-z0-9_]{22,}'
  # Google API keys.
  'AIza[0-9A-Za-z_-]{35}'
  # AWS access keys, Stripe live keys, Slack tokens, SendGrid keys, npm tokens, Tailscale keys.
  '(?<![A-Z0-9])(AKIA|ASIA)[0-9A-Z]{16}(?![A-Z0-9])'
  '(?<![A-Za-z0-9_])[sr]k_live_[A-Za-z0-9]{16,}'
  'xox[abprs]-[A-Za-z0-9-]{10,}'
  'SG\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{20,}'
  '(?<![A-Za-z0-9_])npm_[A-Za-z0-9]{36}'
  'tskey-[A-Za-z0-9-]{10,}'
  # Signed tokens (JWT/JWS): header.payload.signature, each base64url.
  'eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}'
  # A password inside a URL (user:password@host).
  '[A-Za-z][A-Za-z0-9+.-]*://[^/\s:@"<>]{1,64}:[^/\s:@"<>]{3,64}@[A-Za-z0-9.-]+'
)

case "$mode" in
  --diff)    content() { git diff --cached -U0 --no-color -- . "${excludes[@]}"; git diff -U0 --no-color -- . "${excludes[@]}"; } ;;
  --tree)    content() { git ls-files -z -- . "${excludes[@]}" | xargs -0 -r grep -nH -I '' 2>/dev/null || true; } ;;
  --history) content() { git log --all -p --no-color -U0 -- . "${excludes[@]}"; } ;;
  --range)
    if [[ ${#range[@]} -eq 0 ]]; then echo "usage: $0 --range <rev-list arguments>" >&2; exit 2; fi
    # The commit messages, then the added lines (in "+" form, like --diff).
    content() {
      git log --no-color --format='%B' "${range[@]}" --
      git log -p --no-color -U0 --format= "${range[@]}" -- . "${excludes[@]}" | grep -E '^\+' | grep -v '^+++' || true
    } ;;
  *) echo "usage: $0 [--diff|--tree|--history|--range <revs...>]" >&2; exit 2 ;;
esac

data="$(content | grep -v 'check-secrets: allow' || true)"
if [[ "$mode" == "--diff" ]]; then
  # Only look at added lines, and skip this script's own diff.
  data="$(printf '%s\n' "$data" | grep -E '^\+' | grep -v '^+++' || true)"
fi

found=0
for p in "${patterns[@]}"; do
  hits="$(printf '%s\n' "$data" | grep -P -- "$p" | grep -Pv -- "$allowlist" || true)"
  if [[ -n "$hits" ]]; then
    found=1
    echo "Possible secret/personal data (pattern: $p):" >&2
    printf '%s\n' "$hits" | head -20 >&2
  fi
done

denylist="$(git rev-parse --git-common-dir)/info/secret-denylist"
if [[ ( "$mode" == "--diff" || "$mode" == "--range" ) && -s "$denylist" ]]; then
  words="$(grep -v '^\s*#' "$denylist" | tr -d '\r' | grep -v '^\s*$' || true)"
  if [[ -n "$words" ]]; then
    hits="$(printf '%s\n' "$data" | grep -iF -- "$words" || true)"
    if [[ -n "$hits" ]]; then
      found=1
      echo "Personal data from $denylist:" >&2
      printf '%s\n' "$hits" | head -20 >&2
    fi
  fi
fi

if [[ $found -ne 0 ]]; then
  echo "check-secrets: FAILED" >&2
  exit 1
fi
echo "check-secrets: clean ($mode)"
