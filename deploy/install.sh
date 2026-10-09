#!/usr/bin/env bash
# Pairnets server installer for Ubuntu (the server is "the nest"). Idempotent: run it again to upgrade.
# The self-updater (update.sh) runs it the same way on every upgrade, without a terminal.
#
#   sudo ./install.sh
#       first install: links this server to your Pairnets account. It prints a link; open it on any device,
#       sign in and press "Add this server" (pairnets-link.sh does this part). Your computers then reach the
#       server through https://sync.pairnets.app: no open ports, no domain, nothing to set up in Cloudflare.
#       On a server that is already installed it upgrades, keeping the data, settings, link or name and tunnel,
#       and never starts a new link.
#   sudo ./install.sh --link
#       link an installed server to your Pairnets account again (after it was removed from the account, or to
#       move it from its own domain to sync.pairnets.app)
#   sudo ./install.sh --public-url https://sync.example.com
#       advanced, your own domain: the server listens on 127.0.0.1 only and your own Cloudflare Tunnel gives it
#       its public name (asks for the tunnel token, or reads it from TUNNEL_TOKEN; see docs/HOWTO.md)
#
# At the end it prints the next steps: for a linked server, sign in on each computer with the same Pairnets
# account; with your own domain, a one-time link (on a terminal) to set up the nest's website. Exits non-zero
# when the server does not answer after the install. More options: --help.
#
# Run it from the extracted release folder (it must contain pairnets-server).
set -euo pipefail

PORT=5075
BIND=""
PORT_GIVEN=""
TUNNEL=""
PUBLIC_URL=""
LINK=""
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR=/opt/pairnets
DATA_DIR=/var/lib/pairnets
CONF_DIR=/etc/pairnets
ENV_FILE=$CONF_DIR/pairnets.env
TUNNEL_ENV=$CONF_DIR/tunnel.env
RELAY_ENV=$CONF_DIR/relay.env
SERVICE=pairnets-server
TUNNEL_SERVICE=pairnets-tunnel
# Pairnets reaches a linked server on this port, always (cloud/RELAY.md, section 5).
RELAY_PORT=5075
DEFAULT_RELAY_SERVICE=https://sync.pairnets.app

die() { echo "error: $*" >&2; exit 1; }

usage() {
  cat <<'USAGE'
Usage: sudo ./install.sh [options]

  sudo ./install.sh
      First install: links this server to your Pairnets account. It prints a link to open on any
      device; sign in there and press "Add this server". Nothing to set up in Cloudflare.
      On a server that is already installed: upgrade, keeping the data, settings, link and tunnel.
  sudo ./install.sh --public-url https://sync.example.com
      Advanced, your own domain: the public name you gave your own Cloudflare Tunnel. Asks for the
      tunnel token (or reads it from TUNNEL_TOKEN). See docs/HOWTO.md.

Options:
  --link                        link this server to your Pairnets account (again); a first install
                                without options does this by itself
  --public-url https://<name>   your own domain as the nest's public name (sets up your Cloudflare Tunnel)
  --cloudflare-tunnel           set up the tunnel again (new token) for the name already configured
  --bind <ip>                   advanced: listen on this address instead of using the tunnel
  --port <port>                 the port the server listens on (default 5075)
  -h, --help                    show this help
USAGE
}

usage_error() { echo "error: $*" >&2; echo >&2; usage >&2; exit 2; }

missing_value() { # missing_value <option>
  local example=192.0.2.10
  case "$1" in
    --public-url) example=https://sync.example.com ;;
    --port) example=5075 ;;
  esac
  echo "error: $1 needs a value, for example: $1 $example" >&2
  exit 2
}

set_option() { # set_option <option> <value>
  [[ -n "$2" ]] || missing_value "$1"
  case "$1" in
    --bind) BIND="$2" ;;
    --port) PORT="$2"; PORT_GIVEN=1 ;;
    --public-url) PUBLIC_URL="$2" ;;
  esac
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --bind|--port|--public-url)
      # Last on the line, or followed by another option: the value is missing.
      if [[ $# -lt 2 || "$2" == -* ]]; then missing_value "$1"; fi
      set_option "$1" "$2"
      shift 2 ;;
    --bind=*|--port=*|--public-url=*) set_option "${1%%=*}" "${1#*=}"; shift ;;
    --link) LINK=asked; shift ;;
    --cloudflare-tunnel) TUNNEL=1; shift ;;
    -h|--help) usage; exit 0 ;;
    https://*) usage_error "unknown option: $1 (to give the nest this name use: --public-url $1)" ;;
    *) usage_error "unknown option: $1" ;;
  esac
