# Pairnets Cloud, version 2: one address for everyone

Status: **draft for building, nothing deployed.** 2026-10-09. This file extends `CONTRACT.md` (version 1). Where they
disagree, this file wins. Words MUST / MUST NOT as in `CONTRACT.md`.

## 0. What changes, in one picture

Everyone uses **one address, `https://sync.pairnets.app`**. Nobody gets a name of their own. A person signs in with
their Pairnets account; the service quietly passes each app's traffic to that person's own server (the nest), which
keeps all the files. Each nest has its own Cloudflare Tunnel with **no public hostname at all**: the only way in is a
private Workers VPC link that belongs to the service.

```
 app ──https──> sync.pairnets.app/n/<nestId>/api/...  (Worker "pairnets-sync": accounts, pages, relay)
                        │ service binding
                        v
                Worker "pairnets-router" (no route; one VPC link per nest: N_<nestId> = tunnel)
                        │ Workers VPC (private)
                        v
                cloudflared on the nest ──> http://127.0.0.1:5075  (the nest; files stay here)
```

Proven on the owner's account on 2026-10-09: a `vpc_networks` binding with `tunnel_id` reaches `127.0.0.1` and
`localhost` on the cloudflared host (no ingress rule, no DNS record); 50 MB uploads, 200 MB downloads, SSE and
WebSockets pass; 1,000 such bindings deploy in one Worker; an open WebSocket survives a redeploy; a new binding is live
in about 30 s.

What stays: version 1's accounts, identities, sessions, cookies, CSRF rule, email sign-in, Google sign-in, rate-limit
machinery, audit, the per-nest key derivation (§5.1 of CONTRACT.md) and the app device-code sign-in (§6.9, extended
below). Nests on their **own domain** (`install.sh --public-url`) keep working exactly as today; linking them to an
account (version 1 claim and nest login) stays available but is not part of the main path.

**Origin.** `PUBLIC_ORIGIN = https://sync.pairnets.app` (was `id.pairnets.app`). The Worker is renamed `pairnets-sync`.

## 1. New formats

| Thing | Format | Regex / notes |
|---|---|---|
| Server link id (internal) | `svl_` + 26 base32-lower | |
| Server device code | `psd_` + base64url(32 bytes) | installer memory only; stored as SHA-256 |
| Server user code | 8 base32-upper, shown `XXXX-XXXX` | normalised like the app user code (CONTRACT §1.4) |
| Relay address of a nest | `https://sync.pairnets.app/n/<nestId>/` | what apps store as their server URL |
| Nest key | 32 bytes = CONTRACT §5.1 `heartbeatKey` (same derivation, same `key_version`) | sent once to the installer as base64url (43 chars) |
| Router binding name | `N_` + nest id, e.g. `N_nst_0123…` | |
| Nonce | base64url(16 bytes) (22 chars) | §4 |

Nest ids are made by the service (`nst_` + 26 base32-lower, CONTRACT §1.2).

## 2. Adding a server (the installer's device-code flow)

The installer runs on the nest machine with no options (`curl …/get.sh | sudo bash`). It never asks the person to type
anything into the terminal: approving happens in a browser on any device.

1. `POST /v1/servers/start` (no auth, no Origin; per-IP 10/hour) body `{"hostname":"soro","serverVersion":"1.0.48"}`
   (both optional strings, at most 64 characters, control characters removed) → **200**
   ```json
   {"deviceCode":"psd_…","userCode":"ABCD-EFGH","verificationUri":"https://sync.pairnets.app/add",
    "verificationUriComplete":"https://sync.pairnets.app/add?code=ABCD-EFGH","expiresIn":900,"interval":3}
   ```
2. The installer prints the link and the code and polls. The person opens `/add?code=…` (session required, else
   `/login?next=/add?code=…`). The page shows the hostname, version and code, and **Add this nest** / **Not mine**.
   It uses:
   - `GET /v1/servers/requests/{userCode}` (session) → 200 `{userCode, hostname, serverVersion, createdAt, expiresAt, status}` / 404.
   - `POST /v1/servers/approve` (session + Origin) `{userCode, approve: true|false, label?}` →
     200 `{status:"approved", nestId}` · 200 `{status:"denied"}` · 404 `not_found` · 409 `already_decided` ·
     400 `expired` · 409 `nest_limit` (an account has at most 3 nests) · 503 `full` (MAX_NESTS reached) ·
     502 `cloudflare_failed`.
   On approve the service, in this order: creates the nest row (`mode='relay'`, `status='pending'`,
   `label` = given label or hostname or "My server", `public_url=''`); creates the tunnel
   (`POST /accounts/{acc}/cfd_tunnel {name:"pairnets-<nestId>", config_src:"cloudflare"}`), sets its ingress to
   `[{"service":"http_status:404"}]` (no hostname is ever routed), stores `tunnel_id`; marks the link `approved` with
   the nest id; then starts a **router deploy** (§5). Any Cloudflare failure deletes what was made (tunnel, nest row)
   and answers 502 `cloudflare_failed`; a failed undo leaves the nest row `status='broken'` for the sweep (§7).
