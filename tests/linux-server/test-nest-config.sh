#!/usr/bin/env bash
# Linux test box: gives the installed nest the test-only settings from docs/PREDEPLOY.md, so the API tour
# and the browser tests can use its website, email links and Google sign-in:
#   - HTTPS on 127.0.0.1:15443 with a "localhost" certificate made now (Sync__TlsDir), public name
#     https://localhost:15443
#   - email through a fake SMTP server on 127.0.0.1:15025 that saves every message (fake-smtp.py)
#   - Google through a fake on http://127.0.0.1:15480 (fake-google.py)
# Then restarts the server and waits until both ports answer. Run as root after install-check.sh:
#
#   sudo tests/linux-server/test-nest-config.sh
set -euo pipefail
# shellcheck source=tests/linux-server/lib.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

[[ $# -eq 0 ]] || { echo "usage: $0" >&2; exit 2; }
need_root
[[ -f "$ENV_FILE" ]] || die "the server is not installed (run install-check.sh first)"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PYTHON="$(command -v python3)" || die "python3 is missing"

step "certificate for https://localhost:$E2E_HTTPS_PORT"
install -d -m 0755 "$E2E_DIR"
install -d -m 0750 -o root -g pairnets "$E2E_TLS_DIR"
rm -f "$E2E_TLS_DIR"/*.pem
openssl req -x509 -newkey rsa:2048 -nodes -days 7 -subj "/CN=localhost" \
  -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" -addext "extendedKeyUsage=serverAuth" \
  -keyout "$E2E_TLS_DIR/privkey.pem" -out "$E2E_TLS_DIR/fullchain.pem" >/dev/null 2>&1 || die "openssl could not make a certificate"
chown root:pairnets "$E2E_TLS_DIR"/*.pem
chmod 0640 "$E2E_TLS_DIR"/*.pem
check "the pairnets user can read the certificate and key" runuser -u pairnets -- test -r "$E2E_TLS_DIR/privkey.pem"

step "fake SMTP ($E2E_SMTP_PORT) and fake Google ($E2E_GOOGLE_PORT)"
install -d -m 0755 "$E2E_DIR/bin"
install -m 0644 "$HERE/fake-smtp.py" "$HERE/fake-google.py" "$E2E_DIR/bin/"
install -d -m 0755 "$E2E_MAIL_DIR"
for unit in pairnets-e2e-smtp pairnets-e2e-google; do
  systemctl stop "$unit" >/dev/null 2>&1 || true
  systemctl reset-failed "$unit" >/dev/null 2>&1 || true
done
systemd-run --quiet --unit=pairnets-e2e-smtp --description="Pairnets test: fake SMTP" -p Restart=on-failure \
  "$PYTHON" -I "$E2E_DIR/bin/fake-smtp.py" --host 127.0.0.1 --port "$E2E_SMTP_PORT" --dir "$E2E_MAIL_DIR"
systemd-run --quiet --unit=pairnets-e2e-google --description="Pairnets test: fake Google" -p Restart=on-failure \
  "$PYTHON" -I "$E2E_DIR/bin/fake-google.py" --host 127.0.0.1 --port "$E2E_GOOGLE_PORT"
check "the fake SMTP server listens" wait_port "$E2E_SMTP_PORT" 20
check "the fake Google listens" wait_port "$E2E_GOOGLE_PORT" 20
banner="$(timeout 5 bash -c "exec 3<>/dev/tcp/127.0.0.1/$E2E_SMTP_PORT; head -n1 <&3; printf 'QUIT\r\n' >&3" 2>/dev/null | tr -d '\r' || true)"
expect "the fake SMTP server greets" "$banner" "220 fake ESMTP"
redirect="$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' "http://127.0.0.1:$E2E_GOOGLE_PORT/auth?redirect_uri=https%3A%2F%2Flocalhost%2Fcb&state=s1&nonce=n1&code_challenge=c1")"
check "the fake Google redirects back with a code" grep -Eq '^302 https://localhost/cb\?state=s1&code=[A-Za-z0-9_-]+$' <<<"$redirect"

step "test-only settings in $ENV_FILE"
set_env Sync__HttpsUrl "https://127.0.0.1:$E2E_HTTPS_PORT"
set_env Sync__TlsDir "$E2E_TLS_DIR"
set_env Sync__PublicUrl "https://localhost:$E2E_HTTPS_PORT"
set_env Sync__SmtpHost 127.0.0.1
set_env Sync__SmtpPort "$E2E_SMTP_PORT"
set_env Sync__SmtpUseTls false
set_env Sync__SmtpFrom nest@example.com
set_env Sync__GoogleClientId client-123.apps.googleusercontent.com
set_env Sync__GoogleClientSecret test-oauth-secret
set_env Sync__GoogleAuthUrl "http://127.0.0.1:$E2E_GOOGLE_PORT/auth"
set_env Sync__GoogleTokenUrl "http://127.0.0.1:$E2E_GOOGLE_PORT/token"
expect "pairnets.env is still 600 root" "$(perms "$ENV_FILE")" "600 root root"

step "restart the server"
mark="$(journal_mark)"
systemctl restart pairnets-server
check "http://127.0.0.1:$E2E_HTTP_PORT/api/health answers" wait_http "$API/api/health" 120
check "https://localhost:$E2E_HTTPS_PORT/api/health answers with the test certificate" \
  wait_http "https://localhost:$E2E_HTTPS_PORT/api/health" 60 --cacert "$E2E_TLS_DIR/fullchain.pem"
hello="$(curl -fsS --max-time 10 "$API/api/hello" || true)"
[[ -n "$hello" ]] || hello='{}'
expect "the nest knows its public name" "$(json_field publicUrl <<<"$hello")" "https://localhost:$E2E_HTTPS_PORT"
check "the website's front page answers" curl -fsS --max-time 10 --cacert "$E2E_TLS_DIR/fullchain.pem" "https://localhost:$E2E_HTTPS_PORT/"
# With Sync__HttpsUrl the server binds every address itself, and Kestrel says so as a warning.
check_journal_clean "$mark" "no warnings or errors from the Pairnets units in the journal" \
  "Kestrel\[0\] Overriding address\(es\) 'http://127\.0\.0\.1:$E2E_HTTP_PORT'"

finish
