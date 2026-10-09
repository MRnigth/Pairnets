# The pre-deploy check

Before anything is deployed (a push to `main`, which is a release; a `v*` tag; a GitHub release; a Cloudflare
deploy; an install on a server), every part of Pairnets is tested on test servers first. If anything fails,
the deploy is blocked.

*(The plain-English part of this page is written when the check is finished; below is how the parts fit together.)*

## How the parts fit together (for developers)

| Part | Where | What |
|---|---|---|
| Gate | `scripts/deploy-gate/` (Node), installed by `scripts/install-deploy-gate.ps1` | Claude Code `PreToolUse` hook plus a git `pre-push` hook. Spots deploy commands and demands a pass record for the exact commit. |
| Runner | `scripts/pre-deploy.ps1` | Runs every step below in a clean temporary checkout of the commit and writes the pass record and a report. |
| API tour | `tests/Pairnets.Tests/E2E/` | Calls every HTTP route, hub method/event and server CLI command, against three kinds of test server. Guard tests fail when something exists that the tour does not call. |
| Avalonia buttons | `tests/Pairnets.Tests/Ui/` | Presses every button, toggle and menu item in every window (headless), and drives the real `DesktopController` against a real test server. |
| WPF buttons | `tests/Pairnets.Client.Tests/` (Windows only) | The same for the Windows app and its `TrayController`. |
| Website buttons | `tests/Pairnets.Browser.Tests/` | A real Chromium (Microsoft.Playwright) clicks every button on every page of the nest's website. |
| Linux test box | `scripts/wsl-testbox.ps1`, `tests/linux-server/` | A throwaway WSL Ubuntu with systemd: installs the server package the real way, runs the tour and the browser tests against it, then updates it and upgrades from the published release. |

### Test servers ("targets")

The API tour and the browser tests run against any of these. The default `dotnet test` run uses the first two.

| Target | Started by | How |
|---|---|---|
| `in-process` | the test | `TestServer` (Kestrel in the test process) |
| `process` | the test | the real server program as its own process (`Infrastructure/ServerProcess.cs`); `PAIRNETS_E2E_SERVER` = path to a published `pairnets-server(.exe)`, else the build output |
| `installed-linux` | `scripts/pre-deploy.ps1` / the `linux-install` CI job | the server installed by `install.sh` under systemd, chosen with `PAIRNETS_E2E_TARGET=installed-linux` |

Environment for `installed-linux` (all set by the runner):

| Variable | Example | Meaning |
|---|---|---|
| `PAIRNETS_E2E_TARGET` | `installed-linux` | turns the target on |
| `PAIRNETS_E2E_URL` | `http://127.0.0.1:15075/` | the plain-HTTP API address |
| `PAIRNETS_E2E_WEBSITE_URL` | `https://localhost:15443/` | the website (also `Sync__PublicUrl`) |
| `PAIRNETS_E2E_TOKEN` | *(read from `/etc/pairnets/pairnets.env` at run time)* | the shared token |
| `PAIRNETS_E2E_SHELL` | `wsl.exe -d pairnets-test -u root --exec` (WSL) or `sudo` (CI) | prefix that runs a command as root on the box; the tests append `bash -c "<script>"` (as separate arguments). With `--exec` the script reaches bash exactly as written; `--` would pass it through a second shell first, which expands `$x` too early |
| `PAIRNETS_E2E_DATA_DIR` | `/var/lib/pairnets` | for the server CLI (`runuser -u pairnets -- /opt/pairnets/pairnets-server <cmd> --data-dir <dir>`) |
| `PAIRNETS_E2E_SERVICE` | `pairnets-server` | stopped and started with `systemctl` around CLI commands that need the server stopped |
| `PAIRNETS_E2E_MAIL_DIR` | `/var/lib/pairnets-e2e/mail` | where the box's fake SMTP server writes each message (raw DATA) as `<unix-ms>-<n>.eml` |
| `PAIRNETS_E2E_REPORT` | `C:\...\tour-installed-linux.json` | the tour writes what it covered here; the runner fails when the file is missing |

On the box, the test-only settings are: `Sync__HttpsUrl=https://127.0.0.1:15443`, a run-time "localhost"
certificate in `Sync__TlsDir`, `Sync__PublicUrl=https://localhost:15443`, SMTP `127.0.0.1:15025` without TLS
(`Sync__SmtpUseTls=false`, `Sync__SmtpFrom=nest@example.com`), and Google pointed at the fake on
`http://127.0.0.1:15480` (`Sync__GoogleAuthUrl=.../auth`, `Sync__GoogleTokenUrl=.../token`, client id
`client-123.apps.googleusercontent.com`, client secret `test-oauth-secret`).

The certificate is self-signed, made fresh on every box (`Sync__TlsDir=/var/lib/pairnets-e2e/tls`, the
certificate is `fullchain.pem` there, readable through `PAIRNETS_E2E_SHELL`): tests against this target
trust it from there or skip certificate checks for it. With `Sync__HttpsUrl` set the server logs one
Kestrel warning at start ("Overriding address(es) ..."): it binds every address itself; that is expected.

**The update during the tour.** Before the tour the runner points the box's root updater at *feed 2*
(the same build, one version higher: 1.0.65000 → 1.0.65001; `update-check.sh prepare`). So a
`POST /api/update` from the tour really updates the installed server: it restarts for a few seconds, and
`status.json` / `update.log` end with `Updated from 1.0.65000 to 1.0.65001.` Call it last, or wait for the
updater to finish. After the tour `update-check.sh verify` checks that update (token, data, health, no
token in `update.log`); when the tour did not ask for one, it asks itself.

