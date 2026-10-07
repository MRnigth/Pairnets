# Testing

All tests are xunit tests in `tests/Pairnets.Tests`. The real server runs **in-process** on a
random localhost port with temporary directories. No network, Cloudflare account or Windows machine is
needed, except for the Windows CI job, which runs the same suite on NTFS.

```bash
dotnet test                                    # everything (30 convergence seeds by default)
dotnet test --filter "FullyQualifiedName~Unit"
dotnet test --filter "FullyQualifiedName~Integration"
dotnet test --filter "FullyQualifiedName~Faults"
PAIRNETS_CONVERGENCE_SEEDS=250 dotnet test --filter "FullyQualifiedName~Convergence"
```

The Windows client (`net8.0-windows`) builds on Linux because its project sets
`EnableWindowsTargeting=true`. Its logic lives in Pairnets.Core and is tested there. The tray UI
itself only runs on Windows.

## What is covered

| Suite | Folder | Highlights |
|-------|--------|-----------|
| Unit | `Unit/` | `PathRules` (every rejection class and safe names), `IgnoreList`, SHA-256 known answers and streaming, the **exhaustive `Decide(L,S,B)` table** (64 combinations → 15 patterns) plus a safety property (local content is only replaced or deleted when it equals the base), state DB (racy-git cache rule, approvals), conflict names, scanner (stability window, locked files, symlinks, invalid names), engine against an in-memory fake server, client settings, rolling log, state locator. |
| Server | `Integration/Server*` | Auth (all token forms, wrong token, token never logged), PUT/DELETE/409/idempotency, invalid names, traversal, case collisions, manifest `since`, Range downloads, history list/restore, retention purge with a fake clock, crash-journal roll-back/roll-forward, data-dir lock, `rescan`, throttle. |
| Two devices | `Integration/TwoDeviceSyncTests.cs` | Desktop and laptop with their own folders and state DBs through the real server: create, nested and Unicode names, empty files, a **101 MB file streamed** both ways, edits back and forth, deletion plus empty-folder pruning, conflicts reaching both PCs, edit-beats-delete, identical first sync transferring nothing, ignored files, files still being written, mass-delete block and allow-once, emptied folder, missing marker or folder, foreign marker, history restore reaching the PCs, wrong token, case collision warnings. |
| Runner | `Integration/RunnerTests.cs` | SignalR push brings a change to the other PC within seconds; the origin ignores its own echo; offline → recovery without losing local edits; blocked status and approval; catch-up notification. |
| Faults | `Faults/` | Network cut mid-upload and mid-download (TCP chaos proxy sending RST), server restart in the middle of a pass, locked files on both sides, disk full during a download. Each checks that no partial file appears at a final path (client folder or server `files/`), state is unchanged, and the next pass recovers. |
| Keys and pairing | `Integration/DeviceKeyTests.cs`, `PairingTests.cs`, `SignInFlowTests.cs`, `Unit/AuthStoreTests.cs`, `PairRequestTests.cs` | A computer's own key (shown once, only its hash stored), removal that closes the live push connection, silent switch from the shared token, "every endpoint needs a key" over all routes, the ask → approve → collect flow with codes, expiry, rate limits and the apps' shared sign-in logic (`Nest`, `PairingFlow`) against the real server. |
| Nest website | `Integration/NestWebsiteTests.cs`, `PasskeyWebsiteTests.cs`, `EmailLinkTests.cs`, `GoogleSignInTests.cs`, `HttpsTests.cs`, `Unit/WebAuthnTests.cs` | Setup link, password, cookie flags, same-origin checks, CSP; passkeys against a **software authenticator** (ES256 and RS256; wrong challenge, origin, relying party, missing presence, tampered signature, cloned counter); email links with a fake mail sender and a fake SMTP server; Google against a fake token endpoint that also checks PKCE; HTTPS with throwaway certificates and hot-swapped renewals. |
| Public site | `Unit/SiteTests.cs` | Every local link exists, downloads point at files `release.yml` builds, pages obey their own CSP. |
| Convergence | `Convergence/` | Seeded random simulation: two devices create, edit, delete and rename files, with sync passes at random points (so both accumulate offline edits). At the end both folders and the server are byte-identical, and **every content any pass ever saw still exists** in a folder, `files/` or `history/`. CI runs 250 seeds on Linux and 50 on Windows. |

A mutation check: making `Decide` return `Download` instead of `Conflict` makes the convergence
test report lost content within the first few seeds.

## What is not automated

* Real passkeys, real mail delivery and a real Google account: the website was driven in Chromium with
  its virtual authenticator (`WebAuthn.addVirtualAuthenticator`), but a phone's passkey, SPF/DKIM and
  Google's consent screen need a person.
* The tray UI on real Windows hardware: icon colors, menus, toasts, DPAPI under a real user
  profile, Recycle Bin, sleep/resume events, the HKCU Run key.
* The Cloudflare Tunnel and the systemd units on a real Ubuntu host (`install.sh` is checked
  with shellcheck and the unit with `systemd-analyze verify` in development).
* Very large trees (100k+ files) and multi-gigabyte files over a slow link.

See the final checklist in the README for the first things to verify by hand.
