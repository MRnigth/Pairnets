# Decisions

Judgment calls made while building Pairnets v1, and why. The guiding rule: when in doubt, choose
what cannot lose or silently overwrite data.

## Setup and placeholders

* **Unfilled placeholders.** Server IP, folders and sizes were not provided. Docs use
  `https://sync.example.com`; the folder is chosen in the first-run window; the
  design assumes about 20 GB with single files of several GB, so everything is streamed and never
  buffered in memory.
* **Missing `.gitignore`.** The repo contained only `LICENSE`, so the standard Visual Studio
  `.gitignore` (`dotnet new gitignore`) was added along with Pairnets's exclusions.
* **Branches.** Work is committed on a feature branch and each milestone is pushed to `main`, as
  requested.
* **.NET SDK on the build machine.** Ubuntu's own `dotnet-sdk-8.0` package lacks the WindowsDesktop
  SDK, so the Microsoft build (from packages.microsoft.com) is used to build the client on Linux.

## Sync semantics

* **Mass-delete guard, literal rule.** A pass is blocked when it would delete more than
  `min(20 % of tracked files, 50)` on either side, with no minimum floor (your choice). In small
  folders this means even one or two deletions need "Allow these deletions". Tracked = files with
  an agreed base.
* **Allow-once scope.** An approval covers exactly the next pass and only the deletions that were
  listed. If more deletions appear, the pass is blocked again.
* **Empty folder.** If the folder is empty while files are tracked, the pass is blocked (it
  usually means a wrong or unplugged drive). If the deletions are really wanted, the same "Allow"
  action applies.
* **Server identity and rollback.** The server has a random id and a version counter. A client
  refuses to sync when the id changes or the version goes backwards (e.g. restored backup), and
  offers "Re-link". Re-link forgets all bases, so the next pass merges like a first sync, which
  never deletes or overwrites. This was not in the spec but closes a real overwrite path
  (stale server content would otherwise look like "only the server changed").
* **Foreign marker.** A folder with a `.pairnets-marker` but no matching state is blocked until the
  user confirms (folder moved, or state reset), instead of silently resyncing.
* **Locked or fresh files** are "unknown" for the pass, never "deleted". The same applies to
  folders that cannot be listed.
* **Guard (d) on deletes.** Before deleting a local file, the engine re-hashes it. If it changed,
  it is not deleted; the next pass uploads it (edit beats delete). Before overwriting, a changed
  file is renamed to a conflict copy.
* **Order of operations.** Deletes run before uploads and downloads, so a file replaced by a
  folder of the same name (or the reverse) does not collide.
* **Case-only renames** are a delete plus a create, like all renames in v1.
* **A file and a folder with the same name** created on the two PCs at the same time cannot both
  exist; the server rejects the second one as a name collision and the user is warned.
* **Same content, different base.** If an upload's content equals what the server already has, the
  server answers 200 without a new version, even if the base differs.
* **`since` and tombstones.** Tombstones are kept forever in the manifest (a few bytes each) so that
  delta manifests stay correct.
* **History minimum.** "Keep the latest 5 versions" counts history versions (previous and deleted
  contents), not the current file.
* **Hash cache.** The cached hash is reused only if size and mtime match and the file was last
  modified at least 2 s before it was hashed (the "racy git" rule). Cheap, and it removes the only
  way mtime could hide a change.
* **Device id** is the device name. Names must differ between the PCs; the default (computer name)
  does. (Later, computers that sign in get their own id from the nest, which also keeps their names
  unique.)

## Server

* **`GET /api/info`** was added (server id, version, API version) for "Test connection" and the
  identity check.
* **Error bodies** carry `message` as well as `code`.
* **Commit journal.** The multi-step move (old → history, temp → files, manifest) is recorded in a
  journal row and replayed at startup, so a crash or power loss never leaves `files/` inconsistent
  with the manifest.
* **Pre-check before receiving the body.** PUT checks base and case collisions before reading
  the body, and the client sends `Expect: 100-continue`, so a stale 2 GB upload is refused before it
  is sent. The check is repeated under the lock after the upload.
* **A file added by hand in `files/`** that a client overwrites is moved to history first.
* **Data-dir lock.** The service holds `DataDir/.lock`; the maintenance CLI refuses to run
  concurrently.
* **Default bind** is `127.0.0.1:5075`, set in code so `ASPNETCORE_URLS` and `--urls` override it
  cleanly. `install.sh` keeps it there (the Cloudflare Tunnel brings requests in) and refuses `0.0.0.0`.
* **Logging under systemd** uses the systemd console formatter (journald priorities). ASP.NET's own
  request-URL logging is at Warning, so `access_token` never reaches the logs.
* **`MemoryDenyWriteExecute`** is not set in the unit, because the .NET JIT needs W+X memory.
* **Single-file publish** keeps `libe_sqlite3.so` next to the binary (no self-extraction), which
  works under `ProtectSystem=strict`.
* **Data on another disk is bind-mounted onto `/var/lib/pairnets`**, not moved with
  `Sync__DataDir`. The hardened unit may only write there (`ReadWritePaths`), and the updater's path
  unit and `update.sh` watch `/var/lib/pairnets/update`, so DEPLOY.md copies the data, mounts the
  new place onto the old path through `/etc/fstab`, and adds a `RequiresMountsFor=` drop-in so the
  server never starts on an empty folder when the disk is missing.
* **Only the newest setup link works.** `owner-link` cancels every older unused link. A link shown
  to someone else (a shared terminal, a chat) would otherwise stay good for 24 hours, and on a nest
  without an owner the first person to open it takes the nest.
* **A setup link can replace a forgotten password, briefly.** The browser that opened a setup link
  may set a new password without the old one for 30 minutes, and only if the password is older than
  that sign-in (so once per link); every other signed-in browser is then signed out, in case someone
  else changed it. Every other browser still needs the current password to change it, so one left
  signed in cannot lock you out. That keeps "SSH, `owner-link`, new password" a real way back in.
* **Maintenance commands run as the data folder's owner.** On Linux and macOS the CLI compares the
  folder's owner with the user running it and stops with the right `sudo -u pairnets …` line: run
  as root, SQLite would leave root-owned `-wal`/`-shm` files the service can no longer write.
* **Computers on the old shared token can be forgotten on the website.** They have no key to
  remove, so "Forget this computer…" only clears their row; while the token is still on, the
  computer shows up again when it next connects, and the dialog says so.
* **Destructive switches ask first.** Turning off the shared token, removing the password and
  removing a passkey each ask for a confirmation with one sentence about the consequence.

## Client

* **Toasts** use `NotifyIcon.ShowBalloonTip`, which Windows 10/11 render as toast notifications.
  This avoids a third-party notification package. Each kind of toast appears at most once per 10
  minutes.
* **Tray icons** are drawn at runtime (colored circles with a white glyph: ✓, ↻, ⏸, !, ✕, –), so
  they read at 16 px and there are no binary assets.
* **Look and feel.** Windows (WPF) and Mac/Linux (Avalonia) share one design: cards, a segmented
  Activity / Needs attention switch, coloured activity icons, and light and dark palettes that follow
  the system setting (on Windows the `AppsUseLightTheme` value, switched live, with a matching title
  bar). The line icons are our own 24×24 path geometries, so no icon font or third-party icon
  licence is involved. CI renders the real Windows windows in light and dark
  (`tools/RenderScreens.Wpf`, artifact `windows-ui-screenshots`), which also proves every window's
  XAML loads.
* **Host:** a WPF `Application` without a main window, plus a WinForms `NotifyIcon` for the tray.
* **Recycle Bin** through `SHFileOperation` (silent). For paths longer than 260 characters, or when
  that fails, the file is deleted permanently. The server still has it in history.
* **DPAPI** with the CurrentUser scope plus fixed application entropy.
* **Folder moved.** If the configured folder is missing, "Locate the sync folder…" accepts a new
  location only if its marker matches; the state database is then copied to the new location's
  state directory.
* **First sync** with files on both sides shows an explanation of the merge before starting.
* **The way back from the browser is a `pairnets://` link that carries nothing.** After Allow, the
  nest's website opens `pairnets://signed-in` (only on the computer that is signing in, which the
  server knows by its address). The installers register the scheme; the started second instance
  pokes the running app through a per-user named pipe (`Pairnets.Core/Client/AppActivation.cs`) and
  quits, and the app just comes to the front — the key still arrives only through the app's secret
  poll. So if another program grabs the scheme, or anyone opens the link, nothing is exposed and
  nothing is approved. On Windows the poking instance passes its foreground right along
  (`AllowSetForegroundWindow`), otherwise the window would only flash in the taskbar. A poke can
  land between creating the pipe and waiting on it; Windows then fails the wait instead of
  completing it, and the listener counts that as a knock so no poke is ever lost.
