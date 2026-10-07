# Deploying Pairnets

## 1. Server (Ubuntu, Tailscale)

(Without Tailscale: see [1b](#1b-server-without-tailscale-cloudflare-tunnel).)

Prerequisites: Ubuntu 22.04 or newer, Tailscale installed and logged in (`tailscale up`),
`curl` and `openssl` (`sudo apt install curl openssl`). No .NET runtime is needed: the server is
self-contained.

```bash
# Download the release (replace vX.Y.Z) and check its checksum
curl -LO https://github.com/MRnigth/Pairnets/releases/download/vX.Y.Z/pairnets-server-linux-x64.tar.gz
curl -LO https://github.com/MRnigth/Pairnets/releases/download/vX.Y.Z/SHA256SUMS.txt
sha256sum --check --ignore-missing SHA256SUMS.txt

tar xzf pairnets-server-linux-x64.tar.gz
cd pairnets-server-linux-x64
sudo ./install.sh
```

`install.sh` does the following:

1. Finds the Tailscale IPv4 address (`tailscale ip -4`) and binds to it only. Pass
   `--bind 100.x.y.z` to choose it yourself. It never binds to `0.0.0.0`.
2. Creates the system user `pairnets`, `/opt/pairnets` (program), `/var/lib/pairnets` (data, mode 700)
   and `/etc/pairnets` (config, root only).
3. Generates a token with `openssl rand -hex 32` the first time and writes `/etc/pairnets/pairnets.env`
   with mode 600. Upgrades keep the existing token.
4. Installs and starts `pairnets-server.service`, waits for `/api/health`, and **prints the server
   URL and token** to enter on both PCs. The token is printed only once; store it in a password
   manager.

To upgrade, extract the new release and run `sudo ./install.sh` again.

Useful commands:

```bash
sudo systemctl status pairnets-server
sudo journalctl -u pairnets-server -f          # logs (never contain the token)
curl http://$(tailscale ip -4):5075/api/health
```

### Configuration

`/etc/pairnets/pairnets.env` (see `deploy/pairnets.env.example`):

| Variable | Default | Meaning |
|----------|---------|---------|
| `SYNC_TOKEN` | – (required, ≥ 16 chars) | shared secret |
| `ASPNETCORE_URLS` | `http://127.0.0.1:5075` | listen address: use `http://<tailscale-ip>:5075` |
| `Sync__DataDir` | `/var/lib/pairnets` | data directory |
| `Sync__UpdateDir` | `/var/lib/pairnets/update` (set by install.sh) | where update requests go; must be the folder the root updater watches |
| `Sync__HistoryRetentionDays` | 30 | delete history versions older than this... |
| `Sync__HistoryMinVersions` | 5 | ...but always keep this many per file |
| `Sync__TrustProxyHeaders` | `false` | `true` behind a tunnel or reverse proxy on the same machine: the client address comes from `CF-Connecting-IP` (or the last `X-Forwarded-For` entry), believed only on loopback connections. Set by `--cloudflare-tunnel`. |
| `PAIRNETS_PUBLIC_URL` | – | the `https://` address the PCs use behind a tunnel; only shown by `install.sh` |

If Tailscale is not up yet at boot, binding fails and systemd retries every 5 seconds
(`Restart=always`).

### Tailscale ACL: only your two PCs may connect

In the Tailscale admin console (Access controls), make sure only your PCs can reach port 5075 on
the server. Starting point (also in `deploy/tailscale-acl.hujson`):

```hujson
{
  "hosts": { "pairnets-server": "100.x.y.z" },
  "acls": [
    { "action": "accept", "src": ["<desktop-device-name>", "<laptop-device-name>"], "dst": ["pairnets-server:5075"] }
    // ...your other rules. Anything not allowed is denied.
  ]
}
```

Check from another tailnet device that `curl http://100.x.y.z:5075/api/health` times out.

## 1b. Server without Tailscale (Cloudflare Tunnel)

For people who cannot use Tailscale or open ports. The server listens on `127.0.0.1:5075` and
`cloudflared` carries requests for a public hostname (`https://sync.example.com`) to it over an
outbound connection. Step-by-step with the dashboard: [HOWTO 4b](HOWTO.md#4b-no-tailscale-use-a-cloudflare-tunnel-instead).

1. In the Cloudflare dashboard (Zero Trust → Networks → Tunnels) create a *Cloudflared* tunnel,
   copy its token, and add a public hostname → `HTTP` `localhost:5075`. Turn off Bot Fight Mode for
   the domain.
2. On the server:

   ```bash
   sudo ./install.sh --cloudflare-tunnel --public-url https://sync.example.com
   # or: TUNNEL_TOKEN=<token> sudo -E ./install.sh --cloudflare-tunnel --public-url ...
   ```

   `install.sh` then also:
   * installs `cloudflared` from `pkg.cloudflare.com` (signed apt repository) if `/usr/bin/cloudflared` is missing;
   * binds to `127.0.0.1` (keeping the port of an earlier install) and sets `Sync__TrustProxyHeaders=true`;
   * writes the token to `/etc/pairnets/tunnel.env` (root, mode 600) and installs
     `pairnets-tunnel.service`, which runs `cloudflared tunnel run` as a dynamic, unprivileged user.
     The token is not put in the unit file or on a command line, where other local users could
     read it (`cloudflared service install <token>` would do both);
   * waits for `https://sync.example.com/api/health` and prints that URL.

Running it again with a new token replaces the token. Upgrades (`sudo ./install.sh` without options,
or the self-updater) keep the tunnel as it is. To go back to Tailscale:
`sudo ./install.sh --bind 100.x.y.z` and `sudo systemctl disable --now pairnets-tunnel`.

```bash
sudo systemctl status pairnets-tunnel
sudo journalctl -u pairnets-tunnel -f
curl https://sync.example.com/api/health
```

Big uploads are sent in pieces of 4 to 50 MB, sized to the connection (Cloudflare's free plan
refuses request bodies over 100 MB);
older apps that send a big file in one request get `413` through the tunnel, so update the apps
along with the server. See [SECURITY.md](SECURITY.md) for what Cloudflare can see.

## 2. Windows PCs

1. Download `pairnets-client-win-x64.zip` from the release and extract `Pairnets.exe` somewhere
   permanent, for example `%LocalAppData%\Programs\Pairnets\`. No installation or admin rights are
   needed. An optional installer (`PairnetsSetup.exe`) is attached when the release workflow could
   build it.
2. Start `Pairnets.exe`. The first-run window asks for:
   * **Server URL**: `http://<server-tailscale-ip>:5075/` as printed by `install.sh`;
   * **Token**: as printed by `install.sh`;
   * **Folder to sync**, for example `D:\Work`;
   * **Device name**: defaults to the computer name. It must differ between your PCs.

   Press **Test connection**, then **Start syncing**. If the server already has files and the
   folder is not empty, Pairnets explains how they will be merged (nothing is deleted).
3. Optional: tick **Start with Windows** (in Settings or the tray menu).

On the second PC, do the same. Choosing an empty folder downloads everything; choosing a folder
that already holds a copy transfers only differences.

## 3. Backups

The data directory is the whole server state. A consistent backup with the service running:

```bash
BACKUP=/mnt/backup/pairnets
sudo mkdir -p "$BACKUP"
# 1. Manifest, copied consistently by SQLite (safe while the service writes)
sudo sqlite3 /var/lib/pairnets/manifest.db ".backup '$BACKUP/manifest.db'"
# 2. Files and history (only adds/updates; history makes old versions immutable)
sudo rsync -a --delete --exclude 'manifest.db*' --exclude 'tmp/' --exclude '.lock' \
     /var/lib/pairnets/ "$BACKUP/data/"
```

For a fully consistent snapshot, stop the service for the minute it takes
(`sudo systemctl stop pairnets-server`, back up, `sudo systemctl start pairnets-server`). PCs simply
retry while it is down.

### Restore

```bash
sudo systemctl stop pairnets-server
sudo rsync -a --delete /mnt/backup/pairnets/data/ /var/lib/pairnets/
sudo cp /mnt/backup/pairnets/manifest.db /var/lib/pairnets/manifest.db
sudo rm -f /var/lib/pairnets/manifest.db-wal /var/lib/pairnets/manifest.db-shm
sudo chown -R pairnets:pairnets /var/lib/pairnets
# Reconcile files/ with the manifest (adds files the manifest does not know, reports missing ones)
sudo -u pairnets /opt/pairnets/pairnets-server rescan --data-dir /var/lib/pairnets
sudo systemctl start pairnets-server
```

A restored server is older than what the PCs have seen, so each PC stops with **"The server went
back in time"**. Choose **Re-link to this server** in the tray menu. Pairnets then merges the PC's
folder with the restored server: nothing is deleted or overwritten, files that differ become
conflict copies, and files that only exist on the PC are uploaded again.

## 4. Maintenance commands

Stop the service first: the data directory is locked while it runs.

```bash
sudo systemctl stop pairnets-server
sudo -u pairnets /opt/pairnets/pairnets-server rescan --dry-run --data-dir /var/lib/pairnets
sudo -u pairnets /opt/pairnets/pairnets-server history list "Docs/report.docx" --data-dir /var/lib/pairnets
sudo -u pairnets /opt/pairnets/pairnets-server history restore "Docs/report.docx" 20261002T101500123Z-1a2b3c4d --data-dir /var/lib/pairnets
sudo -u pairnets /opt/pairnets/pairnets-server history purge --dry-run --data-dir /var/lib/pairnets
sudo systemctl start pairnets-server
```

Restoring a version makes it the current version; both PCs download it on their next sync. The
previous current version goes to history too, so a restore can itself be undone.

## 4b. Updating the server

**From the app (recommended).** When the server runs an older version than the app, Pairnets shows
"Your server should be updated" with an **Update server** button. What happens:

1. The app sends `POST /api/update` (with the token).
2. The server (which has no root rights) only creates the empty file
   `/var/lib/pairnets/update/request`.
3. The root-owned unit `pairnets-update.path` notices it and starts `pairnets-update.service`, which
   runs `/opt/pairnets/update.sh` once.
4. `update.sh` downloads the newest release from the fixed address
   `github.com/MRnigth/Pairnets/releases/latest/download`, checks it against `SHA256SUMS.txt`,
   refuses the same or an older version, allows one attempt per 10 minutes, and runs that release's
   `install.sh`. Your token, address and settings are kept.
5. Progress is written to `/var/lib/pairnets/update/status.json`; the app shows it and reconnects.

This needs the GitHub repository to be public (the server downloads without signing in), and a
server installed from a release that already contains `update.sh`. A server installed before that
must be updated by hand once:

```bash
curl -fsSL https://raw.githubusercontent.com/MRnigth/Pairnets/main/deploy/get.sh | sudo bash
```

Upgrades keep the address the server listens on and every line of `/etc/pairnets/pairnets.env`; pass
`--bind`/`--port` only to change the address. Check the updater with
`systemctl status pairnets-update.path` and `sudo tail -n 50 /var/lib/pairnets/update/update.log`. To turn
self-updates off: `sudo systemctl disable --now pairnets-update.path`.

**When an update from the app does not work.** Every run of `update.sh` is logged to
`/var/lib/pairnets/update/update.log` (the previous log is kept as `update.log.1`), and anything that
fails unexpectedly is reported to the app instead of leaving it waiting. In the app, turn on
**Settings → Debug mode** and click **Update server** (or **Check for update**). The update window
then shows every step the app takes and, at the end, what the server reports: whether `update.sh`
is installed, whether `pairnets-update.path` is enabled, whether a request is waiting, the last
`status.json` and the last lines of `update.log`. The token is never shown. **Copy details** puts
it all on the clipboard; the same report is `GET /api/update/diagnostics` (with the token). A
request that the updater does not pick up within 45 seconds is reported as such, and the report
shows the state of `pairnets-update.path` and `pairnets-update.service`. If the watcher is not
active, run `sudo systemctl reset-failed pairnets-update.path pairnets-update.service && sudo systemctl
restart pairnets-update.path`. As a fallback, `pairnets-update.timer` checks for a waiting request
every 2 minutes, and installing (or the one-line command) always restarts the watcher. To check it quickly:
`systemctl is-active pairnets-update.path pairnets-update.timer` should print `active` twice. If the self-update itself is broken, update by
hand once with the command above.

## 5. Uninstall

```bash
sudo systemctl disable --now pairnets-server
sudo systemctl disable --now pairnets-tunnel 2>/dev/null   # only with a Cloudflare Tunnel
sudo rm -f /etc/systemd/system/pairnets-server.service /etc/systemd/system/pairnets-tunnel.service
sudo systemctl daemon-reload
sudo rm -rf /opt/pairnets /etc/pairnets
# Your data stays in /var/lib/pairnets until you delete it yourself.
# cloudflared stays installed (sudo apt remove cloudflared); delete the tunnel in the Cloudflare dashboard.
```