3. `POST /v1/servers/poll` (no auth, no Origin) `{"deviceCode":"psd_…"}` →
   - 200 `{"status":"pending"}` · 200 `{"status":"denied"}` · 429 `slow_down` (faster than `interval`) ·
     400 `expired` (unknown, expired or already delivered)
   - 200, **once** (the link becomes `delivered`):
     ```json
     {"status":"approved","nestId":"nst_…","label":"soro","relayUrl":"https://sync.pairnets.app/n/nst_…/",
      "tunnelToken":"eyJ…","nestKey":"<43 chars>","keyVersion":1,"serviceUrl":"https://sync.pairnets.app"}
     ```
     The tunnel token is read from Cloudflare at this moment (`GET …/cfd_tunnel/{id}/token`) and never stored. The
     nest key is derived (CONTRACT §5.1), never stored.
4. The installer writes the token to `/etc/pairnets/tunnel.env` and the nest settings (§3) to
   `/etc/pairnets/pairnets.env` (both root, mode 600), starts `pairnets-server` and `pairnets-tunnel`, and waits for
   `GET https://sync.pairnets.app/n/<nestId>/api/health` to answer `ok` (the router needs about 30 s; wait up to 3
   minutes). The service marks the nest `active` the first time a relayed request reaches it.

A user code lives 15 minutes. Unknown or wrong codes on the page: 404 "That code is not valid or has expired".

## 3. The nest in relay mode

Settings (environment, written by `install.sh` into `/etc/pairnets/pairnets.env`):

| Setting | Value |
|---|---|
| `Sync__RelayNestId` | the nest id |
| `Sync__RelayKey` | the nest key, base64url (43 chars) |
| `Sync__RelayServiceUrl` | `https://sync.pairnets.app` (default when unset) |
| `Sync__TrustProxyHeaders` | `true` |
| `ASPNETCORE_URLS` | `http://127.0.0.1:5075` (the router always calls port 5075) |

`Sync:PublicUrl` stays unset in relay mode, so the nest's own website, passkeys, email links and Google are off and
`/api/pair/*` answers 409 (there is no website to approve on). Both relay settings MUST be set together or not at all
(startup error otherwise); the key MUST decode to exactly 32 bytes.

Behaviour in relay mode:

- **Client address.** When relay mode is on, `ProxyClientAddressMiddleware` takes the client address from
  `X-Pairnets-Client-IP` (set by the service, which removes any value the caller sent) before `CF-Connecting-IP` and
  `X-Forwarded-For`, still only on loopback connections.
- **`GET /api/hello`** adds `"relay":{"nestId":"nst_…","serviceUrl":"https://sync.pairnets.app"}` and keeps
  `publicUrl:null`, `signIn:false`. Apps in relay mode never auto-move to another address (§6).
- **Admin endpoints** (§4) under `/api/relay/`, anonymous for `TokenAuthMiddleware`, each checked with the nest key.
- Everything else (the sync API, `/hub`, uploads in pieces, health) is unchanged and works through the relay.

## 4. The service's signed calls to the nest (`/api/relay/*`)

Only the service can reach a relayed nest, but the relay also carries apps' requests, so admin calls are signed.

Headers on every admin request:

| Header | Value |
|---|---|
| `X-Pairnets-Nest` | the nest id |
| `X-Pairnets-Ts` | Unix seconds, decimal |
| `X-Pairnets-Nonce` | base64url(16 random bytes), 22 chars |
| `X-Pairnets-Sig` | base64url(HMAC-SHA256(K, M)), 43 chars |

```
K = the 32-byte nest key
M = UTF-8( "ra1" + "\n" + nestId + "\n" + ts + "\n" + nonce + "\n" + METHOD + " " + pathAndQuery + "\n"
           + lowercase_hex(SHA-256(exact body bytes)) )
```

`METHOD` is uppercase; `pathAndQuery` is the path as the nest receives it (`/api/relay/devices`, no `/n/<id>` prefix).
An empty body hashes the empty string.

The nest checks, in order (any failure → **401** `{"error":"bad_signature"}` with no other detail):
1. Relay mode is on and `X-Pairnets-Nest` equals `Sync:RelayNestId`.
2. `X-Pairnets-Sig` is 43-char strict base64url and verifies (constant time).
3. `abs(now - ts) <= 120`.
4. The nonce has not been seen in the last 10 minutes (kept in memory; at most 10,000 entries, oldest dropped first).

