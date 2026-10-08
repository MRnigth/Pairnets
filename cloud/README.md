# Pairnets Cloud (`cloud/`)

A small hosted account service, `pairnets-id`, meant to run as a Cloudflare Worker at `https://id.pairnets.app`.
People sign in once with Google or email; the service knows which nests (Pairnets servers) are theirs and can vouch for
them to their own nest with a short-lived signed message, so they can sign in to the nest's website without setting up
Google or email on the server. Files never go through this service: each owner still runs their own nest.

**Status: nothing is deployed.** There is no `id.pairnets.app`, no database, no keys and no Google client yet. Released
servers and apps do not talk to this service. Setting it up later needs the owner (Cloudflare dashboard, Google OAuth
client, secrets, the production signing key; see `CONTRACT.md` section 10).

## What is here

| Path | What |
|---|---|
| `CONTRACT.md` | The agreement between this service and the nest (C# server in `src/Pairnets.Server`): formats, endpoints, the signed login assertion, the HMAC-signed nest requests, the D1 schema, test vectors, threats. Read it first. |
| `src/` | The Worker (TypeScript, no runtime dependencies, WebCrypto only). *Not written yet.* |
| `migrations/` | D1 migrations, starting with `0001_init.sql` (the SQL is in `CONTRACT.md` section 7). *Not written yet.* |
| `test/` | vitest tests (Workers pool). `test/vectors/assertion-v1.json` holds the golden vectors that the C# tests also read. *Not written yet.* |

## Running the tests (once the code exists)

Needs Node 20 or newer.

```
cd cloud
npm ci
npx tsc --noEmit        # type check
npm test                # vitest: the Worker in a local Workers runtime, with D1
npm run vectors         # only on purpose: regenerates test/vectors/assertion-v1.json with new test keys
```

Every key the tests use is generated while they run. Nothing secret is ever committed: real secrets live only in the
Cloudflare dashboard (or `wrangler secret put`), and locally in `cloud/.dev.vars`, which git ignores (as are
`.wrangler/` and `node_modules/`).

The C# side of the contract is tested with the rest of the repo (`dotnet test Pairnets.sln`), including
`HostedGoldenVectorTests`, which checks the vectors written here.
