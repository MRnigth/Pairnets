# How Pairnets was built, and where things live

Pairnets was built in several focused pushes. This is the durable, code-oriented summary; it is not
a chat log. Paths are current (`Pairnets.*`).

## Core sync (the foundation)
Everything data-safety-critical is in `src/Pairnets.Core` and runs on every OS:
- **Path rules & ignore list** — Windows-safe names, glob ignores.
- **Three-hash decision** — `SyncDecision.Decide(L, S, B)` is a pure function over the local hash,
  the server hash, and the last agreed base; exhaustively table-tested
  (`tests/Pairnets.Tests/Unit/DecideTableTests.cs`). It is why files are never silently overwritten.
- **Client state** — SQLite (WAL, `synchronous=FULL`) with a racy-safe hash cache and delete
  approvals; **streaming SHA-256**; the **sync engine** (marker guard, mass-delete guard, overwrite
  guards) and the **runner** (debounced watcher, SignalR push, backoff, startup catch-up).
- `StatusSnapshot` is the single immutable view both UIs render; `ActivityFeed`, `Format`,
  `LiveLists`, `DeviceMap`, `HistoryModels` back the UI pages.

## Server (`src/Pairnets.Server`)
ASP.NET Core minimal API + SignalR + SQLite manifest, journaled file store with 30-day history,
token auth with a per-IP throttle, rescan/history maintenance commands. Reached on loopback behind a
Cloudflare Tunnel; `ProxyClientAddressMiddleware` recovers the real client IP from `CF-Connecting-IP`.

## Transport: Cloudflare Tunnel + chunked uploads
Cloudflare's free plan caps request bodies at 100 MB, so uploads are **chunked and resumable**
(`/api/upload*`, `Storage/UploadSessions.cs`, client `Api/PairnetsApiClient.UploadInPiecesAsync`),
with an **adaptive piece size** (`Api/PieceSizer.cs`, ~30 s/piece, 4–50 MiB). Tailscale was removed;
`install.sh --public-url https://…` sets up `cloudflared` and `pairnets-tunnel.service`.

## Desktop apps
- **Windows:** `src/Pairnets.Client` (WPF).
- **Mac/Linux:** `src/Pairnets.Desktop` (Avalonia 11).
- Sidebar window: Overview (status + a "this computer ⇄ server ⇄ other computer" picture with moving
  dots), Activity (by day), History (restore deleted files/old versions), Devices, Needs attention,
  Settings. A tray/menu-bar quick panel mirrors the status.
- Server version pill + in-app "Update the server" flow; in-app update checks (`UpdateChecker`,
  `UpdateService`); speed limits and ETA; the round "ring" logo (every WPF window sets its own icon
  so the taskbar refreshes after an update). Tray icons are coloured status glyphs, not the logo.

## Server self-update (trust boundary)
The server never runs as root. `POST /api/update` only writes a request file; a root systemd path
unit (`pairnets-update.path`) runs `update.sh`, which verifies SHA256SUMS, refuses downgrades, rate-
limits, and re-runs the installer keeping token + data. An old server returns 404 and the app shows a
one-time setup command.

## Migration (Tether → Pairnets)
Old names live only in `src/Pairnets.Core/Legacy/TetherNames.cs` + `TetherMigration.cs`. Both apps
call `TakeOverTether()` before the single-instance lock; the server `install.sh` step 0 migrates
`/etc/tether` + `/var/lib/tether` and renames the service user. The server sends both `X-Pairnets-*`
and `X-Tether-*` headers during the transition. `git grep -i tether` should hit only these places +
the "Moving from Tether" docs.

<a id="sign-in"></a>
## Sign-in (in flight — `feature/sign-in`)
Goal: the website and the apps share a login so adding a computer is three steps. Design:
- **Per-computer keys** — each computer gets its own `pn_` key (only its SHA-256 is stored, in a
  separate `auth.db`); the old shared token still works and silently upgrades.
- **Device pairing** — a new computer shows an `XXXX-XXXX` code; you approve it on the nest website;
  the computer then mints its own key.
- **The nest website** — setup, sign-in, approve, devices, security pages; sessions in a `__Host-`
  cookie; strict CSP.
- **Four sign-in methods** — passkey (own WebAuthn verifier on `System.Formats.Cbor`), password,
  email link (SMTP), Google (OIDC + PKCE). The last usable method can't be removed.
- **Public site** — `site/` (static, for Cloudflare Pages): home, add-a-computer, help.
- **Connection guard** — a removed computer's live push connection is closed within 10 s.

Porting note: this was built before the rename/tunnel, so it is reapplied onto `main` with the
behind-the-tunnel origin fixes (origin derived from `Sync:PublicUrl`, not `Request.IsHttps`) and the
old Tailscale/certbot HTTPS layer dropped. Starting points on `main`:
`Web/TokenAuthMiddleware.cs`, `SyncOptions.Token`, the client's per-request token header, and the
SignalR `AccessTokenProvider` in `SyncRunner.ConnectHubAsync`.
