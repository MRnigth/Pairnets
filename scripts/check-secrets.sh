#!/usr/bin/env bash
# Scans for things that must never be committed: sync tokens, tunnel tokens, private
# (carrier-grade NAT / VPN) IPs, personal Windows paths. Run before every push (and in CI).
#
#   scripts/check-secrets.sh            # staged changes + working tree diff
#   scripts/check-secrets.sh --tree     # every tracked file
#   scripts/check-secrets.sh --history  # every commit in the history
#
# A line can opt out with the marker "check-secrets: allow" (use sparingly,
# only for documented test vectors).
set -euo pipefail

mode="${1:---diff}"
cd "$(git rev-parse --show-toplevel)"

# Known public test vectors (SHA-256 of "abc" and of the empty string).
# Plus the 100.64.0.0/10 range itself, which is public documentation.
allowlist='ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad|e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855|100\.64\.0\.0/10'

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
)

case "$mode" in
  --diff)    content() { git diff --cached -U0 --no-color -- . ':(exclude)scripts/check-secrets.sh'; git diff -U0 --no-color -- . ':(exclude)scripts/check-secrets.sh'; } ;;
  --tree)    content() { git ls-files -z | grep -zv '^scripts/check-secrets.sh$' | xargs -0 -r grep -nH -I '' 2>/dev/null || true; } ;;
  --history) content() { git log --all -p --no-color -U0 -- . ':(exclude)scripts/check-secrets.sh'; } ;;
  *) echo "usage: $0 [--diff|--tree|--history]" >&2; exit 2 ;;
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

if [[ $found -ne 0 ]]; then
  echo "check-secrets: FAILED" >&2
  exit 1
fi
echo "check-secrets: clean ($mode)"
