# Changelog

Notable changes, newest first. Main builds are versioned `1.0.<CI run number>` and published as the
rolling "Latest build" release; the milestones below name the version where known.

## 1.0.45 — 2026-10-07
- **Transport:** run without Tailscale through a **Cloudflare Tunnel** only; the server listens on
  loopback and `cloudflared` brings requests in. Large files upload in resumable pieces sized to the
  connection.
- **Rename:** Tether → **Pairnets** across code, projects, services, installers and docs, with
  automatic takeover of existing Tether servers and apps.

## 1.0.41–1.0.43 — 2026-10-06
- New round "ring" logo; Settings moved next to the app name; the Windows taskbar now shows the new
  logo after an update (every window sets its own icon).

## 1.0.39 — 2026-10-06
- **Redesigned window with a sidebar:** Overview (with a live computers-and-server picture), Activity,
  **History** (restore deleted files and old versions), **Devices**, Needs attention, Settings, and a
  tray/menu-bar quick panel.

## 2026-10-06 (earlier)
- Server version shown in the app, with in-app "Update the server"; a server updater that can't get
  stuck; Debug mode and "Report a bug".

## 2026-10-02 to 2026-10-03
- Server self-update (root systemd path unit; downgrade-proof, checksum-verified).
- In-app update checks for the apps.
- Speed: up to 4 transfers at once; wait while the other computer uploads a big batch; upload/download
  speed limits; server free space; a "Connected" pill; slower Settings scroll.
- "Friendly" UI animations and dead-end-free server updates.
- Redesigned UI (cards, icons, segmented tabs, dark mode) on all platforms.
- Mac and Linux desktop apps (Avalonia); one-line installers; macOS .dmg and Linux desktop packages.
- First UI: tray app, main window with status/progress/activity/needs-attention.
- CI on Ubuntu/Windows/macOS; a rolling "Latest build" release with direct download links.
- Full docs: step-by-step guide, architecture, security, deploy, testing, decisions.

## 2026-10-02 — foundation
- `Pairnets.Core`: Windows-safe path rules and ignore list; streaming SHA-256; the pure three-hash
  `Decide(L,S,B)` with an exhaustive table test; SQLite client state with a racy-safe hash cache;
  the streaming HTTP client; the sync engine (marker, mass-delete and overwrite guards); the sync
  runner (debounced watcher, SignalR push, backoff, catch-up).
- `Pairnets.Server`: manifest store, journaled file store with history, token auth with throttle,
  SignalR hub, rescan/history maintenance.
- Tests: server API, storage, journal recovery, auth; two-device and safety-guard integration;
  fault injection; seeded two-device convergence with a no-content-lost invariant.