**Order on the box** (`scripts/pre-deploy.ps1` step 8 and the `linux-install` CI job, phase A):
`install-check.sh --feed feed1` → `test-nest-config.sh` → `update-check.sh prepare --feed feed2` → the
tests with `--filter "FullyQualifiedName~InstalledLinux"` (the runner fails when `PAIRNETS_E2E_REPORT` was
not written) → `update-check.sh verify`. Phase B, on a fresh box: `upgrade-check.sh --feed feed1` (the
published release from the real one-liner, files put on it through the API, then its own updater
installs feed 1), then `lint.sh` (shellcheck).

### The fake Google (same rules in `Infrastructure/FakeGoogle.cs` and `tests/linux-server/fake-google.py`)

- `GET /auth?redirect_uri=..&state=..&nonce=..&code_challenge=..` redirects (302) to
  `<redirect_uri>?state=<state>&code=<code>`, where `<code>` is base64url (no padding) of the JSON
  `{"sub":"<sub>","email":"<email>","nonce":"<nonce>","challenge":"<code_challenge>"}`. The identity is
  `sub=e2e-google-user`, `email=owner@example.com` unless the query has `login_hint=<email>` (then `sub` is
  `e2e-` plus the part before the `@`).
- `POST /token` (form: `client_id`, `client_secret`, `grant_type=authorization_code`, `code`, `code_verifier`,
  `redirect_uri`) checks the client, decodes the code, checks that base64url(SHA-256(code_verifier)) equals the
  `challenge`, and answers `{"id_token": "<jwt>", "access_token": "unused", "token_type": "Bearer"}`. The JWT's
  header is `{"alg":"RS256","typ":"JWT"}`, its signature is three dummy bytes, and its claims are `iss` = the
  fake's base address, `aud` = the client id, `sub`, `email`, `email_verified: true`, `nonce`, and `exp` = now
  plus 5 minutes.
- A code the C# fake's test-set `IdTokenFor` answers keeps working as before (existing tests).

### Ports on the Linux test box

`15075` HTTP API, `15443` website (HTTPS), `15025` fake SMTP, `15480` fake Google. WSL forwards them to
`127.0.0.1` on Windows (WSL 2's default NAT mode: `wslrelay.exe` listens on Windows' 127.0.0.1 for every
port a box listens on, also on 127.0.0.1 inside the box; `localhost` works too). Every WSL 2 distro shares
one network, so only one box at a time may run the server: the runner stops a kept box before it starts
the next one.

### The Linux test box (`scripts/wsl-testbox.ps1`)

| Command | What |
|---|---|
| `-EnsureBase` | once per computer (about 5 minutes): Ubuntu 24.04 as its own distro `pairnets-test-base` (`wsl --install --name --location --no-launch`, or Canonical's root file system with `wsl --import` on an older WSL), `/etc/wsl.conf` with systemd on, `default=root`, `appendWindowsPath=false`; curl, openssl, python3, shellcheck; saved as `base.tar`, the distro removed again |
| `-Fresh` | a new box `pairnets-test` from `base.tar` (about 20 seconds), waits for systemd, and keeps it running with a hidden `sleep infinity` session (WSL stops an idle distro even when systemd runs in it) |
| `-Stop` / `-Remove` | stop a box but keep it / throw it away; `-Name` picks another box |

Everything lives in `%LOCALAPPDATA%\Pairnets-predeploy\wsl`; other WSL distros are never touched. A root
shell in the box: `wsl -d pairnets-test`. The systemd hardening in `pairnets-server.service` works under
the WSL kernel as it is (the checks look at the running service: no capabilities, `NoNewPrivileges`).

### The runner (`scripts/pre-deploy.ps1`)

`scripts\pre-deploy.ps1 -Repo <checkout> -Commit <sha> [-SummaryFile <json>] [-Only <step,...>] [-KeepBox]`
runs in a temporary git worktree of the commit (`%LOCALAPPDATA%\Pairnets-predeploy\work\<sha8>`, removed
afterwards), one run at a time (`run.lock`). Steps: `0 preflight`, `1 line-endings`, `2 secrets`, `3 gate`,
`4 build`, `5 publish`, `6 render-screens`, `7 tests`, `8 linux-box`, `9 cloud`. `-Only` takes names or
numbers and adds the steps they need (`tests` and `linux-box` need `build` and `publish`). The packages it
builds are version `1.0.65000` (feed 1) and `1.0.65001` (feed 2).

It writes `%LOCALAPPDATA%\Pairnets-predeploy\reports\<sha8>-<yyyyMMdd-HHmmss>\`: `report.txt` (PASS/FAIL
and time per step, then what went wrong in plain words, failing tests grouped by area), one log per step,
`TestResults\*.trx`, the boxes' journals and the tour report. The boxes' tokens are hidden in all of them.
`-SummaryFile` gets `{ "passed", "sha", "report", "partial", "steps": [ { "name", "passed", "skipped",
"seconds", "problems": [...], "notes": [...] } ] }`. Exit code 0 only when every step passed.

Only after a full run (no `-Only`) where everything passed it writes the pass record
`%LOCALAPPDATA%\Pairnets-predeploy\stamps\<full sha>.pass` (UTF-8, LF):

```
schema=1
sha=<full sha>
tree=<git rev-parse <sha>^{tree}>
finished=<UTC, e.g. 2026-10-09T21:15:00Z>
steps=preflight,line-endings,secrets,gate,build,publish,render-screens,tests,linux-box,cloud
report=<path of report.txt>
```

A failed box is kept for a look (`wsl -d pairnets-test`, or `pairnets-test-b` for phase B after a failed
phase A); `-KeepBox` keeps it after a pass too. `scripts\wsl-testbox.ps1 -Remove [-Name ...]` throws it away.
