# Decisions

Judgment calls made while building Tether v1, and why. The guiding rule: when in doubt, choose
what cannot lose or silently overwrite data.

## Setup and placeholders

* **Unfilled placeholders.** Server IP, folders and sizes were not provided. Docs use
  `http://<tailscale-ip>:5075` / `100.x.y.z`; the folder is chosen in the first-run window; the
  design assumes about 20 GB with single files of several GB, so everything is streamed and never
  buffered in memory.
* **Missing `.gitignore`.** The repo contained only `LICENSE`, so the standard Visual Studio
  `.gitignore` (`dotnet new gitignore`) was added along with Tether's exclusions.
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
* **Foreign marker.** A folder with a `.tether-marker` but no matching state is blocked until the
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
  does.

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
  cleanly. `install.sh` binds to the Tailscale IP and refuses `0.0.0.0`.
* **Logging under systemd** uses the systemd console formatter (journald priorities). ASP.NET's own
  request-URL logging is at Warning, so `access_token` never reaches the logs.
* **`MemoryDenyWriteExecute`** is not set in the unit, because the .NET JIT needs W+X memory.
* **Single-file publish** keeps `libe_sqlite3.so` next to the binary (no self-extraction), which
  works under `ProtectSystem=strict`.

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

## Tooling and tests

* **xunit 2.9 + runner 3.1** and Microsoft packages only (Microsoft.Data.Sqlite, SignalR client,
  Microsoft.Extensions.Logging, ProtectedData).
* **Windows CI runs the full test suite** (with 50 convergence seeds), not just the client build,
  because NTFS case-insensitivity and file locking are where the product runs.
* **Fault injection** uses a real TCP proxy that resets connections, instead of mocks.
* **Secret scanning** (`scripts/check-secrets.sh`) looks for 64-hex tokens, Tailscale CGNAT IPs,
  token assignments and personal Windows paths. It runs before each push and in CI over the whole
  history.
* **Commit attribution.** At your request, commits no longer carry `Co-Authored-By` trailers.

## Mac and Linux desktop apps (added after v1 scope)

* **Separate Avalonia app (`src/Tether.Desktop`) for macOS and Linux; Windows keeps the WPF app.**
  This was your choice. WPF only runs on Windows, and Avalonia (MIT, a mature .NET cross-platform UI)
  is the closest equivalent. Avalonia is the only third-party runtime dependency. All sync logic
  and the window state (`ClientSession`, `ActivityFeed`, `StatusSnapshot`) live in Tether.Core, so
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
  (`TETHER_SCREENSHOT_DIR=... dotnet test --filter DesktopUi`).
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
* **New main window (sidebar).** The window has a sidebar (Overview, Activity, History, Needs
  attention, Settings) instead of two tabs and a row of six equal buttons. Sync now, Pause and Open
  folder stay at the top right; the log and bug report moved into a "⋯" menu. The app update is a
  card at the bottom of the sidebar instead of a banner over everything. Settings is a page; the
  first-time setup keeps its own window. Both apps (WPF and Avalonia) share the design, and all the
  logic behind it (the map, history lists, activity by day) is in Tether.Core.
* **"This computer ⇄ server ⇄ other computer".** The server remembers which computers use it (the
  name each sends, when it was last heard from, whether its push channel is open, and which app it
  runs) in `devices.json`, so the overview can show the other computer as online or "last seen 3 h
  ago" and animate files moving. It is display only: nothing in syncing depends on it. Names not
  seen for 60 days are left out of the picture; the server keeps at most 32.
* **History in the app.** The server already kept versions and had list/restore endpoints; the
  History page uses them, so getting a file back no longer needs SSH. "Deleted files" covers the
  last 30 days (the server's default retention). A restore is an ordinary new version, so it can be
  undone the same way.
* **Quick panel.** Clicking the tray icon on Windows opens a small panel next to the taskbar (like
  OneDrive or Dropbox); double-click opens the window. On macOS Avalonia's menu-bar icon only ever
  shows its menu (it reports no clicks), so the panel opens from "Quick status…" in that menu; on
  Linux a click opens it where the desktop reports clicks.