done

# Linked to Pairnets (--link) and your own domain (--public-url) are two ways to the same thing: one at a time.
if [[ -n "$LINK" ]]; then
  [[ -z "$PUBLIC_URL" ]] || usage_error "use --link (your Pairnets account, sync.pairnets.app) or --public-url (your own domain), not both"
  [[ -z "$BIND" && -z "$TUNNEL" ]] || usage_error "--link sets up the tunnel itself, which listens on 127.0.0.1; leave out --bind and --cloudflare-tunnel"
  [[ -z "$PORT_GIVEN" || "$PORT" == "$RELAY_PORT" ]] || usage_error "--link uses port $RELAY_PORT (where Pairnets reaches the server); leave out --port"
fi

[[ $EUID -eq 0 ]] || die "run as root (sudo $0)"
[[ -x "$SRC_DIR/pairnets-server" ]] || die "pairnets-server binary not found next to install.sh"
[[ "$PORT" =~ ^[0-9]+$ ]] || die "invalid port: $PORT"
command -v systemctl >/dev/null || die "systemd is required"

# env_value <key>: the value of <key> in $ENV_FILE, or nothing. Keys match without regard to case, as
# the server reads them.
env_value() {
  local value
  value="$(grep -iE "^[[:space:]]*$1=" "$ENV_FILE" 2>/dev/null | head -n1 | cut -d= -f2-)" || true
  value="${value%$'\r'}" # a file edited on Windows
  value="${value%\"}"; value="${value#\"}"
  value="${value%\'}"
  printf '%s' "${value#\'}"
}

# The nest's public name as the server reads it: Sync__PublicUrl, then PUBLIC_URL, then PAIRNETS_PUBLIC_URL.
public_name() {
  local key value
  for key in Sync__PublicUrl PUBLIC_URL PAIRNETS_PUBLIC_URL; do
    value="$(env_value "$key")"
    if [[ -n "${value//[[:space:]]/}" ]]; then
      printf '%s' "${value%/}"
      return 0
    fi
  done
}

# relay_value <key>: a value from $RELAY_ENV (written by pairnets-link.sh), or nothing.
relay_value() {
  grep -m1 "^$1=" "$RELAY_ENV" 2>/dev/null | cut -d= -f2- || true
}

# 0. Pairnets used to be called Tether. The first time, take over a Tether server on this machine:
#    its files, history, token, address and Cloudflare Tunnel. Data is only moved, never deleted.
TUNNEL_FROM_TETHER=""
if [[ -f /etc/tether/tether.env && ! -e "$CONF_DIR" ]]; then
  [[ ! -e "$DATA_DIR" ]] || die "found a Tether server, but $DATA_DIR already exists; move it away and run this again (nothing was changed)"
  echo "Found a Tether server: moving it to Pairnets (files, history, token and settings are kept)"
  for unit in tether-update.path tether-update.timer tether-update.service tether-tunnel tether-server; do
    systemctl disable --now "$unit" >/dev/null 2>&1 || true
  done
  if [[ -d /var/lib/tether ]]; then
    mv /var/lib/tether "$DATA_DIR"
  fi
  mv /etc/tether "$CONF_DIR"
  mv "$CONF_DIR/tether.env" "$ENV_FILE"
  sed -i -e "s#/var/lib/tether#$DATA_DIR#g" -e 's#^TETHER_PUBLIC_URL=#PAIRNETS_PUBLIC_URL=#' "$ENV_FILE"
  if id tether >/dev/null 2>&1 && ! id pairnets >/dev/null 2>&1; then
    # Same user id, new name: every file keeps its owner.
    usermod -l pairnets -d "$DATA_DIR" tether
    if getent group tether >/dev/null; then groupmod -n pairnets tether; fi
  fi
  rm -f /etc/systemd/system/tether-server.service /etc/systemd/system/tether-tunnel.service \
    /etc/systemd/system/tether-update.service /etc/systemd/system/tether-update.path /etc/systemd/system/tether-update.timer
  rm -rf /opt/tether
  systemctl daemon-reload
  if [[ -f "$TUNNEL_ENV" ]]; then
    TUNNEL_FROM_TETHER=1
  fi
  echo "Moved /var/lib/tether to $DATA_DIR and /etc/tether to $CONF_DIR; the system user tether is now pairnets."
