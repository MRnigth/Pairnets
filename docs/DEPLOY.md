# Deploying Pairnets

## 1. Server (Ubuntu)

The server (your *nest*) keeps your files. It listens only on `127.0.0.1:5075`: `cloudflared` keeps
an outbound connection to Cloudflare and brings requests to it, so no port is opened anywhere. Your
computers find it in one of two ways:

* **Linked to your Pairnets account** (the usual way, [below](#linked-to-your-pairnets-account)):
  they reach it through `https://sync.pairnets.app` and sign in with the same account. No domain,
  and nothing to set up at Cloudflare.
* **Your own domain** ([Advanced: your own domain](#advanced-your-own-domain)): it has its own name,
  such as `https://sync.example.com`, through your own Cloudflare Tunnel, and its own website.

Prerequisites for both: Ubuntu 22.04 or newer (x64) and `curl` and `openssl`
(`sudo apt install curl openssl`). No .NET runtime is needed: the server is self-contained.

The newest release is always the rolling **Latest build** on GitHub (rebuilt from every change to the
`main` branch), so these links never change:

```bash
# Download the newest release and check its checksum
curl -LO https://github.com/MRnigth/Pairnets/releases/latest/download/pairnets-server-linux-x64.tar.gz
curl -LO https://github.com/MRnigth/Pairnets/releases/latest/download/SHA256SUMS.txt
sha256sum --check --ignore-missing SHA256SUMS.txt

tar xzf pairnets-server-linux-x64.tar.gz
cd pairnets-server-linux-x64
sudo ./install.sh
```

(`curl -fsSL https://raw.githubusercontent.com/MRnigth/Pairnets/main/deploy/get.sh | sudo bash` does
the same in one line; options for `install.sh` go after `-s --`.)

### Linked to your Pairnets account

On a machine without Pairnets, `install.sh` without options links the server to your Pairnets
account (`pairnets-link.sh`; the design is in [cloud/RELAY.md](../cloud/RELAY.md)):

1. It asks Pairnets for a code and prints a link such as `https://sync.pairnets.app/add?code=ABCD-EFGH`.
   Open it on any device, sign in to your Pairnets account, check that the page shows the same code
   and press **Add this nest**. Nothing is typed on the server. The link works for 15 minutes;
   **Not mine**, or a link that runs out, changes nothing (run the command again).
2. Pairnets makes a Cloudflare Tunnel for this server alone. It has no public hostname: only
   Pairnets' own service reaches it, through a private link. The tunnel's token, the server's id and
   its key (with which Pairnets signs its own calls to the server) come back once.
   `pairnets-link.sh` keeps them in `/etc/pairnets/tunnel.env` and `/etc/pairnets/relay.env` (root,
   mode 600) and never shows them or puts them on a command line; `install.sh` moves the id and key
   into `pairnets.env`.
3. The rest is as with your own domain ([below](#advanced-your-own-domain)): `cloudflared`, the
   `pairnets` user and folders, `/etc/pairnets/pairnets.env` (here with `Sync__RelayNestId`,
   `Sync__RelayKey`, `Sync__RelayServiceUrl` and `Sync__TrustProxyHeaders=true`, listening on
   `127.0.0.1:5075`, the port Pairnets always uses), the self-updater and the services.
4. It waits for `/api/health` on this machine, then up to 3 minutes until
   `https://sync.pairnets.app/n/<server id>/api/health` answers through Pairnets (a new server is
   reachable after about 30 seconds).
5. It prints the next step: **on each computer, install Pairnets and choose Continue with email
   (or Google), with the same account.**

Upgrades (`sudo ./install.sh` again, or the self-updater) keep the link and never start a new one.
`sudo ./install.sh --link` links an installed server again: after it was removed from your account,
or to move a server from its own domain to Pairnets' address (its own name, and so its website, is
then no longer used, and each computer signs in again with the account). A first install that
stopped after the link was made picks the link up again for the next 50 minutes; after that Pairnets
has forgotten a server that never connected, so it asks for a new link.

A linked server has no website of its own: your computers and the server are listed, and removed, at
<https://sync.pairnets.app/account>. Pairnets passes on only requests under `/api/` and `/hub`. Its
own calls (`/api/relay/…`: how the server is doing, and its computers) are signed with the server's
key; the server refuses every other call there.

### Advanced: your own domain

The server gets its own public hostname (`https://sync.example.com`) from your own Cloudflare Tunnel.
That hostname is also your nest's own name: the apps find the nest by it, and the nest's website
lives there. Step by step with the dashboard:
[HOWTO section 3](HOWTO.md#3-advanced-your-own-domain).

Prerequisites: a domain on Cloudflare, and a tunnel created in the dashboard (Zero Trust → Networks →
Tunnels → *Cloudflared*) with its token copied and a public hostname → `HTTP` `localhost:5075`. Turn
off Bot Fight Mode for the domain. Then, from the release folder above:

```bash
sudo ./install.sh --public-url https://sync.example.com
```

`install.sh` asks you to paste the tunnel token; nothing is shown while you paste, and it never ends
up in your shell history. To run it without the prompt (in a script), read the token into the
environment first, still without showing it or typing it on a command line:

```bash
read -rsp 'Tunnel token: ' TUNNEL_TOKEN; echo; export TUNNEL_TOKEN
sudo --preserve-env=TUNNEL_TOKEN ./install.sh --public-url https://sync.example.com
unset TUNNEL_TOKEN
```

`install.sh` does the following:

1. Asks for the tunnel token and installs `cloudflared` from `pkg.cloudflare.com` (signed apt
   repository) if `/usr/bin/cloudflared` is missing.
2. Creates the system user `pairnets`, `/opt/pairnets` (program), `/var/lib/pairnets` (data, mode 700)
   and `/etc/pairnets` (config, root only).
3. Writes `/etc/pairnets/pairnets.env` with mode 600: listen on `127.0.0.1`, `Sync__TrustProxyHeaders=true`,
   and the nest's name from `--public-url` (`PAIRNETS_PUBLIC_URL`; it replaces any older
   `PUBLIC_URL` or `Sync__PublicUrl` line, and the settings of a link to Pairnets). The first time it
   also makes the old shared token (`SYNC_TOKEN`, from `openssl rand -hex 32`), which only older
   Pairnets apps use. Upgrades keep the existing token and settings. It never listens on `0.0.0.0`.
4. Writes the tunnel token to `/etc/pairnets/tunnel.env` (root, mode 600) and installs
   `pairnets-tunnel.service`, which runs `cloudflared tunnel run` as a dynamic, unprivileged user.
   The token is not put in the unit file or on a command line, where other local users could read
   it (`cloudflared service install <token>` would do both).
5. Installs the self-updater: `/opt/pairnets/update.sh` and the units `pairnets-update.path`,
   `pairnets-update.service` and `pairnets-update.timer` (see [4b](#4b-updating-the-server)).
6. Installs and starts `pairnets-server.service` and waits for `/api/health` locally. If the server
   does not answer, `install.sh` stops with an error and tells you where to look
   (`sudo journalctl -u pairnets-server -n 50`). Then it checks `https://sync.example.com` through the
   tunnel.
7. When run in a terminal, prints a **one-time link to set up your nest's website** and the next
   steps: open the link, add a passkey or a password, then on each computer install the app, type
   the nest's name and sign in with the browser.

To upgrade, run `sudo ./install.sh` again from a new release (or let the self-updater do it): the
settings, the nest's name, the sign-ins and the tunnel stay as they are. Running it with
`--public-url` again asks for a new tunnel token and replaces it. On a server linked to your
Pairnets account, `--public-url` moves it to your own domain (remove it from your account page
afterwards).

Useful commands (both ways):

```bash
sudo systemctl status pairnets-server pairnets-tunnel
sudo journalctl -u pairnets-server -f          # logs (never contain keys or tokens)
sudo journalctl -u pairnets-tunnel -f
curl https://sync.example.com/api/health       # your own domain
```

Big uploads are sent in pieces of 4 to 50 MB, sized to the connection (Cloudflare refuses request
bodies over 100 MB). See [SECURITY.md](SECURITY.md) for what Cloudflare (and Pairnets' service, for a
linked server) can see.

#### Your nest's own name (HTTPS)

The name you give with `--public-url` (for example `https://sync.example.com`) is how everything
finds your nest:

* **The apps** ask for it when you sign in, and talk to the nest there.
* **The nest's website** (approving computers, the Devices and Security pages) only answers on this
  name, over HTTPS. Behind the tunnel, Cloudflare handles HTTPS and passes requests to
  `127.0.0.1:5075`; the server believes the "this came in over HTTPS" note only from the tunnel on
  the same machine, which `Sync__TrustProxyHeaders=true` (set by `install.sh`) allows.
* **Passkeys** are tied to it (`Sync__PasskeyRpId`, below, can tie them to a parent domain instead).

**Get into the website.** `install.sh` prints a setup link at the end. To make a new one (it works
once, for 24 hours, and cancels any older unused link; the service may keep running):

```bash
sudo -u pairnets /opt/pairnets/pairnets-server owner-link
```

Open it, then add a passkey (Windows Hello, Face ID, Touch ID, your phone or a security key) or a
password of at least 10 characters. Use the same command if you ever lose every way to sign in.
With `--if-new` it prints a link only when no way to sign in is set up yet.

**Change the name.** Add the new public hostname to the same tunnel in the Cloudflare dashboard, then
run `sudo ./install.sh --public-url https://new.example.com` (it asks for the tunnel token again).
Keep the old hostname in the tunnel until each computer has connected once: the apps move to the
new name by themselves after checking it is the same nest. Passkeys made for the old name stop
working (unless `Sync__PasskeyRpId` ties them to a parent domain both names share); sign in with
your password or a new setup link and add a passkey again.

### Configuration

`/etc/pairnets/pairnets.env` (see `deploy/pairnets.env.example`). Edit it with
`sudo nano /etc/pairnets/pairnets.env` and apply with `sudo systemctl restart pairnets-server`.
Upgrades keep every line.

| Variable | Default | Meaning |
|----------|---------|---------|
| `SYNC_TOKEN` | – (required, ≥ 16 chars; `install.sh` makes one) | the old shared token. Only older apps use it; switch it off on the website's Security page. The server still needs a value to start. |
| `ASPNETCORE_URLS` | `http://127.0.0.1:5075` | listen address: keep it on `127.0.0.1`, the tunnel brings the computers in |
| `PAIRNETS_PUBLIC_URL` (or `PUBLIC_URL`, or `Sync__PublicUrl`) | – (set by `install.sh --public-url`) | the nest's own name, `https://` without a path. It turns on the website and signing in. If more than one is set, `Sync__PublicUrl` wins, then `PUBLIC_URL`. It must stay unset on a server linked to Pairnets. |
| `Sync__RelayNestId`, `Sync__RelayKey` | – (set by `install.sh` when it links the server to your Pairnets account) | the server's id at Pairnets (`nst_` and 26 characters) and its key (base64url of 32 bytes; root only, never logged). Both or neither: with one missing or malformed the server does not start. Together they turn on relay mode: no website of its own, `/api/hello` says `relay`, and Pairnets' signed calls under `/api/relay/` are accepted. |
| `Sync__RelayServiceUrl` | `https://sync.pairnets.app` | the Pairnets service a linked server is reached through |
| `Sync__PasskeyRpId` | the nest's host name | the domain passkeys are tied to. Set a parent domain (`example.com`) only if other sites on it should share the passkeys; it must be the nest's host or a parent of it. |
| `Sync__DataDir` | `/var/lib/pairnets` | data directory. Keep it there: the hardened service and the updater expect that path (to use another disk, see [Moving the nest's data to another disk](#moving-the-nests-data-to-another-disk)). |
| `Sync__UpdateDir` | `/var/lib/pairnets/update` (set by install.sh) | where update requests go; must be the folder the root updater watches |
| `Sync__HistoryRetentionDays` | 30 | delete history versions older than this... |
| `Sync__HistoryMinVersions` | 5 | ...but always keep this many per file |
| `Sync__TrustProxyHeaders` | `false` (`true` set by install.sh) | the client address comes from `CF-Connecting-IP` (or the last `X-Forwarded-For` entry), and HTTPS from `X-Forwarded-Proto`, believed only on loopback connections. On a linked server this is always on, and Pairnets' `X-Pairnets-Client-IP` comes first |
| `Sync__UploadStallTimeout` | `00:01:00` | drop an upload that sends nothing for this long |
| `Sync__SmtpHost`, `Sync__SmtpPort` (587), `Sync__SmtpUser`, `Sync__SmtpPassword`, `Sync__SmtpFrom`, `Sync__SmtpUseTls` (`true`) | – | email sign-in links, see [below](#email-sign-in-links-optional) |
| `Sync__GoogleClientId`, `Sync__GoogleClientSecret` | – | sign in with Google, see [below](#sign-in-with-google-optional) |
| `Sync__HttpsUrl`, `Sync__TlsDir` | – (`TlsDir`: `DataDir/tls`) | advanced, not used with the tunnel: let the server speak HTTPS itself, with `fullchain.pem` and `privkey.pem` in `TlsDir` |

### Email sign-in links (optional)

Only with your own domain (a server linked to Pairnets has no website of its own; there, Continue
with email and Google are your Pairnets account's). With mail settings, the nest can email you a sign-in link, and the apps offer **Continue with email**.
Any SMTP service works, for example Resend (`smtp.resend.com`, user `resend`, an API key as the
password). Add to `/etc/pairnets/pairnets.env`:

```bash
Sync__SmtpHost=smtp.example.com
Sync__SmtpPort=587
Sync__SmtpUser=<user name>
Sync__SmtpPassword=<password or API key>
Sync__SmtpFrom="Pairnets nest <nest@example.com>"
```

The sender's domain must be allowed to send through that service (SPF and DKIM), or the mail lands in
spam. Restart the server, open your nest's website, go to **Security → Email sign-in link**, type
your address and press **Send a confirmation link**. Open the link in that email: from then on the
nest sends sign-in links to that address only (each works once, for 15 minutes).

### Sign in with Google (optional)

Only with your own domain, like email links above. This uses your own Google sign-in client, so
nothing goes through anyone else's account.

1. In the Google Cloud console (APIs & Services → Credentials), create an **OAuth client ID** of
   type **Web application**. Add `https://sync.example.com/auth/google/callback` as an authorised
   redirect URI. If Google asks, set up the consent screen first; your own account is the only one
   that has to sign in.
2. Add to `/etc/pairnets/pairnets.env`, then restart the server:

   ```bash
   Sync__GoogleClientId=<client id>
   Sync__GoogleClientSecret=<client secret>
   ```

3. On your nest's website go to **Security → Google → Connect Google** and choose your account.
   Only that Google account can sign in afterwards, and the apps offer **Continue with Google**.

## 2. Computers (Windows, Mac, Linux)

1. Install Pairnets: `PairnetsSetup.exe` (or `pairnets-client-win-x64.zip`, just `Pairnets.exe`) on
   Windows, the `.dmg` on a Mac, `pairnets-desktop-linux-x64.tar.gz` on Linux, all from
   <https://github.com/MRnigth/Pairnets/releases/latest>. Details per system:
   [HOWTO section 5](HOWTO.md#5-set-up-the-first-computer-for-example-the-desktop).
2. Start it. The first-run window is a sign-in window. **Linked to your Pairnets account:** choose
   **Continue with email** (or Google) and sign in with the same account; Pairnets asks your server
   for this computer's own key and hands it over once. **Your own domain:** type your nest's name
   (`sync.example.com`) and sign in; your browser opens the nest's website, check that the code
   matches the one in the app and press **Allow**. Either way the computer gets its own key.
3. Choose the folder to sync, for example `D:\Work`, and press **Start syncing**. If the server
   already has files and the folder is not empty, Pairnets merges them (nothing is deleted).

Do the same on each computer. Choosing an empty folder downloads everything; choosing a folder that
already holds a copy transfers only differences.

## 3. Backups

The data directory is the whole server state: `files/`, `history/`, `manifest.db` (the list of
files), `auth.db` (the computers' keys and your website sign-in) and `devices.json`. A consistent
backup with the service running:

```bash
sudo apt install sqlite3 rsync     # once
BACKUP=/mnt/backup/pairnets
sudo mkdir -p "$BACKUP"
# 1. The two databases, copied consistently by SQLite (safe while the service writes)
sudo sqlite3 /var/lib/pairnets/manifest.db ".backup '$BACKUP/manifest.db'"
sudo sqlite3 /var/lib/pairnets/auth.db ".backup '$BACKUP/auth.db'"
# 2. Files, history and the rest (only adds/updates; history makes old versions immutable)
sudo rsync -a --delete --exclude 'manifest.db*' --exclude 'auth.db*' --exclude 'tmp/' --exclude '.lock' \
     /var/lib/pairnets/ "$BACKUP/data/"
```

For a fully consistent snapshot, stop the service for the minute it takes
(`sudo systemctl stop pairnets-server`, back up, `sudo systemctl start pairnets-server`). Computers
simply retry while it is down. Keep backups private: they hold your files.

### Restore

```bash
sudo systemctl stop pairnets-server
sudo rsync -a --delete /mnt/backup/pairnets/data/ /var/lib/pairnets/
sudo cp /mnt/backup/pairnets/manifest.db /mnt/backup/pairnets/auth.db /var/lib/pairnets/
sudo rm -f /var/lib/pairnets/manifest.db-wal /var/lib/pairnets/manifest.db-shm \
           /var/lib/pairnets/auth.db-wal /var/lib/pairnets/auth.db-shm
sudo chown -R pairnets:pairnets /var/lib/pairnets
# Reconcile files/ with the manifest (adds files the manifest does not know, reports missing ones)
sudo -u pairnets /opt/pairnets/pairnets-server rescan --data-dir /var/lib/pairnets
sudo systemctl start pairnets-server
```

A restored server is older than what the computers have seen, so each one stops with **"The server
went back in time"**. Choose **Re-link to this server** in the tray menu. Pairnets then merges the
computer's folder with the restored server: nothing is deleted or overwritten, files that differ
become conflict copies, and files that only exist on the computer are uploaded again. A computer
added after the backup was made is not in the restored `auth.db`: sign it in again.

### Moving the nest's data to another disk

The hardened service may only write to `/var/lib/pairnets`, and the updater expects that exact path.
So keep the path and mount the new place onto it. The new disk must use a Linux file system (ext4,
for example) and already be mounted at boot, here at `/mnt/data`:

```bash
sudo systemctl stop pairnets-server
# 1. Copy everything, keeping owners and modes
sudo mkdir -p /mnt/data/pairnets
sudo rsync -aH /var/lib/pairnets/ /mnt/data/pairnets/
# 2. Put the old folder aside and leave an empty one to mount onto
sudo mv /var/lib/pairnets /var/lib/pairnets.old
sudo install -d -m 0700 -o pairnets -g pairnets /var/lib/pairnets
# 3. Mount the new place onto /var/lib/pairnets at every boot
echo '/mnt/data/pairnets  /var/lib/pairnets  none  bind,nofail,x-systemd.requires-mounts-for=/mnt/data  0  0' | sudo tee -a /etc/fstab
sudo systemctl daemon-reload
sudo mount /var/lib/pairnets
# 4. Never start the server without it (it would see an empty folder)
sudo mkdir -p /etc/systemd/system/pairnets-server.service.d
printf '[Unit]\nRequiresMountsFor=/var/lib/pairnets\n' | sudo tee /etc/systemd/system/pairnets-server.service.d/data-disk.conf
sudo systemctl daemon-reload
sudo systemctl start pairnets-server
```

Check that the apps sync and that `curl https://sync.example.com/api/health` says `ok`, then delete
the old copy with `sudo rm -rf /var/lib/pairnets.old`. Updates keep the mount and the drop-in file.

## 4. Maintenance commands

Stop the service first for `rescan` and `history`: the data directory is locked while it runs.

```bash
sudo systemctl stop pairnets-server
sudo -u pairnets /opt/pairnets/pairnets-server rescan --dry-run --data-dir /var/lib/pairnets
sudo -u pairnets /opt/pairnets/pairnets-server history list "Docs/report.docx" --data-dir /var/lib/pairnets
sudo -u pairnets /opt/pairnets/pairnets-server history restore "Docs/report.docx" 20261002T101500123Z-1a2b3c4d --data-dir /var/lib/pairnets
sudo -u pairnets /opt/pairnets/pairnets-server history purge --dry-run --data-dir /var/lib/pairnets
sudo systemctl start pairnets-server
```

Restoring a version makes it the current version; every computer downloads it on its next sync. The
previous current version goes to history too, so a restore can itself be undone.

These work while the service runs:

```bash
sudo -u pairnets /opt/pairnets/pairnets-server devices list               # computers with their own key
sudo -u pairnets /opt/pairnets/pairnets-server devices remove "LAPTOP"    # its key stops within 30 s
sudo -u pairnets /opt/pairnets/pairnets-server owner-link                 # one-time link to the website
```

## 4b. Updating the server

**From the app (recommended).** When the server runs an older version than the app, Pairnets shows
"Your server should be updated" with an **Update server** button. What happens:

1. The app sends `POST /api/update` (with its key).
2. The server (which has no root rights) only creates the empty file
   `/var/lib/pairnets/update/request`.
3. The root-owned unit `pairnets-update.path` notices it and starts `pairnets-update.service`, which
   runs `/opt/pairnets/update.sh` once.
4. `update.sh` downloads the newest release (the rolling **Latest build**) from the fixed address
   `github.com/MRnigth/Pairnets/releases/latest/download`, checks it against `SHA256SUMS.txt`,
   refuses the same or an older version, allows one attempt per 10 minutes, and runs that release's
   `install.sh`. Your settings, the nest's name, sign-ins and computers are kept.
5. Progress is written to `/var/lib/pairnets/update/status.json`; the app shows it and reconnects.

With **Settings → Updates and speed → Update the server automatically** on, the app does this by
itself whenever it finds the server out of date.

This needs the GitHub repository to be public (the server downloads without signing in), which it
is, and a server installed from a release that already contains `update.sh`. A server installed
before that must be updated by hand once:

```bash
curl -fsSL https://raw.githubusercontent.com/MRnigth/Pairnets/main/deploy/get.sh | sudo bash
```

Upgrades keep the address the server listens on and every line of `/etc/pairnets/pairnets.env`; pass
`--bind`/`--port` only to change the address. Check the updater with
`systemctl status pairnets-update.path` and `sudo tail -n 50 /var/lib/pairnets/update/update.log`. To turn
self-updates off: `sudo systemctl disable --now pairnets-update.path pairnets-update.timer`.

**When an update from the app does not work.** Every run of `update.sh` is logged to
`/var/lib/pairnets/update/update.log` (the previous log is kept as `update.log.1`; tunnel tokens,
link codes and other secrets are masked), and anything that fails unexpectedly is reported to the
app instead of leaving it waiting. In the app, turn on **Settings → Debug mode** and click **Update
server** (or **Check for update**). The update window then shows every step the app takes and, at
the end, what the server reports: whether `update.sh` is installed, whether `pairnets-update.path`
is enabled, whether a request is waiting, the last `status.json` and the last lines of
`update.log`. Secrets are never shown. **Copy details** puts it all on the clipboard; the same
report is `GET /api/update/diagnostics` (it needs a computer's key). A request that the updater
does not pick up within 45 seconds is reported as such, and the report shows the state of
`pairnets-update.path` and `pairnets-update.service`. If the watcher is not active, run
`sudo systemctl reset-failed pairnets-update.path pairnets-update.service && sudo systemctl restart pairnets-update.path`.
As a fallback, `pairnets-update.timer` checks for a waiting request every 2 minutes, and installing
(or the one-line command) always restarts the watcher. To check it quickly:
`systemctl is-active pairnets-update.path pairnets-update.timer` should print `active` twice. If the
self-update itself is broken, update by hand once with the command above.

## 5. Uninstall

```bash
sudo systemctl disable --now pairnets-server
sudo systemctl disable --now pairnets-update.path pairnets-update.timer 2>/dev/null
sudo systemctl disable --now pairnets-tunnel 2>/dev/null   # only with a Cloudflare Tunnel
sudo rm -f /etc/systemd/system/pairnets-server.service /etc/systemd/system/pairnets-tunnel.service \
           /etc/systemd/system/pairnets-update.service /etc/systemd/system/pairnets-update.path \
           /etc/systemd/system/pairnets-update.timer
sudo rm -rf /etc/systemd/system/pairnets-server.service.d   # only there if you moved the data
sudo systemctl daemon-reload
sudo rm -rf /opt/pairnets /etc/pairnets
# Your data stays in /var/lib/pairnets until you delete it yourself.
# cloudflared stays installed (sudo apt remove cloudflared); delete the tunnel in the Cloudflare dashboard.
```

A server linked to your Pairnets account: also press **Remove this nest from my account** at
<https://sync.pairnets.app/account>, which deletes its tunnel. Its files stay on the server.
