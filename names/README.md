# Pairnets names (`names/`)

A small service, `pairnets-names`, meant to run as a Cloudflare Worker at `https://names.pairnets.app`. It gives each
Pairnets server (nest) a free name such as `alice.pairnets.app` and its **own** Cloudflare Tunnel, so the server is
set up with one command and no domain or Cloudflare account of its own:

```bash
curl -fsSL https://raw.githubusercontent.com/MRnigth/Pairnets/main/deploy/get.sh | sudo bash -s -- --name alice
```

Only the name and the tunnel are made here. Files never pass through this service and are never stored here; each
nest stays on its owner's server. The installer side is `deploy/pairnets-name.sh` (run by `install.sh --name`).

**Status: nothing is deployed.** There is no `names.pairnets.app`, no database and no API token yet. Released servers
only talk to this service when someone runs `install.sh --name`. Switching it on needs the owner (checklist below).

## How it works

| Call (from `pairnets-name.sh`) | What happens |
|---|---|
| `POST /v1/claim/start` `{"name","email"}` → 202 `{"claim_id","expires_in"}` | Checks the name (3 to 32 of `a-z 0-9 -`, no kept-back name, not taken) and the limits, then emails a 6-digit code (15 minutes, 5 tries). |
| `POST /v1/claim` `{"claim_id","code"}` → 201 `{"name","public_url","tunnel_token","manage_key"}` | Creates the tunnel (`config_src: cloudflare`), reads its token, sets its ingress (`<name>.pairnets.app` → `http://localhost:5075`, then `http_status:404`), adds a proxied CNAME to `<tunnel id>.cfargotunnel.com`. Any failure deletes what was made. The token and key are answered once and never stored (only a SHA-256 of the key). |
| `POST /v1/rotate` `{"name"}` + `Authorization: Bearer pnk_…` → 200 `{"name","tunnel_token"}` | A new tunnel secret, then the tunnel's open connections are closed. |
| `DELETE /v1/name` `{"name"}` + `Authorization: Bearer pnk_…` → 204 (202 while Cloudflare finishes) | Deletes the DNS record, the connections and the tunnel; the name is free again. |
| `GET /v1/available?name=alice` → `{"name","available","reason"?}` | A quick check (the claim checks everything again). |
| `GET /` | A short page: what this is, and the abuse address (support@pairnets.app). |

Errors are `{"error": "<code>", "message": "<one plain line>"}`; the installer prints the message. Limits: 120
requests a minute per IP, 5 codes an hour per IP and 3 a day per address, at most 3 names per IP and one per email
address, `DAILY_NAMES` new names a day, `MAX_NAMES` in all, `MAIL_DAILY_LIMIT` emails a day, 10 wrong keys an hour per
IP, 5 rotations an hour per name. `NAMES_PAUSED = "1"` stops new names (rotate and give back still work). An hourly
Cron deletes expired codes and old counters, and finishes any cleanup a failed claim or give-back left behind.
Why it is built this way: `docs/DECISIONS.md`, "Free names".

## What is here

| Path | What |
|---|---|
| `src/` | The Worker (TypeScript, no runtime dependencies). `app.ts` routes; `routes/` holds the endpoints; `cloudflare.ts` the Cloudflare API calls. |
| `migrations/` | D1 schema (`0001_init.sql`). Additive migrations only. |
| `test/` | vitest tests in the Workers runtime (Miniflare, local D1) with a fake Cloudflare API and a fake mailer. |
| `wrangler.toml` | Settings with placeholder ids, the variables, the hourly Cron, and the list of secrets. |

## Running the tests

Needs Node 22 or newer.

```bash
cd names
npm ci
npx tsc --noEmit        # type check
npm test                # vitest: the Worker in a local Workers runtime, with D1
```

The tests never reach the network and every secret is made up while they run. Real secrets live only in the
Cloudflare dashboard (or `wrangler secret put`), and locally in `names/.dev.vars`, which git ignores.

## Switching it on (owner checklist)

Before opening it to the public, wait for Cloudflare's answer about large-file traffic through tunnels on the Free
plan (see `docs/DECISIONS.md`); a Pro plan may be needed.

1. **API token.** Cloudflare dashboard → My Profile → API Tokens → Create Token → Custom token, with exactly
   *Account → Cloudflare Tunnel → Edit* and *Zone → DNS → Edit* for the zone `pairnets.app` only. Copy it once.
2. **Database.** `cd names && npx wrangler d1 create pairnets-names`, put the printed `database_id` in
   `wrangler.toml`, then `npx wrangler d1 migrations apply pairnets-names --remote`.
3. **Email.** In Resend, make a new sending key for `pairnets.app` (separate from the account service's).
4. **Secrets.** `npx wrangler secret put` for each of `CF_API_TOKEN` (step 1), `CF_ACCOUNT_ID` (Workers & Pages,
   right column), `CF_ZONE_ID` (the pairnets.app Overview page, right column), `RESEND_API_KEY` (step 3) and
   `HASH_KEY` (`node -e "console.log(require('crypto').randomBytes(32).toString('base64url'))"`).
5. **Deploy.** Uncomment the `routes` line in `wrangler.toml` (custom domain `names.pairnets.app`) and run
   `npx wrangler deploy`. Open `https://names.pairnets.app/`: the page should show.
6. **pairnets.app settings.** Security → Bots: Bot Fight Mode **off** (the apps cannot pass browser checks). Keep the
   names in `src/names.ts` `RESERVED` in step with what the zone uses.
7. **A real run.** On a spare Ubuntu machine: `curl -fsSL https://raw.githubusercontent.com/MRnigth/Pairnets/main/deploy/get.sh | sudo bash -s -- --name <a test name>`
   (or, before this is on main, unpack a release from this branch and run `sudo ./install.sh --name <a test name>`).
   Type your email and the code. Check: the installer ends with "Your nest's address: https://<name>.pairnets.app";
   `sudo ls -l /etc/pairnets` shows `name.env` and `tunnel.env` as `-rw-------` root; the dashboard shows one tunnel
   `pairnets-<name>-…` (Healthy) and one CNAME.
8. **The nest's website.** Open the setup link it printed, add a passkey or password.
9. **A computer.** Install the app, type `<name>.pairnets.app`, sign in, approve, sync a small file and a big one.
10. **Rotate and give back.** `sudo /opt/pairnets/pairnets-name.sh rotate` (the tunnel reconnects), then
    `sudo /opt/pairnets/pairnets-name.sh release`: the tunnel and the CNAME are gone from the dashboard.

**Taking a name back** (abuse, or an owner who lost their key and wrote from the email address they claimed with):
delete its DNS record and tunnel in the dashboard, then
`npx wrangler d1 execute pairnets-names --remote --command "DELETE FROM names WHERE name = '<name>'"`.