fi

EXISTING_URL=""
if [[ -f "$ENV_FILE" ]]; then
  EXISTING_URL="$(grep '^ASPNETCORE_URLS=' "$ENV_FILE" | head -n1 | cut -d= -f2- || true)"
fi
EXISTING_PUBLIC_URL="$(public_name)"
EXISTING_RELAY="$(env_value Sync__RelayNestId)"
EXISTING_RELAY_SERVICE="$(env_value Sync__RelayServiceUrl)"
EXISTING_RELAY_SERVICE="${EXISTING_RELAY_SERVICE:-$DEFAULT_RELAY_SERVICE}"

if [[ -n "$PUBLIC_URL" ]]; then
  PUBLIC_URL="${PUBLIC_URL%/}"
  [[ "$PUBLIC_URL" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ ]] || die "--public-url must look like https://sync.example.com (https, no path)"
fi

# 0b. Linking to a Pairnets account (cloud/RELAY.md, section 2). A first install without options does it, and --link. It
#     never happens on an upgrade: an installed server (it has $ENV_FILE) keeps what it has. pairnets-link.sh prints
#     a link and a code, waits until the owner approves the server on any device, and writes the tunnel token
#     (tunnel.env) and the server's id and key at Pairnets (relay.env), both root only. From here on the tunnel goes
#     the same way as with --public-url, except that Pairnets made it and there is no name of our own.
if [[ -z "$LINK" && -z "$PUBLIC_URL" && -z "$BIND" && -z "$TUNNEL" && ! -f "$ENV_FILE" ]]; then
  LINK=first
fi
RELAY_ID=""
RELAY_KEY=""
RELAY_SERVICE=""
TUNNEL_TOKEN="${TUNNEL_TOKEN:-}"
if [[ -n "$LINK" ]]; then
  [[ -z "$PORT_GIVEN" || "$PORT" == "$RELAY_PORT" ]] || die "Pairnets reaches the server on port $RELAY_PORT; leave out --port (or use --public-url for your own domain)"
  PORT=$RELAY_PORT
  PORT_GIVEN=1
  # What the rest needs is checked first, so a link is not made for an install that cannot finish.
  [[ -f "$SRC_DIR/pairnets-link.sh" ]] || die "pairnets-link.sh not found next to install.sh"
  [[ -f "$SRC_DIR/$TUNNEL_SERVICE.service" ]] || die "$TUNNEL_SERVICE.service not found next to install.sh"
  command -v curl >/dev/null || die "curl is required to link this server to Pairnets (sudo apt install curl)"
  if [[ ! -f "$ENV_FILE" ]]; then
    command -v openssl >/dev/null || die "openssl is required to generate a token (apt install openssl)"
  fi
  # A first install that stopped after the link was made (a failed download, say) picks up from there, as long as
  # the link is fresh: Pairnets forgets a server that never connected within an hour.
  if [[ "$LINK" == first && -f "$TUNNEL_ENV" && -n "$(find "$RELAY_ENV" -mmin -50 2>/dev/null || true)" ]]; then
    echo "Using the link to your Pairnets account made a moment ago"
  else
    bash "$SRC_DIR/pairnets-link.sh"
  fi
  RELAY_ID="$(relay_value Sync__RelayNestId)"
  RELAY_KEY="$(relay_value Sync__RelayKey)"
  RELAY_SERVICE="$(relay_value Sync__RelayServiceUrl)"
  RELAY_SERVICE="${RELAY_SERVICE:-$DEFAULT_RELAY_SERVICE}"
  TUNNEL_TOKEN="$(grep -m1 '^TUNNEL_TOKEN=' "$TUNNEL_ENV" 2>/dev/null | cut -d= -f2- || true)"
  if [[ ! "$RELAY_ID" =~ ^nst_[0-9a-hjkmnp-tv-z]{26}$ || ! "$RELAY_KEY" =~ ^[A-Za-z0-9_-]{43}$ || -z "$TUNNEL_TOKEN" ]]; then
    die "the link to your Pairnets account was not finished (see above); run this again"
  fi
  TUNNEL=1
