# Deploying Tether

## 1. Server (Ubuntu, Tailscale)

Prerequisites: Ubuntu 22.04 or newer, Tailscale installed and logged in (`tailscale up`),
`curl` and `openssl` (`sudo apt install curl openssl`). No .NET runtime is needed: the server is
self-contained.

```bash
# Download the release (replace vX.Y.Z) and check its checksum
curl -LO https://github.com/MRnigth/Tether/releases/download/vX.Y.Z/tether-server-linux-x64.tar.gz
curl -LO https://github.com/MRnigth/Tether/releases/download/vX.Y.Z/SHA256SUMS.txt
sha256sum --check --ignore-missing SHA256SUMS.txt

tar xzf tether-server-linux-x64.tar.gz
cd tether-server-linux-x64
sudo ./install.sh
```

`install.sh` does the following:

1. Finds the Tailscale IPv4 address (`tailscale ip -4`) and binds to it only. Pass
   `--bind 100.x.y.z` to choose it yourself. It never binds to `0.0.0.0`.
2. Creates the system user `tether`, `/opt/tether` (program), `/var/lib/tether` (data, mode 700)
   and `/etc/tether` (config, root only).
3. Generates a token with `openssl rand -hex 32` the first time and writes `/etc/tether/tether.env`
   with mode 600. Upgrades keep the existing token.
4. Installs and starts `tether-server.service`, waits for `/api/health`, and **prints the server
   URL and token** to enter on both PCs. The token is printed only once; store it in a password
   manager.

To upgrade, extract the new release and run `sudo ./install.sh` again.

Useful commands:

```bash
sudo systemctl status tether-server
sudo journalctl -u tether-server -f          # logs (never contain the token)
curl http://$(tailscale ip -4):5075/api/health
```

### Configuration

`/etc/tether/tether.env` (see `deploy/tether.env.example`):

| Variable | Default | Meaning |
|----------|---------|---------|
| `SYNC_TOKEN` | – (required, ≥ 16 chars) | shared secret |
| `ASPNETCORE_URLS` | `http://127.0.0.1:5075` | listen address: use `http://<tailscale-ip>:5075` |
| `Sync__DataDir` | `/var/lib/tether` | data directory |
| `Sync__HistoryRetentionDays` | 30 | delete history versions older than this... |
| `Sync__HistoryMinVersions` | 5 | ...but always keep this many per file |

If Tailscale is not up yet at boot, binding fails and systemd retries every 5 seconds
(`Restart=always`).

### Tailscale ACL: only your two PCs may connect

In the Tailscale admin console (Access controls), make sure only your PCs can reach port 5075 on
the server. Starting point (also in `deploy/tailscale-acl.hujson`):

```hujson
{
  "hosts": { "tether-server": "100.x.y.z" },
  "acls": [
    { "action": "accept", "src": ["<desktop-device-name>", "<laptop-device-name>"], "dst": ["tether-server:5075"] }
    // ...your other rules. Anything not allowed is denied.
  ]
}
```

Check from another tailnet device that `curl http://100.x.y.z:5075/api/health` times out.

## 2. Windows PCs

1. Download `tether-client-win-x64.zip` from the release and extract `Tether.exe` somewhere
   permanent, for example `%LocalAppData%\Programs\Tether\`. No installation or admin rights are
   needed. An optional installer (`TetherSetup.exe`) is attached when the release workflow could
   build it.
2. Start `Tether.exe`. The first-run window asks for:
   * **Server URL**: `http://<server-tailscale-ip>:5075/` as printed by `install.sh`;
   * **Token**: as printed by `install.sh`;
   * **Folder to sync**, for example `D:\Work`;
   * **Device name**: defaults to the computer name. It must differ between your PCs.

   Press **Test connection**, then **Start syncing**. If the server already has files and the
   folder is not empty, Tether explains how they will be merged (nothing is deleted).
3. Optional: tick **Start with Windows** (in Settings or the tray menu).

On the second PC, do the same. Choosing an empty folder downloads everything; choosing a folder
that already holds a copy transfers only differences.

## 3. Backups

The data directory is the whole server state. A consistent backup with the service running:

```bash
BACKUP=/mnt/backup/tether
sudo mkdir -p "$BACKUP"
# 1. Manifest, copied consistently by SQLite (safe while the service writes)
sudo sqlite3 /var/lib/tether/manifest.db ".backup '$BACKUP/manifest.db'"
# 2. Files and history (only adds/updates; history makes old versions immutable)
sudo rsync -a --delete --exclude 'manifest.db*' --exclude 'tmp/' --exclude '.lock' \
     /var/lib/tether/ "$BACKUP/data/"
```

