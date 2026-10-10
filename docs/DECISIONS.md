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

## Tooling and tests

* **xunit 2.9 + runner 3.1** and Microsoft packages only (Microsoft.Data.Sqlite, SignalR client,
  Microsoft.Extensions.Logging, ProtectedData).
* **Windows CI runs the full test suite** (with 50 convergence seeds), not just the client build,
  because NTFS case-insensitivity and file locking are where the product runs.
* **Fault injection** uses a real TCP proxy that resets connections, instead of mocks.
* **Secret scanning** (`scripts/check-secrets.sh`) looks for 64-hex tokens, carrier-grade NAT/VPN IPs,
  token assignments, Cloudflare Tunnel tokens and personal Windows paths. It runs before each push and in CI over the whole
  history.
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