Endpoints (JSON; bodies at most 4 KiB):

| Endpoint | Body | 200 answer |
|---|---|---|
| `GET /api/relay/status` | | `{"serverVersion":"1.0.48","devices":2,"freeBytes":123,"dataBytes":456}` |
| `GET /api/relay/devices` | | `{"devices":[{"id":"…","name":"Laptop","system":"Windows 11","createdAt":"…","lastSeen":"…"|null}]}` |
| `POST /api/relay/devices` | `{"name":"Laptop","system":"Windows 11","approvedBy":"m***@gmail.com"}` | `{"id":"…","name":"Laptop","key":"pn_…"}` |
| `DELETE /api/relay/devices/{id}` | | `{"removed":true}`; unknown id → 404 `{"error":"not_found"}` |

`POST` creates the device exactly like an approved pairing (`AuthStore.AddDevice`, `approvedBy = "approved by
<approvedBy> on sync.pairnets.app"`); names are made unique by the nest. `DELETE` does what `DELETE /api/devices/{id}`
does (revoke, forget the cached key, `DeviceRemoved` on the hub, close the device's connections).

## 5. The relay and the router

**Relay** (`pairnets-sync`): `ANY /n/{nestId}/{rest}`.
1. `nestId` matches the nest id regex, the nest exists with `mode='relay'` and `status` in (`pending`,`active`), else
   **404** `{"error":"nest_unknown","message":"This server is not linked to Pairnets any more."}`.
2. `rest` MUST start with `api/` or be `hub` / start with `hub/` or `hub?`; and MUST NOT start with `api/relay/` → else
   **404** `{"error":"not_found"}`.
3. Forward to the router: same method, body streamed, headers copied except `Cookie`, `X-Pairnets-Client-IP`,
   `X-Pairnets-Route`, `X-Pairnets-Sig`, `X-Pairnets-Nonce`, `X-Pairnets-Ts`, `X-Pairnets-Nest` and `CF-*`; adds
   `X-Pairnets-Client-IP: <CF-Connecting-IP>` and `X-Pairnets-Route: <nestId>`. The path becomes `/` + `rest`.
   WebSocket upgrades are passed through.
4. If the router or the VPC link fails (exception, or the router answers 502/503 from the link):
   **503** `{"error":"nest_offline","message":"Your server is not connected right now. Check that it is on."}`.
   Every relay error is JSON (apps read non-JSON errors as "old server" or "tunnel down").
5. The first successful answer from a `pending` nest marks it `active` (`confirmed_at`), at most one write per minute.
6. Rate limits on the relay, two budgets (`relay.ts`; reasons in `docs/DECISIONS.md`, "Sync feedback"):
   - per address, 600 requests a minute for everything without a computer's key (`X-Sync-Token`, `Authorization:
     Bearer`, or `access_token` on the hub) and everything that fails: an unknown nest, a refused path, a key the nest
     answers 401/403, a nest that is offline. Over it, every relayed request from that address gets 429 until the minute
     is over. This is checked first, before the nest is looked up.
   - per nest and computer key, 6,000 requests a minute: what a signed-in app sends. Each computer has its own, so
     two computers behind one home address do not share one budget. The key is only ever hashed.
   Every 429 is `{"error":"rate_limited",...}` with `Retry-After` (seconds to the end of the minute). The apps wait that
   long, send the same request again and go on one at a time for a while. Bodies are not limited beyond Cloudflare's
   own 100 MB.

**Router** (`pairnets-router`, `workers_dev = false`, no routes; reachable only by the `ROUTER` service binding):
reads `X-Pairnets-Route`, picks binding `N_<nestId>`, removes the header, fetches
`http://127.0.0.1:5075<path><query>` through it with the same method, headers and body (`redirect: "manual"`), and
returns the answer as is. Unknown route → 404 `{"error":"nest_unknown"}`; a failing link → 503
`{"error":"nest_offline"}`.

**Router deploy.** The router's code is deployed once from `cloud/router/` (wrangler). Its bindings are changed by
`pairnets-sync` through the Cloudflare API: `PATCH /accounts/{acc}/workers/scripts/pairnets-router/settings`
(multipart, part `settings` = JSON `{"bindings":[…]}`; verified on 2026-10-09: the answer is 200 with the new
`bindings`, the code is kept, and the running router sees the new list within about 20 s), the full list every time: one
`{"type":"vpc_network","name":"N_<nestId>","tunnel_id":"<tunnel id>"}` per nest with `mode='relay'` and a tunnel.
Deploys are serialised with a D1 lease (`router_state`, one row): a deploy in progress less than 2 minutes old makes the
next one wait (the cron and the next approval retry); `router_state.version` counts deploys, `nests.routed_version` is
set for every nest included. The hourly cron redeploys when any relay nest's `routed_version` is NULL.

## 6. Apps

**Sign in (CONTRACT §6.9, extended).** The app calls `POST /v1/app/start` as before; the page `/app?code=` now also
lets the person choose which server this computer joins (default: their only one), then **Allow**.
`POST /v1/app/approve` takes `{userCode, approve, nestId}` (`nestId` required when approving, except when the account has no nest yet, below; it MUST be the
account's relay nest). The first successful `POST /v1/app/poll` after approval asks the nest for a key
(`POST /api/relay/devices` with the app's name and system) and answers, **once**:

```json
{"status":"approved","appToken":"pca_…","expiresIn":3600,"email":"you@example.com",
 "nest":{"id":"nst_…","label":"soro","serverUrl":"https://sync.pairnets.app/n/nst_…/"},
 "device":{"id":"…","name":"Laptop","key":"pn_…"}}
```

If the nest cannot be reached at that moment the poll answers **503** `nest_offline` and the login stays `approved`
(the app keeps polling; the key is asked for again on the next poll). The service never stores the device key.

**No nest yet.** If the account has no relay nest, the page says how to set one up and still offers **Allow**, which
sends `{userCode, approve: true}` without `nestId` (allowed only while the account has none; otherwise 400
`bad_request`). The login is then held for 30 minutes from that moment, and every poll answers
`200 {"status":"no_nest","email":"you@example.com"}` until the account has a relay nest (`pending` or `active`); the next
poll after that joins the newest one and continues as above. The app shows "Your account has no nest yet" and finishes
by itself once the nest is added.

**What the app stores:** `ServerUrl = nest.serverUrl`, the device key (protected as today), `DeviceId`, and the
account email for display. Everything else works as for a nest on its own domain: all API paths are relative to the
server URL (`api/…`, `hub`), so the `/n/<nestId>/` prefix carries through.

**Relay mode in the app** = `ServerUrl` is `https://sync.pairnets.app/n/<nest id>/`. Then:
- never move to `hello.publicUrl` (it is null anyway);
- "Manage computers" opens `https://sync.pairnets.app/account`;
- "Add another computer" says: install Pairnets and choose Continue with email (or Google) with the same account;
- a 503 `nest_offline` reads "Your server is not connected right now".

## 7. Account page, removal and the sweep

`/account` lists the account's servers: label, online (a relayed `GET /api/relay/status` within the last request,
timeout 5 s), version, free space, and per server its computers (`GET /api/relay/devices`) with **Remove** (`DELETE
/api/relay/devices/{id}`), plus **Remove this nest from my account** (deletes the router binding, the tunnel and the nest row; the
nest itself keeps its files and stops being reachable). JSON for the page: `GET /v1/servers` (session) →
`[{id,label,status,online,serverVersion,freeBytes,devices:[…]}]`, `DELETE /v1/servers/{id}/devices/{deviceId}`,
`DELETE /v1/servers/{id}` (session + Origin).

The hourly cron also: deletes expired server links; redeploys the router when needed (§5); for nests with
`status='broken'` (or `pending` older than 1 hour and never reached): deletes the tunnel, redeploys, deletes the row.

## 8. Limits and configuration

- `MAX_NESTS` (default 900): relay nests in all, under Cloudflare's 1,000 tunnels per account.
- At most 3 nests per account; 10 server starts an hour per IP; 20 approvals an hour per account.
- New Worker secrets: `CF_API_TOKEN` (Account: Cloudflare Tunnel Edit, Workers Scripts Edit, and the Connectivity
  Directory permission needed to bind VPC networks), `CF_ACCOUNT_ID`. Existing: `HB_MASTER`, `COOKIE_KEY`,
  `SIGNING_KEY`, `RESEND_API_KEY`, `TURNSTILE_SECRET`, `GOOGLE_CLIENT_SECRET`.
- Plan: Workers Paid ($5/month) for request volume. Workers VPC is free during its beta.

## 9. Threats (additions to CONTRACT §9)

| Threat | What stops it |
|---|---|
| Someone reaches a nest directly | The tunnel has no public hostname; only the router's VPC binding reaches it. |
| An app (or anyone) calls admin endpoints through the relay | The relay refuses `api/relay/*`; the nest requires an HMAC with the nest key. |
| Replay of a captured admin call | 120 s window plus a 10-minute nonce memory. |
| A nest's own web pages on the shared origin | The relay passes only `api/*` and `hub`; the nest's website is off in relay mode. |
| Guessing a nest id to reach someone's server | Ids have 130 bits; even then every sync call needs a device key. |
| A spoofed client address for the nest's throttles | The relay overwrites `X-Pairnets-Client-IP` with Cloudflare's view of the caller. |
| The service is compromised | It could issue device keys for any nest: the same trust as version 1's login assertions. Files still never stay on the service. |