For a fully consistent snapshot, stop the service for the minute it takes
(`sudo systemctl stop tether-server`, back up, `sudo systemctl start tether-server`). PCs simply
retry while it is down.

### Restore

```bash
sudo systemctl stop tether-server
sudo rsync -a --delete /mnt/backup/tether/data/ /var/lib/tether/
sudo cp /mnt/backup/tether/manifest.db /var/lib/tether/manifest.db
sudo rm -f /var/lib/tether/manifest.db-wal /var/lib/tether/manifest.db-shm
sudo chown -R tether:tether /var/lib/tether
# Reconcile files/ with the manifest (adds files the manifest does not know, reports missing ones)
sudo -u tether /opt/tether/tether-server rescan --data-dir /var/lib/tether
sudo systemctl start tether-server
```

A restored server is older than what the PCs have seen, so each PC stops with **"The server went
back in time"**. Choose **Re-link to this server** in the tray menu. Tether then merges the PC's
folder with the restored server: nothing is deleted or overwritten, files that differ become
conflict copies, and files that only exist on the PC are uploaded again.

## 4. Maintenance commands

Stop the service first: the data directory is locked while it runs.

```bash
sudo systemctl stop tether-server
sudo -u tether /opt/tether/tether-server rescan --dry-run --data-dir /var/lib/tether
sudo -u tether /opt/tether/tether-server history list "Docs/report.docx" --data-dir /var/lib/tether
sudo -u tether /opt/tether/tether-server history restore "Docs/report.docx" 20261002T101500123Z-1a2b3c4d --data-dir /var/lib/tether
sudo -u tether /opt/tether/tether-server history purge --dry-run --data-dir /var/lib/tether
sudo systemctl start tether-server
```

Restoring a version makes it the current version; both PCs download it on their next sync. The
previous current version goes to history too, so a restore can itself be undone.

## 4b. Updating the server

**From the app (recommended).** When the server runs an older version than the app, Tether shows
"Your server should be updated" with an **Update server** button. What happens:

1. The app sends `POST /api/update` (with the token).
2. The server (which has no root rights) only creates the empty file
   `/var/lib/tether/update/request`.
3. The root-owned unit `tether-update.path` notices it and starts `tether-update.service`, which
   runs `/opt/tether/update.sh` once.
4. `update.sh` downloads the newest release from the fixed address
   `github.com/MRnigth/Tether/releases/latest/download`, checks it against `SHA256SUMS.txt`,
   refuses the same or an older version, allows one attempt per 10 minutes, and runs that release's
   `install.sh`. Your token, address and settings are kept.
5. Progress is written to `/var/lib/tether/update/status.json`; the app shows it and reconnects.

This needs the GitHub repository to be public (the server downloads without signing in), and a
server installed from a release that already contains `update.sh`. A server installed before that
must be updated by hand once:

```bash
curl -fsSL https://raw.githubusercontent.com/MRnigth/Tether/main/deploy/get.sh | sudo bash
```

Upgrades keep the address the server listens on and every line of `/etc/tether/tether.env`; pass
`--bind`/`--port` only to change the address. Check the updater with
`systemctl status tether-update.path` and `sudo tail -n 50 /var/lib/tether/update/update.log`. To turn
self-updates off: `sudo systemctl disable --now tether-update.path`.

**When an update from the app does not work.** Every run of `update.sh` is logged to
`/var/lib/tether/update/update.log` (the previous log is kept as `update.log.1`), and anything that
fails unexpectedly is reported to the app instead of leaving it waiting. In the app, turn on
**Settings → Debug mode** and click **Update server** (or **Check for update**). The update window
then shows every step the app takes and, at the end, what the server reports: whether `update.sh`
is installed, whether `tether-update.path` is enabled, whether a request is waiting, the last
`status.json` and the last lines of `update.log`. The token is never shown. **Copy details** puts
it all on the clipboard; the same report is `GET /api/update/diagnostics` (with the token). A
request that the updater does not pick up within 45 seconds is reported as such, and the report
shows the state of `tether-update.path` and `tether-update.service`. If the watcher is not
active, run `sudo systemctl reset-failed tether-update.path tether-update.service && sudo systemctl
restart tether-update.path`. As a fallback, `tether-update.timer` checks for a waiting request
every 2 minutes, and installing (or the one-line command) always restarts the watcher. If the self-update itself is broken, update by
hand once with the command above.

## 5. Uninstall

```bash
sudo systemctl disable --now tether-server
sudo rm /etc/systemd/system/tether-server.service && sudo systemctl daemon-reload
sudo rm -rf /opt/tether /etc/tether
# Your data stays in /var/lib/tether until you delete it yourself.
```
