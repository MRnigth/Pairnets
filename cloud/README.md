# Pairnets Cloud (`cloud/`)

A small hosted account service, `pairnets-id`, meant to run as a Cloudflare Worker at `https://id.pairnets.app`.
People sign in once with Google or email; the service knows which nests (Pairnets servers) are theirs and can vouch for
them to their own nest with a short-lived signed message, so they can sign in to the nest's website without setting up
Google or email on the server. Files never go through this service: each owner still runs their own nest.

**Status: nothing is deployed.** There is no `id.pairnets.app`, no database, no keys and no Google client yet. Released
servers and apps do not talk to this service. Setting it up later needs the owner (Cloudflare dashboard, Google OAuth
client, secrets, the production signing key; see `CONTRACT.md` section 10 and the checklist below).

## What is here

| Path | What |
|---|---|
| `CONTRACT.md` | The agreement between this service and the nest (C# server in `src/Pairnets.Server`): formats, endpoints, the signed login assertion, the HMAC-signed nest requests, the D1 schema, test vectors, threats. Read it first. |
| `CONTRACT-NOTES-worker.md` | Where the Worker had to fill a gap in the contract, and the one deliberate difference. |
| `src/` | The Worker (TypeScript, no runtime dependencies, WebCrypto only). `app.ts` routes; `routes/` holds the endpoints. |
| `migrations/` | D1 migrations: `0001_init.sql` is exactly the SQL of `CONTRACT.md` section 7 (a test checks this). |
| `test/` | vitest tests in the Workers runtime (Miniflare, local D1). `test/vectors/assertion-v1.json` holds the golden vectors that the C# tests also read. |
| `scripts/keygen.mjs` | `npm run keygen`: prints a new ES256 signing key pair (nothing is written to disk). |
| `wrangler.toml` | Worker settings with placeholder ids, the variables, the hourly Cron, and the list of secrets. |

## Running the tests

Needs Node 22 or newer.

```
cd cloud
npm ci
npx tsc --noEmit        # type check
npm test                # vitest: the Worker in a local Workers runtime, with D1
npm run vectors         # only on purpose: regenerates test/vectors/assertion-v1.json with new test keys
```

Every key the tests use is generated while they run, and the tests never reach the network (Google, Turnstile and
Resend are fakes). Nothing secret is ever committed: real secrets live only in the Cloudflare dashboard (or
`wrangler secret put`), and locally in `cloud/.dev.vars`, which git ignores (as are `.wrangler/` and `node_modules/`).

The C# side of the contract is tested with the rest of the repo (`dotnet test Pairnets.sln`), including
`HostedGoldenVectorTests`, which checks the vectors written here.

## Setting it up later (owner checklist)

1. Cloudflare: create the D1 database `pairnets-id`, put its id in `wrangler.toml`, apply `migrations/`, attach the
   custom domain `id.pairnets.app`, and create a Turnstile widget for that name (site key in `TURNSTILE_SITE_KEY`).
2. Google Cloud: a web OAuth client with redirect URI `https://id.pairnets.app/login/google/callback` and scopes
   `openid email`; its id goes in `GOOGLE_CLIENT_ID`.
3. Resend: verify the sending domain for `noreply@pairnets.app` and make an API key.
4. Secrets (dashboard or `npx wrangler secret put NAME`): `SIGNING_KEY` (from `npm run keygen`), `HB_MASTER` and
   `COOKIE_KEY` (each from `npm run keygen -- --random`), `GOOGLE_CLIENT_SECRET`, `TURNSTILE_SECRET`, `RESEND_API_KEY`.
5. The public half of the signing key goes into `src/Pairnets.Server/Auth/HostedKeys.cs`; keep a second "next" key
   offline for rotation.