elif [[ -n "$EXISTING_RELAY" && -n "$TUNNEL" && -z "$PUBLIC_URL" ]]; then
  # Pairnets made this server's tunnel; a token for another one would cut it off.
  die "this server is linked to your Pairnets account, which made its tunnel. To get a new one, link it again: sudo ./install.sh --link"
fi

# cloudflared, from Cloudflare's signed package repository (for the tunnel).
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

# The Cloudflare Tunnel is the way in: a link, a new public address, or a first install, sets it up.
if [[ -n "$PUBLIC_URL" ]] || [[ -z "$BIND" && -z "$EXISTING_URL" ]]; then
  TUNNEL=1
fi
if [[ -n "$TUNNEL" ]]; then
  # The tunnel delivers requests to localhost, so the server listens there only.
  [[ -z "$BIND" || "$BIND" == 127.0.0.1 ]] || die "--cloudflare-tunnel listens on 127.0.0.1; leave out --bind"
  BIND=127.0.0.1
  if [[ -z "$PORT_GIVEN" && "$EXISTING_URL" =~ :([0-9]+)/?$ ]]; then
    PORT="${BASH_REMATCH[1]}"
  fi
  if [[ -z "$RELAY_ID" ]]; then
    [[ -n "$PUBLIC_URL" || -n "$EXISTING_PUBLIC_URL" ]] || die "add --public-url https://<the public hostname you gave the tunnel>, or link this server to your Pairnets account instead: sudo ./install.sh --link"
    if [[ -z "$TUNNEL_TOKEN" ]]; then
      echo "Paste the tunnel token from the Cloudflare dashboard (Networks > Tunnels > your tunnel > the long"
      echo "text at the end of the install command; the whole command works too). It is not shown as you paste:"
      { read -rs TUNNEL_TOKEN < /dev/tty; } 2>/dev/null || die "no terminal to ask for the token; run with TUNNEL_TOKEN=<token> in the environment instead"
      echo
    fi
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
[[ -n "$BIND" ]] || die "nothing to listen on: run with --link to link this server to your Pairnets account, or --public-url https://<the public hostname of your Cloudflare Tunnel> (see docs/HOWTO.md)"
[[ "$BIND" != "0.0.0.0" && "$BIND" != "::" ]] || die "refusing to listen on all interfaces; use the Cloudflare Tunnel (--link or --public-url)"

# 1. Service account and directories.
if ! id pairnets >/dev/null 2>&1; then
  useradd --system --home-dir "$DATA_DIR" --no-create-home --shell /usr/sbin/nologin pairnets
fi
install -d -m 0755 -o root -g root "$INSTALL_DIR"
install -d -m 0700 -o pairnets -g pairnets "$DATA_DIR"
install -d -m 0700 -o root -g root "$CONF_DIR"
install -d -m 0750 -o pairnets -g pairnets "$DATA_DIR/update"

# 2. Program files (stop first so the binary can be replaced).
if systemctl is-active --quiet "$SERVICE"; then
  systemctl stop "$SERVICE"
