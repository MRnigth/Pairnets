# Architecture

Pairnets keeps one folder identical on your computers (Windows, Mac and Linux, built for using one
at a time) through a small server, the *nest*, reached through a Cloudflare Tunnel: the server
listens on 127.0.0.1 and `cloudflared` brings requests in over an outbound connection. This document
explains how, and why it cannot silently overwrite your files.

```
 Desktop                                Ubuntu server (behind the tunnel)                   Laptop
 ┌───────────────────┐  HTTPS + key     ┌────────────────────────────────┐  HTTPS + key     ┌───────────────────┐
 │ Pairnets app      │ ───────────────▶ │ pairnets-server (ASP.NET Core) │ ◀─────────────── │ Pairnets app      │
 │  window + tray    │                  │  /api/*  + SignalR /hub        │                  │  window + tray    │
 │ Pairnets.Core     │ ◀── "Changed" ── │  nest website + /web/api/*     │ ── "Changed" ──▶ │ Pairnets.Core     │
 │  engine, runner,  │    push          │  manifest.db, auth.db (SQLite) │    push          │  engine, runner,  │
 │  state.db         │                  │  files/   current versions     │                  │  state.db         │
 └───────────────────┘                  │  history/ old + deleted        │                  └───────────────────┘
                                        └────────────────────────────────┘
```

* **Pairnets.Core** (net8.0, no UI): path rules, ignore list, hashing, the decision function, the
  client state database, the scanner, the HTTP client, the sync engine and the runner, plus what
  both apps show and do (`ClientSession`, `StatusSnapshot`, `ActivityFeed`), signing in (`Nest`,
  `PairingFlow`), the update check and Reset. Everything here runs and is tested on Linux.
* **Pairnets.Server**: ASP.NET Core minimal API + SignalR, the SQLite manifest (`manifest.db`) and
  sign-in database (`auth.db`), files on disk, the nest's website (plain HTML, CSS and JavaScript
  built into the program), systemd.
* **Pairnets.Client**: the Windows app (WPF, with a WinForms tray icon).
* **Pairnets.Desktop**: the Mac and Linux app (Avalonia). It mirrors the Windows app page for page;
  both are thin and draw from the same `StatusSnapshot`.

## The three-hash algorithm

For every path the engine computes three SHA-256 hashes (null = absent or deleted):

| Name | Meaning |
|------|---------|
| **L** | content of the local file now |
| **S** | content on the server now (null if absent or tombstoned) |
| **B** | the *base*: the content both sides agreed on at the last successful sync of this path |

`SyncDecision.Decide(L, S, B)` is a pure function. Every reachable case
(15 equality patterns, covering all 64 combinations of null/a/b/c) is listed here and tested
exhaustively in `tests/Pairnets.Tests/Unit/DecideTableTests.cs`:

| L | S | B | Action | Why |
|---|---|---|--------|-----|
| – | – | – | none | nothing anywhere |
| – | – | a | clear base | deleted on both sides |
| – | a | – | download | new on the server |
| – | a | a | delete on server | deleted here, untouched on server |
| – | a | b | download | deleted here but **edited** on server: the edit wins |
| a | – | – | upload (base "none") | new here |
| a | – | a | delete locally | deleted on server, untouched here (Recycle Bin) |
| a | – | b | upload (base "none") | deleted on server but **edited** here: the edit wins |
| a | a | – | record base | identical on both sides (first sync transfers nothing) |
| a | a | a | none | in sync |
| a | a | b | record base | both made the same change |
| a | b | – | conflict | created on both sides with different content |
| a | b | a | download | only the server changed |
| a | b | b | upload (base = b) | only this PC changed |
| a | b | c | conflict | both changed differently |

**Conflict:** the local file is renamed to `name (conflict DEVICE yyyy-MM-dd HHmmss).ext` (plus a
counter if needed), uploaded as a new file so the other PC gets it too, and the server version is
downloaded under the original name. A notification names the conflict copy.

### Why the base prevents overwrites

