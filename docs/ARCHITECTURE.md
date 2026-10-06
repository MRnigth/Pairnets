# Architecture

Tether keeps one folder identical on two Windows PCs (used one at a time) through a small server
on your Tailscale network. This document explains how, and why it cannot silently overwrite your
files.

```
 Desktop (Windows)                    Ubuntu server (Tailscale only)                Laptop (Windows)
 ┌───────────────────┐   HTTP + token   ┌──────────────────────────────┐   HTTP + token   ┌───────────────────┐
 │ Tether.Client     │ ───────────────▶ │ tether-server (ASP.NET Core) │ ◀─────────────── │ Tether.Client     │
 │  tray UI (thin)   │                  │  /api/*  + SignalR /hub      │                  │  tray UI (thin)   │
 │ Tether.Core       │ ◀── "Changed" ── │  manifest.db (SQLite)        │ ── "Changed" ──▶ │ Tether.Core       │
 │  engine, runner,  │    push          │  files/   current versions   │    push          │  engine, runner,  │
 │  state.db         │                  │  history/ old + deleted      │                  │  state.db         │
 └───────────────────┘                  └──────────────────────────────┘                  └───────────────────┘
```

* **Tether.Core** (net8.0, no UI): path rules, ignore list, hashing, the decision function, the
  client state database, the scanner, the HTTP client, the sync engine and the runner. Everything
  here runs and is tested on Linux.
* **Tether.Server**: ASP.NET Core minimal API + SignalR, SQLite manifest, files on disk, systemd.
* **Tether.Client**: WPF/WinForms tray app that only shows state and forwards clicks.

## The three-hash algorithm

For every path the engine computes three SHA-256 hashes (null = absent or deleted):

| Name | Meaning |
|------|---------|
| **L** | content of the local file now |
| **S** | content on the server now (null if absent or tombstoned) |
| **B** | the *base*: the content both sides agreed on at the last successful sync of this path |

`SyncDecision.Decide(L, S, B)` is a pure function. Every reachable case
(15 equality patterns, covering all 64 combinations of null/a/b/c) is listed here and tested
exhaustively in `tests/Tether.Tests/Unit/DecideTableTests.cs`:

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

`%LocalAppData%\Tether\<hash of folder path>\state.db` (SQLite, WAL, `synchronous=FULL`):

| Table | Content |
|-------|---------|
| `files` | `path`, `baseHash` (B), and a cache `size`, `mtimeTicks`, `hash`, `hashedAtTicks`. The cached hash is reused only if size and mtime match **and** the file was modified at least 2 s before it was hashed ("racy-git" rule), so two same-size edits in one timestamp tick are never missed. |
| `remote` | mirror of the server manifest, kept current with `GET /api/manifest?since=cursor` |
| `warnings` | paths the server rejected (invalid name, case collision); retried only when the file or the server changes |
| `pendingDeletes`, `approvedDeletes` | the mass-delete guard's blocked set and the user's one-pass approval |
| `meta` | marker id, server id, manifest cursor, folder |

## A sync pass

1. **Preflight** (nothing is touched if any check fails): folder exists; `.tether-marker` exists and
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

Downloads stream to `.tether-tmp\<guid>.part`, are verified against the server hash and length,
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

Storage under `DataDir` (default `/var/lib/tether`):

```
files/      current version of every file (same relative paths as the clients)
history/    history/<path>/<yyyyMMddTHHmmssfffZ>-<hash8>: overwritten and deleted versions
tmp/        in-flight uploads (emptied at startup)
manifest.db files(path, pathLower, hash, size, modifiedMs, deleted, version), meta, journal
devices.json the computers that use the server: name, last seen, app (for the apps' overview; not used for syncing)
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

### HTTP API

All endpoints except health need the token (`X-Sync-Token`, `Authorization: Bearer`, or
`access_token` query on `/hub` only). JSON is camelCase; errors are `{"code","message"}`.

| Method & path | Result |
|---------------|--------|
| `GET /api/health` | `200 ok` (no auth) |
| `GET /api/info` | `{serverId, version, apiVersion}` (used by "Test connection") |
| `GET /api/manifest[?since=v]` | `[{path,hash,size,modifiedMs,deleted,version}]`; headers `X-Tether-Server-Id`, `X-Tether-Version` |
| `GET /api/file?path=` | file stream with `ETag`, `X-Tether-Hash`, `X-Tether-Modified-Ms`, Range support; 404 if absent/deleted |
| `PUT /api/file?path=&base=&mtime=` | 200 entry · 409 `conflict` · 409 `case-collision` · 400 `invalid-name` · 401 |
| `DELETE /api/file?path=&base=` | 200 tombstone (idempotent) · 409 `conflict` · 404 never existed |
| `GET /api/history?path=` | `[{id, storedAtUtc, size, hash8}]` newest first |
| `POST /api/history/restore?path=&id=` | restores the version as a normal new version |
| `GET /api/devices` | `[{name, online, lastSeenUtc, app}]`: every computer that sent a request (`X-Device-Id`, plus `X-Tether-App` such as "Windows 1.0.60"); `online` while its push channel is open |
| `/hub` (SignalR) | server → clients: `Changed(deviceId, path)` |

The apps' **History** page reads the whole manifest (`GET /api/manifest`, tombstones carry the time
of deletion in `modifiedMs`) and `GET /api/history`, and restores with `POST /api/history/restore`.
Because a computer ignores the server's `Changed` echo of its own requests, the app asks its runner
for a sync right after a restore.

## Designed for later

* **Rename detection**: the engine plans per path from hashes; a later pass can pair a planned
  "delete A" with an "upload B" of the same hash and send a server-side move instead.
* **Chunked/resumable transfer**: downloads already use HTTP Range-capable endpoints and stream to a
  temp file; uploads go through `ITetherApi.UploadAsync`, which can be replaced by a chunked
  protocol without touching the decision logic.
