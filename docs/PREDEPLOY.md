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
| `PAIRNETS_E2E_SHELL` | `wsl.exe -d pairnets-test -u root --` (WSL) or `sudo` (CI) | prefix that runs a command as root on the box; the tests append `bash -c "<script>"` |
| `PAIRNETS_E2E_DATA_DIR` | `/var/lib/pairnets` | for the server CLI (`runuser -u pairnets -- /opt/pairnets/pairnets-server <cmd> --data-dir <dir>`) |
| `PAIRNETS_E2E_SERVICE` | `pairnets-server` | stopped and started with `systemctl` around CLI commands that need the server stopped |
| `PAIRNETS_E2E_MAIL_DIR` | `/var/lib/pairnets-e2e/mail` | where the box's fake SMTP server writes each message (raw DATA) as `<unix-ms>-<n>.eml` |
| `PAIRNETS_E2E_REPORT` | `C:\...\tour-installed-linux.json` | the tour writes what it covered here; the runner fails when the file is missing |

On the box, the test-only settings are: `Sync__HttpsUrl=https://127.0.0.1:15443`, a run-time "localhost"
certificate in `Sync__TlsDir`, `Sync__PublicUrl=https://localhost:15443`, SMTP `127.0.0.1:15025` without TLS
(`Sync__SmtpUseTls=false`, `Sync__SmtpFrom=nest@example.com`), and Google pointed at the fake on
`http://127.0.0.1:15480` (`Sync__GoogleAuthUrl=.../auth`, `Sync__GoogleTokenUrl=.../token`, client id
`client-123.apps.googleusercontent.com`, client secret `test-oauth-secret`).

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
`127.0.0.1` on Windows.