* **Opening Pairnets again brings the running one forward, like other apps.** Every second start
  (Start menu, a shortcut, the installer's "Start now", a `pairnets://` link) uses the same poke
  instead of showing "already running". The running app answers with one byte once its window is
  really up (within 10 s: a first window on a slow computer can take seconds), so a stuck app gets a
  clear "isn't responding" message instead of silence; the second start keeps trying to connect for
  10 s, covering a Pairnets that is still starting up.
  A start at login (`--autostart`) never pokes, so it never pops a window up. The pipe is
  `CurrentUserOnly`: another user on the same computer can neither poke nor answer it. On macOS
  opening the running app again starts no second process; the app's Reopen event does the same.

## Tooling and tests

* **xunit 2.9 + runner 3.1** and Microsoft packages only (Microsoft.Data.Sqlite, SignalR client,
  Microsoft.Extensions.Logging, ProtectedData).
* **Windows CI runs the full test suite** (with 50 convergence seeds), not just the client build,
  because NTFS case-insensitivity and file locking are where the product runs.
* **Fault injection** uses a real TCP proxy that resets connections, instead of mocks.
* **Secret scanning** (`scripts/check-secrets.sh`) looks for 64-hex tokens, carrier-grade NAT/VPN IPs,
  token assignments, Cloudflare Tunnel tokens and personal Windows paths, and (since 2026-10-11) the key formats
  of every service in reach: GitHub, Google, Resend, Turnstile, AWS, Stripe, Slack, SendGrid, npm, Tailscale,
  signed tokens, `pn_` computer keys and passwords in URLs. The git `pre-push` hook runs it on every push and
  in CI over the whole history.
* **Commit attribution.** At your request, commits no longer carry `Co-Authored-By` trailers.

## Mac and Linux desktop apps (added after v1 scope)

* **Separate Avalonia app (`src/Pairnets.Desktop`) for macOS and Linux; Windows keeps the WPF app.**
  This was your choice. WPF only runs on Windows, and Avalonia (MIT, a mature .NET cross-platform UI)
  is the closest equivalent. Avalonia is the only third-party runtime dependency. All sync logic
  and the window state (`ClientSession`, `ActivityFeed`, `StatusSnapshot`) live in Pairnets.Core, so
  both UIs stay thin and behave the same.
* **Token storage per OS.** Windows: DPAPI. macOS: the login Keychain through the Security framework
  (the token never appears on a command line). Linux: the desktop keyring through `secret-tool`
  (token passed on stdin). Without a keyring it falls back to a file readable only by the user
  (mode 600).
* **Trash per OS.** Windows: Recycle Bin. macOS: `NSFileManager trashItemAtURL`, which needs no
  Finder automation permission. Linux: `gio trash`. Each falls back to a permanent delete, because
  the server keeps history.
* **Mac builds are unsigned and not notarized** (no Apple Developer account). They are ad-hoc signed
  (required for Apple Silicon). The `get.sh --mac` command installs without the Gatekeeper prompt,
  because files downloaded with curl are not quarantined. Signing and notarization can be added to
  `release.yml` later with repository secrets.
* **Menu-bar app on Mac** (`LSUIElement`, no Dock icon), like other sync tools.
* **Unicode normalization.** macOS treats "é" (one character) and "e + combining accent" as the
  same file name. The name-collision key is now NFC-normalized as well as lower-cased, so the
  server rejects such pairs like case variants.
* **Sleep/resume** is signalled to the runner on Windows. On Mac and Linux the 5-minute full pass
  and the SignalR reconnect cover catch-up after wake.
* **One-line installer (`deploy/get.sh`)** downloads from GitHub releases and verifies against
  `SHA256SUMS.txt` before running anything. Server mode needs root; desktop mode refuses root and
  installs into the home folder.
* **Main window.** The apps now open a window (status, progress, activity, needs attention) in
  addition to the tray/menu-bar icon. Closing it keeps syncing in the background.
* **Screenshots** in `docs/images` are rendered by a headless UI test
  (`PAIRNETS_SCREENSHOT_DIR=... dotnet test --filter DesktopUi`).
* **Rolling "latest" release.** Every push to `main` deletes and recreates one release, tagged
  `latest` and titled "Latest build", with the packages from that commit. The download links in the
  README and HOWTO (`releases/download/latest/<file>`) therefore always give the newest build
  without anyone tagging a version. Versioned `v*` releases are created separately and are never
  touched by this job.
* **Upload stall timeout.** The server abandons an upload that sends no data for 60 s
  (`Sync:UploadStallTimeout`) and deletes its temp file, matching the client's 60 s watchdog.
  Kestrel's minimum data rate is averaged over the whole body, so after a fast start a connection
  that dies without a reset (a laptop dropping off Wi-Fi) would otherwise hold the request and its
  temp file for hours.
* **Four transfers at a time.** Each small file costs a network round trip, so the engine overlaps
  uploads (and downloads) four at a time (1/2/4/8 in Settings). Deletes and conflicts stay one by
  one. A network or token failure still ends the pass exactly as before.
* **Speed limits** are one token bucket per direction shared by all transfers (100 KB/s minimum, so
  the 60 s stall watchdog never fires while waiting for the limit).
* **Waiting for big batches.** A pass with 100+ uploads announces itself over the push channel; the
  other computer holds remote syncs and defers downloads (its own uploads continue) until the batch
  ends, the sender disconnects or is quiet for 2 minutes, or 30 minutes pass. This avoids hundreds
  of tiny passes that each rescan the folder, and sharing Wi-Fi with the upload.
* **Pause cancels the running pass.** Half-sent files are discarded on both sides; nothing partial
  is ever stored, and Resume finishes the work.
* **Update check** reads `version.json` from the latest release without signing in, so it needs a
  public repository. Builds are numbered `1.0.<release run>`. The Windows app installs updates
  itself (checksum-verified, silent installer); the unsigned Mac and Linux apps open the download
  page.
* **Server self-update** runs as root only through a systemd path unit that the unprivileged server
  triggers with an empty file, and only installs the newest official release after checking it.
  The server process never gets root. See SECURITY.md.
* **New main window (sidebar).** The window has a sidebar (Overview, Activity, History, Devices,
  Needs attention, Settings) instead of three tabs and a row of six equal buttons. Sync now, Pause and Open
  folder stay at the top right; the log and bug report moved into a "⋯" menu. The app update is a
  card at the bottom of the sidebar instead of a banner over everything. Settings is a page; the
  first run keeps its own window (today the sign-in window). Both apps (WPF and Avalonia) share the design, and all the
  logic behind it (the map, history lists, activity by day) is in Pairnets.Core.
* **"This computer ⇄ server ⇄ other computer".** The overview draws the computers from the same
  list as the Devices page (`GET /api/devices`, kept by the server in `devices.json`), so it can show
  the other computer as online or "last seen 3 h ago", and animates files moving from this computer's
  transfers and the other computer's push messages. It is display only: nothing in syncing depends
  on it. Computers not seen for 60 days are left out of the picture (the Devices page lists them all).
* **History in the app.** The server already kept versions and had list/restore endpoints; the
  History page uses them, so getting a file back no longer needs SSH. "Deleted files" covers the
  last 30 days (the server's default retention). A restore is an ordinary new version, so it can be
  undone the same way.
* **Quick panel.** Clicking the tray icon on Windows opens a small panel next to the taskbar (like
  OneDrive or Dropbox); double-click opens the window. On macOS Avalonia's menu-bar icon only ever
  shows its menu (it reports no clicks), so the panel opens from "Quick status…" in that menu; on
  Linux a click opens it where the desktop reports clicks.

## Cloudflare Tunnel

* **Why a tunnel at all.** When no machine can accept incoming connections (no port forwarding,
  CGNAT), something with a public address has to sit in the middle. Cloudflare Tunnel was chosen
  because it is free, needs only outbound connections from the server, carries WebSockets (the push
  channel), and the PCs need nothing installed. The cost: Cloudflare ends HTTPS at its edge and can
  see the traffic, and the server is reachable from the internet (the token is the gate). Both are
  spelled out in SECURITY.md and HOWTO section 2.
* **The only way in.** Pairnets first ran inside a Tailscale network, with the tunnel as a second
  option. Tailscale was removed: one way to set up and explain, and nothing to install on the
  computers. A first `install.sh` needs `--public-url` and sets up the tunnel; there is no Tailscale
  lookup, ACL file or `tailscaled` dependency any more. A server set up the old way keeps its listen
  address on upgrade; running `install.sh --public-url …` once moves it to the tunnel.
* **Our own `pairnets-tunnel.service` instead of `cloudflared service install <token>`.** The official
  command puts the tunnel token in a world-readable unit file and on a command line. Ours reads it
  from `/etc/pairnets/tunnel.env` (root, 600) and runs cloudflared as a dynamic unprivileged user.
  cloudflared comes from Cloudflare's signed apt repository, so it updates with the system.
* **Uploads in pieces of at most 50 MiB.** Cloudflare's free and Pro plans refuse request bodies
  over 100 MB (413), and Pairnets syncs multi-GB files. 50 MiB leaves room for headers and rounding;
  the server takes up to 64 MiB per piece. The pieces also make big uploads resumable: every byte
  the server takes in is written, hashed and counted together, so after a cut the client continues
  from the server's count instead of starting again.
* **Piece size follows the connection.** Cloudflare takes in a whole piece before passing it on, so
  a cut costs the piece in flight, and on a slow or crowded upload (2 Mbit/s) one 50 MB piece takes
  minutes. The client aims at about 30 seconds per piece from the measured speed (4 to 50 MiB,
  growing at most twofold per piece so one fast moment cannot overshoot) and halves it after a cut.
  Until the speed is known, only files over 50 MiB are split, so a fast network sends small files
  in one round trip as before; on a slow one, any file that would take longer than one piece is
  split too. Only the client decides this: the server takes any piece size at any offset.
* **Sessions in memory, not in the manifest.** An unfinished upload is not data yet; keeping it out
  of SQLite keeps the commit path and journal unchanged. A restart (rare) drops sessions and the
  client sends that file again on its next pass. Idle sessions expire after an hour; at most 64 are
  open, so a buggy client cannot fill tmp/ without bound.
* **Old servers keep working.** A client that gets a plain 404 for `POST /api/upload` sends the file
  in one request, as before, and does not ask again. `ApiVersion` stays 1: "Test connection"
  requires an exact match, so a bump would have locked new apps out of every existing server.
* **Real client addresses.** Behind cloudflared every request comes from 127.0.0.1, so one
  stranger trying tokens would have slowed down everyone through the shared failure counter.
  `Sync:TrustProxyHeaders` takes `CF-Connecting-IP` (or the last `X-Forwarded-For` entry) instead,
  only on loopback connections and only when install.sh turned it on for a tunnel.
* **The website trusts the tunnel by the connection, not the address.** Once the client address is
  replaced, the request no longer looks local, so the middleware also marks that the connection
  itself came from loopback, and only that mark lets `X-Forwarded-Proto: https` count. (Checking the
  replaced address made the website redirect to itself forever behind every tunnel.)
* **`install.sh` fails when the server does not answer.** It waits for the local
  health check (up to 5 minutes while systemd says the server is starting: a big nest checks its data
  folder first) and exits non-zero otherwise, so the self-updater reports a failed update instead of
  a success. Giving `--public-url` removes old `PUBLIC_URL=` / `Sync__PublicUrl=` lines, which the
  server reads first, so the given name always wins. On a terminal it prints the setup link at the
  end; never into the updater's log.
* **`Cache-Control: no-store, no-transform` on every response**, so no proxy ever caches a file or
  manifest, or recompresses a download whose hash the client checks.
* **No token on the health check.** The apps leave the token off `/api/health`. If the answer comes
  through Cloudflare on a plain `http://` address, "Test connection" refuses it before the token is
  ever sent. Cloudflare's own error pages (tunnel down, bot check) are recognised by their HTML body
  and Cloudflare headers and explained in plain words; Pairnets's JSON errors relayed by Cloudflare are
  left alone.

## Big transfers continue where they stopped

* **This replaces two earlier rules:** "Sessions in memory, not in the manifest" (above) and the "half-sent
  files are discarded" part of "Pause cancels the running pass". An upload or download that stops part-way
  is now kept and continued. Still nothing partial is ever stored as a file.
* **Uploads in pieces outlive a pass.** A broken connection, Pause or closing the app no longer drops the
  upload on the server. The computer keeps the upload's id together with exactly the content it carries
  (size, modification time, content hash, base, server address) in state.db (new `uploads` table, schema 2),
  and the next pass asks the server how much it has and sends only the rest. Within one pass a broken
  connection is retried for about a minute (after 1, 2, 4, 8, 15 and 30 s); then the pass ends offline and the
  runner's retries (2, 5, 10, 30, 60 s) take over, each continuing from the server's count.
* **The whole file is checked again, so two versions are never mixed.** When it continues, the computer reads
  the bytes the server already has once more from the file (they are not sent again) and hashes the whole
  file as it is now; the server compares that with its own hash of what it received. If the file changed in
  between in a way that size and time do not show, the commit is refused and the file is sent again from the
  start. The cost is reading the part already sent once more per continue (local disk: seconds to a minute
  for 10 GB); trusting a hash kept from before would have been faster but could store a mixed file.
* **Given up only on purpose.** `DELETE /api/upload/{id}` is sent only when the upload will not be continued:
  the file changed, was deleted, is in sync now, or the server has another version. An upload nobody comes
  back for (Reset, Re-link, uninstall) is dropped by the server after an hour without data, as before; so a
  laptop that sleeps longer than an hour in the middle of an upload sends that file again.
* **"Busy" is not a failure.** While an earlier piece of the same upload is still arriving (the server waits
  up to 60 s on a quiet connection), the server answers `upload-busy` and `GET /api/upload/{id}` says
  `busy: true`. The computer waits (up to 3 minutes) instead of counting a failure and shrinking the pieces.
  Older servers answer `upload-offset` instead, which is handled the same way.
* **Uploads also survive a restart of the server.** Each session now has a small description next to its part
  file in tmp/ (`id.session`: path, base, size, mtime), and the part file is written to disk after every piece.
  At start-up the server keeps the pairs that received data within the last hour (everything else in tmp/ is
  still deleted, after the journal is replayed) and takes them up again. The running hash of what arrived lived
  only in memory, so each kept part file is read and hashed once more, in the background; the upload answers
  busy meanwhile, so no request waits for it. A part damaged over the restart fails the commit's hash check and
  is refused, never stored.
* **Big downloads continue too.** Files from 16 MB keep their partial download in `.pairnets-tmp` when they are
  cut off, paused or closed, recorded in state.db (new `downloads` table) with the version (content hash) its
  bytes belong to. The next pass sends `Range` with `If-Range` (a file's ETag is its hash), so the server sends
  only the rest of that same version, or the whole file if it changed meanwhile. A new version in the manifest
  deletes the old partial at once. The whole file is still hashed and checked against the server's hash before
  it replaces anything; a partial that fails the check is deleted and the file is downloaded again in the same
  pass. Smaller files take about a minute or less even on a slow link, so they simply start again.
* **No answer takes longer than Cloudflare waits (100 s), and no request carries more than 100 MB.** Pieces are at
  most 50 MiB (the server takes up to 64 MiB); one PUT is only used for files up to 50 MiB, or with a server from
  before pieces. The commit no longer waits for gigabytes to reach the disk (each piece already did). Restoring a
  big history version used to copy and hash it inside the request; it now runs in the background, the request
  waits at most 60 s and otherwise answers 202, and the app asks again with the same ticket until it is done.
  Left as it was: a file someone put into `files/` by hand is hashed under the server's lock when a computer
  overwrites it (rare, and only once).
* **Going back to an older app** needs Settings → Start over: an app from before refuses a state.db at schema 2
  ("created by a newer Pairnets"). Nothing is lost by that; it only stops syncing until reset.

## Renamed from Tether to Pairnets

* **Why.** "Tether" is also the name of a large crypto company, and the matching domains were taken.
  The app, code, binaries, services, folders and repository are now **Pairnets**
  (`MRnigth/Pairnets`; GitHub redirects the old address).
* **Old names live in one place**, `Pairnets.Core/Legacy/TetherNames.cs`, used only by the migration
  and compatibility code, so no rename sweep can drop them by accident. Tests pin them.
* **One machine at a time.** The server reads `X-Tether-Client` and sends every response header
  under both names (`X-Pairnets-*` and `X-Tether-*`), and the apps read the new names first and then
  the old ones. Tether apps and a Pairnets server, or the other way round, keep syncing.
* **The apps take over Tether's data on first start.** `%AppData%\Tether` and `%LocalAppData%\Tether`
  (settings, sync state, logs, the Linux token file) are moved to the Pairnets folders. Nothing moves
  while Tether is running (its single-instance mutex or lock file is held): Pairnets asks to quit it
  first. The token is read with Tether's DPAPI entropy, Keychain service or secret-tool attributes when
  Pairnets has none yet, and stored again under Pairnets' name; the old entry is left in place. The
  start-at-login entry is replaced. In each synced folder `.tether-marker` is renamed to
  `.pairnets-marker` (same folder id, so nothing is blocked) and `.tether-tmp` is removed; both old
  names stay reserved and ignored, so they can never sync.
* **The installers take over the old programs.** The Windows installer has a new AppId and folder and
  runs Tether's uninstaller silently (it removes only the program, never `%AppData%`). The Linux
  installer stops and removes the old program files. `install.sh` moves a Tether server
  (`/var/lib/tether`, `/etc/tether`, the `tether` user, renamed in place so file owners stay, and
  the units including the tunnel) on its first run; it only moves data and never deletes it.
* **No old file names in releases** (your choice). Tether apps and servers look for `TetherSetup.exe`,
  `tether-server-linux-x64.tar.gz` and so on, so they can't update themselves into Pairnets. Each
  machine gets Pairnets by hand once, and the migration then happens on its own.

## Sign-in is required, not silent (your choice)

* **The silent key upgrade is gone.** Computers set up before per-computer keys used to trade the
  shared token for their own key in the background (`POST /api/devices/upgrade`), so they never saw
  a sign-in screen. You asked for the opposite: on a nest that can sign computers in, such a computer
  now stops syncing with "Sign in to your nest" and a person signs it in through the browser. The
  endpoint was removed entirely — the only way to a key is Allow on the nest. Files, folder and
  settings stay untouched while it waits; against an old server without sign-in the shared token
  keeps working, because there is nothing to sign in to.
* **The sign-in screen leads with Google and email** (styled after Claude's sign-in: "Continue with
  Google", or, an email box with "Continue with email"), with "More ways to sign in in your browser"
  for password and passkey. The buttons only show what the nest actually offers; `/api/hello` now
  says which ways are set up. The chosen way rides in the approval link (`&method=google`), and the
  nest's sign-in page starts that way by itself; a `next` path (checked to be a path on this site,
  on the server too) rides through Google's OAuth state and the email link's fragment so the browser
  lands back on the approval page. The email typed in the app is only compared against the owner's
  saved address — the link always goes to the saved address, and the answer never says whether they
  matched, so nothing leaks and no stranger gets mail.
* **The address-and-token first-run screen is gone** (your choice). The first-run window is the
  sign-in screen and nothing else; the "Connect with server address and token instead" link and the
  form behind it were removed from both apps. Consequence: a nest with no public name of its own
  (`install.sh --public-url`) can no longer be connected from the app's first run. The Settings page
  keeps its address and token fields for computers that are already set up. The "server rejected the
  token" notices now say "Sign in to your nest" and open the sign-in window.
* **Reset this app** (Settings → Start over, both apps; your choice of scope). It removes this
  computer from the nest (best effort, as Sign out does), stops syncing, then deletes the settings
  file (not the whole folders: a leftover `Tether\` folder would be taken over again), the saved key
  (new `ISecretProtector.Forget`: Keychain item, `secret-tool clear`, the 600 fallback file; DPAPI's
  key lives in settings.json), the sync notes of every folder (`state.db` in the hash-named folders
  of `LocalDir`), and turns start-at-login off. In the synced folder it removes only `.pairnets-marker`
  and `.pairnets-tmp`, never user files; without the marker the next sign-in is a plain first-sync
  merge instead of a "foreign marker" question. Logs and the single-instance lock are kept (a bug
  report is made from the logs). Reset always ends at the sign-in window with blank settings, so
  nothing is prefilled.
* **`pairnets://` "come back" pipe on macOS/Linux.** The pipe name is now `pairnets-` plus 8 hex of the
  user name's hash (a long user name plus macOS's long temp folder passed the 104-byte socket path
  limit, which made every poke fail and could crash the app on a pairnets:// launch), and the next
  listening instance opens before the current one closes (a poke arriving in the gap was lost).

## Pairnets Cloud version 2: the nest side (one address for everyone)

The design is `cloud/RELAY.md`; these are the nest's and the installer's own choices.

* **Relay mode is two settings, both or neither.** `Sync:RelayNestId` and `Sync:RelayKey` turn it on;
  one without the other, an id that is not `nst_` and 26 base32 characters, or a key that is not
  strict base64url of exactly 32 bytes stops the server at start, with a message like the other
  settings' (the key itself is never repeated). `Sync:RelayServiceUrl` defaults to
  `https://sync.pairnets.app`; https only (plain http only to this machine, for tests).
* **A linked nest has no name of its own, enforced.** RELAY.md says `Sync:PublicUrl` stays unset in
  relay mode; with both set the server refuses to start rather than silently preferring one, and
  `install.sh` removes whichever does not fit (a link removes the own-name lines, `--public-url`
  removes the relay lines).
* **The caller's address in relay mode.** `X-Pairnets-Client-IP` is read before `CF-Connecting-IP`
  and `X-Forwarded-For`, only on loopback connections and only in relay mode. Relay mode also turns
  the address middleware on by itself, even without `Sync:TrustProxyHeaders` (which `install.sh`
  writes anyway): a linked nest is always behind the service's tunnel on this machine, and without
  it one stranger would slow down every computer through the shared failure counter.
* **The "ra1" check.** In RELAY.md's order: the nest id, the signature (43 characters of strict
  base64url, compared in constant time), the time (120 s either way), the nonce (10 minutes, at most
  10,000, oldest dropped first). The time must be plain decimal seconds and the nonce strict
  base64url of 16 bytes; both are checked with the signature, before anything is computed. A nonce
  is only remembered once every other check passed, so nobody without the key can fill the memory
  and push real nonces out. Every failure is the same 401 `{"error":"bad_signature"}`; the log says
  which check failed, never a header's value. The signed path is the request target exactly as it
  arrived (still escaped), which is what the Worker builds from `url.pathname + url.search`. Worked
  examples made with openssl are in `RelaySignatureTests` for the Worker to check against.
* **Nothing under `/api/relay` is open.** `TokenAuthMiddleware` lets the whole path through (no
  computer key is needed or accepted there), a middleware checks the signature of every request under
  it, known route or not, and the endpoint group checks again that it was verified, so no spelling of
  a path can reach an endpoint unchecked. The route test that walks every endpoint now expects 401
  there.
* **Small additions to RELAY.md's answers.** Bodies over 4 KiB are refused before the signature with
  413 `{"error":"too_large"}` (that reveals nothing), and an add request that is not JSON gets 400
  `{"error":"bad_request"}`. These endpoints answer in RELAY.md's `{"error":…}` shape, not the nest's
  `{code, message}`.
* **What the service is told.** `dataBytes` is the size of the current files from the manifest (old
  versions in history/ are not counted; walking the folder would be slow on a big nest). `lastSeen`
  comes from the devices list in memory and is null for a computer that never connected yet. A
  computer added by the service is noted as "approved by <masked email> on sync.pairnets.app" (the
  host of `Sync:RelayServiceUrl`), and named, made unique and adopted exactly like **Allow** on the
  nest's website; removing one goes through the same code as `DELETE /api/devices/{id}`.
* **`/api/hello` of a nest that is not linked is unchanged.** The `relay` field is left out (not
  `null`) when there is none. `/api/pair/start` on a linked nest says to sign in with the account.
* **The installer links on a first install only.** Without options, no `pairnets.env` and nothing
  of Tether's to take over, `install.sh` runs `pairnets-link.sh`; an installed server (it has
  `pairnets.env`) never starts a link, so the self-updater cannot. `--link` (not named in RELAY.md)
  links an installed server again: after it was removed from the account (Remove this nest from my account deletes
  its tunnel), or to move it from its own domain. It refuses `--public-url`, `--bind`,
  `--cloudflare-tunnel` and any port but 5075; `--cloudflare-tunnel` on a linked server is refused
  too, since only the service can make its tunnel.
* **`relay.env` between the helper and `install.sh`.** The helper writes the tunnel token
  (`tunnel.env`) first and `relay.env` last, both root only (600); `install.sh` moves the id and key
  into `pairnets.env` and deletes `relay.env`. A first install that stopped after the link (a failed
  download, say) uses a `relay.env` younger than 50 minutes instead of linking again: the service
  forgets a server that never connected within an hour.
* **The helper waits politely.** It polls at the service's interval, 5 s slower from each
  `slow_down` on (as RFC 8628 says), keeps trying through a missing answer or a 5xx until the code
  expires, and stops with a plain message on Not mine or an expired code, having written nothing. It
  shows only a link on the service itself, checks every value's shape (the key's last character
  included) before writing it, and keeps the device code, token and key off the screen and off
  curl's command line (JSON on standard input, as the names helper does).
* **The installer waits for the relay, but does not fail on it.** After the local health check it
  waits up to 3 minutes for `/n/<id>/api/health` through the service and warns if it does not answer,
  like the own-domain tunnel check: the server itself runs, and a new route can take a little longer.
* **Logs.** `update.sh` and the server's update-log reader also hide `…RelayKey=` values and device
  codes, although `install.sh` never prints them.
* **`InstallHintTests` reads the options of the argument loop.** It used to read the first
  `case "$1" in`, which is `missing_value`'s, so it only knew `--port` and `--public-url`; it now
  starts at the `while` loop (the same fix as on the names branch).

## Pairnets Cloud version 2 in the apps: one address for everyone (shared logic only)

* **Relay mode is read from the address alone.** A computer is "in relay mode" when its server address
  is `https://sync.pairnets.app/n/<nest id>/`: the service's own origin, `/n/`, and a well-formed nest
  id, nothing after it (`Relay` in Pairnets.Core; tests pass another service origin). No new setting
  says so. Every API path and the push channel were already relative to the address, so the
  `/n/<nest id>/` part carries through by itself; typing such an address without `https://` keeps its
  path too (any other path after a typed name is still dropped, as before).
* **The service's own errors are told apart by their body, not by the mode.** The service answers
  `{"error":"nest_offline"}` (503) and `{"error":"nest_unknown"}` (404); a nest's errors are
  `{"code":…}`. The API client checks every 404/502/503 for these two before anything reads a 404 as
  "an old server" or "no such file". `nest_offline` is a network failure ("Your server is not
  connected right now. Check that it is on."): the pass is offline and retried. `nest_unknown` counts
  as signed out ("This server is not linked to Pairnets any more. Sign in again to choose a
  server."): the pass stops with "Sign in again…". Neither is sticky: the next pass that gets through
  syncs as normal. Cloudflare's own error pages in front of the service no longer send people to a
  tunnel they do not have.
* **Signing in with the account** (`AccountSignIn`): the browser link is the service's own (anything
  pointing elsewhere is rebuilt from the service's address and the code), plus `&method=email` or
  `&method=google` for the button pressed. "Slow down" makes the app ask half as often again (at most
  every 30 s). An answer whose server address is not a relay address of this same service for the
  server it names is refused. The account token in the answer is signed out of at once, whatever
  happens next; the app keeps only the computer's own key and the account's email (for showing).
* **Never moves away from a relay address**, even if the server one day names another address.
* **The email is kept while the key is.** Saving Settings keeps the account email only with the same
  key on the same address; a typed token or another address drops it. Signing in on a nest directly
  clears it.

## The apps' sign-in window: a Pairnets account first

Built from the approved pictures (first screen, "Finish in your browser", "Your account has no nest
yet"), the same in both apps.

* **The account comes first, always.** The window opens on "Sign in to Pairnets" (Continue with
  Google, or with email), also on a computer that used its own nest before. "I run my own nest on my
  own domain: use its address" at the bottom leads to the nest's own sign-in, unchanged, with the
  nest this computer used already filled in; "Sign in with a Pairnets account instead" leads back. A
  relay address is never suggested there as a nest's name.
* **The address typed next to "Continue with email" goes along** to the browser page as `&email=`,
  as the nest's own sign-in does, so the page can start with it. The button stays as drawn (not
  dimmed); pressed without an address it says "Type your email address first." instead.
* **"Make an account"** opens the service's sign-in page (`/login`): accounts are made by signing in.
* **Finish in your browser** shows the host it opened (sync.pairnets.app), the code, and the
  countdown under the spinner; a note from the sign-in ("Allowed. Your server is not connected right
  now…", "still trying…") takes the spinner's line while it lasts. The page opens once; "Open the
  page again" reopens it.
* **No nest yet** shows while the account has none (and its nest is not merely offline): the masked
  account (`m•••@gmail.com`), "Show me how" (the account page), and a spinner until the account adds
  a nest, then on to the folder by itself. "Sign out" there stops the sign-in (nothing was kept yet);
  "I run my own nest" stops it too.
* **Turned down, expired or failed** goes back to the first screen with the reason in a note. After
  the 30-minute hold for a nest it says so, not "the code expired".
* **The folder step is the old one**, saying "Signed in as LAPTOP on soro" (the nest's label), and
  saves through `AccountSignIn.SettingsAfterSignIn`, the key protected as before.
* **Colours and type are the apps' own**: the primary buttons use the apps' accent blue rather than
  the near-black of the pictures, and the window opens at 460×660 inside, as drawn (it can be
  resized; the bottom links stay at the bottom).
* **Signed out or reset while the nest could not be told**: in relay mode the note says to remove
  the computer on the account page (sync.pairnets.app/account), not on "your nest's Devices page".
* **Windows screenshots**: the render tool drew everything at 2.25× and cropped it (the bitmap's DPI
  and an extra scale both applied) and lost the logo (it looked for the icon in the tool's own
  files). Both fixed, so the Windows shots match the Mac/Linux ones.

## No ids or keys in the Worker settings file (9 October 2026)

* **The owner asked that no keys show anywhere in the code.** Real secrets were already only in
  Cloudflare's secret store. Now the ids are too: the Google client id and the Turnstile site key
  (public, but account-specific) are set with `setup-secrets.mjs` like the secrets, and the code reads
  them the same way.
* **The database is found by its name** (`pairnets-sync`), so `wrangler.toml` has no
  `database_id`: the first `wrangler deploy` created it, later deploys and `d1 migrations apply`
  look it up by name.
* **First deploy, same day:** `pairnets-router`, then `pairnets-sync` on the custom domain
  `sync.pairnets.app`; migrations 0001 and 0002 applied; the generated keys and the account and
  Google client ids set. The outside keys (Cloudflare API token, Turnstile, Resend, Google client
  secret) wait for the owner to paste them.

## Legal and help pages (10 October 2026)

* **Nine real pages on pairnets.app**: Privacy, Terms, Guidelines, FAQ, Cookie settings, Security,
  Delete my account, Open-source licences and Contact. They are hand-written like the other pages and
  linked from a new three-column footer on every page. `/.well-known/security.txt` points at the
  Security page. Its `Expires` date must be moved on within a year; a test fails when it runs out.
* **Who runs Pairnets**: "MRnigth, a private person in Denmark". The owner chose the GitHub name over
  their full name. Danish law and the GDPR apply, and complaints go to Datatilsynet. One address
  covers help, privacy requests, abuse and security: support@pairnets.app. Users must be 13 or older,
  the Danish age for agreeing on your own.
* **The texts only say what the code does.** The pages state:
  * what sync.pairnets.app stores and for how long (taken from the D1 schema and the hourly sweep);
  * that IPs are never stored raw;
  * that files pass through and are never kept;
  * that HTTPS is unwrapped at Cloudflare, and there is no end-to-end encryption yet;
  * that the service itself can let a computer in;
  * that database backups (D1 Time Travel) keep deleted rows for up to 30 days.

  A change to what the service stores needs the Privacy Policy and Cookie settings pages changed with
  it.
* **Google sign-in**: the Privacy Policy carries Google's "Limited Use" statement and is linked from
  the home page. Google's Branding page needs both before the app can leave Testing.
* **The API**: the Terms say the `/v1/` API and the relay are made for the Pairnets apps, nests and
  installer. People may use them with their own tools, for their own account and within the limits,
  and they may change without notice.
  * sync.pairnets.app answers `/privacy`, `/terms`, `/guidelines`, `/cookies`, `/security`, `/faq`,
    `/help`, `/contact`, `/delete-account` and `/licenses` with a redirect to the website.
  * Every page there links them.
  * The sign-in page says that continuing means agreeing to the Terms and the Privacy Policy.
  * Every email ends with a Privacy link.
* **Cookie banner**: only necessary cookies are used, so the law asks for no consent. The owner still
  wanted a banner. Both sites show a small notice ("only cookies that are needed to sign you in")
  with an OK button and a Cookie settings link. OK is remembered in the browser's local storage
  (`pn-cookie-notice`), never in a cookie. The Cookie settings page shows the three kinds with their
  switches: Necessary is always on; Analytics and Advertising are not used.
  * Since 19 October 2026 pairnets.app has no notice: it sets no cookies at all, and the notice covered about
    a sixth of a phone screen on the first visit. The footer's Cookie settings link stays. sync.pairnets.app,
    which does set the sign-in cookies, keeps its notice.
* **In the apps** (both, page for page):
  * The sign-in screen's first step has the same small print, with Terms and Privacy Policy links.
  * Settings ends with an "About Pairnets" card: version, licence, who makes it, and links to the
    website's help and legal pages.
  * The addresses are fixed in `PairnetsLinks` (Core) and do not follow the account service's
    address.
  * The nest's own sign-in page links Privacy and Terms too.
* **Not a lawyer's work**: the texts are standard and careful, but no lawyer has read them.

## A username, and pairnets.app shows who is signed in (10 October 2026)

* **What the owner asked for**: pairnets.app's top bar should not always say "Sign in". When you are signed in it
  shows a round letter, your username and an "Account →" button (on a phone only the letter and the button).
  The username is yours to set and change on the account page.
* **The username is a display name**, not a login: 1 to 32 characters, not unique, never used to sign in. Empty means
  not set, and then the part of the email before the @ is shown. Stored in `accounts.username` (migration 0003).
  `PATCH /v1/me {username}` sets it; the audit log records that it changed, not the name.
* **How the website knows**: the sign-in cookie belongs to sync.pairnets.app (`__Host-`, so no other site can read
  it). pairnets.app's script asks `GET https://sync.pairnets.app/v1/signed-in` with the browser's credentials.
  Both are the same site, so the SameSite=Lax cookie goes along, and the answer is only `{signedIn, name}`. It is the
  one route with CORS, and only for `https://pairnets.app` and `https://www.pairnets.app`. Every change still needs
  the account page's own Origin. The website sets no new cookie and stores nothing; its CSP allows `connect-src`
  to sync.pairnets.app only. If the check fails (offline, blocked), the page keeps "Sign in".
* **Not a hint cookie on `.pairnets.app`**: one would be simpler but goes stale (a session signed out from another
  browser would still show as signed in), and it would be a new cookie for the cookie page.
* **Updates show at once**: after the first deploy the owner saw the new Home button with the old styles, because
  browsers keep `/assets/*` for an hour. sync.pairnets.app now links every stylesheet and script as
  `/assets/<name>?v=<fingerprint of its text>`, so a changed file has a new address. pairnets.app (plain files, no
  build step) has its `/assets/*` checked on every visit instead (`max-age=0, must-revalidate`; unchanged files get a 304).

## Donations: "Support Pairnets ♥" through Ko-fi (11 October 2026)

* **What the owner asked for**: a "buy me a coffee" link so people who like Pairnets can help keep it running.
  It shows in four places: a "Like Pairnets?" box at the bottom of the home page, a "Support Pairnets ♥" link in
  every page's footer, the same link first under Settings → About Pairnets in both apps, and GitHub's Sponsor button
  (`.github/FUNDING.yml`, which GitHub only reads from the default branch).
* **Ko-fi, not Buy Me a Coffee**: Ko-fi takes 0% of one-off donations (only PayPal's or Stripe's card fee); Buy Me a
  Coffee takes 5%. The owner makes the Ko-fi page themselves.
* **Everything links `https://pairnets.app/donate`**, and only `site/_redirects` names the Ko-fi page. Switching
  services (or the page's name) is a website deploy, never an app update. `SiteTests` checks that the footer, the home
  page, `PairnetsLinks.Donate` and `FUNDING.yml` all agree.
* **No Ko-fi widget or button image**: the site's CSP allows no outside scripts, frames or images, so the coffee cup is
  an inline SVG drawn in the site's own colours.
* **Nothing is paywalled**: the Terms say a donation is a gift that buys no features, support or promise that the
  service stays up. The Privacy Policy says what Ko-fi shows the owner (name, amount, message), that it is used only to
  say thank you and for tax records, and that it is never linked to a Pairnets account.

## Faster uploads, and Resume that carries on (10 October 2026)

What the owner saw on a test setup (two computers and a nest on one PC, all through sync.pairnets.app): uploads
were slow, and after Pause → Resume the app said only "Syncing" for many minutes. The logs showed why.

* **The relay allowed 600 requests a minute per address.** Computers in one home share an address, and every
  file synced between them costs a request on each (one uploads it, the other downloads it), so a home got about
  5 small files a second, and then a wall of "429 Too many requests". The relay now allows **6,000 a minute**,
  counted by Cloudflare's Rate Limiting binding instead of a D1 row per request (no database round trip on
  every file), and it reuses each nest's row for 30 s instead of reading it on every request. So a nest that is
  unlinked keeps being relayed for at most 30 s. A limiter that fails lets the request through: the limit only
  stops floods.
* **A 429 means "wait", not "this file failed".** The app holds back every request (all transfers, not only the
  one that was refused) for the time the answer's `Retry-After` asks (at least 1 s, at most 2 minutes) and then
  sends the same files again; nothing is counted as an error and a big upload goes on where it was, with its
  piece size unchanged (nothing was cut). Only a 429 that says how long counts: a nest's own 429s (too many
  sign-ins, an update asked for a moment ago) mean something else. After 30 refusals in a row for one file the
  pass ends as if offline. Considered and left out: halving the number of transfers after a 429. Under a limit
  per minute that only moves the same number of requests around; the wait is what stops the hammering.
* **Two pieces of a big upload on the way at once.** Cloudflare takes in a whole piece before it passes it on.
  With one piece at a time the line to Cloudflare and the line to the nest took turns being idle: the nest's log
  showed 4–6 s without data between pieces, and a 548 MB file arrived at about 4 MiB/s although each piece
  moved at 9–11 MiB/s. Now the next piece goes out as soon as the previous one's bytes are sent, marked
  `turn=wait`; the nest keeps it waiting (without reading it) until the one before has arrived, up to 60 s,
  and refuses it at once (wrong offset) when the one before was cut off, so the app asks how much arrived and
  goes on from there. Never more than two, because the nest writes and hashes a file in order and only one
  piece can be arriving at a time anyway. A nest says it takes this in `/api/info` (`piecesInFlight: 2`);
  with an older nest the app sends one piece at a time as before. Pieces stay at most 50 MiB.
* **Looking through the folder keeps its work.** New and changed files are read whole once to fingerprint them
  (needed to tell what changed, see "three hashes"). The scan used to save those fingerprints only at the very
  end, so every Pause, restart or session restart threw them away: on a folder with 116 GB of new model files
  each Resume started reading all of it again and never got through. Now they are saved every 250 files or 2 s
  and when the scan is stopped, so the next one reads only what is left.
* **"Checking files…" says how far it is.** The scan reports files checked, how many were new or changed, and
  the big file being read with its bytes ("12,400 files checked, 310 new or changed · reading model.gguf (1.20
  GB of 9.00 GB)"), in the status card and the tray tooltip of both apps (`StatusSnapshot.Scan`, shown by the
  existing headline and detail lines, so the two apps stay alike). Files under 16 MB go by too fast to name.
* **Pause and Resume show the right thing.** Status changes go one at a time, and while paused only "Paused"
  is shown, and "Paused" only while paused. Before, a pass that was still stopping could show "Paused" after
  Resume, or "Syncing" after Pause. A saved pause is now applied before the runner starts, so a paused app never
  begins a pass at launch.
* **The speed falls to zero when nothing moves.** It was only worked out when bytes arrived, so the last value
  stayed on screen through every wait. Now it is worked out again every second. The units stay as they were
  (1024-based "MB/s").

## Sync feedback: real speeds, saying what it does, "too many requests" (10 October 2026)

The owner's first real use (PC-1 on Windows and PC-2 on Linux behind one home address, a server, all through
sync.pairnets.app) showed three problems: about 37 of PC-1's 38,206 uploads failed with 429, the app said only
"Syncing" for 8 minutes while it fingerprinted 114 GB, and the picture *guessed* that the other computer was sending.

### The live report: one message for every computer's live state

* **Message.** App → server `ReportTransfer(TransferReport)`; server → the other apps `PeerTransfer(name,
  TransferReport)` (names in `PushNames`, Core `Models.cs`). `TransferReport` is
  `(upBytesPerSecond, downBytesPerSecond, filesDone, filesTotal, bytesDone, bytesTotal, uploading, downloading,
  stalled, ageSeconds)`:
  * speeds: bytes really moved in each direction over the last 5 s, measured by the app that moves them;
  * files and bytes done of its current batch (back-to-back passes count as one batch, as on its own screen);
  * `uploading` / `downloading`: files in flight in that direction now, true also while stalled, so a stalled line
    still knows its direction;
  * `stalled`: files in flight but no byte for 5 s, or held by a 429;
  * `ageSeconds`: set by the server only; 0 when pushed live, the report's age when replayed to an app that connects
    later. "Last seen" = arrival time minus this (no clock skew between computers matters).
* **Cadence.** About once a second while the app transfers, and once all zero when it stops. The server passes on at
  most 5 a second per connection (a change between moving and not moving always goes through), keeps only the latest
  per connection in memory (`LiveTransfers`), replays the ones from the last 10 s to a new connection, and sends an
  all-zero report for a computer that disconnects while moving files. Apps drop a report after 10 s without a newer one.
* **One source.** `StatusSnapshot.LiveOf(name)` gives the same thing for this computer (its own numbers) and for the
  others (their reports); the picture (`DeviceMap`), the Devices page and the "waiting for PC-1" progress all read it.
  The 8-second "a change arrived, so it is sending" guess and "this one uploads, so the other receives" are gone: a
  line moves only for a computer that reports moving bytes. The animation work (`feature/live-transfers`) can draw
  direction, speed and stalled from this report without a second message.
* **Why the apps report and the server does not count.** The app knows what the server cannot: files left in its
  batch, that it is held by a 429, which direction is stalled. The server could measure bytes on `GET`/`PUT
  /api/file`, which would add only computers running an app too old to report; those show no speed rather than a
  wrong one, and the apps update themselves. It also keeps the file-transfer path untouched. If wanted later, the
  server can fill in a report for a computer that has not sent one for 10 s, on the same `PeerTransfer` message.
* **Old servers and apps.** A server without `ReportTransfer` answers the call with an error; the app then stops
  sending on that connection (tries again after a reconnect) and shows no live data, with no error anywhere. An old
  app ignores `PeerTransfer` (SignalR drops unknown messages).

### Saying what it does

* Priority of the status: Paused, then "Slowed down by the server", then "Waiting for PC-1 to finish uploading",
  then the pass's stage: "Reading the server's list", "Checking files · 12,000 of 38,206", "Uploading" / "Downloading"
  / "Uploading and downloading" with "37 of 120 files · 4.90 MB/s · about 3 min left". Everything else keeps its old
  words. `StatusLine` is the same in one line for the tray icon's tip and menu, in both apps.
* **Checking lists the folder first** (a second or two for 38,000 files), so the total is known before any file is
  read. Fingerprints are saved every 500 files or 5 s and when the check is cancelled; before, a pause threw away
  everything read so far, so the check after Resume started again from zero.
* **While checking, the wait for another computer's batch is not shown**: the status says what this computer does.
* **Resume says "Syncing…"** at once (it used to say "Up to date" until the pass began).
* **Motion was left alone** (the badge, the dots): the live-transfers work owns it. While "slowed down" the badge
  still turns; the snapshot says `Stalled` and `IsSlowedDown` for that work to show. The one exception: this
  computer's line stops while requests are held, because no bytes move.

### "Too many requests" is back-pressure

* **In the apps.** One gate per client (`BackPressure`): a 429 holds every request until its `Retry-After` (capped at
  10 minutes; without one 5 s, doubling to a minute while 429s go on), the same request is sent again (a single
  upload from the start of the file with a fresh hash, a piece from its own start), and then one request runs at a
  time, doubling every 15 s without another 429 until there is no limit. A request's own timeout does not run while it
  waits. Nothing counts as a failed file. After 30 refusals of one request the pass ends as offline ("Too many
  requests to …; Pairnets keeps trying") and the runner retries with its usual backoff. The nest's own 429s on
  joining and on "update the server" keep their meaning; only the service's `rate_limited` is waited out there.
* **In the Worker (relay).** Two budgets instead of one 600-a-minute per address:
  * **per address, 600 a minute** (the old number) for requests without a computer's key, and for everything that
    fails: unknown nest, refused path, a key the nest answers 401/403, an offline nest. Checked first, before the
    nest is looked up. This is the abuse guard: a caller without a working key never gets more than this.
  * **per nest and computer key, 6,000 a minute (100 a second)**: what a signed-in app sends. Two computers at home
    each have their own. A made-up key costs one request to the nest, gets 401, and counts against the address.
  * **Why 6,000.** A small-file upload is one request. With 4 transfers at once and 40–150 ms per request through
    the relay, one computer makes about 1,600–6,000 a minute; the owner's PC-1 hit 600 after 383 files. 6,000 covers
    a fast connection with 4 at once; 8 at once on a very fast link can touch it, and then the app slows down for a
    moment instead of failing. Each relayed request still costs one D1 write (the counter), as before; the per-address
    check adds one read.
  * **Owner's call:** the two numbers (600 per address, 6,000 per computer). Higher per computer is cheap to allow; a
    lower one would make big first syncs slow down more often.

## Animations show what really moves (10 October 2026)

* **The owner asked for it:** the status badge used to spin the same way for every kind of work, and the
  dots on the overview's lines kept one pace even when nothing moved. The owner approved a moving preview
  first.
* **The badge follows `StatusSnapshot.Motion`.** A slow turn while checking. An arrow rising on green while
  uploading, or falling on blue while downloading. Both arrows taking turns when it does both. A still,
  faded arrow on orange, "Waiting for the network", once no byte has moved for 5 seconds (`StallAfter`).
* **The dots go as fast as the bytes.** Speed is measured each way over the last 3 seconds
  (`UploadBytesPerSecond`, `DownloadBytesPerSecond`), mapped to a dot speed with a log curve and a cap
  (`DeviceMap.DotSpeed`). An upload or download keeps counting for 1.5 s after it ends, so the picture
  doesn't flicker between small files.
* **No more guessing about the other computer.** It used to "download" whenever this one uploaded.
  * The server now counts the bytes of each computer's file requests (`TransferMeter`) and sends
    `PeerTransfer(device, up, down, uploading, downloading)` once a second while it moves files, then once
    with both flags off. The other computer's line uses only that (or, from an older server, the changes
    heard from it).
  * After a sync with nothing left over, an app calls `Synced(version)`; the others get
    `PeerSynced(device, version)`. When that version covers this computer's latest change, the other
    computer gets a check mark, "Has your changes", for a minute.
  * Old apps and old servers keep working; they just show less.
* **MB/s on the lines** is on to begin with, and can be turned off in Settings ("Show speed on the map").
  It never shows in the small tray picture.

## One release from the waiting branches (11 October 2026)

Sync feedback, live transfers and faster uploads were built side by side and each solved part of the same thing. The
owner chose "both go ahead, merge at the end". This is how they fit together.

* **One source for every computer's speed: its own report.** Each app sends `ReportTransfer(report)` about once a
  second while it moves files, and the others get `PeerTransfer(name, report)` (sync feedback). The server's own byte
  counter from live transfers (`TransferMeter`) is gone: two sources for one number would disagree, and the app's report
  says more (files left, stalled, what it is doing) and travels the same way through sync.pairnets.app.
* **Kept from live transfers:** `Synced(version)` / `PeerSynced(name, version)` and the "Has your changes" check mark,
  the badge's motion, the moving dots, the MB/s on the lines and its switch. The other computer's entry
  (`StatusSnapshot.Peers`) holds both its latest report and what it has synced (`PeerActivity`); a report that goes
  quiet for 10 s is dropped, what it has synced stays.
* **A computer's box says what it does, its line how fast:** "Downloading your changes · 1,204 files left" under the
  name, "↓ 4.20 MB/s" on the line. The Devices page, which has no lines, keeps the speed in its text.
* **One tick a second in the app** (instead of three timers) brings the speeds, "moving" (with its 1.5 s grace) and
  "stalled" up to date, forgets quiet reports and sends this computer's own. The speeds each way are measured over 3 s
  for the map and the report; both together over 5 s for the time left.
* **Stalled** is files in flight with no byte for 5 s, or requests held by "too many requests". `Stalled` and
  `StalledSince` are set together; the badge then shows the still orange arrow, but the title still says "Slowed down by
  the server" when that is the reason, and "Waiting for the network" otherwise.
* **Words in the title** follow sync feedback ("Checking files", "Reading the server's list", "Uploading",
  "Waiting for PC-1 to finish uploading"), plus "Waiting for the network" and "Deleting files" from live transfers.
* **Checking the folder** lists it first, so there is a total from the start (sync feedback), and saves what it read
  every 250 files or 2 s and when stopped (faster uploads). The status shows both the count and the big file being read:
  "12,400 of 38,206 files checked, 310 new or changed · reading model.gguf (1.20 GB of 9.00 GB)".
* **"Too many requests"** is handled in one place for every request (`BackPressure`: hold everything, send the refused
  request again, then one at a time for a while). The pieces of an upload that sends two at once are the exception:
  the next piece may already be reading the file, so the sender never sends a piece again by itself. It holds every
  other request and reports the slow-down; the upload then asks the server how much it has and goes on from there.
* **The relay's limits** are three of Cloudflare's rate limiting bindings, so a relayed request costs no database
  round trip: 20,000 a minute per address (a flood ceiling), 6,000 a minute per computer key, and 600 a minute per
  address for requests without a key and for failures. A binding cannot be read without counting, so the request that
  goes over the 600 turns the address off for 60 s, remembered by the Worker instance. The Worker tests use a counting
  stand-in per test, because the test runtime's own binding counts by the wall clock and keeps its counts between tests.
* **A website-only push to main is not an app update:** release.yml ignores `site/**` as it already ignored `cloud/**`.

## The "Sign in an app?" page (10 October 2026)

The page where you press Allow so a computer can sign in (sync.pairnets.app/app) now has the apps' "Quiet" look. The
owner picked option A from three designed options.
* **One calm column, and the code is the question**: "Does Pairnets on PC-1 show this code?", with the code in big
  letters. System, app version and times are folded under "Details". The buttons say "Yes, allow PC-1" and "No, this
  isn't me".
* **Every answer has a screen of its own** (signed in, waiting for your nest, not let in, code ran out) instead of a
  line under greyed-out buttons. A reload draws the answered screen and says which answer was given and when.
* **Errors can be retried**: the error is said in plain words and both buttons work again.
* **The look is scoped to this page** (`app.css` on `<body class="q-page">`); the other pages keep theirs. Its
  colours are the apps' Quiet palette and follow the system's light or dark setting.
* **Instrument Sans comes from the Worker itself**: Latin-only WOFF2 files (about 15 KB each, regular, medium and
  semibold) under `/assets/fonts/`, with their copyright and licence kept inside. That needed one addition to the
  CSP, `font-src 'self'`: fonts from this site only, nothing from elsewhere.
* **Bringing the app to the front**: after Allow, the page opens `pairnets://signed-in` by itself only when the
  browser looks like it runs on the computer that asked: not a phone or tablet, and the same system (Windows, macOS
  or Linux, as the app reports). The link carries nothing, so a wrong guess exposes nothing; it only spares people an
  error on a phone. Everyone else gets an "Open Pairnets" button. A true same-computer check would mean storing
  something about the asking computer's address, so it was left out.
* **The cookie notice** sits in the page's flow under the content on this page, so it can never cover the buttons.
* Nothing about the security changed: the session requirement, the Origin check, the code check and the rate limits
  are the same.

## The nest's emails

* **Three emails, one look.** The sign-in link, the "confirm your email" mail and a new **welcome** mail
  share one layout that matches the website (grey page, white rounded card, the logo, one blue button;
  light and dark). They are plain files in `src/Pairnets.Server/Mail/Templates/` (an HTML and a text
  version of each, plus `layout.html`), built into the program like the nest's website, so changing
  the words needs no code. `Mail/EmailTemplates.cs` fills in the blanks; everything inserted is
  HTML-escaped and a blank with no value is an error, not an empty space. Previews:
  `docs/images/email-*.png` (made with `PAIRNETS_EMAIL_PREVIEW_DIR`, see `EmailTemplatesTests`).
* **The logo comes from the nest itself** (`/assets/icon-256.png`, a PNG because mail programs do not
  show SVG), so opening a mail never loads anything from pairnets.app or another site. With images
  blocked the mail still reads fine: the name "Pairnets" is text.
* **Subjects and the link sentence are unchanged** ("Your Pairnets sign-in link", "Confirm your email for
  Pairnets", "It works once, for 15 minutes"), and the link still carries its code in the `#` part, so
  nothing that reads these mails (tests, filters, people's habits) breaks.
* **The welcome is sent once, when an address is first confirmed** (a different address counts as new;
  confirming the same one again, or signing in by email link, sends nothing). It is best effort: if the
  mail server refuses, the sign-in still works and only a log line says so (no address or link in it).
  It is not counted in the "5 sign-in emails an hour" limit, because nobody can ask for it: it only
  follows a confirm link that was opened from the inbox.

## Secrets audit (2026-10-11)

* **Everything on GitHub was checked for keys and passwords, and none were found.** The check covered every
  branch and tag, every commit and message, and the old commits GitHub still keeps from earlier history rewrites.
  It looked for the key formats above, the owner's real VPN addresses, names and paths, and the real keys saved on
  the owner's PC. Releases, workflows and screenshots were checked too.
* **Every push is now scanned before it is public**, not only by CI afterwards. The `pre-push` hook scans exactly
  the commits a push sends (`check-secrets.sh --range <sha> --not --remotes`) on every branch. It uses a copy of the
  scanner installed next to the gate, so branches made before `--range` existed are covered too.
* **Personal details stay out of the public scanner.** A real name, email address or machine name can't be listed
  in a public file. The scanner reads them from `.git/info/secret-denylist`, which never leaves the PC, and applies
  them to new work only (`--diff`, `--range`). Old commits keep what they had, so `--tree` and `--history` don't use
  that list, and the pre-deploy check of an older branch can still pass.
* **Made-up names in test data.** A test used the owner's first name for a laptop; it now says "Alex". The old
  commits were left as they are rather than rewriting main.

## New look, stage 3: the Overview and the nest picture (2026-10-14)

* **One model draws the page in both apps.** `OverviewModel` (Core) holds every word, button and card of the nine states;
  `NestMapModel` holds the picture of the computers. The two apps only draw them (`OverviewView`, `HubView`), so a change
  to the words is made once and tested once.
* **The old `DeviceMapView` stays for the tray panel.** The Overview uses the new `HubView`; the tray panel is another
  helper's job and still draws the old small picture until it moves over.
* **Only what the app knows is shown.** The design shows a list of what a stopped sync would delete, how many changes wait
  while offline, and what another computer is about to send. The status now carries `PendingDeletePaths` (the first
  eight files) and `PendingChanges`. The offline "Next try" tile says "In a few seconds" because the retry time is not
  in the status, and "Coming next from LAPTOP" and "Waiting to go up" show "Latest changes" instead, because the engine
  has no such lists.
* **Folder rows are open or closed with a click.** The first folder starts open; "Moving now" lists the files that move
  now (or the first waiting ones when nothing moves yet).
* **More than five other computers:** the picture draws four and a "+N more" link that opens Devices.
* **Motion:** active lines carry a few light dashes that travel along them. They stop when Windows is set to fewer
  animations. The Mac/Linux app has no such setting yet, so its lines always move while the window is visible.
* **Tests that checked the old status card** now check the new title ("Uploading 3 files to your nest", "Stopped to keep
  your files safe") and the picture's direction instead of the old badge glyph; the intent (the page says what it does and
  moves the right way) is the same.

## Visual design: New look (replaces Quiet) (2026-10-14)

The apps, the sign-in and bug-report windows, the tray panel and the nest's sign-in page now share one look: soft
blue and white (and a dark twin), rounded cards, a left side with your computers and the pages, and the Plus Jakarta
Sans font. It replaces the earlier "Quiet" look (the `feature/quiet-ui` branch, never released); the pieces of Quiet
that were still useful (account info, notice settings, activity groups) were ported. Stage 3 above records how the
Overview and the nest picture are built; this entry records what was left out and why.

* **Left out because the app has no data for it.** The design draws more than the app knows. Rather than show made-up
  numbers, these are not built, and each comes back when the engine or the nest can supply it:
  * **Needs you: Deny / Allow buttons and the two-copy chooser** (keep this one, keep that one, keep both). The app can
    only list the conflict copies and the deletion it is waiting on; **Show in folder** and the existing decision
    buttons stay.
  * **History: the "On" column and the version counts.** The nest returns a file's versions only after you pick the
    file, so a list of every file cannot say how many versions each has or which computers hold it.
  * **Two of the design's notification switches.** Account has the three the app really acts on (needs you, a
    computer asks to join, an app update is ready). The other two have nothing behind them.
  * **The Overview's offline countdown** ("next try in 12 s") and the **Waiting lists** ("Coming next from LAPTOP",
    "Waiting to go up"). Stage 3 covers the wording that replaces them.
* **No reduced-motion setting on Mac and Linux.** WPF reads Windows' "fewer animations". Avalonia has no such setting
  to read, so the travelling dashes always move while the window is visible there.
* **A computer's icon is stored on the nest, so the nest needs the update.** The choice (desktop, laptop, office PC,
  mini PC, TV PC) is saved per computer on the nest and shown on every computer. A nest from before this change does
  not send an icon: the pencil is hidden and the icon is guessed from the name (LAPTOP or BOOK gives a laptop).
* **Plus Jakarta Sans, under the SIL Open Font License 1.1.** The font files and their licence text are in
  `assets/fonts` (`PlusJakartaSans-OFL.txt`, kept under the font's own name next to the files it covers) and are built
  into both apps, so no machine needs the font installed and nothing is fetched from the internet. The font is listed
  with the other open-source licences on `pairnets.app/licenses` (the page both apps link under Settings → About).
  The licence allows shipping the font with software as long as the licence text goes with it and the font is not
  sold on its own.
* **Screenshots.** `docs/images` was regenerated with both tools. The documentation uses the Avalonia set
  (`main-window-*`, `sign-in-*`, `server-update-*`, `tray-panel-*`, `bug-report-*`, and `stage4-*` for Account,
  Devices, Activity and Needs you), because it runs on every system and the pages match the WPF ones page for page.
  The WPF tool writes the same screens as `windows-*` files; those are not kept in the repository, to keep it small.

## Audit fixes (2026-10-14)

An audit against ten outside guides (security, legal, polish, design, pre-ship) found four High issues; three are
fixed here, the fourth (who runs the service, by legal name and address) is the owner's call and is unchanged.

* **The root updater never touches the server user's folder as root.** Every write, rename and log append in
  `/var/lib/pairnets/update` runs as `pairnets` (`runuser`), so a symlink planted there reaches nothing that user
  couldn't already reach. `tests/linux-server/update-symlink-check.sh` proves it and runs in the pre-deploy check.
* **Allowing a computer needs a sign-in from the last 15 minutes**, as do approving a server and removing a computer
  (saying "no" doesn't). The Allow page shows the country and city Cloudflare saw for the request, and the account
  gets a notice email for every allowed computer. Names lose hidden and right-to-left characters. Migration 0004
  adds the two place columns and must be applied before the Worker is deployed.
* **The relay passes only the headers the apps read**, and a `Location` only when it stays inside the same
  `/n/<id>/`. The nest's website isn't reachable through the relay, and its downloads set no
  `Content-Disposition`, so nothing else is needed.
* **Per-address limits group an IPv6 /64.** `/assets` doesn't count toward the 300-a-minute limit, and a full
  bucket stops writing. 15 of the day's 95 emails are kept for security notices; per-address email limits count only
  emails actually sent.
* **One friendly error mapping** (`FriendlyErrors` in Core) for everything the apps show; type names, status codes
  and parser text go to the log only. Join requests are cleaned the same way (`PlainText.Clean`), and the nest sends
  a new `PairRequestedFrom` with the address alongside the old `PairRequested`, so old apps keep working.
* **App windows:** keyboard-only focus rings on the custom controls, one sign-in window at a time, Escape asks
  before stopping a sign-in that is under way (the Mac/Linux window gained Escape to match), the saved email is
  filled in, and Sign out can't run twice.
* **Website:** Sign in goes to `sync.pairnets.app/account` (it used to point at the owner's own nest). Pages say
  what is true today; Google, GitHub and Ko-fi are listed as independent controllers; muted text and links meet
  contrast. `/get.sh` now points at the copy in the latest release, so it answers 404 until the next release
  includes it: release before deploying the site.

## Relay budget per nest, nest limit, unused accounts (2026-10-17)

The owner's answers to the 2026-10-14 audit questions:

* **A relay budget per nest of 30,000 requests a minute** (`RELAY_NEST_LIMIT`), about five computers syncing flat out.
  Requests, not bytes: Cloudflare bills a Worker by requests, and bytes passing through cost nothing, so a request
  budget is the one that protects the bill. The apps already wait out a 429 (`BackPressure`).
* **3 nests per account**, as before, now `MAX_NESTS_PER_ACCOUNT` in wrangler.toml. Nests an account already has are
  never removed; only a new one is refused.
* **Accounts unused for a year are deleted, 30 days after a warning email** (`src/inactive.ts`). A nest that is online,
  or a computer that uses the account, counts as use, so nobody whose setup is running loses it. No warning that went
  out, no delete. The daily cap of 20 warnings keeps them from using up the emails sign-ins need.

## Signed releases (2026-10-17)

* **What is signed:** `SHA256SUMS.txt` only, with ECDSA P-256 / SHA-256, as the DER signature `openssl dgst -sign`
  writes (`SHA256SUMS.txt.sig`). It lists every release file, `version.json` included, so one signature covers the
  installers and the version number: an old signed release can't be passed off as a newer one.
* **One public key, four places:** `deploy/release-signing.pub` (built into Pairnets.Core) and copies in
  `deploy/update.sh` and `deploy/get.sh`, which must work without the repository. A test checks the copies match
  and that the apps accept a signature openssl made with the real key.
* **Fail closed.** No secret, or a secret that doesn't match the public key: `scripts/sign-release.sh` fails and
  nothing is published. No or a bad signature: the apps show "isn't signed by Pairnets" and offer only Hide (never
  the download page), update.sh changes nothing, get.sh stops.
* **The switch-over needs nothing special.** Apps and servers up to 1.0.56 don't check, so they update to the first
  signed release as before; from then on they check. `upgrade-check.sh` installs the published release with that
  release's own `get.sh` (where pairnets.app/get.sh points), because main's copy checks a signature that a release
  from before the switch doesn't have.
* **Tests use throwaway keys** (`TestReleaseKey`, `make-feed.sh`'s `feed-signing.key`), handed to the scripts as
  `PAIRNETS_RELEASE_KEY`. That override is an environment variable of a root service or of the person running
  get.sh, the same trust as the existing `PAIRNETS_BASE_URL`.
* **A lost or leaked key** means a new pair, a new secret and a release with the new public key, signed with the
  old key, so installed apps still take it.

## Signing in again with Google (2026-10-17)

* **A fresh Google round trip is the re-check; Google's login time is not.** Allowing a computer, approving a
  server, removing a nest and deleting the account need a sign-in from the last 15 minutes. The Google button then
  sends `max_age=0`, but Google cannot be made to ask for the password again: someone already signed in to Google
  gets the account picker and an `id_token` carrying the old `auth_time`, or none. The Worker used to refuse that
  ("Google sign-in did not work. Try again."), so signing in again with Google never worked. Now the round trip
  itself counts: new state, nonce and PKCE tied to this browser, the account picker, and a newly signed token.
  A stolen site cookie alone still can't pass it, and every allowed computer still sends a notice email. `max_age=0`
  stays as a hint. `prompt=login` was not used: Google doesn't support it.

## Small fixes from the 1.0.55 test (2026-10-17)

* **Narrow windows fit by changing the layout, not by opening wider.** The Mac/Linux window opens at 980 px and the
  Windows one can be made 940 px wide; both left the page about 600 px, where texts were cut off or drawn over each
  other. The window sizes stay; the pages adapt, the same way in both apps (`PageLayout` in Core): the computer cards
  go 3, 2 or 1 across, the Activity search box moves under the filter buttons when they would not fit beside it, the
  Overview's fact links ("Check for update", "Open") sit under their value, and the side's "Other computers" heading
  is never cut (its note is, with "…").
* **The nest picture shrinks only as a last resort.** It is a drawing with fixed parts. This computer's line always
  fits its speed pill or word; the others' side gives up its spare room next, down to what their names need; only
  then is the whole picture shrunk evenly (about 88 % at 980 px). Wide windows draw it exactly as before.
* **Signed out on purpose is grey, not red.** After this computer's own Sign out the apps used to draw "no session"
  as offline: a red "Can't reach it · trying again". Now a separate state (`BlockReason.SignedOutByYou`) says
  "Signed out" in grey with one "Sign in…" button. A computer removed elsewhere still gets the red "This computer was
  removed" wording, since that one may not have been the person's doing.
* **Screen-reader names come from the button's own text.** WPF names a button only when its content is plain text,
  so 33 buttons with an icon and a text had none. Rather than naming each by hand, `ScreenReaderNames` names every
  button that has no name of its own after its first text (kept in step: Pause → Resume), or its tooltip. A sweep test
  checks every button of the main window and the tray panel in both apps.

## Waiting for another computer's batch sees bytes, not only files (2026-10-17)

* **A computer waiting for another's big batch now counts bytes going up as a sign of life.** It used to count only
  finished files, so one file that took longer than the quiet time (2 minutes; a big video on a slow upload) ended the
  wait, and the rest of the batch came in pieces. Now the sender repeats its "uploading a batch" message every 15 s while
  its bytes move, and live speed reports with bytes going up count too. A stuck sender moves nothing, repeats nothing,
  and the others stop waiting as before; the 30-minute limit and "Download now anyway" stay.
* **The repeat comes from the sync engine, not the app window**, so it works on every computer, with or without its
  window open. Keep `BatchReannounceInterval` well under the others' `PeerQuietTimeout`.

## Every app copy updates itself, and never sends you to the browser without saying why (2026-10-19)

* **Found from a 1.0.52 that opened the release page instead of updating.** The release was fine (an installed 1.0.52
  still updates itself to the signed releases: it reads `version.json` and `SHA256SUMS.txt` and ignores the `.sig`). The
  copy was a `Pairnets.exe` run from its own folder, not the install folder, and the Windows app only updated itself when
  `PairnetsSetup.exe` had put it there; the Mac/Linux app never updated itself. Both opened the download page silently.
* **A copy no installer looks after now swaps its own file** (`SelfReplace`): Windows from `pairnets-client-win-x64.zip`,
  Linux from `pairnets-desktop-linux-x64.tar.gz`. The package is checked against the signed `SHA256SUMS.txt` like the
  installer; the running file is renamed to `.old` (both systems allow that), the new one takes its name, the app starts
  it with `--after-update` and quits; the new app waits up to 30 s for the old one to let go of the single-instance lock
  and removes `.old`. Any failure puts the old file back. Installed Windows copies keep using `PairnetsSetup.exe`.
* **Mac stays by hand for now** (swapping an app bundle can't be tested here), but the card says so before its
  **Open download page** button. Everywhere else a failure shows the reason on the card with **Try again**; nothing opens
  the browser on its own.
* **Old apps must keep updating.** The release keeps every file name and format an older app or server reads
  (`version.json`, plain `SHA256SUMS.txt`, `PairnetsSetup.exe`, the zip, the Linux and server `.tar.gz`); `OldUpdaterCompatTests`
  runs a frozen copy of the 1.0.52 updater against a release laid out like the workflow's and checks the workflow still
  publishes those names. Copies on 1.0.59 or older that ran from the zip or on Linux need one update by hand.
* **Shipped as a quick fix, 1.0.59.1 (tag `v1.0.59.1`).** A fourth number had never been used: `PairnetsInfo.FormatVersion`
  dropped it, so a 1.0.59.1 app would have called itself 1.0.59, offered 1.0.59.1 again after every install and kept
  calling its server out of date. It now keeps a fourth number when there is one. Every comparison (the apps,
  1.0.52's frozen copy, update.sh's `sort -V`) puts 1.0.59 < 1.0.59.1 < 1.0.60, and the next main build (1.0.<run>,
  60 or more) is newer. The tag's release job now passes `--latest`, because the apps and update.sh read GitHub's
  latest release; the next push to main makes "Latest build" the latest again. The website's download links point at
  the "Latest build" release, so new installs from pairnets.app get the fix only once it is on main.

## Pairnets in File Explorer, like OneDrive (2026-10-19)

* **Left side, right-click items and folder icon: registry only, on by default.** Explorer's left side gets "Pairnets"
  through Microsoft's per-user recipe for cloud storage apps (a shell folder under HKCU that points at the Pairnets
  folder); the right-click items ("Open in Pairnets", "Show sync status") are HKCU verbs limited to the folder with
  `AppliesTo`, so Windows 11 lists them under "Show more options" (its short menu needs a signed package). The folder
  gets its blue icon from a hidden desktop.ini, which was already never synced. Settings has a switch; the uninstaller
  (`--unregister-explorer`) and Reset take it all away. Linux gets the GTK/KDE sidebar bookmark, a Dolphin service menu,
  GNOME Files/Nemo scripts and a gio folder icon; the Mac gets the folder icon (Finder's `Icon\r` file is never synced;
  Apple has no API to add a Finder sidebar entry).
* **Ticks and Files On-Demand use Windows' own Cloud Files API, opt-in, marked "Preview".** "Files in Explorer" in Settings: Plain folder
  (the default), Ticks all here, Files On-Demand. The folder is registered with `StorageProviderSyncRootManager` (the
  Windows 10 SDK projection, so the Windows app now targets `net8.0-windows10.0.19041.0`) and the rest is P/Invoke into
  cldapi.dll. It works from the normal per-user install (no package identity): checked on GitHub's Windows computers.
  Files are marked where they are, never copied or moved. Not possible (stays a plain folder, says why): network or
  removable drives, non-NTFS drives, a folder inside or around another cloud folder such as OneDrive.
* **The engine never reads a file whose content is on the nest.** Its content is the version last synced (the notes
  say which), so scanning, the "did it change?" checks before replacing or deleting, and conflicts use that. The folder
  is connected with `BLOCK_SELF_IMPLICIT_HYDRATION`, so a mistake fails instead of downloading everything. Cloud
  placeholders are reparse points: they count as files only while the cloud folder is on (plain folders behave as before).
* **Opening a file on the nest downloads exactly that version.** The placeholder carries the version's hash; the nest's
  answer is checked before the first byte is handed to Windows and the whole file is checked at the end. "Always keep on
  this device" fetches, "Free up space" leaves the content on the nest again (only for files in sync).
* **Turning it off brings every file home first.** The folder stays a cloud folder (keeping everything here) until
  every file is downloaded, then the placeholders become plain files and Windows forgets the folder. The uninstaller
  asks first when files are only on the nest. Reset makes the folder plain without the nest; files only on the nest
  stay as placeholders that open again once Pairnets is set up.
* **Tests never touch this PC's Explorer.** The registry tests write below a throwaway key; the cloud folder tests run
  only with `PAIRNETS_CLOUD_TESTS=1` (CI), and `tools/ExplorerShots` (pictures of real Explorer) refuses to run outside CI.
* **Placeholders are always ordinary files and folders to the scanner**, also while the cloud folder is off (or after
  leaving it): skipping them would make their files look deleted. A file whose content is only on the nest is never read;
  unknown to the notes (new, or renamed/moved here) it is "not known yet", and the version it stands for is never
  deleted on the nest (`ScanResult.HeldVersions`), so a renamed file left on the nest still opens, then syncs under its
  new name. Each placeholder carries "hash\npath on the nest" as its identity for exactly that.
* **Leaving turns files back first, then Windows forgets the folder.** Windows 11 may keep its cloud mark on folders
  after that (seen on GitHub's runners); such folders are ordinary to everyone and every file in them is here.
* **Windows' callbacks never let an error escape** (it would end the app), Explorer's keep/free choices go through one
  worker, Pairnets' own files are never freed, and the whole-tree work (pinning, leaving) runs off the window's thread.

## Computer and nest names (2026-10-19)

* **A new computer's own name.** Windows: its host name as typed when it was set up ("StudyPC"), not the old
  all-capitals 15-letter name `Environment.MachineName` gives ("STUDYPC"). A Mac: its Computer Name from Sharing
  settings ("Family MacBook Pro"). Linux: its pretty host name when set, otherwise the host name without its domain.
  A server linking to an account sends `hostnamectl --pretty` (or its short host name) as the nest's first name. All
  names are cleaned like the nest cleans them and kept to 64 characters, which also fixes a long accented name being
  refused at sign-in (the app counted letters, the service code points).
* **No PC-1 default.** "PC-1" was never a default in the code: it came from the old one-PC test setup. Names that
  look like a placeholder ("PC-1", "pc 2") or the old all-capitals name get a one-time card on Needs you: "Use
  StudyPC" or "Keep PC-1". Either answer is final (`NameOfferAnswered`). Only computers with their own key get it.
* **Renaming on the account page.** The page asks the nest (signed relay call `PATCH /api/relay/devices/{id}`); the
  nest stays the one place a computer's name lives and pushes `DeviceRenamed` as when an app renames itself. The same
  name twice gets " (2)", and the page says why. An older nest answers 405 and the page asks to update it.
* **Nest names in the apps.** The owner's name for a nest lives in D1 (`nests.label`) and is sent to the nest
  (`PUT /api/relay/name`) on a rename and whenever the account page finds the nest out of date. The nest keeps it,
  shows it in `/api/info` and pushes `NestRenamed`; the apps then show it where they said "Your nest" (side panel,
  map, tray panel, Devices page, overview). Until a nest has a name they keep saying "Your nest".

## Crash cards: every crash caught and kept, cleaned, on the computer (2026-10-19)

* **Both apps now catch every error nothing else caught**, in one place (`CrashCatcher` in Core): on the screen side
  (the app keeps running and offers a bug report, as before), behind the scenes (the app closes) and in background
  tasks nobody waited for. The Mac/Linux app used to catch only the screen side. Each is logged as before and kept as
  a crash card in `%LocalAppData%\Pairnets\crashes` (`~/.local/share/Pairnets/crashes`, …).
* **A card holds no personal data.** It has the app, version, system, the error kind and the Pairnets code it went
  through, with the message and details passed through `PrivacyCleaner`: quoted text, paths, file names, web
  addresses, emails, IP addresses, tokens, long ids and the names this computer is known by (user, computer, sync
  folder, device name, nest address, account email) are replaced. No log lines. Stack frames keep only the source
  file's name and line. The cleaner removes too much rather than too little.
* **One card per kind of crash.** The id is the error kind plus the first five Pairnets places in its stack (no line
  numbers or versions), so the same crash again only counts up. The newest 20 cards are kept.
* **After a crash that closed the app, the next start says so once** ("Pairnets closed unexpectedly", an Attention
  notice) and "Report a bug" shows the cards near the top. **Nothing is sent anywhere**: sending crash reports
  (with the user's yes, and a privacy page change first) is a separate, undecided step.

## Design review fixes: colours, focus, motion, sizes (2026-10-19)

* **The keyboard focus ring is the dark accent ink, not the accent.** `T.AccentInk` (#1F55C9 light, 6.6:1 on a
  card; #9CC2FF dark) as a 2 px ring 3 px outside the control, the same in both apps (WPF `FocusRing` styles, Avalonia
  `AdornerLayer.DefaultFocusAdorner`). It shows only after Tab or the arrow keys, never after a click; the old faint
  in-template rings are gone so there is one ring, not two.
* **Reduce motion is read once at start** (macOS NSWorkspace, GNOME `enable-animations`, Windows' animation switch
  when the Mac/Linux app runs there for tests). With it on, `Motion.Apply` takes the animations out of the app's
  styles, so classes written in XAML (a row's "enter") do nothing too, and bars jump instead of gliding.
* **Contrast values come from the measured review** (`findings-apps-numbers.md`): switch off track 3:1+, Fix button
  #B95309, dark hover blue #2560D6, amber/grey dots, soft text. Both palettes keep one hex per key; `PaletteParityTests`
  holds them together and keeps the fixed pairs above their minimum.
* **Where the apps differed, the Mac/Linux values won** (bold button words, accent-ink bold links, soft caps and day
  headings, bold fact values, 1 px field focus, radius 14 strips, 36 px icon squares, pressed colours instead of
  85 % opacity, sign-in field 50, base text 14), except two: the danger hover is the darker red #A61B25 in both (not
  88 % opacity, which made white text fainter), and the pop keeps Avalonia's overshoot. WPF still has no letter
  spacing, so its caps labels stay without the 0.8 tracking.
* **Windows scroll bars** are thin and rounded with no arrows (thumb `T.Dash`, `T.Off` under the mouse), drawn from
  existing keys so the palettes stay identical.

## Design review: same words and states in both apps (2026-10-19)

* **Red only for a real problem; switched off is grey.** A computer that is not online is "off since 10:45" (grey,
  dashed line or ring, no cross) on the nest picture, the left side, the tray and the Computers page. It is red "can’t
  reach" with a cross only when it vanished in the middle of moving files: its last live report said it was busy and it
  never sent the closing, empty report, less than an hour ago (`Presences.Of`). A stopped sync always ends with that
  empty report, so a computer that is shut down normally never turns red. The state is told by the line style and the
  words too, never by colour alone.
* **One count, one name.** "3 of 6 online" (this computer included) in the side group, on the page tile and on the
  page itself; the page is "Computers" (the nest's own web page keeps its name "Devices"). Tile notes are status words
  ("Up to date", "Stopped", "Offline", "Waiting", a percentage), not commands.
* **Needs you and History look the same in both apps.** Every Needs-you card: title with its time, the words, then
  the code (for a computer asking to join) and the button at the bottom right; only the join card's button is blue.
  The join words live in Core (`JoinRequest.Advice`). History counts "older versions" (the current one not counted),
  shows the "Restore puts that version back…" note, and Restore has an undo arrow in both apps.
* **Up and down are named the same everywhere:** "uploading"/"downloading" on the picture, the side and the tray (the
  tray's live row shows a down arrow while a file comes from the nest).

## Design review, first batch: one blue button, shortcuts, Settings at once (2026-10-19)

* **"Sync now" is blue only when it is the useful thing to do (A1).** `OverviewSyncNow.For(state)` in Core decides it for
  both apps: blue while up to date or waiting for another computer, resting (greyed, "Pairnets is syncing now") while a
  sync runs, plain when signed out, offline, paused or stopped, so the card's own button is the only blue one. The
  motion between the looks is for a later design round.
* **Stopped to keep files safe: the calm choice is the bright one (A2).** "Keep my files" changes nothing (syncing stays
  stopped) and opens the folder to check it; there is no "reject" command, and opening the folder is what someone would
  do next. "Allow these deletions (N)…" became an outlined amber button. The question's buttons say "Delete N files" and
  "Cancel" (Cancel is the default) in both apps; WPF's message box could not relabel its buttons, so `Dialogs.Confirm`
  draws a small window of its own.
* **Keyboard shortcuts in the main window (A4):** Ctrl+F search (Activity, History), Ctrl+R and F5 sync now, Ctrl+,
  Settings, Ctrl+1…6 the six pages (`Shortcuts` in Core; ⌘ on a Mac). The Mac app gets a menu bar (Pairnets: About,
  Settings…; File, Sync, View, Window, Help), built only on macOS. Tooltips that exist name the shortcut.
* **Settings switches apply at once (A11).** Switches, the files-at-a-time choice and the speed limits apply when
  changed (a limit's number when its box is left); only the text boxes (folder, name, ignore rules, nest address and
  key) wait for Save, so Save and Cancel stay. Two settings the sync engine reads at its start (wait for big batches,
  files at a time) start the session again; the limits change on the running connection (`SettingsSwitches`).
  Signing in again or out now lives only on the Account page, managing computers only on their page (Settings links
  there). Every switch sits on the right with its words on the left, sign-in step 4 too.
* **Page tiles under the computers (A12).** The open side panel is top-aligned: the tiles follow the computers and the
  spare space goes to the bottom; the computers' list still scrolls when the window is short. The dock is unchanged.
* **A failed update says why (A13).** One reason line under the title (`UpdateProblem`); the button is the next step:
  "Try again" after a broken download, "Open download page" after a checksum mismatch or an installer that would not
  start, and none for a release Pairnets did not sign (it is never pointed to). The X alone puts the card away. Moving
  these into a notification center is for a later design round.
* **Words (A14):** the nest's version reads "Nest 1.0.58", the advanced link "nest address and key".

## The 1.0.60 branch: how the waiting branches were put together (2026-10-20)

`release/1.0.60` is main (1.0.59.1) with Explorer integration, computer names, crash cards, the second-start fix,
the version-archive notes and the design review fixes merged in, in that order. Where they met:

* **A second Pairnets talks to the running one in one way.** The Explorer branch sent the right-click item's command
  through the pipe; the second-start fix made the running app answer once its window is up. Now a poke carries an
  optional message ended by a zero byte (a plain poke sends just the zero byte, an older Pairnets' poke nothing at
  all), and the answer comes once the window came forward or the command was done. `SecondStart` sends a right-click
  item's command itself, so a stuck app is reported for those too.
* **The update card keeps 1.0.59.1's behaviour, in the design review's look.** The design review (A13) sent a checksum
  mismatch or an installer that would not start to the download page; 1.0.59.1, released after it was written, says why
  and offers "Try again" for every failure and never opens the browser by itself. That stays. From the review: the
  reason line under the title (`UpdateReason`, also used for the Mac's "update by hand" words), no button for a release
  Pairnets did not sign (only the X, instead of "Hide"), and `UpdateProblem`, which now takes its words from
  `UpdateTexts.Failed`. The reason line sits under the title row, the whole card wide (as in 1.0.59.1): beside the
  button it was a narrow column, and the Mac's longer words made the card so tall that the page tiles left the window.
* **"Show Pairnets in File Explorer" and "Files in Explorer" apply at once**, like the other switches since A11.
  The switch puts the entry there or takes it away; a new Files-in-Explorer choice starts the session again (the engine
  connects to the cloud folder at its start). The Mac/Linux page's switch does the same for the file manager.
* **The Needs-you card for a new computer name** uses the review's single card layout, with "Keep PC-1" as a plain
  button before the blue "Use StudyPC" (both apps).
* **The design review fixes are one merge commit** (the last one), so they can be taken out again with one revert
  if they are not kept.

## End-to-end encryption plan (2026-10-19)

The owner wants to be able to say "we can't see your files". The plan is in [ENCRYPTION.md](ENCRYPTION.md); nothing
is built yet, and steps 1, 2, 6 and 8 there wait for the owner's yes or no.

* **Encrypt on the computer; the nest keeps only encrypted data.** The service can create a computer key on any linked
  nest (`HB_MASTER` → `/api/relay/devices`), so an encrypted channel to a nest that still keeps plain files would
  not stop it reading them. With the nest as an opaque store, neither the relay, the service, Cloudflare nor root on
  the nest can read files, names or folders.
* **.NET's own crypto only:** `AesGcm`, `HKDF`, `HMACSHA256`, `ECDiffieHellman` (P-256) and `RandomNumberGenerator`,
  plus the DPAPI / Keychain / keyring storage the apps already use. No new packages.
* **Flat ids, not encrypted folder names.** Each file is stored under an HMAC of its full path, so the nest sees no
  tree, depth or names, Linux name-length limits never bite, and the nest's per-path base check, history and
  `since` manifest work unchanged. The real path lives in an encrypted `meta` column. Name rules (case, Windows names)
  move into the app.
* **The three-hash logic keeps working on keyed tags.** L, S and B become `HMAC(tag key, id ‖ content)`, so
  `SyncDecision.Decide` is untouched and the nest never sees a plain-content hash. The nest checks transport
  integrity with a SHA-256 of the encrypted bytes.
* **1 MiB AES-GCM segments with a per-upload data key.** Resumable pieces and ranged downloads map to segment
  boundaries, and the app keeps the wrapped data key with the upload so a resumed piece is byte-identical.
* **A new computer is unlocked by another computer (6-digit code compare with a commitment) or by the recovery key**,
  never by anything the service holds. Signing in still only lets a computer talk to the nest.
* **Recovery key with no copy at Pairnets.** Losing every computer and the recovery key loses the files; the app and
  the site say so before encryption is turned on. Any copy at Pairnets would break the promise.
* **A removed computer leads to a new ring generation**, and computers refuse an older generation than the newest
  they have seen, so a replayed ring cannot re-admit it. Files it already had cannot be taken back, and the docs say
  so.
* **Existing nests move over in a separate `sealed/` area and switch in one step**; plain files and their history are
  deleted when every computer has switched or after 7 days. Old apps get `426 update_required` from an encrypted
  nest; `ApiVersion` stays 1 and the capability is an optional field, as before.
* **Claims stay narrow.** The pages may say Pairnets can't see files, names or folders. They must also say what it
  still sees: the account, computer names, when computers are online, and file counts, sizes and change times. They
  must not say "zero-knowledge", "unbreakable", "audited" or "we can recover your files".

## Encryption core (1.0.61.1, 2026-10-20)

The owner approved the build plan for 1.0.61.1 with the recommended answers: encrypt on the computer (step 1), a
recovery key with no copy at Pairnets (step 2), a "Turn on encryption" button for existing nests (step 6), and real
computer names kept for now (step 8, not built). The core is in `src/Pairnets.Core/Crypto`. Where it differs from
ENCRYPTION.md:

* **A file's segments are bound to a random file id in its header, not to the path id.** 1.0.60 moves and renames
  files that live only on the nest without downloading them, so the blob must stay valid under a new path. The
  encrypted meta pins the blob instead: it holds the path, size, modified time, the plain SHA-256 and the SHA-256 of
  the blob's header, and its associated data is the path id. The nest still can't swap two files' blobs (the header
  hash wouldn't match) or move a meta blob to another row.
* **tag = HMAC(tag key, path id ‖ plain SHA-256).** A move gets its new tag from the meta without reading the file.
  The id and tag keys come from generation 0 and never change, so a rotation doesn't make every file look changed.
  The content-wrap and meta keys are per generation, so new uploads after a removal use keys the removed computer
  never had.
* **Computer envelopes are signed.** Anyone can encrypt to a public key, so a ring sealed to a computer is also signed
  (ECDSA P-256) by the computer that made it. A computer takes a ring only if a computer its own ring trusts signed it,
  it is for the same nest, it has no older generation, and every generation it already has is unchanged. Each computer
  has two key pairs, one for agreement and one for signing. A computer with no ring yet never takes a computer
  envelope: its first ring comes through pairing (sealed with the paired session key) or the recovery key.
* **Honest limit:** a computer that is offline when another is removed could still take a ring signed by the removed
  computer, if that computer and the nest work together before the honest rotation reaches it.
* **The recovery key is 31 random base32 letters (155 bits) plus a check letter.** That makes eight groups of four.
  0, 1 and 8 count as O, I and B when typed.
* **Meta blobs are padded to 64 bytes**, so their length says little about the name.
