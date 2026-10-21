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
     400 `expired` · 409 `nest_limit` (an account has at most 4 nests) · 503 `full` (MAX_NESTS reached) ·
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
| `GET /api/relay/status` | | `{"serverVersion":"1.0.48","devices":2,"freeBytes":123,"dataBytes":456,"name":"soro"|null}` |
| `PUT /api/relay/name` | `{"name":"soro"}` | `{"name":"soro"}`; empty → 400 `{"error":"bad_name"}` |
| `GET /api/relay/devices` | | `{"devices":[{"id":"…","name":"Laptop","system":"Windows 11","createdAt":"…","lastSeen":"…"|null}]}` |
| `POST /api/relay/devices` | `{"name":"Laptop","system":"Windows 11","approvedBy":"m***@gmail.com"}` | `{"id":"…","name":"Laptop","key":"pn_…"}` |
| `PATCH /api/relay/devices/{id}` | `{"name":"Work laptop"}` | `{"id":"…","name":"Work laptop"}` (the name stored, `"Work laptop (2)"` when taken); bad name → 400 `{"error":"bad_name"}`; unknown id → 404 `{"error":"not_found"}` |
| `DELETE /api/relay/devices/{id}` | | `{"removed":true}`; unknown id → 404 `{"error":"not_found"}` |