Every upload and delete carries the hash the client believes the server has (`base`, or
`none` for "must not exist"). The server compares it with its current state under a global lock and
answers **409 conflict** if it differs. So an upload can only replace exactly the version the
client has seen; if the other PC changed the file in the meantime, the upload is refused, and
the next pass sees `L ≠ S, B ≠ S` and makes a conflict copy instead. Downloads are guarded the same
way on the client: just before replacing a local file the engine re-hashes it, and if it no
longer has the content the decision was based on, it keeps it as a conflict copy (guard *d*).
Mtimes never decide anything; they only let the scanner skip re-hashing unchanged files.

## Client state database

`%LocalAppData%\Pairnets\<hash of folder path>\state.db` (SQLite, WAL, `synchronous=FULL`):

| Table | Content |
|-------|---------|
| `files` | `path`, `baseHash` (B), and a cache `size`, `mtimeTicks`, `hash`, `hashedAtTicks`. The cached hash is reused only if size and mtime match **and** the file was modified at least 2 s before it was hashed ("racy-git" rule), so two same-size edits in one timestamp tick are never missed. |
| `remote` | mirror of the server manifest, kept current with `GET /api/manifest?since=cursor` |
| `warnings` | paths the server rejected (invalid name, case collision); retried only when the file or the server changes |
| `pendingDeletes`, `approvedDeletes` | the mass-delete guard's blocked set and the user's one-pass approval |
| `meta` | marker id, server id, manifest cursor, folder |

## A sync pass

1. **Preflight** (nothing is touched if any check fails): folder exists; `.pairnets-marker` exists and
   matches the state; the folder is not empty while state tracks files; the server id is the one
   this folder was synced with and its version did not go backwards (restored backup).
2. **Server manifest**: full on startup, manual and periodic passes; delta (`since`) otherwise.
3. **Scan**: walk the folder (never following symlinks/junctions), skip ignored names, hash new or
   changed files. Files modified in the last 2 s, locked, or changing while hashed are *unknown*
   this pass: never uploaded and never treated as deleted.
4. **Plan**: `Decide(L, S, B)` for the union of local, server and base paths.
5. **Mass-delete guard**: if the pass would delete more than `min(20 % of tracked files, 50)` on
   either side, or the folder is empty, nothing runs and the user is asked. "Allow these
   deletions" approves exactly one pass and only the listed deletions.
6. **Execute** (deletes first, then conflicts, uploads, downloads). Per-file errors are isolated;
   network or auth errors abort the pass and the runner retries with backoff.

Downloads stream to `.pairnets-tmp\<guid>.part`, are verified against the server hash and length,
get the server's mtime and then atomically replace the target. The recorded base is the hash of
what was actually downloaded. Uploads stream from a `FileShare.ReadWrite|Delete` handle; the base
becomes the hash the server returns.

## When passes run (SyncRunner)

* full pass at startup (how the laptop catches up after being off) and after resume from sleep;
* local changes (FileSystemWatcher, debounced 2 s; buffer overflow → full pass);
* SignalR `Changed(deviceId, path)` from the other PC (≈ 0.3 s); own echoes are ignored;
* every 5 minutes as a safety net; on "Sync now".

Only one pass runs at a time; requests during a pass are merged into one follow-up pass. Offline:
retry after 2, 5, 10, 30, 60 s; SignalR reconnects forever (0, 2, 5, 10, 30 s).

## Server

Storage under `DataDir` (default `/var/lib/pairnets`):

```
files/      current version of every file (same relative paths as the clients)
history/    history/<path>/<yyyyMMddTHHmmssfffZ>-<hash8>: overwritten and deleted versions
tmp/        in-flight uploads, including the part files of uploads in pieces (emptied at startup)
manifest.db files(path, pathLower, hash, size, modifiedMs, deleted, version), meta, journal
auth.db     computers (name and the SHA-256 of their key), waiting sign-in requests, the owner's sign-in (password hash, passkeys, sessions, email, Google) and setup and email links (hashes only)
devices.json the computers that use the server: name, first and last seen, app version, system, last change (shown in the apps; not used for syncing)
update/     the self-updater's request file, status.json and update.log
.lock       held by the running service; maintenance commands refuse to run while it is held
```

