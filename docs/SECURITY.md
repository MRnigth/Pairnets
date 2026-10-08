# Security

## Threat model

Pairnets is for one person with a few computers and one server (the *nest*), reached through a
Cloudflare Tunnel on the owner's domain.

**What protects your files**

| Layer | Protection |
|-------|------------|
| Network | The server listens **only** on `127.0.0.1` (never `0.0.0.0`; `install.sh` refuses it). `cloudflared` (service `pairnets-tunnel`, an unprivileged dynamic user) connects out to Cloudflare; no port is open on the server or any router. Computers connect with HTTPS to your hostname. The tunnel token is kept in `/etc/pairnets/tunnel.env` (root, mode 600), never in the world-readable unit file or on a command line. With `Sync__TrustProxyHeaders` the server takes the client address from `CF-Connecting-IP` on loopback connections only, so the failure throttle and the logs see each client, not cloudflared. The apps refuse a plain `http://` address that turns out to be behind Cloudflare, before they send a key or token. |
| A key per computer | Every computer has its own key (`pn_` plus 256 random bits). The nest shows it to the app once and keeps only its SHA-256 hash, in `/var/lib/pairnets/auth.db`. Removing a computer on the nest's **Devices** page stops its key at once and closes its live connection (removed with `pairnets-server devices remove` on the server: within 30 seconds). Every endpoint needs a key, except the health check, `/api/hello`, the two sign-in calls below and the nest's website, which has its own sign-in. After 5 bad keys from one address, answers are delayed (up to 10 s each). |
| Signing a computer in | The app asks the nest to join (`POST /api/pair/start`) and shows a code such as `KQ7M-4PXD`. You sign in on the nest's website, check that the code matches and press **Allow**. Only then can the app collect its key, with a secret only it knows (sent in the request body, never in a web address). A code lasts 10 minutes, an approved key must be collected within 5, at most 3 requests can wait per address (20 in all), and these calls are limited to 120 a minute per address. The `pairnets://` link that brings the app back to the front carries nothing: the key only travels through the app's own request. |
| The old shared token | Before per-computer keys, all computers used one shared token (`SYNC_TOKEN`). The nest still accepts it from older apps (compared in constant time over SHA-256 digests) until you switch it off on the website's **Security** page; switching it off also cuts computers that are still connected with it. A current app that only has the shared token stops syncing on a nest with its own name and asks you to sign in; it never trades the token for a key by itself. |
| The nest's website | It only answers on the nest's own HTTPS name. The first visit uses a one-time setup link from `pairnets-server owner-link` (it works once, for 24 hours, and making a new one cancels any older unused link; the code sits after `#`, so it never appears in a web address the server logs). You then add a passkey or a password (at least 10 characters, stored as a salted PBKDF2 hash). Optional: an email link (needs mail settings on the server; a sign-in link only ever goes to the address you confirmed, lasts 15 minutes, and the nest sends at most 5 emails an hour) and Google (needs your own Google sign-in client; only the Google account you connected gets in). Wrong passwords count across all addresses and are checked one at a time. You can't remove the last way to sign in. The session cookie is `__Host-`, Secure, HttpOnly and SameSite=Lax, and ends after 30 days without use. Requests that change something must come from the nest's own pages. Pages have a strict content security policy (no inline scripts, nothing from other sites, no framing). |
| Secrets at rest | Server: `/etc/pairnets/pairnets.env` (the shared token, and mail and Google secrets if you add them), owner root, mode 600, read by systemd before it drops privileges. `auth.db` holds only hashes of keys, sessions and links, the password hash and the passkeys' public keys. Computers: the key is encrypted with Windows DPAPI (current user) in `%AppData%\Pairnets\settings.json`, or kept in the macOS Keychain or the Linux keyring (without a keyring: a file only you can read). It is never written in plain text and never logged. **Settings → Start over → Reset this app** removes it. Requests are logged with method, route and file path only, never the rest of the query string or any header, and ASP.NET's own URL logging is off, so `access_token` cannot leak into journald. |
| Names | The server rejects path traversal (`..`, rooted paths, drive letters, backslashes), Windows-hostile names (`< > : " \| ? *`, control characters, trailing dots/spaces, `CON`, `NUL`, `COM1`… with or without extension), paths over 1024 characters and names that differ only in letter case from existing ones. It canonicalizes every path, checks it stays inside `files/`, and refuses any path through a symlink. Clients never follow symlinks, junctions or reparse points. |
| Process | The service runs as the unprivileged `pairnets` user under systemd hardening: `NoNewPrivileges`, `ProtectSystem=strict` with write access only to `/var/lib/pairnets`, `ProtectHome`, `PrivateTmp`, `PrivateDevices`, no capabilities, restricted address families and namespaces. |
| Data loss | The server never destroys data outside the history purge. Every overwritten or deleted file is kept in `history/` for 30 days and at least the last 5 versions. Clients refuse to run when the folder looks wrong (missing marker, empty folder) or a pass would delete many files. |
| Privacy | No telemetry or analytics. The apps talk to your nest, and to GitHub to look for new versions (at start-up and once a day; you can turn this off in **Settings → Updates and speed**) and to download them. The server talks to Cloudflare (the tunnel) and to GitHub only when it is asked to update itself. Only if you set them up: the server sends sign-in emails through your mail service and asks Google to confirm a Google sign-in. |
| Computer list | For the Devices page and the overview, the server keeps in `devices.json` the device name each computer sends, its Pairnets version and system (`X-Pairnets-Client`), when it was first and last seen, and its last change. Only requests that passed the key check count (the health check never does), and over-long version or system values are ignored. It is shown, never trusted: syncing does not depend on it. |

