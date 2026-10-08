# Security

## Threat model

Pairnets is for one person with two PCs and one server, reached through a Cloudflare Tunnel on the
owner's domain.

**What protects your files**

| Layer | Protection |
|-------|------------|
| Network | The server listens **only** on `127.0.0.1` (never `0.0.0.0`; `install.sh` refuses it). `cloudflared` (service `pairnets-tunnel`, an unprivileged dynamic user) connects out to Cloudflare; no port is open on the server or any router. PCs connect with HTTPS to your hostname. The tunnel token is kept in `/etc/pairnets/tunnel.env` (root, mode 600), never in the world-readable unit file or on a command line. With `Sync__TrustProxyHeaders` the server takes the client address from `CF-Connecting-IP` on loopback connections only, so the failure throttle and the logs see each client, not cloudflared. The apps refuse a plain `http://` address that turns out to be behind Cloudflare, before they send the token. |
| Token | Every endpoint except `/api/health` requires a 256-bit shared token (`openssl rand -hex 32`). It is compared in constant time over SHA-256 digests. After 5 bad attempts from one address, responses are delayed (up to 10 s each). |
| Token at rest | Server: `/etc/pairnets/pairnets.env`, owner root, mode 600, read by systemd before it drops privileges. Client: encrypted with Windows DPAPI (current user) in `%AppData%\Pairnets\settings.json`. It is never written in plain text and never logged. **Settings → Reset this app** removes it (the Keychain and keyring entries on Mac and Linux too). Requests are logged without query strings or headers, and ASP.NET's own URL logging is off, so `access_token` cannot leak into journald. |
| Names | The server rejects path traversal (`..`, rooted paths, drive letters, backslashes), Windows-hostile names (`< > : " \| ? *`, control characters, trailing dots/spaces, `CON`, `NUL`, `COM1`… with or without extension), paths over 1024 characters and names that differ only in letter case from existing ones. It canonicalizes every path, checks it stays inside `files/`, and refuses any path through a symlink. Clients never follow symlinks, junctions or reparse points. |
| Process | The service runs as the unprivileged `pairnets` user under systemd hardening: `NoNewPrivileges`, `ProtectSystem=strict` with write access only to `/var/lib/pairnets`, `ProtectHome`, `PrivateTmp`, `PrivateDevices`, no capabilities, restricted address families and namespaces. |
| Data loss | The server never destroys data outside the history purge. Every overwritten or deleted file is kept in `history/` for 30 days and at least the last 5 versions. Clients refuse to run when the folder looks wrong (missing marker, empty folder) or a pass would delete many files. |
| Privacy | No telemetry, analytics, update checks or third-party services. The client only talks to your server. |
| Computer list | For the Devices page and the overview, the server keeps in `devices.json` the device name each computer sends, its Pairnets version and system (`X-Pairnets-Client`), when it was first and last seen, and its last change. Only requests that passed the token check count (the health check never does), and over-long version or system values are ignored. It is shown, never trusted: syncing does not depend on it. |

**What is not protected**

* **A compromised device that has the token** can read, change and delete every
  synced file. Deletions and changes stay recoverable from `history/` for the retention period,
  unless the attacker also has shell access to the server.
* **Anyone with root on the server** can read everything. Files are not encrypted at rest on the
  server (no end-to-end encryption in v1). Use full-disk encryption on the server if that matters.
* **Cloudflare sees everything.** HTTPS ends at Cloudflare's edge, which passes requests on through
  the tunnel, so Cloudflare (and anyone who controls your Cloudflare account) can see the token and
  file contents in transit. Use end-to-end encryption, once it exists, for files that must stay
  private from it.
* **The server is on the internet.** Anyone can reach
  `https://<your hostname>`; the 256-bit token is what keeps them out (guessing it is not
  feasible, and repeated failures are slowed down per client address). Whoever has the token can
  sync from anywhere, so rotate it if it may have leaked. Whoever has the *tunnel* token can run a
  connector for your hostname and receive its traffic: keep it secret, and if it leaks, refresh it
  in the Cloudflare dashboard and run `install.sh --cloudflare-tunnel` again.
* Your Windows account. DPAPI ties the token to your Windows login, so malware running as you can
  read it.

## Rotating the token

Rotate it if a PC is lost or you suspect it leaked.

```bash
# On the server
NEW=$(openssl rand -hex 32)
sudo sed -i "s/^SYNC_TOKEN=.*/SYNC_TOKEN=$NEW/" /etc/pairnets/pairnets.env
sudo systemctl restart pairnets-server
echo "$NEW"    # enter it in Pairnets → Settings on both PCs, then clear your terminal
```

Both PCs show "The server rejected the token" until you enter the new one. Nothing is lost: local
changes made meanwhile sync after you update the token.

If a PC is lost, the new token is all it takes: the lost PC can no longer connect.

## Server self-update

Anyone with the token can ask the server to update itself (`POST /api/update`). The request can
only start `/opt/pairnets/update.sh` (root-owned, installed by `install.sh`); the server writes an
empty file and the script never reads its content. The script installs only the newest release
from the fixed GitHub address, verifies `SHA256SUMS.txt` over HTTPS, refuses downgrades and runs at
most once per 10 minutes. The server service itself keeps all its systemd hardening and never gets
root. Turn it off with `sudo systemctl disable --now pairnets-update.path`.

## Reporting

This is a personal project. Open an issue on the repository without including tokens, IPs or file
contents.