`version` is a global counter incremented on every change, which is what makes
`GET /api/manifest?since=` correct. Tombstones are never removed from the manifest.

**PUT flow:** validate the path → stream the body into `tmp/` while hashing → fsync → take the
global lock → case-collision check → compare base → (same hash: 200, no new version) → write a
journal row → move the current file to `history/` → rename the temp file into `files/` → set
mtime → commit manifest row + version + delete journal row in one transaction → release → broadcast.
At startup the journal is replayed: a change whose temp file still exists is rolled back, one whose
file reached `files/` is completed. `files/` therefore never contains a half-written file.

**Uploads in pieces:** big files are sent in pieces, because proxies cap one request
(Cloudflare's free plan: 100 MB) and because a broken connection should cost one piece, not the
whole file. `PieceSizer` picks the size: between 4 and 50 MiB, aiming at about 30 seconds per piece
at the speed the last pieces went (growing at most twofold per piece), and halved after a broken
connection. A file goes in pieces when it is over 50 MiB, or, once the speed is known, when it
would take longer than one piece. `POST /api/upload` runs the same checks as PUT (name, base, case) and opens a session:
a part file in `tmp/` plus a running SHA-256, kept in memory (`UploadSessions`). Each
`PUT /api/upload/{id}?offset=` must start exactly where the part file ends; every byte that
arrives is written, hashed and counted together, so after a cut the client asks
`GET /api/upload/{id}` and continues from there. The client's `PieceReader` likewise hashes every
byte once, also when it reads a piece again. `POST /api/upload/{id}/commit?hash=` requires all
bytes and the matching hash, then commits through the same path as PUT (fsync, lock, base check,
journal, history, manifest, broadcast). Sessions idle for an hour are dropped, at most 64 are open,
and a restart drops them all (the client then sends that file again). An older server answers
`404` to `POST /api/upload` and the client falls back to one PUT.

## Signing a computer in

1. The app reads `GET /api/hello` on the name you typed: is this a Pairnets nest, what is its HTTPS
   name, and which sign-in ways can the app offer.
2. It asks to join with `POST /api/pair/start` and gets a code (`KQ7M-4PXD`) and a secret only it
   knows, then opens your browser at `https://<nest>/link?code=…` (with `&method=google` or
   `&method=email` when you chose one).
3. You sign in on the nest's website. The approval page shows the code, the computer's name and
   system, and whether the request came from the same address as your browser. **Allow** calls
   `POST /web/api/pair/{code}/approve`.
4. The app asks `POST /api/pair/poll` every 2 seconds with its secret. After Allow it gets its own
   key, once, and keeps it in the system's secret store. The website then opens `pairnets://signed-in`
   on that computer, which only brings the app to the front.

Codes last 10 minutes; your other computers hear `PairRequested` on the push channel and show a
"wants to join" notice that leads to the same page.

### HTTP API

Who may call:

* **open**: no key needed.
* **key**: a computer's own key, or the old shared token while it is allowed, in `X-Sync-Token`,
  `Authorization: Bearer`, or the `access_token` query on `/hub` only. A removed computer gets
  401 `device-removed`; the shared token, once switched off, 401 `shared-token-off`.
* **own key**: a computer's own key only.

JSON is camelCase; errors are `{"code","message"}`.

| Method & path | Who | Result |
|---------------|-----|--------|
| `GET /api/health` | open | `200 ok` |
| `GET /api/hello` | open | `{product, apiVersion, serverVersion, publicUrl, deviceKeys, signIn, methods: {password, passkeys, email, google}}` |
| `POST /api/pair/start` | open, rate-limited | `{name, system, appVersion}` → `{pollToken, code, verifyUrl, expiresInSeconds, intervalSeconds}` · 409 without a public name · 429 when too many wait |
| `POST /api/pair/poll` | open, rate-limited | `{pollToken}` → `{status}`: `pending`, `approved` (with `id, name, key`, handed out once), `denied`, `expired` or `used` |
| `GET /api/me` | key | `{id, name, kind}`: who the nest thinks this computer is (`device-key` or `shared-token`) |
| `PATCH /api/devices/me` | own key | `{name}`: rename this computer (the nest keeps names unique) |
| `DELETE /api/devices/{id}` | key | remove a computer: its key stops at once and its push connection closes ("Sign out of this computer") |
| `GET /api/info` | key | `{serverId, version, apiVersion, serverVersion, diskFreeBytes, diskTotalBytes, updater}` |
| `POST /api/update` | key | 202 with the updater's status · 409 `updater-missing` · 429 (one request a minute) |
| `GET /api/update/diagnostics` | key | the updater's state, its units and the last lines of `update.log`, secrets hidden (Debug mode) |
| `GET /api/manifest[?since=v]` | key | `[{path,hash,size,modifiedMs,deleted,version}]`; headers `X-Pairnets-Server-Id`, `X-Pairnets-Version` |
| `GET /api/file?path=` | key | file stream with `ETag`, `X-Pairnets-Hash`, `X-Pairnets-Modified-Ms`, Range support; 404 if absent/deleted |
| `PUT /api/file?path=&base=&mtime=` | key | 200 entry · 409 `conflict` · 409 `case-collision` · 400 `invalid-name` · 401 |
| `POST /api/upload?path=&base=&size=&mtime=` | key | 201 `{id, received: 0}` · the same 400/409 as PUT · 503 `busy` (64 uploads open) |
| `PUT /api/upload/{id}?offset=` | key | piece of at most 64 MiB: 200 `{id, received}` · 409 `upload-offset` (offset is not where the server's copy ends) · 413 `too-large` · 404 unknown or expired |
| `GET /api/upload/{id}` | key | 200 `{id, received}` (where to continue) · 404 |
| `POST /api/upload/{id}/commit?hash=` | key | like PUT: 200 entry · 409 `conflict` / `case-collision` · 409 `upload-offset` (bytes missing) · 400 `upload-mismatch` (hash differs; upload dropped) |
| `DELETE /api/upload/{id}` | key | 204, drops the upload and its part file (idempotent) |
| `DELETE /api/file?path=&base=` | key | 200 tombstone (idempotent) · 409 `conflict` · 404 never existed |
| `GET /api/history?path=` | key | `[{id, storedAtUtc, size, hash8}]` newest first |
| `POST /api/history/restore?path=&id=` | key | restores the version as a normal new version |
| `GET /api/devices` | key | `[{name, firstSeen, lastSeen, online, appVersion, system, lastChange, id}]`: every computer that sent an authenticated request (`X-Device-Id`, plus `X-Pairnets-Client` such as "1.0.38; Windows"); `online` while its push channel is open or it was seen in the last 2 minutes; `id` only for computers with their own key |
| `/hub` (SignalR) | key | server → clients: `Changed(deviceId, path)`, `PeerBatch` (another computer's big upload starts or ends), `DeviceRemoved`, `DeviceRenamed`, `PairRequested(code, name, system)`, `PairDecided(code, approved)`; clients → server: `BatchStarted(count)`, `BatchFinished()` |

Every API response carries `Cache-Control: no-store, no-transform`, so a proxy in between
(Cloudflare) never caches or rewrites files, manifests or errors. (The website's pages are
`no-store` too; its scripts and styles are `no-cache` with an `ETag`.)

The apps' **History** page reads the whole manifest (`GET /api/manifest`, tombstones carry the time
of deletion in `modifiedMs`) and `GET /api/history`, and restores with `POST /api/history/restore`.
Because a computer ignores the server's `Changed` echo of its own requests, the app asks its runner
for a sync right after a restore.

### The nest's website

Plain pages built into the server program, served **only on the nest's own HTTPS name** (directly,
or through the tunnel with `Sync:TrustProxyHeaders`). Anywhere else a page redirects to that name,
or answers 404 "no website yet" when the nest has none. Every page has a strict content security
policy (no inline scripts or styles, nothing from other sites, no framing).

| Page | What it is |
|------|------------|
| `/` | goes to `/devices` when signed in, else `/signin`, or `/setup` before any way to sign in exists |
| `/setup` | first visit: takes the one-time code from `pairnets-server owner-link` (after `#code=`), then adds a passkey or a password |
| `/signin` | password, passkey, email link or Google, whichever are set up |
| `/link?code=` | approve or turn away a computer that wants to join |
| `/devices` | your computers and waiting requests: rename or remove them |
| `/security` | sign-in ways, where you are signed in, the old shared token switch |
| `/email-link` | where a sign-in or confirmation email lands |
| `/assets/*` | the website's CSS and JavaScript |

Its JSON API is `/web/api/*`. Every call must reach the nest's own name; calls that change something
must come from the nest's own pages (same `Origin`); answers are never cached. **open** below means
no session is needed (the call itself checks a code, a password or a passkey); **signed in** needs
the website's session cookie.

| Method & path | Who | Result |
|---------------|-----|--------|
| `GET /web/api/state` | open | signed in or not, whether a way to sign in exists, the usable ways, the nest's name |
| `POST /web/api/setup` | open | `{code}` from the setup link: signs this browser in (one use, 24 hours; only the newest link works) |
| `POST /web/api/signin/password` | open, rate-limited | `{password}`; wrong passwords from any address add up and are checked one at a time |
| `POST /web/api/signin/passkey/options`, `POST /web/api/signin/passkey` | open, rate-limited | passkey sign-in (WebAuthn) |
| `POST /web/api/signin/email/request` | open, rate-limited | `{email, next}` → always 204; a link goes only to the owner's confirmed address (at most 5 emails an hour) |
| `POST /web/api/signin/email/confirm` | open, rate-limited | `{code}` from an email link (15 minutes, once) |
| `GET /auth/google/start?purpose=&next=`, `GET /auth/google/callback` | open (browser redirects) | sign in with the connected Google account (PKCE), or connect one (`purpose=connect`, signed in) |
| `POST /web/api/signout` | signed in | ends this browser's session |
| `GET /web/api/devices` | signed in | computers, waiting requests, whether the shared token is allowed |
| `PATCH /web/api/devices/{id}`, `DELETE /web/api/devices/{id}` | signed in | rename / remove a computer |
| `GET /web/api/pair/{code}`, `POST …/approve`, `POST …/deny` | signed in | the approval page |
| `GET /web/api/security` | signed in | sign-in ways, sessions, shared token, how many computers use which |
| `POST /web/api/password`, `DELETE /web/api/password` | signed in | set or change the password (the current one is needed), or remove it |
| `POST /web/api/passkeys/register/options`, `POST /web/api/passkeys/register`, `GET /web/api/passkeys`, `DELETE /web/api/passkeys/{id}` | signed in | add, list and remove passkeys |
| `POST /web/api/email`, `DELETE /web/api/email` | signed in | send a confirmation link to a new address / turn email links off |
| `DELETE /web/api/google` | signed in | disconnect Google |
| `DELETE /web/api/sessions/{id}` | signed in | sign another browser out |
| `POST /web/api/shared-token` | signed in | `{allowed}`: switch the old shared token on or off (off also closes its live connections) |

Removing the last way to sign in is refused (409). The website and its API need no computer key;
`/web/api`, `/auth`, `/assets` and the pages are the only paths besides `/api/health`,
`/api/hello` and `/api/pair/*` that the key check lets through.

## Designed for later

* **Rename detection**: the engine plans per path from hashes; a later pass can pair a planned
  "delete A" with an "upload B" of the same hash and send a server-side move instead.
* **Resumable transfer**: big uploads already go in resumable pieces within one pass;
  downloads use HTTP Range-capable endpoints and stream to a temp file, so a download that
  continues where it stopped needs only client work.