fi
install -m 0755 -o root -g root "$SRC_DIR/pairnets-server" "$INSTALL_DIR/pairnets-server"
for f in "$SRC_DIR"/*.so "$SRC_DIR"/appsettings.json "$SRC_DIR"/VERSION; do
  [[ -e "$f" ]] && install -m 0644 -o root -g root "$f" "$INSTALL_DIR/"
done
# The self-updater (runs as root only when the server asks for it; see DEPLOY.md) and the link helper.
for f in update.sh pairnets-link.sh; do
  if [[ -f "$SRC_DIR/$f" ]]; then
    install -m 0755 -o root -g root "$SRC_DIR/$f" "$INSTALL_DIR/$f"
  fi
done

# 3. Environment file. The shared token (for older apps) is generated once, kept on upgrades and never
#    printed: it stays in this root-only file.
TOKEN=""
if [[ ! -f "$ENV_FILE" ]] || ! grep -q '^SYNC_TOKEN=.\{16,\}' "$ENV_FILE"; then
  command -v openssl >/dev/null || die "openssl is required to generate a token (apt install openssl)"
  TOKEN="$(openssl rand -hex 32)"
fi
umask 077
if [[ -f "$ENV_FILE" && -z "$TOKEN" ]]; then
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
# drop_env <keys>: removes the lines of these keys (an extended regex) from $ENV_FILE.tmp, in any spelling, since the
# server reads names without regard to case; prints the keys it removed, comma-separated.
drop_env() {
  local pattern="^[[:space:]]*($1)="
  grep -iE "$pattern" "$ENV_FILE.tmp" | cut -d= -f1 | tr -d '[:blank:]' | paste -sd, - || true
  { grep -viE "$pattern" "$ENV_FILE.tmp" || true; } > "$ENV_FILE.tmp.new"
  mv "$ENV_FILE.tmp.new" "$ENV_FILE.tmp"
}
if [[ -n "$TUNNEL" ]]; then
  # Behind the tunnel every request comes from 127.0.0.1; take the client's address from Cloudflare.
  set_env Sync__TrustProxyHeaders true
fi
LEFT_OWN_NAME=""
LEFT_RELAY=""
if [[ -n "$RELAY_ID" ]]; then
  # Linked to Pairnets: the apps reach the server through the service, which signs its own calls to the server with
  # the key. A linked server has no public name of its own (and so no website), so an older name goes.
  LEFT_OWN_NAME="$(drop_env 'PUBLIC_URL|PAIRNETS_PUBLIC_URL|Sync__PublicUrl')"
  drop_env 'Sync__Relay[A-Za-z]*' >/dev/null
  set_env Sync__RelayNestId "$RELAY_ID"
  set_env Sync__RelayKey "$RELAY_KEY"
  set_env Sync__RelayServiceUrl "$RELAY_SERVICE"
  if [[ -n "$LEFT_OWN_NAME" ]]; then
    echo "This server is now reached through $RELAY_SERVICE; its own name ($EXISTING_PUBLIC_URL) is no longer used."
  fi
fi
if [[ -n "$PUBLIC_URL" ]]; then
  # The nest's public name: the server builds its website, sign-in links and passkeys on it, and the
  # apps type it to sign in. The server reads Sync__PublicUrl and PUBLIC_URL before PAIRNETS_PUBLIC_URL,
  # so those lines go: the name given here always wins.
  REPLACED="$(drop_env 'PUBLIC_URL|Sync__PublicUrl')"
  if [[ -n "$REPLACED" ]]; then
    echo "Replaced ${REPLACED//,/ and } in $ENV_FILE: the nest's name is now PAIRNETS_PUBLIC_URL=$PUBLIC_URL"
  fi
  set_env PAIRNETS_PUBLIC_URL "$PUBLIC_URL"
  # Its own name and tunnel replace a link to Pairnets (whose tunnel token was just replaced).
  LEFT_RELAY="$(drop_env 'Sync__Relay[A-Za-z]*')"
fi
chown root:root "$ENV_FILE.tmp"
chmod 600 "$ENV_FILE.tmp"
mv "$ENV_FILE.tmp" "$ENV_FILE"
# The link's settings are in $ENV_FILE now (root only, like relay.env was); nothing needs the key any more.
rm -f "$RELAY_ENV"
unset RELAY_KEY

# 4. systemd unit.
install -m 0644 -o root -g root "$SRC_DIR/pairnets-server.service" /etc/systemd/system/pairnets-server.service
if [[ -f "$SRC_DIR/pairnets-update.service" && -f "$SRC_DIR/pairnets-update.path" ]]; then
  install -m 0644 -o root -g root "$SRC_DIR/pairnets-update.service" /etc/systemd/system/pairnets-update.service
  install -m 0644 -o root -g root "$SRC_DIR/pairnets-update.path" /etc/systemd/system/pairnets-update.path
fi
if [[ -f "$SRC_DIR/pairnets-update.timer" ]]; then
  install -m 0644 -o root -g root "$SRC_DIR/pairnets-update.timer" /etc/systemd/system/pairnets-update.timer
fi
# 4b. Cloudflare Tunnel (a link, --public-url or --cloudflare-tunnel; upgrades keep an existing one as it is).
if [[ -n "$TUNNEL" ]]; then
  # The token stays in a root-only file: unit files are readable by every local user.
  printf 'TUNNEL_TOKEN=%s\n' "$TUNNEL_TOKEN" > "$TUNNEL_ENV.tmp"
  chown root:root "$TUNNEL_ENV.tmp"
  chmod 600 "$TUNNEL_ENV.tmp"
  mv "$TUNNEL_ENV.tmp" "$TUNNEL_ENV"
fi
# Nothing below needs it, and no other program (the pairnets user's owner-link below) inherits it.
unset TUNNEL_TOKEN
if [[ -n "$TUNNEL" || -n "$TUNNEL_FROM_TETHER" || -f /etc/systemd/system/$TUNNEL_SERVICE.service ]] && [[ -f "$SRC_DIR/$TUNNEL_SERVICE.service" ]]; then
  install -m 0644 -o root -g root "$SRC_DIR/$TUNNEL_SERVICE.service" /etc/systemd/system/$TUNNEL_SERVICE.service
fi

systemctl daemon-reload
systemctl enable --now "$SERVICE"
if [[ -n "$TUNNEL" || -n "$TUNNEL_FROM_TETHER" ]]; then
  systemctl enable "$TUNNEL_SERVICE"
  systemctl restart "$TUNNEL_SERVICE"
fi
if [[ -f /etc/systemd/system/pairnets-update.path ]]; then
  # Clear any "failed" state (e.g. a start limit hit) and make sure the watcher really runs.
  systemctl reset-failed pairnets-update.path pairnets-update.service 2>/dev/null || true
  systemctl enable pairnets-update.path
  systemctl restart pairnets-update.path
fi
if [[ -f /etc/systemd/system/pairnets-update.timer ]]; then
  systemctl enable --now pairnets-update.timer
fi

# 5. Wait for health, on this machine first. This result decides the exit code, so the self-updater
#    reports a server that does not come back as a failed update. The server only answers after it has
#    checked its data folder, which takes a while on a big nest: wait up to 5 minutes while systemd says
#    it is starting or running, and stop at once when it has failed.
LOCAL_HEALTH=failed
HEALTH_WAIT=300
if command -v curl >/dev/null; then
  SECONDS=0
  while (( SECONDS < HEALTH_WAIT )); do
    if curl -fsS --max-time 5 "http://$BIND:$PORT/api/health" >/dev/null 2>&1; then LOCAL_HEALTH=ok; break; fi
    case "$(systemctl is-active "$SERVICE" 2>/dev/null || true)" in
      active|activating|reloading) sleep 1 ;;
      *) break ;;
    esac
  done
else
  LOCAL_HEALTH=unchecked
  echo "Note: curl is not installed, so the server's health was not checked (sudo apt install curl)." >&2
fi
if [[ "$LOCAL_HEALTH" == failed ]]; then
  echo "WARNING: the service did not answer on http://$BIND:$PORT/api/health (state: $(systemctl is-active "$SERVICE" 2>/dev/null || true), after ${SECONDS}s)." >&2
  echo "         Check: sudo systemctl status $SERVICE" >&2
  echo "         Log:   sudo journalctl -u $SERVICE -n 50" >&2
fi

SERVER_DATA_DIR="$(env_value Sync__DataDir)"
SERVER_DATA_DIR="${SERVER_DATA_DIR:-$DATA_DIR}"
NEST_URL="$(public_name)"
LINKED_ID="$(env_value Sync__RelayNestId)"
LINKED_SERVICE="$(env_value Sync__RelayServiceUrl)"
LINKED_SERVICE="${LINKED_SERVICE:-$DEFAULT_RELAY_SERVICE}"

# Then through a new tunnel: it needs a few seconds to connect, and DNS for a new hostname a little longer.
TUNNEL_HEALTH=""
if [[ -n "$TUNNEL" && -n "$NEST_URL" && "$LOCAL_HEALTH" == ok ]]; then
  TUNNEL_HEALTH=failed
  for _ in $(seq 1 30); do
    if curl -fsS --max-time 10 "$NEST_URL/api/health" >/dev/null 2>&1; then TUNNEL_HEALTH=ok; break; fi
    sleep 2
  done
  if [[ "$TUNNEL_HEALTH" == failed ]]; then
    echo "WARNING: $NEST_URL/api/health did not answer through the tunnel yet. Check:" >&2
    echo "         - the tunnel runs: sudo systemctl status $TUNNEL_SERVICE (log: sudo journalctl -u $TUNNEL_SERVICE -n 50)" >&2
    echo "         - in the Cloudflare dashboard the tunnel has a public hostname $NEST_URL -> HTTP localhost:$PORT" >&2
    echo "         - Bot Fight Mode is off for the domain (Security > Bots)" >&2
  fi
fi

# A newly linked server, through Pairnets: the service starts routing to a new server within about 30 seconds.
RELAY_HEALTH=""
if [[ -n "$RELAY_ID" && "$LOCAL_HEALTH" == ok ]]; then
  RELAY_HEALTH=failed
  echo "Waiting for Pairnets to reach this server (usually about 30 seconds, at most 3 minutes)..."
  SECONDS=0
  while (( SECONDS < 180 )); do
    if [[ "$(curl -fsS --max-time 10 "$LINKED_SERVICE/n/$RELAY_ID/api/health" 2>/dev/null || true)" == ok ]]; then RELAY_HEALTH=ok; break; fi
    sleep 3
  done
  if [[ "$RELAY_HEALTH" == failed ]]; then
    echo "WARNING: Pairnets could not reach this server yet. Check:" >&2
    echo "         - the tunnel runs: sudo systemctl status $TUNNEL_SERVICE (log: sudo journalctl -u $TUNNEL_SERVICE -n 50)" >&2
    echo "         - this server is listed on $LINKED_SERVICE/account; if it is not, link it again: sudo ./install.sh --link" >&2
    echo "         It may also just need a few more minutes: try again with curl $LINKED_SERVICE/n/$RELAY_ID/api/health" >&2
  fi
fi

# 6. The one-time link to set up the nest's website, only for a person at a terminal (never into the
#    self-updater's log), once the server runs (it reads the nest's name from what the server saved).
#    Only with a name of its own: a server linked to Pairnets has no website.
OWNER_LINK_CMD="sudo -u pairnets $INSTALL_DIR/pairnets-server owner-link"
if [[ "$SERVER_DATA_DIR" != "$DATA_DIR" ]]; then
  OWNER_LINK_CMD="$OWNER_LINK_CMD --data-dir $SERVER_DATA_DIR"
fi
OWNER_LINK=""
OWNER_LINK_STATE=""
OWNER_ERROR=""
if [[ -t 1 && "$LOCAL_HEALTH" == ok && -n "$NEST_URL" && -z "$LINKED_ID" ]]; then
  # --if-new prints nothing when the owner already has a way to sign in.
  if OWNER_OUT="$(cd / && timeout 60 runuser -u pairnets -- "$INSTALL_DIR/pairnets-server" owner-link --if-new --data-dir "$SERVER_DATA_DIR" 2>&1)"; then
    OWNER_LINK="$(printf '%s\n' "$OWNER_OUT" | grep -E '^https://' | tail -n1 || true)"
    if [[ -n "$OWNER_LINK" ]]; then OWNER_LINK_STATE="new"; else OWNER_LINK_STATE="exists"; fi
  else
    OWNER_LINK_STATE="failed"
    OWNER_ERROR="$(printf '%s\n' "$OWNER_OUT" | head -n1)"
  fi
fi

UPDATER_STATE="$(systemctl is-active pairnets-update.path 2>/dev/null || true)"
TUNNEL_STATE=""
if [[ -f /etc/systemd/system/$TUNNEL_SERVICE.service ]]; then
  TUNNEL_STATE="$(systemctl is-active "$TUNNEL_SERVICE" 2>/dev/null || true)"
fi

echo
if [[ "$LOCAL_HEALTH" == failed ]]; then
  echo "Pairnets server is installed, but it is not answering (see the warning above)."
else
  echo "Pairnets server is installed."
fi
echo "  Data:    $SERVER_DATA_DIR"
echo "  Config:  $ENV_FILE (root only)"
echo "  Logs:    sudo journalctl -u $SERVICE -f"
echo "  Updater: pairnets-update.path is ${UPDATER_STATE:-unknown} (should be: active)"
if [[ -n "$TUNNEL_STATE" ]]; then
  echo "  Tunnel:  $TUNNEL_SERVICE is $TUNNEL_STATE (should be: active)"
fi
if [[ -n "$LINKED_ID" ]]; then
  echo "  Linked:  to your Pairnets account at $LINKED_SERVICE (this server is $LINKED_ID)"
fi
echo
if [[ "$LOCAL_HEALTH" == failed ]]; then
  [[ -z "$NEST_URL" ]] || echo "Your nest's address: $NEST_URL"
  echo "The server did not start, so the install is not finished: see the warning above."
  exit 1
fi

if [[ -n "$LINKED_ID" ]]; then
  # Linked to Pairnets: the computers sign in with the same account, and the service brings them here.
  echo "This server is linked to your Pairnets account. Your files stay on this server; Pairnets only"
  echo "passes your computers' traffic on to it."
  echo
  echo "Next steps:"
  echo "  On each computer: install Pairnets and choose Continue with email, with the same account."
  echo "  (Get the app at https://pairnets.app/add. Continue with Google works too, with the same account.)"
  echo "  Your computers and this server are listed at $LINKED_SERVICE/account"
  if [[ -n "$LEFT_OWN_NAME" ]]; then
    echo "  Computers that used $EXISTING_PUBLIC_URL sign in again the same way."
  fi
  exit 0
fi

if [[ -n "$LEFT_RELAY" ]]; then
  echo "This server was linked to your Pairnets account; now it uses its own name. Remove it from your"
  echo "account at $EXISTING_RELAY_SERVICE/account, and sign each computer in again with the name below."
  echo
fi

if [[ -z "$NEST_URL" ]]; then
  # Advanced --bind without a public name: no website, so only the shared token works.
  echo "This nest has no public name, so it has no website and the apps cannot sign in with the browser."
  echo "Link it to your Pairnets account with: sudo ./install.sh --link"
  echo "(or give it your own domain with: sudo ./install.sh --public-url https://sync.example.com)."
  echo "Until then only older Pairnets apps connect, with the address http://$BIND:$PORT/ and the shared"
  echo "token kept in $ENV_FILE (show it with: sudo grep SYNC_TOKEN $ENV_FILE)."
  exit 0
fi

echo "Your nest's address: $NEST_URL"
echo
echo "Next steps:"
case "$OWNER_LINK_STATE" in
  new)
    echo "  1. Set up your nest's website: open this link in your browser. It works once, within"
    echo "     24 hours; keep it to yourself."
    echo "       $OWNER_LINK"
    ;;
  exists)
    echo "  1. Your nest's website is already set up: $NEST_URL/"
    echo "     (Locked out? Make a one-time link to get back in with: $OWNER_LINK_CMD)"
    ;;
  failed)
    echo "  1. Set up your nest's website. The setup link could not be made just now:"
    echo "       ${OWNER_ERROR:-no answer}"
    echo "     Make one with: $OWNER_LINK_CMD"
    ;;
  *)
    echo "  1. Set up your nest's website (if you have not yet): make a one-time link with"
    echo "       $OWNER_LINK_CMD"
    ;;
esac
echo "  2. On each computer: install the Pairnets app (https://pairnets.app/add), type the nest's"
echo "     name ${NEST_URL#https://}, press \"Sign in with your browser\", sign in on the nest's page"
echo "     and approve the computer."
echo
echo "For older apps only: they ask for the address and a shared token. The token is kept in"
echo "$ENV_FILE (show it with: sudo grep SYNC_TOKEN $ENV_FILE)."
