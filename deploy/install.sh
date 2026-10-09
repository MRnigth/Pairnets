#!/usr/bin/env bash
# Pairnets server installer for Ubuntu (the server is "the nest"). Idempotent: run it again to upgrade.
# The self-updater (update.sh) runs it the same way on every upgrade, without a terminal.
#
#   sudo ./install.sh --name alice
#       first install with a free name, alice.pairnets.app: asks for your email address and the code sent to it,
#       and the Pairnets name service makes the Cloudflare Tunnel (pairnets-name.sh; nothing to set up in Cloudflare)
#   sudo ./install.sh --public-url https://sync.example.com
#       first install: the server listens on 127.0.0.1 only and a Cloudflare Tunnel gives it its public
#       name, with no open ports (asks for the tunnel token, or reads it from TUNNEL_TOKEN; see docs/HOWTO.md)
#   sudo ./install.sh
#       upgrade: keeps the data, settings, name and tunnel already configured
#
# At the end it prints the nest's address and, on a terminal, a one-time link to set up the nest's
# website. Then each computer installs the Pairnets app, types the nest's name and signs in with the
# browser. Exits non-zero when the server does not answer after the install. More options: --help.
#
# Run it from the extracted release folder (it must contain pairnets-server).
set -euo pipefail

PORT=5075
BIND=""
PORT_GIVEN=""
TUNNEL=""
PUBLIC_URL=""
NAME=""
EMAIL=""
RELEASE_NAME=""
SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
INSTALL_DIR=/opt/pairnets
DATA_DIR=/var/lib/pairnets
CONF_DIR=/etc/pairnets
ENV_FILE=$CONF_DIR/pairnets.env
TUNNEL_ENV=$CONF_DIR/tunnel.env
NAME_ENV=$CONF_DIR/name.env
SERVICE=pairnets-server
TUNNEL_SERVICE=pairnets-tunnel

die() { echo "error: $*" >&2; exit 1; }

usage() {
  cat <<'USAGE'
Usage: sudo ./install.sh [options]

  sudo ./install.sh --name alice
      First install with a free name: alice.pairnets.app. Asks for your email address and the code
      sent to it; the tunnel is made for you, with nothing to set up in Cloudflare.
  sudo ./install.sh --public-url https://sync.example.com
      First install with your own domain, or a new name for the nest: the public name you gave your
      Cloudflare Tunnel. Asks for the tunnel token (or reads it from TUNNEL_TOKEN). See docs/HOWTO.md.
  sudo ./install.sh
      Upgrade: keeps the data, settings, name and tunnel already configured.

Options:
  --name <name>                 a free name for the nest: <name>.pairnets.app (sets up the tunnel)
  --email <address>             with --name: where the code goes (asked for when left out)
  --release-name                give the free name back (the nest cannot be reached until it has a new one)
  --public-url https://<name>   your own domain as the nest's public name (sets up the Cloudflare Tunnel)
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
    --name) example=alice ;;
    --email) example=you@example.com ;;
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
    --name) NAME="$2" ;;
    --email) EMAIL="$2" ;;
  esac
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --bind|--port|--public-url|--name|--email)
      # Last on the line, or followed by another option: the value is missing.
      if [[ $# -lt 2 || "$2" == -* ]]; then missing_value "$1"; fi
      set_option "$1" "$2"
      shift 2 ;;
    --bind=*|--port=*|--public-url=*|--name=*|--email=*) set_option "${1%%=*}" "${1#*=}"; shift ;;
    --cloudflare-tunnel) TUNNEL=1; shift ;;
    --release-name) RELEASE_NAME=1; shift ;;
    -h|--help) usage; exit 0 ;;
    https://*) usage_error "unknown option: $1 (to give the nest this name use: --public-url $1)" ;;
    *) usage_error "unknown option: $1" ;;
  esac
done

# A free name (--name) and your own domain (--public-url) are two ways to the same thing: one at a time.
if [[ -n "$NAME" ]]; then
  [[ -z "$PUBLIC_URL" ]] || usage_error "use --name (a free name like $NAME.pairnets.app) or --public-url (your own domain), not both"
  [[ -z "$BIND" ]] || usage_error "--name sets up the Cloudflare Tunnel, which listens on 127.0.0.1; leave out --bind"
  NAME="${NAME,,}"
  # The same rule as the name service (names/src/names.ts) and pairnets-name.sh.
  if [[ ! "$NAME" =~ ^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$ || "$NAME" == *--* ]]; then
    usage_error "--name takes 3 to 32 letters, digits or hyphens, with no hyphen at the start or end and no two in a row, like: --name alice"
  fi
fi
[[ -z "$EMAIL" || -n "$NAME" ]] || usage_error "--email goes with --name (it is where the code for the free name is sent)"
if [[ -n "$RELEASE_NAME" && -n "$NAME$EMAIL$PUBLIC_URL$BIND$PORT_GIVEN$TUNNEL" ]]; then
  usage_error "--release-name goes on its own: sudo ./install.sh --release-name"
