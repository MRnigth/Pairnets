# Developer handbook

Project-state docs for people (and agents) working on Pairnets. Start here.

| Doc | What it answers |
|-----|-----------------|
| [STATUS.md](STATUS.md) | What's on `main` right now, what's in flight on branches, what's pending. The living snapshot — update it when state changes. |
| [BRANCHES.md](BRANCHES.md) | What every branch is, whether it's merged/active/duplicate, and why some merged branches look "ahead". |
| [HANDOFFS.md](HANDOFFS.md) | How the codebase was built, feature by feature, and where each part lives. |
| [ROADMAP.md](ROADMAP.md) | Deliberately-not-in-v1 work and planned follow-ups. |
| [../../CHANGELOG.md](../../CHANGELOG.md) | Dated history of notable changes. |

For architecture, deploy, security and testing, see the main [docs/](../) folder
([ARCHITECTURE](../ARCHITECTURE.md) · [DEPLOY](../DEPLOY.md) · [SECURITY](../SECURITY.md) ·
[TESTING](../TESTING.md) · [DECISIONS](../DECISIONS.md)).

## House rules (read before committing)

- **A push to `main` is a release.** `.github/workflows/release.yml` runs on every push to
  `main` and rebuilds the rolling **"Latest build"** GitHub release that installed apps
  auto-update from. There is no separate publish step. Stage work on a branch; only merge to
  `main` when you intend to ship.
- **Build/test:** `dotnet build Pairnets.sln` and `dotnet test Pairnets.sln` (.NET 8). The WPF
  client (`src/Pairnets.Client`) builds only on Windows; Core, Desktop and Tests build anywhere.
  `PAIRNETS_CONVERGENCE_SEEDS=250` runs the deep convergence suite.
- **Before pushing:** `bash scripts/check-secrets.sh --diff` and
  `shellcheck deploy/*.sh scripts/*.sh` must be clean. The scanner rejects real-looking tokens
  and CGNAT `100.64.x`–`100.127.x` IPs even in samples — use placeholders
  (`https://sync.example.com`, `192.0.2.x`).
- **Mirror the UIs.** Logic lives in `Pairnets.Core`; the WPF (`Pairnets.Client`) and Avalonia
  (`Pairnets.Desktop`) apps are thin and mirror each other page-for-page — change both together.
  `StatusSnapshot` is the single source both UIs render.
- **Record judgment calls** in [../DECISIONS.md](../DECISIONS.md).
