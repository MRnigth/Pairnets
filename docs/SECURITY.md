# Security

## Threat model

Pairnets is for one person with a few computers and one server (the *nest*), reached either through
Pairnets' service at `https://sync.pairnets.app` (a server linked to the owner's Pairnets account) or
through a Cloudflare Tunnel on the owner's own domain.

**What protects your files**

| Layer | Protection |
|-------|------------|
| Network | The server listens **only** on `127.0.0.1` (never `0.0.0.0`; `install.sh` refuses it). `cloudflared` (service `pairnets-tunnel`, an unprivileged dynamic user) connects out to Cloudflare; no port is open on the server or any router. Computers connect with HTTPS to your hostname. The tunnel token is kept in `/etc/pairnets/tunnel.env` (root, mode 600), never in the world-readable unit file or on a command line. With `Sync__TrustProxyHeaders` the server takes the client address from `CF-Connecting-IP` on loopback connections only, so the failure throttle and the logs see each client, not cloudflared. The apps refuse a plain `http://` address that turns out to be behind Cloudflare, before they send a key or token. |
| A server linked to Pairnets (relay mode) | Its Cloudflare Tunnel has **no public hostname**: only Pairnets' own router reaches it, through a private Workers VPC link, and only to `127.0.0.1:5075`. The service passes on requests under `/api/` and `/hub` only, never `/api/relay/`, so the server has no website there (`Sync:PublicUrl` stays unset, the server refuses to start with both, and `/api/pair/*` answers 409). Every sync call still needs a computer's own key. The service sets `X-Pairnets-Client-IP` to the caller's address as Cloudflare saw it, after removing any value the caller sent; the server believes it only in relay mode and only on loopback connections (then before `CF-Connecting-IP`), so the failure throttle still counts per caller. The server's id and key are in `/etc/pairnets/pairnets.env` (root, 600); both or neither must be set, and the key must be exactly 32 bytes of strict base64url, or the server does not start. |
| Pairnets' own calls to a linked server | `/api/relay/status`, and listing, adding and removing computers under `/api/relay/devices`. A computer's key or the shared token is no way in: every request there must carry `X-Pairnets-Nest`, `X-Pairnets-Ts`, `X-Pairnets-Nonce` and `X-Pairnets-Sig`, an HMAC-SHA256 with the server's 32-byte key over the nest id, the time, the nonce, the method, path and query, and a SHA-256 of the exact body ("ra1"). The server checks, in this order: the nest id, the signature (constant time), the time (within 120 s), and that the nonce was not seen in the last 10 minutes (kept in memory, at most 10,000, oldest dropped first; a nonce is only remembered once everything else passed). Any failure is the same 401 `bad_signature` with no detail; bodies over 4 KiB are refused first. The log says which check failed, never a header's value. Adding a computer is the same as **Allow** (a new key, only its hash kept); removing one stops its key at once, tells the apps and closes its connections. |
| Linking a server | `install.sh` (`pairnets-link.sh`) gets a device code and a short code from Pairnets. The device code stays in the installer's memory and only travels in request bodies on standard input, never on a command line. The owner approves the server on `https://sync.pairnets.app/add` while signed in to their account (or presses **Not mine**); the code lasts 15 minutes, and the installer polls no faster than the service allows. The tunnel token and the server's key arrive once, are checked for their exact shapes, and are written only to root-only files (`tunnel.env`, and `relay.env` until `install.sh` moves it into `pairnets.env`), never shown. Only an https service is used (plain http only to this machine, for tests). |
| A key per computer | Every computer has its own key (`pn_` plus 256 random bits). The nest shows it to the app once and keeps only its SHA-256 hash, in `/var/lib/pairnets/auth.db`. Removing a computer on the nest's **Devices** page stops its key at once and closes its live connection (removed with `pairnets-server devices remove` on the server: within 30 seconds). Every endpoint needs a key, except the health check, `/api/hello`, the two sign-in calls below and the nest's website, which has its own sign-in. After 5 bad keys from one address, answers are delayed (up to 10 s each). |
| Signing a computer in | The app asks the nest to join (`POST /api/pair/start`) and shows a code such as `KQ7M-4PXD`. You sign in on the nest's website, check that the code matches and press **Allow**. Only then can the app collect its key, with a secret only it knows (sent in the request body, never in a web address). A code lasts 10 minutes, an approved key must be collected within 5, at most 3 requests can wait per address (20 in all), and these calls are limited to 120 a minute per address. The `pairnets://` link that brings the app back to the front carries nothing: the key only travels through the app's own request. |
| The old shared token | Before per-computer keys, all computers used one shared token (`SYNC_TOKEN`). The nest still accepts it from older apps (compared in constant time over SHA-256 digests) until you switch it off on the website's **Security** page; switching it off also cuts computers that are still connected with it. A current app that only has the shared token stops syncing on a nest with its own name and asks you to sign in; it never trades the token for a key by itself. |
| The nest's website | It only answers on the nest's own HTTPS name. The first visit uses a one-time setup link from `pairnets-server owner-link` (it works once, for 24 hours, and making a new one cancels any older unused link; the code sits after `#`, so it never appears in a web address the server logs). You then add a passkey or a password (at least 10 characters, stored as a salted PBKDF2 hash). Optional: an email link (needs mail settings on the server; a sign-in link only ever goes to the address you confirmed, lasts 15 minutes, and the nest sends at most 5 emails an hour) and Google (needs your own Google sign-in client; only the Google account you connected gets in). Wrong passwords count across all addresses and are checked one at a time. You can't remove the last way to sign in. The session cookie is `__Host-`, Secure, HttpOnly and SameSite=Lax, and ends after 30 days without use. Requests that change something must come from the nest's own pages. Pages have a strict content security policy (no inline scripts, nothing from other sites, no framing). |
| Secrets at rest | Server: `/etc/pairnets/pairnets.env` (the shared token, and mail and Google secrets if you add them, and a linked server's key), owner root, mode 600, read by systemd before it drops privileges. `auth.db` holds only hashes of keys, sessions and links, the password hash and the passkeys' public keys. Computers: the key is encrypted with Windows DPAPI (current user) in `%AppData%\Pairnets\settings.json`, or kept in the macOS Keychain or the Linux keyring (without a keyring: a file only you can read). It is never written in plain text and never logged. **Settings → Start over → Reset this app** removes it. Requests are logged with method, route and file path only, never the rest of the query string or any header, and ASP.NET's own URL logging is off, so `access_token` cannot leak into journald. |
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
* **For a server linked to Pairnets: whoever can sign in to your Pairnets account**, and Pairnets'
  service itself, can let a new computer in (the service asks the server for a key when an app signs
  in with your account). This is the same trust as signing in to the nest's own website. HTTPS ends
  at Cloudflare, where the service runs, so the traffic could be seen there in transit, as with your
  own tunnel. Your files are never kept on the service.
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
**⋯** menu (a server linked to Pairnets: press **Remove** next to it at
<https://sync.pairnets.app/account>). Its key stops working at once; nothing else changes. On the
server you can do the same:

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
