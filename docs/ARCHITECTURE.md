# Architecture

Pairnets keeps one folder identical on two Windows PCs (used one at a time) through a small server
on your Tailscale network, or reachable through a Cloudflare Tunnel (the server then listens on
127.0.0.1 and `cloudflared` brings requests in over an outbound connection). This document explains
how, and why it cannot silently overwrite your files.

```
 Desktop (Windows)                      Ubuntu server (Tailscale or tunnel)                 Laptop (Windows)
 ┌───────────────────┐   HTTP + token   ┌────────────────────────────────┐   HTTP + token   ┌───────────────────┐
 │ Pairnets.Client   │ ───────────────▶ │ pairnets-server (ASP.NET Core) │ ◀─────────────── │ Pairnets.Client   │
 │  tray UI (thin)   │                  │  /api/*  + SignalR /hub        │                  │  tray UI (thin)   │
 │ Pairnets.Core     │ ◀── "Changed" ── │  manifest.db (SQLite)          │ ── "Changed" ──▶ │ Pairnets.Core     │
 │  engine, runner,  │    push          │  files/   current versions     │    push          │  engine, runner,  │
 │  state.db         │                  │  history/ old + deleted        │                  │  state.db         │
 └───────────────────┘                  └────────────────────────────────┘                  └───────────────────┘
```

* **Pairnets.Core** (net8.0, no UI): path rules, ignore list, hashing, the decision function, the
  client state database, the scanner, the HTTP client, the sync engine and the runner. Everything
  here runs and is tested on Linux.
* **Pairnets.Server**: ASP.NET Core minimal API + SignalR, SQLite manifest, files on disk, systemd.
* **Pairnets.Client**: WPF/WinForms tray app that only shows state and forwards clicks.

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
devices.json the computers that use the server: name, first and last seen, app version, system, last change (shown in the apps; not used for syncing)
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

### HTTP API

All endpoints except health need the token (`X-Sync-Token`, `Authorization: Bearer`, or
`access_token` query on `/hub` only). JSON is camelCase; errors are `{"code","message"}`.

| Method & path | Result |
|---------------|--------|
| `GET /api/health` | `200 ok` (no auth) |
| `GET /api/info` | `{serverId, version, apiVersion}` (used by "Test connection") |
| `GET /api/manifest[?since=v]` | `[{path,hash,size,modifiedMs,deleted,version}]`; headers `X-Pairnets-Server-Id`, `X-Pairnets-Version` |
| `GET /api/file?path=` | file stream with `ETag`, `X-Pairnets-Hash`, `X-Pairnets-Modified-Ms`, Range support; 404 if absent/deleted |
| `PUT /api/file?path=&base=&mtime=` | 200 entry · 409 `conflict` · 409 `case-collision` · 400 `invalid-name` · 401 |
| `POST /api/upload?path=&base=&size=&mtime=` | 201 `{id, received: 0}` · the same 400/409 as PUT · 503 `busy` (64 uploads open) |
| `PUT /api/upload/{id}?offset=` | piece of at most 64 MiB: 200 `{id, received}` · 409 `upload-offset` (offset is not where the server's copy ends) · 413 `too-large` · 404 unknown or expired |
| `GET /api/upload/{id}` | 200 `{id, received}` (where to continue) · 404 |
| `POST /api/upload/{id}/commit?hash=` | like PUT: 200 entry · 409 `conflict` / `case-collision` · 409 `upload-offset` (bytes missing) · 400 `upload-mismatch` (hash differs; upload dropped) |
| `DELETE /api/upload/{id}` | 204, drops the upload and its part file (idempotent) |
| `DELETE /api/file?path=&base=` | 200 tombstone (idempotent) · 409 `conflict` · 404 never existed |
| `GET /api/history?path=` | `[{id, storedAtUtc, size, hash8}]` newest first |
| `POST /api/history/restore?path=&id=` | restores the version as a normal new version |
| `GET /api/devices` | `[{name, firstSeen, lastSeen, online, appVersion, system, lastChange}]`: every computer that sent an authenticated request (`X-Device-Id`, plus `X-Pairnets-Client` such as "1.0.38; Windows"); `online` while its push channel is open or it was seen in the last 2 minutes |
| `/hub` (SignalR) | server → clients: `Changed(deviceId, path)` |

Every response carries `Cache-Control: no-store, no-transform`, so a proxy in between (Cloudflare)
never caches or rewrites files, manifests or errors.

The apps' **History** page reads the whole manifest (`GET /api/manifest`, tombstones carry the time
of deletion in `modifiedMs`) and `GET /api/history`, and restores with `POST /api/history/restore`.
Because a computer ignores the server's `Changed` echo of its own requests, the app asks its runner
for a sync right after a restore.

## Designed for later

* **Rename detection**: the engine plans per path from hashes; a later pass can pair a planned
  "delete A" with an "upload B" of the same hash and send a server-side move instead.
* **Resumable transfer**: big uploads already go in resumable pieces within one pass;
  downloads use HTTP Range-capable endpoints and stream to a temp file, so a download that
  continues where it stopped needs only client work.