`POST` creates the device exactly like an approved pairing (`AuthStore.AddDevice`, `approvedBy = "approved by
<approvedBy> on sync.pairnets.app"`); names are made unique by the nest. `DELETE` does what `DELETE /api/devices/{id}`
does (revoke, forget the cached key, `DeviceRemoved` on the hub, close the device's connections).

Friends (§10) add to this list, on nests whose status says `"features":["friends"]`:

| Endpoint | Body | 200 answer |
|---|---|---|
| `GET /api/relay/status` | | adds `"features":["friends"]` and `"sealed":true|false` (an encrypted nest) |
| `GET /api/relay/devices` | | each device adds `"access":"owner"|"view"|"edit"`, `"folders":[…]|null`, `"person":"Anna Berg · a•••@gmail.com"|null`, `"personId":"acc_…"|null` |
| `POST /api/relay/devices` | adds optional `"access"`, `"folders"`, `"person"`, `"personId"` (missing = an owner's computer) | as above; folders on a sealed nest → 400 `{"error":"sealed_whole_only"}`; bad values → 400 `{"error":"bad_access"}` |
| `PATCH /api/relay/people/{personId}` | `{"access":"view"|"edit","folders":[…]|null}` | `{"devices":n}`: every computer of that person changes (0 is fine); sealed + folders → 400 `sealed_whole_only` |
| `DELETE /api/relay/people/{personId}` | | `{"devices":n}`: every computer of that person is removed like `DELETE …/devices/{id}` (0 is fine) |
| `PUT /api/relay/requests` | `{"requests":[{"id":"inv_…","name":"Oskar","email":"oskar@…","access":"view","folders":null,"at":"…"}]}` (at most 20) | replaces the list of people waiting for the owner's answer; only the owner's computers read it |

## 5. The relay and the router

**Relay** (`pairnets-sync`): `ANY /n/{nestId}/{rest}`.
1. `nestId` matches the nest id regex, the nest exists with `mode='relay'` and `status` in (`pending`,`active`), else
   **404** `{"error":"nest_unknown","message":"This server is not linked to Pairnets any more."}`.
   The row is read from D1 once and then reused for **30 s** by that Worker instance (at most 1,000 rows are kept), so
   a nest that is removed or unlinked stops being relayed within 30 s at most; sooner when the router has already
   dropped its link (step 4).
2. `rest` MUST start with `api/` or be `hub` / start with `hub/` or `hub?`; and MUST NOT start with `api/relay/` → else
   **404** `{"error":"not_found"}`.
3. Forward to the router: same method, body streamed, headers copied except `Cookie`, `X-Pairnets-Client-IP`,
   `X-Pairnets-Route`, `X-Pairnets-Sig`, `X-Pairnets-Nonce`, `X-Pairnets-Ts`, `X-Pairnets-Nest` and `CF-*`; adds
   `X-Pairnets-Client-IP: <CF-Connecting-IP>` and `X-Pairnets-Route: <nestId>`. The path becomes `/` + `rest`.
   WebSocket upgrades are passed through. The answer keeps only an allowlist of the server's headers: `Content-Type`,
   `Content-Length`, `Content-Encoding`, `Content-Range`, `Accept-Ranges`, `ETag`, `Last-Modified`, `Retry-After`, `Allow`
   and the `X-Pairnets-Server-Id/Version/Hash/Modified-Ms` headers (also under their old `X-Tether-` names). Everything
   else is dropped (cookies, `Content-Disposition`, `Link`, `Refresh`, `NEL`, CORS ...). `Location` is kept only when it
   points inside the same nest (`/n/{nestId}/...`, sent as a relative address); any other redirect target is removed.
4. If the router or the VPC link fails (exception, or the router answers 502/503 from the link):
   **503** `{"error":"nest_offline","message":"Your server is not connected right now. Check that it is on."}`.
   Every relay error is JSON (apps read non-JSON errors as "old server" or "tunnel down").
   When the router says it does not know the nest, the relay reads the row again (the reused one may be stale): gone
   or no longer linked → **404** `nest_unknown` as in step 1; still linked → the router self-heal, and 503.
5. The first successful answer from a `pending` nest marks it `active` (`confirmed_at`). The update only matches
   `pending` rows, and the reused row (step 1) is marked active at once, so it is one write per server, not one per
   request.
6. Rate limits on the relay, four budgets (`relay.ts`; reasons in `docs/DECISIONS.md`), each counted by one of
   Cloudflare's Workers Rate Limiting bindings, not in D1, so a relayed request costs no database write. The counts are
   per Cloudflare location and approximate, which is fine for limits that only stop floods and abuse.
   - per address, **20,000 requests a minute** for everything (`RELAY_LIMIT`): a flood ceiling, checked first.
   - per nest and computer key, **6,000 requests a minute** (`RELAY_DEVICE_LIMIT`): what a signed-in app sends
     (`X-Sync-Token`, `Authorization: Bearer`, or `access_token` on the hub). Each computer has its own, so two
     computers behind one home address do not share one budget. The limiter only sees a keyed hash of nest and key.
   - per address, **600 requests a minute** for everything without a computer's key and everything that fails: an
     unknown nest, a refused path, a key the nest answers 401/403, a nest that is offline (`RELAY_FAIL_LIMIT`). The
     request that goes over it turns the address off: every relayed request from it then gets 429 for 60 s (remembered
     by the Worker instance, as a binding cannot be read without counting).
   - per nest, **30,000 requests a minute** for everything relayed to it, from all its computers and addresses
     together (`RELAY_NEST_LIMIT`): five computers at their full budget fit under it, so it only stops one nest from
     taking the whole service. Going over it is not the caller's doing, so it isn't counted against the address.

   Every 429 is `{"error":"rate_limited",...}` with `Retry-After` (60, or the seconds left of a turned-off address).
   The apps wait that long, send the same request again and go on one at a time for a while. Bodies are not limited
   beyond Cloudflare's own 100 MB.

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
 "nest":{"id":"nst_…","label":"soro","serverUrl":"https://sync.pairnets.app/n/nst_…/",
         "sharedBy":null,"access":"owner","folders":null},
 "device":{"id":"…","name":"Laptop","key":"pn_…"}}
```

For a nest shared with the account (§10), `sharedBy` is the owner's name (username, or the part of the email before
the @), `access` is `"view"` or `"edit"` and `folders` the shared top-level folders (`null` = the whole nest).

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
`DELETE /v1/servers/{id}` (session + Origin). **Rename** (session + Origin): `PATCH /v1/servers/{id}/devices/{deviceId}`
`{name}` → `PATCH /api/relay/devices/{id}` → `{id,name,requested}` (`name` is what the nest stored); `PATCH
/v1/servers/{id}` `{label}` → the label changes, then `PUT /api/relay/name` (best effort). A status call whose `name`
differs from the label sends the label again; a nest without `name` in its status is never sent one.

The hourly cron also: deletes expired server links; redeploys the router when needed (§5); for nests with
`status='broken'` (or `pending` older than 1 hour and never reached): deletes the tunnel, redeploys, deletes the row.

## 8. Limits and configuration

- `MAX_NESTS` (default 900): relay nests in all, under Cloudflare's 1,000 tunnels per account.
- At most 4 nests per account (`MAX_NESTS_PER_ACCOUNT`, both kinds together; lowering it never removes a nest an account
  has); 10 server starts an hour per IP; 20 approvals an hour per account.
- The relay's four limits (§5 step 6) are Workers Rate Limiting bindings in `wrangler.toml` (`[[ratelimits]]`):
  `RELAY_LIMIT` (`namespace_id = "1001"`, 20,000 a minute per address), `RELAY_DEVICE_LIMIT` (`"1002"`, 6,000 a
  minute per nest and computer key) and `RELAY_FAIL_LIMIT` (`"1003"`, 600 a minute per address for keyless and
  failing requests) and `RELAY_NEST_LIMIT` (`"1004"`, 30,000 a minute per nest). `RELAY_PER_MINUTE`,
  `RELAY_DEVICE_PER_MINUTE`, `RELAY_IP_PER_MINUTE` and `RELAY_NEST_PER_MINUTE` in `src/relay.ts` name the
  same numbers, and a test checks that they match. A `namespace_id` is a number chosen here, not an account id or a
  key; it only has to differ from any other rate limiter on the account. Nothing to create by hand: `wrangler deploy`
  sets the bindings up. Without them (a local run) the relay does not limit and logs one warning each; it never falls
  back to D1. Nest rows are reused for 30 s
  (§5 step 1).
- New Worker secrets: `CF_API_TOKEN` (Account: Cloudflare Tunnel Edit, Workers Scripts Edit, and the Connectivity
  Directory permission needed to bind VPC networks), `CF_ACCOUNT_ID`. Existing: `HB_MASTER`, `COOKIE_KEY`,
  `SIGNING_KEY`, `RESEND_API_KEY`, `TURNSTILE_SECRET`, `GOOGLE_CLIENT_SECRET`.
- Plan: Workers Paid ($5/month) for request volume. Workers VPC is free during its beta.
- Friends (§10): at most 10 per nest (members and open invites); 20 invites a day and 20 join requests an hour per
  account; invite emails 5 a day for the whole service, outside the 95 that sign-ins and notices share.

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
| An invite link is forwarded or leaks | Using it only makes a request; the owner sees the person's name and email and decides. It works once and for 7 days. |
| A friend's computer reaches more than was shared | The nest gates every call by the key's access and folders; the service never asks a nest without `friends` for a friend's key. |

## 10. Friends on a nest (1.0.61.2)

An owner can let other people (friends, each with their own Pairnets account) use a nest: the whole nest or chosen
top-level folders, **view only** (the default) or **can change**. At most **10 friends per nest**, members and open
invites together. An encrypted ("sealed") nest is shared **whole only**. **A link alone never lets anyone in**: the
person asks to join, and the owner lets them in or declines. Words on screen: friend, People, View only, Can change,
Whole nest, "Shared by <name>", "wants to join", Let in, Decline, Ask to join (never member, role or ACL).

**Formats.** Invite id `inv_` + 26 base32-lower. Invite token `pni_` + 32 base32-lower (160 bits), the link
`https://sync.pairnets.app/invite?t=pni_…`; it is stored as it is, so the owner can copy the link again (using it only
makes a request). `access`: `"owner"` (only on the nest), `"view"`, `"edit"`. `folders`: `null` (the whole nest) or
1-20 top-level folder names, exact case, 1-255 characters each, no `/`, `\`, control or invisible characters, not `.`
or `..`, no leading or trailing space; repeats are dropped. Bad values → 400 `bad_access`.

**Tables** (migration `0006_friends.sql`): `nest_members(nest_id, account_id, access, folders, added_at, synced,
removed_at)` and `nest_invites(id, nest_id, token, email, access, folders, created_by, created_at, expires_at,
requested_by, requested_at, decided_at, decision 'in'|'declined', cancelled_at)`. `synced = 0` means the nest still
has to hear about a change, or (with `removed_at`) a removal.

**The owner's routes** (browser session; mutations carry the Origin; another account's nest is 404):

| Method and path | Request | Success | Errors |
|---|---|---|---|
| `POST /v1/servers/{id}/invites` | `{access?, folders?, email?}` | 201 `{id, link, expiresAt, emailed}`; the link lasts 7 days; with `email` Pairnets sends it (the `invite` mail budget; when that is used up, `emailed:false` and no address is kept) | 400 `bad_access`, `bad_request` (not an address), `sealed_whole_only`; 409 `own_nest` (the owner's own address), `too_many_friends`, `nest_offline`, `nest_too_old` (no `friends` in the nest's live status); 429 `rate_limited` (20 a day per account) |
| `GET /v1/servers/{id}/people` | — | 200 `{members:[{accountId, name, email, access, folders, addedAt, synced}], invites:[{id, link, email, access, folders, createdAt, expiresAt, status:"open"|"requested", requester:{accountId, name, email, at}|null}], max:10}` | 404 |
| `POST /v1/servers/{id}/invites/{inv}/approve` | — (**recent sign-in**) | 200 `{status:"in", member}`; writes the member row, emails the friend ("You're in Home nest", `invite` budget), sends the nest the waiting list | 401 `reauth_required`; 400 `bad_request` (nobody asked yet); 409 `already_decided`, `cancelled`; 404 |
| `POST /v1/servers/{id}/invites/{inv}/decline` | — | 200 `{status:"declined"}`; the waiting list is sent again | as approve, no recent sign-in needed |
| `DELETE /v1/servers/{id}/invites/{inv}` | — | 204 (also when already cancelled); the link stops working | 409 `already_decided`; 404 |
| `PATCH /v1/servers/{id}/people/{accountId}` | `{access?, folders?}` (a field left out keeps its value; `folders:null` = whole) | 200 the member; the nest is told first (`PATCH /api/relay/people/{acc}`); offline → kept with `synced:false`, the cron tells it | 400 `bad_access`, `sealed_whole_only` (nothing changes); 409 `nest_too_old`; 404 |
| `DELETE /v1/servers/{id}/people/{accountId}` | — | 204; `DELETE /api/relay/people/{acc}` now, or by the cron when the nest is off; the person is gone from every list at once | 404 |

A request still waiting for an answer when its link ends keeps its place for 30 more days, then goes. Removing a nest
from the account (`DELETE /v1/servers/{id}`) removes its friends and invites with it.

**The friend's routes:**

| Method and path | Request | Success | Errors |
|---|---|---|---|
| `GET /invite?t=pni_…` | page | sign-in first (`/login?next=/invite?t=…`); then "Join Home nest?" with **Ask to join** / **No thanks**, or a page saying where the invite stands: waiting ("Asked to join"), let in, declined, cancelled, run out, used by someone else, already yours, your own invite; unknown → 404 page | — |
| `POST /v1/invite/request` | `{t}` | 200 `{status:"requested", nest, owner}` (once per invite; asking again answers the same) or `{status:"in", …}`; emails the owner "Anna wants to join Home nest" (notice budget) and sends the nest the waiting list (`PUT /api/relay/requests`, best effort) | 404 `not_found`; 400 `expired`; 409 `used`, `cancelled`, `own_nest`, `already_member`, `already_decided`; 429 (20 an hour per account) |
| `GET /v1/shared` | — (session **or** app token) | 200 `{nests:[{id, label, sharedBy, access, folders, addedAt, serverUrl}]}` | 401 |
| `DELETE /v1/shared/{nestId}` | — (session + Origin) | 204: leaving is the owner removing you | 404 |

**App sign-in (§6).** `/app?code=` offers the nests shared with the account after its own, under "Shared with you",
each as "Shared by <owner> · <folders or whole nest> · <view only|can change>" (only once let in, never while
waiting). `POST /v1/app/approve` accepts such a nest. The first poll then checks the nest's status: without `friends`
→ **409 `nest_too_old`** (no key is asked for; an older nest would give a friend everything). Otherwise
`POST /api/relay/devices` carries `access`, `folders`, `person` ("Anna Berg · a•••@gmail.com") and `personId` (the
friend's `acc_…`); a sealed nest refusing folders → 409 `sealed_whole_only`. A friend removed before the key was
collected → 400 `expired`.

**Account page.** Under each nest, **People**: its friends (letter, name, email, folders or Whole nest, view only or
can change, how many computers; **Change…** / **Remove**), people waiting (**Decline** / **Let <name> in**), open links
(**Copy link** / **Cancel**) and **Invite a friend…** (whole nest or chosen folders as a text list, view only or can
change, their email or Copy invite link). Friends' computers are counted under their person, not listed with the
owner's (`GET /v1/servers` adds `access` and `personId` to each computer, and `friends` / `sealed` to each server,
`null` while it is offline). `#people-<nstId>` is that section. `/account?invite=<nstId>&folders=Photos,School&access=view`
opens that nest's form filled in: the apps' Invite window hands over to it, because only a signed-in browser makes
invites (it is in the `next` allow-list, so a sign-in comes back to it). **Shared with you** lists the nests shared
with the account, each with **Leave**.

**Mail.** Invite emails (the friend's invite, and "You're in") use a budget of their own, 5 a day for the whole
service, outside the day's total of 95 that sign-ins and notices share (95 + 5 is Resend's free 100). The owner's
"wants to join" email is a notice.

**Cron.** Invites that ended go (with the email they went to), except ones someone asked with, which wait 30 more days
for an answer; members and invites of a nest that is gone go; up to 50 member rows with `synced = 0` are sent to their
nest again (`PATCH` or `DELETE /api/relay/people/{acc}`): a 200 settles them (a removed row is then deleted), and so
does a refusal for good (4xx: an older nest, a sealed nest and folders), which is audited; offline or 5xx waits for
the next hour.

**Deleting an account** deletes its own nests' members and invites and every invite it made or asked with; where it
was a friend, it is marked removed and the cron tells those nests to drop its computers.