fi

[[ $EUID -eq 0 ]] || die "run as root (sudo $0)"
[[ -x "$SRC_DIR/pairnets-server" ]] || die "pairnets-server binary not found next to install.sh"
[[ "$PORT" =~ ^[0-9]+$ ]] || die "invalid port: $PORT"
command -v systemctl >/dev/null || die "systemd is required"

# Giving the free name back changes nothing else; the nest keeps running and is reached again once it has a name.
if [[ -n "$RELEASE_NAME" ]]; then
  NAME_TOOL="$INSTALL_DIR/pairnets-name.sh"
  [[ -f "$NAME_TOOL" ]] || NAME_TOOL="$SRC_DIR/pairnets-name.sh"
  [[ -f "$NAME_TOOL" ]] || die "pairnets-name.sh not found next to install.sh"
  exec bash "$NAME_TOOL" release
fi

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

# name_value <key>: a value from $NAME_ENV (the free name, written by pairnets-name.sh), or nothing.
name_value() {
  grep -m1 "^$1=" "$NAME_ENV" 2>/dev/null | cut -d= -f2- || true
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

# 0b. A free name from the Pairnets name service (--name). pairnets-name.sh asks for an email address and the code
#     sent to it, and keeps the tunnel token (tunnel.env) and the name's key (name.env) in root-only files. From here
#     on the name goes the same way as --public-url with a token. Upgrades keep both and never claim again.
CURRENT_NAME="$(name_value PAIRNETS_NAME)"
if [[ -n "$NAME" ]]; then
  [[ -f "$SRC_DIR/pairnets-name.sh" ]] || die "pairnets-name.sh not found next to install.sh"
  # The name service points every tunnel at localhost:5075.
  [[ -z "$PORT_GIVEN" || "$PORT" == 5075 ]] || die "--name uses port 5075 (where the free name's tunnel delivers); leave out --port"
  PORT=5075
  PORT_GIVEN=1
  if [[ -z "$CURRENT_NAME" ]]; then
    NAME_ARGS=(claim "$NAME")
    [[ -z "$EMAIL" ]] || NAME_ARGS+=(--email "$EMAIL")
    bash "$SRC_DIR/pairnets-name.sh" "${NAME_ARGS[@]}"
  elif [[ "$CURRENT_NAME" != "$NAME" ]]; then
    die "this nest already has the name $(name_value PAIRNETS_NAME_URL); to change it, give that one back first: sudo $INSTALL_DIR/pairnets-name.sh release"
  elif [[ ! -f "$TUNNEL_ENV" ]]; then
    # The name is this nest's, but its token file is gone: ask the name service for a new token.
    bash "$SRC_DIR/pairnets-name.sh" rotate
  else
    echo "Keeping this nest's name $(name_value PAIRNETS_NAME_URL)"
  fi
  PUBLIC_URL="$(name_value PAIRNETS_NAME_URL)"
  TUNNEL_TOKEN="$(grep -m1 '^TUNNEL_TOKEN=' "$TUNNEL_ENV" 2>/dev/null | cut -d= -f2- || true)"
  [[ -n "$PUBLIC_URL" && -n "$TUNNEL_TOKEN" ]] || die "the free name was not set up (see above); run this again"
elif [[ -n "$CURRENT_NAME" && ( -n "$PUBLIC_URL" || -n "$TUNNEL" ) ]]; then
  # The tunnel belongs to the free name: a token for another tunnel would leave the name pointing nowhere.
  die "this nest has the free name $(name_value PAIRNETS_NAME_URL). For a new tunnel token use: sudo $INSTALL_DIR/pairnets-name.sh rotate. To use your own domain instead, give the name back first: sudo $INSTALL_DIR/pairnets-name.sh release"
fi

if [[ -n "$PUBLIC_URL" ]]; then
  PUBLIC_URL="${PUBLIC_URL%/}"
  [[ "$PUBLIC_URL" =~ ^https://[A-Za-z0-9.-]+(:[0-9]+)?$ ]] || die "--public-url must look like https://sync.example.com (https, no path)"
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

TUNNEL_TOKEN="${TUNNEL_TOKEN:-}"
# The Cloudflare Tunnel is the way in: a new public address, or a first install, sets it up.
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
[[ -n "$BIND" ]] || die "nothing to listen on: run with --public-url https://<the public hostname of your Cloudflare Tunnel> (see docs/HOWTO.md)"
[[ "$BIND" != "0.0.0.0" && "$BIND" != "::" ]] || die "refusing to listen on all interfaces; use the Cloudflare Tunnel (--public-url)"

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
# The self-updater (runs as root only when the server asks for it; see DEPLOY.md).
if [[ -f "$SRC_DIR/update.sh" ]]; then
  install -m 0755 -o root -g root "$SRC_DIR/update.sh" "$INSTALL_DIR/update.sh"
fi
# The free name's tool (status, rotate, release), for later.
if [[ -f "$SRC_DIR/pairnets-name.sh" ]]; then
  install -m 0755 -o root -g root "$SRC_DIR/pairnets-name.sh" "$INSTALL_DIR/pairnets-name.sh"
fi

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
if [[ -n "$TUNNEL" ]]; then
  # Behind the tunnel every request comes from 127.0.0.1; take the client's address from Cloudflare.
  set_env Sync__TrustProxyHeaders true
fi
if [[ -n "$PUBLIC_URL" ]]; then
  # The nest's public name: the server builds its website, sign-in links and passkeys on it, and the
  # apps type it to sign in. The server reads Sync__PublicUrl and PUBLIC_URL before PAIRNETS_PUBLIC_URL,
  # so those lines go: the name given here always wins.
  OVERRIDES='^[[:space:]]*(PUBLIC_URL|Sync__PublicUrl)='
  REPLACED="$(grep -iE "$OVERRIDES" "$ENV_FILE.tmp" | cut -d= -f1 | tr -d '[:blank:]' | paste -sd, - || true)"
  if [[ -n "$REPLACED" ]]; then
    { grep -viE "$OVERRIDES" "$ENV_FILE.tmp" || true; } > "$ENV_FILE.tmp.new"
    mv "$ENV_FILE.tmp.new" "$ENV_FILE.tmp"
    echo "Replaced ${REPLACED//,/ and } in $ENV_FILE: the nest's name is now PAIRNETS_PUBLIC_URL=$PUBLIC_URL"
  fi
  set_env PAIRNETS_PUBLIC_URL "$PUBLIC_URL"
fi
chown root:root "$ENV_FILE.tmp"
chmod 600 "$ENV_FILE.tmp"
mv "$ENV_FILE.tmp" "$ENV_FILE"

# 4. systemd unit.
install -m 0644 -o root -g root "$SRC_DIR/pairnets-server.service" /etc/systemd/system/pairnets-server.service
if [[ -f "$SRC_DIR/pairnets-update.service" && -f "$SRC_DIR/pairnets-update.path" ]]; then
  install -m 0644 -o root -g root "$SRC_DIR/pairnets-update.service" /etc/systemd/system/pairnets-update.service
  install -m 0644 -o root -g root "$SRC_DIR/pairnets-update.path" /etc/systemd/system/pairnets-update.path
fi
if [[ -f "$SRC_DIR/pairnets-update.timer" ]]; then
  install -m 0644 -o root -g root "$SRC_DIR/pairnets-update.timer" /etc/systemd/system/pairnets-update.timer
fi
# 4b. Cloudflare Tunnel (only with --cloudflare-tunnel; upgrades keep an existing one as it is).
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
    if [[ -f "$NAME_ENV" ]]; then
      # A free name: Cloudflare is set up by the name service, so there is nothing to check in a dashboard.
      echo "         - a brand-new name can take a few minutes to work everywhere; try: curl $NEST_URL/api/health" >&2
    else
      echo "         - in the Cloudflare dashboard the tunnel has a public hostname $NEST_URL -> HTTP localhost:$PORT" >&2
      echo "         - Bot Fight Mode is off for the domain (Security > Bots)" >&2
    fi
  fi
fi

# 6. The one-time link to set up the nest's website, only for a person at a terminal (never into the
#    self-updater's log), once the server runs (it reads the nest's name from what the server saved).
OWNER_LINK_CMD="sudo -u pairnets $INSTALL_DIR/pairnets-server owner-link"
if [[ "$SERVER_DATA_DIR" != "$DATA_DIR" ]]; then
  OWNER_LINK_CMD="$OWNER_LINK_CMD --data-dir $SERVER_DATA_DIR"
fi
OWNER_LINK=""
OWNER_LINK_STATE=""
OWNER_ERROR=""
if [[ -t 1 && "$LOCAL_HEALTH" == ok && -n "$NEST_URL" ]]; then
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
if [[ -f "$NAME_ENV" ]]; then
  echo "  Name:    a free name; its key is in $NAME_ENV (root only: keep a copy somewhere safe)"
fi
echo
if [[ "$LOCAL_HEALTH" == failed ]]; then
  [[ -z "$NEST_URL" ]] || echo "Your nest's address: $NEST_URL"
  echo "The server did not start, so the install is not finished: see the warning above."
  exit 1
fi

if [[ -z "$NEST_URL" ]]; then
  # Advanced --bind without a public name: no website, so only the shared token works.
  echo "This nest has no public name, so it has no website and the apps cannot sign in with the browser."
  echo "Give it one with: sudo ./install.sh --name <name> (a free name), or --public-url https://sync.example.com"
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
