# Security

## Threat model

Tether is for one person with two PCs and one server on a private Tailscale network (tailnet).

**What protects your files**

| Layer | Protection |
|-------|------------|
| Network | The server listens **only** on its Tailscale IP (never `0.0.0.0` by default; `install.sh` refuses it). Tailscale encrypts all traffic with WireGuard and authenticates devices. An ACL (see [DEPLOY.md](DEPLOY.md)) limits port 5075 to your two PCs. |
| Token | Every endpoint except `/api/health` requires a 256-bit shared token (`openssl rand -hex 32`). It is compared in constant time over SHA-256 digests. After 5 bad attempts from one address, responses are delayed (up to 10 s each). |
| Token at rest | Server: `/etc/tether/tether.env`, owner root, mode 600, read by systemd before it drops privileges. Client: encrypted with Windows DPAPI (current user) in `%AppData%\Tether\settings.json`. It is never written in plain text and never logged. Requests are logged without query strings or headers, and ASP.NET's own URL logging is off, so `access_token` cannot leak into journald. |
| Names | The server rejects path traversal (`..`, rooted paths, drive letters, backslashes), Windows-hostile names (`< > : " \| ? *`, control characters, trailing dots/spaces, `CON`, `NUL`, `COM1`… with or without extension), paths over 1024 characters and names that differ only in letter case from existing ones. It canonicalizes every path, checks it stays inside `files/`, and refuses any path through a symlink. Clients never follow symlinks, junctions or reparse points. |
| Process | The service runs as the unprivileged `tether` user under systemd hardening: `NoNewPrivileges`, `ProtectSystem=strict` with write access only to `/var/lib/tether`, `ProtectHome`, `PrivateTmp`, `PrivateDevices`, no capabilities, restricted address families and namespaces. |
| Data loss | The server never destroys data outside the history purge. Every overwritten or deleted file is kept in `history/` for 30 days and at least the last 5 versions. Clients refuse to run when the folder looks wrong (missing marker, empty folder) or a pass would delete many files. |
| Privacy | No telemetry, analytics, update checks or third-party services. The client only talks to your server. |

**What is not protected**

* **A compromised device on your tailnet that has the token** can read, change and delete every
  synced file. Deletions and changes stay recoverable from `history/` for the retention period,
  unless the attacker also has shell access to the server.
* **Anyone with root on the server** can read everything. Files are not encrypted at rest on the
  server (no end-to-end encryption in v1). Use full-disk encryption on the server if that matters.
* **Plain HTTP** is used inside the tailnet. This is acceptable because Tailscale already encrypts
  and authenticates the link. The client also accepts `https://` URLs, for example behind
  `tailscale serve`.
* Your Windows account. DPAPI ties the token to your Windows login, so malware running as you can
  read it.

## Rotating the token

Rotate it if a PC is lost or you suspect it leaked.

```bash
# On the server
NEW=$(openssl rand -hex 32)
sudo sed -i "s/^SYNC_TOKEN=.*/SYNC_TOKEN=$NEW/" /etc/tether/tether.env
sudo systemctl restart tether-server
echo "$NEW"    # enter it in Tether → Settings on both PCs, then clear your terminal
```

Both PCs show "The server rejected the token" until you enter the new one. Nothing is lost: local
changes made meanwhile sync after you update the token.

If a PC is lost, also remove it from your tailnet in the Tailscale admin console.

## Reporting

This is a personal project. Open an issue on the repository without including tokens, IPs or file
contents.
