#!/usr/bin/env bash
# Tether server installer for Ubuntu. Idempotent: run it again to upgrade.
#
#   sudo ./install.sh                # first install: bind to this machine's Tailscale IPv4 address
#                                    # upgrade: keep the address, token and settings already configured
#   sudo ./install.sh --bind 100.x.y.z [--port 5075]
#   sudo ./install.sh --cloudflare-tunnel --public-url https://tether.example.com
#                                    # no Tailscale, no open ports: listen on 127.0.0.1 and let a
#                                    # Cloudflare Tunnel bring the PCs in (asks for the tunnel token,
#                                    # or reads it from TUNNEL_TOKEN; see docs/HOWTO.md)
#
# Run it from the extracted release folder (it must contain tether-server).
set -euo pipefail

PORT=5075
BIND=""
PORT_GIVEN=""
TUNNEL=""
PUBLIC_URL=""
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR=/opt/tether
DATA_DIR=/var/lib/tether
CONF_DIR=/etc/tether
ENV_FILE=$CONF_DIR/tether.env
TUNNEL_ENV=$CONF_DIR/tunnel.env
SERVICE=tether-server
TUNNEL_SERVICE=tether-tunnel

die() { echo "error: $*" >&2; exit 1; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --bind) BIND="${2:-}"; shift 2 ;;
    --port) PORT="${2:-}"; PORT_GIVEN=1; shift 2 ;;
    --cloudflare-tunnel) TUNNEL=1; shift ;;
    --public-url) PUBLIC_URL="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) die "unknown option: $1" ;;
  esac
done

[[ $EUID -eq 0 ]] || die "run as root (sudo $0)"
[[ -x "$SRC_DIR/tether-server" ]] || die "tether-server binary not found next to install.sh"
[[ "$PORT" =~ ^[0-9]+$ ]] || die "invalid port: $PORT"
command -v systemctl >/dev/null || die "systemd is required"

EXISTING_URL=""
EXISTING_PUBLIC_URL=""
if [[ -f "$ENV_FILE" ]]; then
  EXISTING_URL="$(grep '^ASPNETCORE_URLS=' "$ENV_FILE" | head -n1 | cut -d= -f2- || true)"
  EXISTING_PUBLIC_URL="$(grep '^TETHER_PUBLIC_URL=' "$ENV_FILE" | head -n1 | cut -d= -f2- || true)"
fi

if [[ -n "$PUBLIC_URL" ]]; then
  PUBLIC_URL="${PUBLIC_URL%/}"
  [[ "$PUBLIC_URL" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ ]] || die "--public-url must look like https://tether.example.com (https, no path)"
fi

# cloudflared, from Cloudflare's signed package repository (for --cloudflare-tunnel).
install_cloudflared() {
  [[ -x /usr/bin/cloudflared ]] && return 0
  command -v apt-get >/dev/null || die "cloudflared is missing and this is not an apt system; install it (https://pkg.cloudflare.com/) and run this again"
  command -v curl >/dev/null || die "curl is required to install cloudflared (apt install curl)"
  echo "Installing cloudflared from Cloudflare's package repository"
  install -d -m 0755 /usr/share/keyrings
  curl -fsSL https://pkg.cloudflare.com/cloudflare-main.gpg -o /usr/share/keyrings/cloudflare-main.gpg
  chmod 0644 /usr/share/keyrings/cloudflare-main.gpg
  echo 'deb [signed-by=/usr/share/keyrings/cloudflare-main.gpg] https://pkg.cloudflare.com/cloudflared any main' > /etc/apt/sources.list.d/cloudflared.list
  chmod 0644 /etc/apt/sources.list.d/cloudflared.list
  apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq cloudflared
}

TUNNEL_TOKEN="${TUNNEL_TOKEN:-}"
if [[ -n "$TUNNEL" ]]; then
  # The tunnel delivers requests to localhost, so the server listens there only.
  [[ -z "$BIND" || "$BIND" == 127.0.0.1 ]] || die "--cloudflare-tunnel listens on 127.0.0.1; leave out --bind"
  BIND=127.0.0.1
  if [[ -z "$PORT_GIVEN" && "$EXISTING_URL" =~ :([0-9]+)/?$ ]]; then
    PORT="${BASH_REMATCH[1]}"
  fi
  [[ -n "$PUBLIC_URL" || -n "$EXISTING_PUBLIC_URL" ]] || die "add --public-url https://<the public hostname you gave the tunnel>"
  if [[ -z "$TUNNEL_TOKEN" ]]; then
    echo "Paste the tunnel token from the Cloudflare dashboard (Networks > Tunnels > your tunnel > the long"
    echo "text at the end of the install command; the whole command works too). It is not shown as you paste:"
    { read -rs TUNNEL_TOKEN < /dev/tty; } 2>/dev/null || die "no terminal to ask for the token; run with TUNNEL_TOKEN=<token> in the environment instead"
    echo
  fi
  # Accept the whole "cloudflared service install <token>" command as well as the bare token.
  TUNNEL_TOKEN="$(printf '%s' "$TUNNEL_TOKEN" | awk '{print $NF}')"
  [[ "$TUNNEL_TOKEN" =~ ^[A-Za-z0-9+/=_-]{40,}$ ]] || die "that does not look like a tunnel token (copy it again from the Cloudflare dashboard)"
  [[ -f "$SRC_DIR/$TUNNEL_SERVICE.service" ]] || die "$TUNNEL_SERVICE.service not found next to install.sh"
  # Before anything is stopped or changed, so a failed download leaves the server as it was.
  install_cloudflared
