# Testing

All tests are xunit tests in `tests/Pairnets.Tests`. The real server runs **in-process** on a
random localhost port with temporary directories. No network, Cloudflare account or Windows machine is
needed, except for the Windows CI job, which runs the same suite on NTFS.

```bash
dotnet test                                    # everything
dotnet test --filter "FullyQualifiedName~Unit"
dotnet test --filter "FullyQualifiedName~Integration"
dotnet test --filter "FullyQualifiedName~Faults"
PAIRNETS_CONVERGENCE_SEEDS=250 dotnet test --filter "FullyQualifiedName~Convergence"
```

A plain `dotnet test` runs about 600 tests: a little under 600 of their own, plus one convergence
test per seed (30 by default). `PAIRNETS_CONVERGENCE_SEEDS` sets the number of seeds; CI uses 250 on
Linux and 50 on Windows and macOS.

The Windows app (`net8.0-windows`) builds on Linux because its project sets
`EnableWindowsTargeting=true`. Its logic lives in Pairnets.Core and is tested there; the WPF windows
themselves only run on Windows, where CI renders every one of them in light and dark
(`tools/RenderScreens.Wpf`). The Mac and Linux app (Avalonia) runs headless in `Ui/DesktopUiTests.cs`,
which also renders the screenshots in `docs/images`
(`PAIRNETS_SCREENSHOT_DIR=docs/images dotnet test --filter RenderScreenshots`).

## What is covered

| Suite | Folder | Highlights |
|-------|--------|-----------|
| Unit | `Unit/` | `PathRules` (every rejection class and safe names), `IgnoreList`, SHA-256 known answers and streaming, the **exhaustive `Decide(L,S,B)` table** (64 combinations → 15 patterns) plus a safety property (local content is only replaced or deleted when it equals the base), state DB (racy-git cache rule, approvals), conflict names, scanner (stability window, locked files, symlinks, invalid names), engine against an in-memory fake server, client settings, rolling log, state locator, piece sizes and speed limits, the update check, Reset (`LocalResetTests.cs`), the `pairnets://` pipe. |
| Server | `Integration/Server*`, `UploadInPieces*` | Auth (all token forms, wrong token, token never logged), PUT/DELETE/409/idempotency, invalid names, traversal, case collisions, manifest `since`, Range downloads, history list/restore, retention purge with a fake clock, crash-journal roll-back/roll-forward, data-dir lock, `rescan`, throttle, uploads in resumable pieces. |
| Two devices | `Integration/TwoDeviceSyncTests.cs` | Desktop and laptop with their own folders and state DBs through the real server: create, nested and Unicode names, empty files, a **101 MB file streamed** both ways, edits back and forth, deletion plus empty-folder pruning, conflicts reaching both PCs, edit-beats-delete, identical first sync transferring nothing, ignored files, files still being written, mass-delete block and allow-once, emptied folder, missing marker or folder, foreign marker, history restore reaching the PCs, wrong token, case collision warnings. |
| Runner and session | `Integration/RunnerTests.cs`, `ClientSessionTests.cs` | SignalR push brings a change to the other PC within seconds; the origin ignores its own echo; offline → recovery without losing local edits; blocked status and approval; catch-up notification; what the window shows; "Update server" step by step; the bug report never carries the token. |
| Keys and pairing | `Integration/DeviceKeyTests.cs`, `PairingTests.cs`, `SignInFlowTests.cs`, `Unit/AuthStoreTests.cs`, `PairRequestTests.cs` | A computer's own key (shown once, only its hash stored), removal that closes the live push connection, no endpoint hands out a key without a person, a computer on the shared token stops until someone signs it in (and keeps syncing against an old server without sign-in), switching the shared token off, "every endpoint needs a key" over all routes, the ask → approve → collect flow with codes, expiry, rate limits and the apps' shared sign-in logic (`Nest`, `PairingFlow`) against the real server. |
| Nest website | `Integration/NestWebsiteTests.cs`, `PasskeyWebsiteTests.cs`, `EmailLinkTests.cs`, `GoogleSignInTests.cs`, `Unit/WebAuthnTests.cs` | Setup link, password, cookie flags, same-origin checks, CSP; passkeys against a **software authenticator** (ES256 and RS256; wrong challenge, origin, relying party, missing presence, tampered signature, cloned counter); email links with a fake mail sender and a fake SMTP server; Google against a fake token endpoint that also checks PKCE. These run against a real HTTPS listener with throwaway certificates (`Infrastructure/TestCertificates.cs`). |
| Tunnel and updater | `Integration/CloudflareTunnelTests.cs`, `UpdateScriptTests.cs` | Client addresses from a local proxy only, Cloudflare's error pages in plain words, plain `http://` through Cloudflare refused before a token is sent; `update.sh` checks, logs and refuses a bad checksum (Linux only). Separate migration tests cover the move from the app's old name. |
| Public site | `Unit/SiteTests.cs` | Every local link exists, downloads point at files `release.yml` builds, pages obey their own CSP. |
| Convergence | `Convergence/` | Seeded random simulation: two devices create, edit, delete and rename files, with sync passes at random points (so both accumulate offline edits). At the end both folders and the server are byte-identical, and **every content any pass ever saw still exists** in a folder, `files/` or `history/`. |

A mutation check: making `Decide` return `Download` instead of `Conflict` makes the convergence
test report lost content within the first few seeds.

On a Windows development machine some tests can fail for local reasons: the website tests, which
need the local HTTPS listener to work, and two symlink tests, which need permission to create
symlinks (Developer Mode). CI runs them all.

## What is not automated

* Real passkeys, real mail delivery and a real Google account: the website was driven in Chromium with
  its virtual authenticator (`WebAuthn.addVirtualAuthenticator`), but a phone's passkey, SPF/DKIM and
  Google's consent screen need a person.
* The apps on real hardware: icon colors, menus, notifications, DPAPI, the Keychain and keyring under
  a real user, Recycle Bin and Trash, sleep/resume events, start at login, the `pairnets://` link
  from a real browser.
* The Cloudflare Tunnel and the systemd units on a real Ubuntu host (`install.sh` is checked
  with shellcheck in CI, and the units with `systemd-analyze verify` in development).
* Very large trees (100k+ files) and multi-gigabyte files over a slow link.

Check those by hand after a change that touches them.
