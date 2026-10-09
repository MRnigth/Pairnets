# Pairnets Cloud (`cloud/`)

Two Cloudflare Workers that give everyone **one address, `https://sync.pairnets.app`**:

- **`pairnets-sync`** (this folder): accounts (Google or email sign-in), the account pages, adding a server, signing in
  apps, and the **relay**: an app talks to `https://sync.pairnets.app/n/<server id>/api/...` and the request is passed to
  that person's own server.
- **`pairnets-router`** (`router/`): no address of its own. It holds one private Workers VPC link per server, each to
  that server's own Cloudflare Tunnel, and is reached only through `pairnets-sync`.

Files never stay on the service: they pass through it to the person's own server (the nest), which keeps them. A
server's tunnel has no public hostname, so the only way in is the router's private link. Servers on their own domain
(`install.sh --public-url`) keep working as before; linking them to an account (version 1) is still there.

**Status: nothing is deployed.** There is no `sync.pairnets.app`, no database, no keys, no tunnels and no Google client
yet. Released servers and apps do not talk to this service. Setting it up needs the owner (checklist below).

## What is here

| Path | What |
|---|---|
| `RELAY.md` | Version 2, "one address for everyone": adding a server, the relay, the router, the signed calls to the server, app sign-in. Where it differs from `CONTRACT.md`, it wins. |
| `CONTRACT.md` | Version 1: formats, accounts, the signed login assertion, the HMAC-signed nest requests, the D1 schema, test vectors, threats. |
| `CONTRACT-NOTES-worker.md` | Where the Worker had to fill a gap in either document, and the few deliberate differences. |
| `src/` | `pairnets-sync` (TypeScript, no runtime dependencies, WebCrypto only). `app.ts` routes; `routes/` holds the endpoints; `relay.ts` is the relay; `router.ts` deploys the router's links; `nestadmin.ts` signs the calls to servers; `cloudflare.ts` is the Cloudflare API client. |
| `router/` | `pairnets-router`: its code (`src/index.ts`), its own `wrangler.toml` and `tsconfig.json`. |
| `migrations/` | D1 migrations. `0001_init.sql` is exactly the SQL of `CONTRACT.md` section 7 (a test checks this); `0002_relay.sql` adds the relay columns, `server_links` and `router_state`. |
| `test/` | vitest tests in the Workers runtime (Miniflare, local D1). `test/fake-relay.ts` is a fake Cloudflare API, the real router code with fake VPC links, and fake servers that check the signed calls with their own code. `test/vectors/assertion-v1.json` holds the golden vectors that the C# tests also read. |
| `scripts/keygen.mjs` | `npm run keygen`: prints a new ES256 signing key pair (nothing is written to disk). |
| `wrangler.toml` | `pairnets-sync` settings with placeholder ids, the variables, the `ROUTER` binding, the hourly Cron, and the list of secrets. |

## Running the tests

Needs Node 22 or newer.

```
cd cloud
npm ci
npx tsc --noEmit            # type check (the Worker and the tests)
npx tsc --noEmit -p router  # type check the router Worker
npm test                    # vitest: both Workers in a local Workers runtime, with D1
npm run vectors             # only on purpose: regenerates test/vectors/assertion-v1.json with new test keys
```

Every key the tests use is generated while they run, and the tests never reach the network (Google, Turnstile, Resend
and the Cloudflare API are fakes; the router's links lead to fake servers). Nothing secret is ever committed: real
secrets live only in the Cloudflare dashboard (or `wrangler secret put`), and locally in `cloud/.dev.vars`, which git
ignores (as are `.wrangler/` and `node_modules/`).

The C# side of the contract is tested with the rest of the repo (`dotnet test Pairnets.sln`), including
`HostedGoldenVectorTests`, which checks the vectors written here. The signed calls to a server (`ra1`) have fixed
vectors in `test/nestadmin.test.ts` for the C# side to use too.

## Setting it up later (owner checklist, version 2)

Do these in order; nothing here is done yet.

1. **Plan.** Workers Paid ($5 a month) on the Cloudflare account that has the `pairnets.app` zone (the relay carries all
   the apps' requests). Workers VPC is free while it is in beta. One account holds at most 1,000 tunnels; `MAX_NESTS`
   (900) stops new servers before that.
2. **API token** (My Profile > API Tokens > Create custom token), for this account only, with exactly:
   - Account > Cloudflare Tunnel > Edit (make, configure and delete the servers' tunnels, read their tokens);
   - Account > Workers Scripts > Edit (set the router's links);
   - the Connectivity Directory permission that binding VPC networks needs (Account > Connectivity Directory > Edit,
     or the name the dashboard shows for Workers VPC).
   It goes into the secret `CF_API_TOKEN` below and nowhere else.
3. **Database.** `npx wrangler d1 create pairnets-sync`, put its id in `wrangler.toml`, then
   `npx wrangler d1 migrations apply pairnets-sync --remote`.
4. **Router first.** `cd router && npx wrangler deploy`. It needs no secrets and gets no route. Its links are set by
   `pairnets-sync`, not by this deploy.
5. **Google:** a web OAuth client with redirect URI `https://sync.pairnets.app/login/google/callback` and scopes
   `openid email`; its id goes in `GOOGLE_CLIENT_ID`. **Turnstile:** a widget for `sync.pairnets.app` (site key in
   `TURNSTILE_SITE_KEY`). **Resend:** the sending domain for `noreply@pairnets.app` and an API key.
6. **Secrets** (dashboard or `npx wrangler secret put NAME` in `cloud/`): `SIGNING_KEY` (from `npm run keygen`),
   `HB_MASTER` and `COOKIE_KEY` (each from `npm run keygen -- --random`), `GOOGLE_CLIENT_SECRET`, `TURNSTILE_SECRET`,
   `RESEND_API_KEY`, `CF_API_TOKEN` (step 2) and `CF_ACCOUNT_ID` (the account's id, on its Overview page).
7. **Deploy** `pairnets-sync` (`npx wrangler deploy` in `cloud/`), then uncomment the `routes` line in `wrangler.toml`
   (custom domain `sync.pairnets.app`) and deploy again.
8. The public half of the signing key goes into `src/Pairnets.Server/Auth/HostedKeys.cs` (only for servers on their own
   domain); keep a second "next" key offline for rotation.
9. **Try it** with one server: run the installer, open the link it prints, choose **Add this nest**, and check that
   `https://sync.pairnets.app/n/<server id>/api/health` answers `ok` within about a minute.

Later:

- **A new router version:** `cd router && npx wrangler deploy`. That deploy clears the router's links; `pairnets-sync`
  puts them back by itself on the next request to any server (within about a minute; servers show "not connected"
  until then). The hourly cron also puts back any server that is missing from the router.
- **Status of the router:** `npx wrangler d1 execute pairnets-sync --remote --command "SELECT * FROM router_state"`
  shows the deploy count, the last deploy and the last error (never the token).
- **Emergency:** removing a server on the account page deletes its link and its tunnel; deleting the `CF_API_TOKEN`
  secret stops any new server from being added (existing ones keep working).
