#!/usr/bin/env bash
# Tether server installer for Ubuntu. Idempotent: run it again to upgrade.
#
#   sudo ./install.sh                # bind to this machine's Tailscale IPv4 address
#   sudo ./install.sh --bind 100.x.y.z [--port 5075]
#
# Run it from the extracted release folder (it must contain tether-server).
set -euo pipefail

PORT=5075
BIND=""
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR=/opt/tether
DATA_DIR=/var/lib/tether
CONF_DIR=/etc/tether
ENV_FILE=$CONF_DIR/tether.env
SERVICE=tether-server

die() { echo "error: $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --bind) BIND="${2:-}"; shift 2 ;;
    --port) PORT="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
done

[[ $EUID -eq 0 ]] || die "run as root (sudo $0)"
[[ -x "$SRC_DIR/tether-server" ]] || die "tether-server binary not found next to install.sh"
[[ "$PORT" =~ ^[0-9]+$ ]] || die "invalid port: $PORT"
command -v systemctl >/dev/null || die "systemd is required"

if [[ -z "$BIND" ]]; then
  if command -v tailscale >/dev/null && BIND="$(tailscale ip -4 2>/dev/null | head -n1)" && [[ -n "$BIND" ]]; then
    echo "Using Tailscale address $BIND"
  else
    BIND=127.0.0.1
    echo "WARNING: could not determine the Tailscale IP (is tailscale up?)." >&2
    echo "WARNING: binding to 127.0.0.1 only; your PCs cannot connect until you re-run with --bind <tailscale-ip>." >&2
  fi
fi
[[ "$BIND" != "0.0.0.0" && "$BIND" != "::" ]] || die "refusing to bind to all interfaces; use the Tailscale IP"

# 1. Service account and directories.
if ! id tether >/dev/null 2>&1; then
  useradd --system --home-dir "$DATA_DIR" --no-create-home --shell /usr/sbin/nologin tether
fi
install -d -m 0755 -o root -g root "$INSTALL_DIR"
install -d -m 0700 -o tether -g tether "$DATA_DIR"
install -d -m 0700 -o root -g root "$CONF_DIR"

# 2. Program files (stop first so the binary can be replaced).
if systemctl is-active --quiet "$SERVICE"; then
  systemctl stop "$SERVICE"
fi
install -m 0755 -o root -g root "$SRC_DIR/tether-server" "$INSTALL_DIR/tether-server"
for f in "$SRC_DIR"/*.so "$SRC_DIR"/appsettings.json; do
  [[ -e "$f" ]] && install -m 0644 -o root -g root "$f" "$INSTALL_DIR/"
done

# 3. Token and environment file (the token is generated once and kept on upgrades).
NEW_TOKEN=""
if [[ -f "$ENV_FILE" ]] && grep -q '^SYNC_TOKEN=.\{16,\}' "$ENV_FILE"; then
  TOKEN="$(grep '^SYNC_TOKEN=' "$ENV_FILE" | head -n1 | cut -d= -f2-)"
else
  command -v openssl >/dev/null || die "openssl is required to generate a token (apt install openssl)"
  TOKEN="$(openssl rand -hex 32)"
  NEW_TOKEN=1
fi
umask 077
cat > "$ENV_FILE.tmp" <<ENV
# Written by install.sh. Keep this file private (root only, mode 600).
SYNC_TOKEN=$TOKEN
ASPNETCORE_URLS=http://$BIND:$PORT
Sync__DataDir=$DATA_DIR
Sync__HistoryRetentionDays=30
Sync__HistoryMinVersions=5
ENV
chown root:root "$ENV_FILE.tmp"
chmod 600 "$ENV_FILE.tmp"
mv "$ENV_FILE.tmp" "$ENV_FILE"

# 4. systemd unit.
install -m 0644 -o root -g root "$SRC_DIR/tether-server.service" /etc/systemd/system/tether-server.service
systemctl daemon-reload
systemctl enable --now "$SERVICE"

# 5. Wait for health.
ok=""
for _ in $(seq 1 30); do
  if command -v curl >/dev/null && curl -fsS "http://$BIND:$PORT/api/health" >/dev/null 2>&1; then ok=1; break; fi
  sleep 1
done
if [[ -z "$ok" ]]; then
  echo "WARNING: the service did not answer on http://$BIND:$PORT/api/health yet." >&2
  echo "         Check: sudo journalctl -u $SERVICE -n 50" >&2
fi

echo
echo "Tether server is installed."
echo "  Data:    $DATA_DIR"
echo "  Config:  $ENV_FILE (root only)"
echo "  Logs:    sudo journalctl -u $SERVICE -f"
echo
echo "Enter these settings in Tether on both PCs:"
echo "  Server URL:  http://$BIND:$PORT/"
if [[ -n "$NEW_TOKEN" ]]; then
  echo "  Token:       $TOKEN"
  echo
  echo "The token is shown only this once. Store it in your password manager."
else
  echo "  Token:       (unchanged; read it with: sudo grep SYNC_TOKEN $ENV_FILE)"
fi
