# Working while the PC is off (cloud sessions)

Pairnets work can go on in a cloud session of the coding assistant while the owner's PC is off. A cloud session gets a
fresh Linux machine with a copy of the GitHub repository.

## In plain words

**What it can do.** Write code, build it, and run most tests: the server, the API tour, the Mac/Linux app's buttons
(headless). It commits to a `wip/...` branch and pushes it to GitHub.

**What it cannot do.** Deploy. Every deploy (a release, a beta, the cloud, a server) needs the pre-deploy check
(docs/PREDEPLOY.md), and that runs only on the owner's Windows PC. The Windows app's own tests (WPF) don't run on Linux
either. So: the cloud session gets a branch ready, and the PC checks it and ships it.

**Starting one.** From the owner's control panel on soro-server (Cloud sessions: pick a `wip/...` branch, type the task),
or from the coding assistant's web app: choose the repository and the branch, and say what to do. When the session
starts, `scripts/cloud-session-start.sh` installs .NET 8 if it's missing, restores the packages, sets the repository's
commit identity and gives the session its rules: work only on `wip/*` branches, never deploy, no attribution, write
"the owner" instead of a name.

**Bringing the work back.** On the PC: `git fetch`, look at the branch, then run the pre-deploy check on it. A beta goes
out by pushing the checked commit to `beta/public` and `beta/developer`; a stable release by moving `main` to it and
tagging it. The control panel's buttons do exactly that through the PC.

## Branches on GitHub

Only `wip/*` branches are pushed for cloud work. Their history is squashed, because some local branches have commit
messages that `scripts/check-secrets.sh` refuses (a personal name). A `wip/*` branch sits on a commit that is already on
GitHub (`main` or `beta/public`), so when it's done the release branch can simply move forward to it.

## If the setup fails

The session says ".NET SDK: MISSING". Then the environment's network access doesn't reach Microsoft's download servers.
In the environment's network settings, allow `dot.net`, `builds.dotnet.microsoft.com`, `api.nuget.org` and
`archive.ubuntu.com` (or pick the default trusted access), then start a new session.

## Parts

| Part | What |
|---|---|
| `.claude/settings.json` | A SessionStart hook. It does nothing unless `CLAUDE_CODE_REMOTE` is `true` (only in cloud sessions). |
| `scripts/cloud-session-start.sh` | Installs the .NET 8 SDK (apt, else dotnet-install.sh), runs `dotnet restore`, sets the commit identity, prints the rules. |