**What is not protected**

* **A compromised computer, or anyone who copies its key** (or the shared token while it is on), can
  read, change and delete every synced file. Deletions and changes stay recoverable from `history/`
  for the retention period, unless the attacker also has shell access to the server. Remove that
  computer on the nest to lock it out.
* **Whoever can sign in to your nest's website** (your password, a passkey, your email inbox or your
  Google account, if you turned those on) can let a new computer in. Protect them like the keys to
  your files.
* **Anyone with root on the server** can read everything. Files are not encrypted at rest on the
  server (no end-to-end encryption in v1). Use full-disk encryption on the server if that matters.
* **Cloudflare sees everything.** HTTPS ends at Cloudflare's edge, which passes requests on through
  the tunnel, so Cloudflare (and anyone who controls your Cloudflare account) can see keys, sign-ins
  and file contents in transit. Use end-to-end encryption, once it exists, for files that must stay
  private from it.
* **The server is on the internet.** Anyone can reach `https://<your hostname>`. What keeps them out
  is a computer key (guessing a 256-bit key is not feasible, and repeated failures are slowed down
  per address) or, on the website, your own sign-in. Whoever has the *tunnel* token can run a
  connector for your hostname and receive its traffic: keep it secret, and if it leaks, refresh it in
  the Cloudflare dashboard and run `install.sh --public-url https://<your hostname>` again (it asks
  for the new token).
* **Your Windows account.** DPAPI ties the key to your Windows login, so malware running as you can
  read it. The same goes for the Keychain and keyring on Mac and Linux.

## A lost computer, or a leaked secret

**A computer was lost or stolen.** Open your nest's website, go to **Devices**, and remove it from its
**⋯** menu. Its key stops working at once; nothing else changes. On the server you can do the same:

```bash
sudo -u pairnets /opt/pairnets/pairnets-server devices list
sudo -u pairnets /opt/pairnets/pairnets-server devices remove "LAPTOP"
```

**The old shared token.** Once every computer has its own key, switch the shared token off on the
website's **Security** page (it shows how many computers still use it). If an older app still needs
it and it may have leaked, make a new one. This writes it straight into the settings file, so it
never appears on a command line or on screen:

```bash
sudo bash -c '
  umask 077
  new=$(openssl rand -hex 32)
  grep -v "^SYNC_TOKEN=" /etc/pairnets/pairnets.env > /etc/pairnets/pairnets.env.new
  echo "SYNC_TOKEN=$new" >> /etc/pairnets/pairnets.env.new
  mv /etc/pairnets/pairnets.env.new /etc/pairnets/pairnets.env
'
sudo systemctl restart pairnets-server
```

Computers with their own key are not affected. A computer that still uses the shared token shows
"The server rejected the token" until you enter the new one (`sudo grep SYNC_TOKEN
/etc/pairnets/pairnets.env`) under **Settings → Advanced: server address and token**, or until you
sign it in.

**The website.** If you think someone else can sign in, change the password on the **Security**
page, remove passkeys you don't recognise, and sign out the sessions listed under **Where you're
signed in**. Lost every way in? `sudo -u pairnets /opt/pairnets/pairnets-server owner-link` prints a
new one-time link (and cancels any older one that was never used).

## Server self-update

Any computer that is let in can ask the server to update itself (`POST /api/update`). The request
can only start `/opt/pairnets/update.sh` (root-owned, installed by `install.sh`); the server writes
an empty file and the script never reads its content. The script installs only the newest release
from the fixed GitHub address, verifies `SHA256SUMS.txt` over HTTPS, refuses downgrades and runs at
most once per 10 minutes. Its log (`/var/lib/pairnets/update/update.log`, shown in the apps' Debug
mode) hides tunnel tokens, setup and sign-in codes and every other secret on a line. The server
service itself keeps all its systemd hardening and never gets root. Turn it off with
`sudo systemctl disable --now pairnets-update.path pairnets-update.timer`.

## Reporting

This is a personal project. Open an issue on the repository without including tokens, keys, IPs or
file contents.