fi
if [[ -z "$BIND" && "$EXISTING_URL" =~ ^http://([^/:]+):([0-9]+)/?$ ]]; then
  # Upgrade: keep the address the server already listens on.
  BIND="${BASH_REMATCH[1]}"
  PORT="${BASH_REMATCH[2]}"
  echo "Keeping the configured address $BIND:$PORT"
fi
if [[ -z "$BIND" ]]; then
  if command -v tailscale >/dev/null && BIND="$(tailscale ip -4 2>/dev/null | head -n1)" && [[ -n "$BIND" ]]; then
    echo "Using Tailscale address $BIND"
  else
    BIND=127.0.0.1
    echo "WARNING: could not determine the Tailscale IP (is tailscale up?)." >&2
    echo "WARNING: binding to 127.0.0.1 only; your PCs cannot connect until you re-run with --bind <tailscale-ip>." >&2
  fi
fi
[[ "$BIND" != "0.0.0.0" && "$BIND" != "::" ]] || die "refusing to bind to all interfaces; use the Tailscale IP, or --cloudflare-tunnel"

# 1. Service account and directories.
if ! id tether >/dev/null 2>&1; then
  useradd --system --home-dir "$DATA_DIR" --no-create-home --shell /usr/sbin/nologin tether
fi
install -d -m 0755 -o root -g root "$INSTALL_DIR"
install -d -m 0700 -o tether -g tether "$DATA_DIR"
install -d -m 0700 -o root -g root "$CONF_DIR"
install -d -m 0750 -o tether -g tether "$DATA_DIR/update"

# 2. Program files (stop first so the binary can be replaced).
if systemctl is-active --quiet "$SERVICE"; then
  systemctl stop "$SERVICE"
fi
install -m 0755 -o root -g root "$SRC_DIR/tether-server" "$INSTALL_DIR/tether-server"
for f in "$SRC_DIR"/*.so "$SRC_DIR"/appsettings.json "$SRC_DIR"/VERSION; do
  [[ -e "$f" ]] && install -m 0644 -o root -g root "$f" "$INSTALL_DIR/"
done
# The self-updater (runs as root only when the server asks for it; see DEPLOY.md).
if [[ -f "$SRC_DIR/update.sh" ]]; then
  install -m 0755 -o root -g root "$SRC_DIR/update.sh" "$INSTALL_DIR/update.sh"
fi

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
if [[ -f "$ENV_FILE" && -z "$NEW_TOKEN" ]]; then
  # Upgrade: keep every setting, only (re)write the address in case --bind/--port was given.
  sed -e "s#^ASPNETCORE_URLS=.*#ASPNETCORE_URLS=http://$BIND:$PORT#" -e "s#^Sync__UpdateDir=.*#Sync__UpdateDir=$DATA_DIR/update#" "$ENV_FILE" > "$ENV_FILE.tmp"
  grep -q '^ASPNETCORE_URLS=' "$ENV_FILE.tmp" || echo "ASPNETCORE_URLS=http://$BIND:$PORT" >> "$ENV_FILE.tmp"
  # The root updater only watches $DATA_DIR/update, whatever data folder the server uses.
  grep -q '^Sync__UpdateDir=' "$ENV_FILE.tmp" || echo "Sync__UpdateDir=$DATA_DIR/update" >> "$ENV_FILE.tmp"
else
  cat > "$ENV_FILE.tmp" <<ENV
# Written by install.sh. Keep this file private (root only, mode 600).
SYNC_TOKEN=$TOKEN
ASPNETCORE_URLS=http://$BIND:$PORT
Sync__DataDir=$DATA_DIR
Sync__UpdateDir=$DATA_DIR/update
Sync__HistoryRetentionDays=30
Sync__HistoryMinVersions=5
ENV
fi
set_env() { # set_env <key> <value>: replace or add one line of $ENV_FILE.tmp
  { grep -v "^$1=" "$ENV_FILE.tmp" || true; echo "$1=$2"; } > "$ENV_FILE.tmp.new"
  mv "$ENV_FILE.tmp.new" "$ENV_FILE.tmp"
}
if [[ -n "$TUNNEL" ]]; then
  # Behind the tunnel every request comes from 127.0.0.1; take the client's address from Cloudflare.
  set_env Sync__TrustProxyHeaders true
fi
if [[ -n "$PUBLIC_URL" ]]; then
  # Only for this script's summary: the address the PCs use.
  set_env TETHER_PUBLIC_URL "$PUBLIC_URL"
fi
chown root:root "$ENV_FILE.tmp"
chmod 600 "$ENV_FILE.tmp"
mv "$ENV_FILE.tmp" "$ENV_FILE"

# 4. systemd unit.
install -m 0644 -o root -g root "$SRC_DIR/tether-server.service" /etc/systemd/system/tether-server.service
if [[ -f "$SRC_DIR/tether-update.service" && -f "$SRC_DIR/tether-update.path" ]]; then
  install -m 0644 -o root -g root "$SRC_DIR/tether-update.service" /etc/systemd/system/tether-update.service
  install -m 0644 -o root -g root "$SRC_DIR/tether-update.path" /etc/systemd/system/tether-update.path
fi
if [[ -f "$SRC_DIR/tether-update.timer" ]]; then
  install -m 0644 -o root -g root "$SRC_DIR/tether-update.timer" /etc/systemd/system/tether-update.timer
fi
# 4b. Cloudflare Tunnel (only with --cloudflare-tunnel; upgrades keep an existing one as it is).
if [[ -n "$TUNNEL" ]]; then
  # The token stays in a root-only file: unit files are readable by every local user.
  printf 'TUNNEL_TOKEN=%s\n' "$TUNNEL_TOKEN" > "$TUNNEL_ENV.tmp"
  chown root:root "$TUNNEL_ENV.tmp"
  chmod 600 "$TUNNEL_ENV.tmp"
  mv "$TUNNEL_ENV.tmp" "$TUNNEL_ENV"
fi
if [[ -n "$TUNNEL" || -f /etc/systemd/system/$TUNNEL_SERVICE.service ]] && [[ -f "$SRC_DIR/$TUNNEL_SERVICE.service" ]]; then
  install -m 0644 -o root -g root "$SRC_DIR/$TUNNEL_SERVICE.service" /etc/systemd/system/$TUNNEL_SERVICE.service
fi

systemctl daemon-reload
systemctl enable --now "$SERVICE"
if [[ -n "$TUNNEL" ]]; then
  systemctl enable "$TUNNEL_SERVICE"
  systemctl restart "$TUNNEL_SERVICE"
fi
if [[ -f /etc/systemd/system/tether-update.path ]]; then
  # Clear any "failed" state (e.g. a start limit hit) and make sure the watcher really runs.
  systemctl reset-failed tether-update.path tether-update.service 2>/dev/null || true
  systemctl enable tether-update.path
  systemctl restart tether-update.path
fi
if [[ -f /etc/systemd/system/tether-update.timer ]]; then
  systemctl enable --now tether-update.timer
fi

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

SERVER_URL="http://$BIND:$PORT/"
CONFIGURED_PUBLIC_URL="$(grep '^TETHER_PUBLIC_URL=' "$ENV_FILE" | head -n1 | cut -d= -f2- || true)"
if [[ "$BIND" == 127.0.0.1 && -n "$CONFIGURED_PUBLIC_URL" ]]; then
  SERVER_URL="$CONFIGURED_PUBLIC_URL/"
fi
if [[ -n "$TUNNEL" ]]; then
  # The tunnel needs a few seconds to connect, and DNS for a new hostname a little longer.
  ok=""
  for _ in $(seq 1 30); do
    if curl -fsS --max-time 10 "${SERVER_URL}api/health" >/dev/null 2>&1; then ok=1; break; fi
    sleep 2
  done
  if [[ -z "$ok" ]]; then
    echo "WARNING: ${SERVER_URL}api/health did not answer through the tunnel yet. Check:" >&2
    echo "         - the tunnel runs: sudo systemctl status $TUNNEL_SERVICE (log: sudo journalctl -u $TUNNEL_SERVICE -n 50)" >&2
    echo "         - in the Cloudflare dashboard the tunnel has a public hostname ${SERVER_URL%/} -> HTTP localhost:$PORT" >&2
    echo "         - Bot Fight Mode is off for the domain (Security > Bots)" >&2
  fi
fi

UPDATER_STATE="$(systemctl is-active tether-update.path 2>/dev/null || true)"
TUNNEL_STATE=""
if [[ -f /etc/systemd/system/$TUNNEL_SERVICE.service ]]; then
  TUNNEL_STATE="$(systemctl is-active "$TUNNEL_SERVICE" 2>/dev/null || true)"
fi

echo
echo "Tether server is installed."
echo "  Data:    $DATA_DIR"
echo "  Config:  $ENV_FILE (root only)"
echo "  Logs:    sudo journalctl -u $SERVICE -f"
echo "  Updater: tether-update.path is ${UPDATER_STATE:-unknown} (should be: active)"
if [[ -n "$TUNNEL_STATE" ]]; then
  echo "  Tunnel:  $TUNNEL_SERVICE is $TUNNEL_STATE (should be: active)"
fi
echo
echo "Enter these settings in Tether on both PCs:"
echo "  Server URL:  $SERVER_URL"
if [[ -n "$NEW_TOKEN" ]]; then
  echo "  Token:       $TOKEN"
  echo
  echo "The token is shown only this once. Store it in your password manager."
else
  echo "  Token:       (unchanged; read it with: sudo grep SYNC_TOKEN $ENV_FILE)"
fi
