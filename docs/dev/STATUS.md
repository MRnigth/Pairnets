# Status

_Living snapshot. Update the date and the tables whenever state changes._

**Last updated:** 2026-10-07
**Shipped:** 1.0.45 (`main`), published as the rolling "Latest build" release.

## On `main`
The complete product: the sync engine and server, both desktop apps (Windows WPF, Mac/Linux
Avalonia), the sidebar window (Overview, Activity, History, Devices, Needs attention, Settings),
server self-update, in-app update checks, speed controls, the round logo, the Cloudflare-Tunnel
transport with chunked/adaptive uploads, and the Tether→Pairnets rename with automatic migration of
existing Tether installs. CI is green on Ubuntu, Windows and macOS.

Authentication is a single shared server token today (`SYNC_TOKEN`), checked by
`TokenAuthMiddleware`.

## In flight (branches, not yet on `main`)
| Work | Branch | State |
|------|--------|-------|
| **Website + app sign-in** (per-computer keys, device pairing, the nest website, passkey/password/email/Google, public `site/`) | `feature/sign-in` | Porting onto current `main` (the original work was built pre-rename/pre-tunnel). See [HANDOFFS.md](HANDOFFS.md#sign-in). |
| **"Moments that matter" animations** (confetti, status moments, motion) | `feature/animations` | Porting onto current `main`. |
| **Slow the main-window list scroll** (match the Settings wheel behaviour) | `feature/main-window-scroll` | Small, planned. |

## Pending (not code — owner actions / unverified)
- First real Cloudflare Tunnel run and a real >100 MB upload (tunnel was emulated in CI, never hit real Cloudflare).
- A real server self-update from the app, on real hardware.
- The Tether→Pairnets migration on real Windows/macOS/Linux machines (only the cross-platform core is unit-tested).
- Eyeball the Windows (WPF) UI — it has only ever been rendered in CI, never seen on a real screen.
- Regenerate `docs/images/*` after UI changes (the render tools do this; see [TESTING](../TESTING.md)).

## Known local test caveat
`dotnet test` on Windows fails two symlink tests (`PathRulesTests.DetectsSymlinksAlongThePath`,
`LocalScannerTests.NeverFollowsSymlinks`) unless Developer Mode (or an elevated shell) is on —
creating a symlink needs that privilege. Not a code bug; CI runs them with the privilege.
