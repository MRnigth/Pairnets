# Worker notes on CONTRACT.md

Where the Worker (`cloud/src`) had to choose something the contract leaves open, reads it in a particular way, or
deliberately differs. Nothing here changes a format, a byte string or a status code the nest relies on. The
integrator can fold the useful ones into `CONTRACT.md`.

## Deviations (please confirm or fix the contract)

1. **Email binding cookie (§6.5).** The contract says every `POST /v1/login/email` answer sets "a new `__Host-pn_el`
   cookie". The Worker always sends a fresh `Set-Cookie` (new `Max-Age=900`), but when the browser already holds a
   well-formed one (43 characters, strict base64url of 32 bytes) it keeps that value. Otherwise a person who asks for
   a second link before opening the first would find the first one refused as `wrong_browser`. The binding still ties
   every link to that one browser.
2. **`/privacy`.** Implemented as the contract's `302 https://pairnets.app/privacy` (the build brief called it a
   placeholder page).

## Gaps filled (the contract does not say; this is what the Worker does)

### Requests in general
- Unknown path or wrong method under `/v1/` → `404 not_found` (there is no 405). The Origin rule runs before routing,
  so a mutation without the right Origin is `403 bad_origin` even on an unknown path.
- The 300-a-minute-per-IP limit counts every request, `/assets/*` included. Assets are served with
  `Cache-Control: public, max-age=3600`; everything else `no-store`.
- Rate limits are fixed windows aligned to Unix time (so day windows are UTC days); `Retry-After` is the number of
  seconds to the end of the window.
- A broken secret (`SIGNING_KEY`, `HB_MASTER`, `COOKIE_KEY` not of the documented shape) → `500 server_error`, no details.

### Sign-in
- `POST /v1/login/email` order: per-IP limit → JSON and address shape (`bad_request`) → Turnstile → daily breaker
  (`503`) → binding cookie → per-address limits (silent) → email budget → send. Per-address counters count every
  request for the address, including ones already dropped (so a 4th request in an hour counts toward the 10 a day).
  If the day's budget runs out between the breaker check and the send (a race), the answer is still `202` and nothing
  is sent, so the answer never depends on the address.
- Emails are sent after the response (`waitUntil`), so timing does not show whether one went out. A Resend failure is
  logged without the address; the answer stays `202`.
- Login emails count against both `EMAIL_DAILY_LIMIT` and the 95-a-day all-mail budget; `EMAIL_DAILY_LIMIT` is capped
  at 95.
- "Not an address": a simple shape check (one `@`, a dot in the domain, no spaces or control characters, ≤ 254).
- Every successful sign-in (not only a re-auth) deletes the session row this browser had before.
- `GET /login` while signed in (no `reauth`, no `error`) → `302` to `next`.
- Google: a callback failure goes to `/login?error=…` without `next`. `error=access_denied` → `google_denied`, any
  other `error` → `google_failed`; both only after the flow cookie and `state` are checked. For a re-auth,
  `auth_time >= flow start` is strict (no skew). `aud` must be a string. An unknown `kid` refetches the JWKS at most
  once a minute.

### Claim and nest-signed requests
- `POST /v1/claim`: `serverVersion` is required (string, ≤ 32 characters after trimming); it is kept only in the
  audit row. The label default "the URL's host" includes a non-default port (`nest.example.com:8443`). The
  "10 failures an hour" counts every 4xx answer except 429.
- `POST /v1/nests/claim-codes`: the body may be empty; `429 too_many_codes` carries `Retry-After` (600 for the
  five-unused limit, the rest of the UTC day for the 20-a-day limit). The 20 a day is counted in `rate_counters`
  because used and expired codes are swept.
- Signed requests (§5.2): the HMAC is computed over the `X-Pairnets-Ts` header text as sent; when it verifies but the
  text is not `^[1-9][0-9]{0,15}$`, step 3 answers `400 bad_request`. A body over 1024 bytes is `400 bad_request`
  in step 3 (after the signature). Step 6 order: heartbeat = pending (`409`) → field types (`400`) → rate limit
  (`429`) → update; confirm = field types → rate limit → update; unlink = reason → rate limit → delete. Limits count
  only requests that passed steps 1–5, so the nest's one retry after `401 stale` never uses up its minute. Confirm and
  unlink share one 10-an-hour counter per nest.
- A `pending` nest older than 1 hour counts as gone for signed requests (`410 unlinked`) even before the hourly sweep
  removes it.
- Confirm also stores `serverVersion` as the nest's last version (the account page shows it before the first
  heartbeat). The heartbeat needs all four fields (`publicHost` a string or `null`).

### Public URL (§1.8)
- Besides the listed names, a host ending in `.` (which would slip past the list, e.g. `pairnets.app.`) and any
  `*.localhost` are refused.

### Apps (§6.9)
- App sessions are stored with `amr = ["app"]` and `auth_time` = when the browser approved; `recentAuth` is always
  false for them. They appear in `GET /v1/sessions` (with `deviceName`) while valid.
- `GET /v1/nests` and `GET /v1/me`: when an `Authorization` header is present only it counts (no fallback to the cookie).
- `GET /v1/app/requests/{userCode}`: a pending request is visible to any signed-in account (the user code is not a
  secret on its own); a decided one only to the deciding account; expired or malformed → `404`.
- `POST /v1/app/approve` on a request another account already decided → `404 not_found` (not `409`), so other
  accounts learn nothing. A poll within 3 s of the last counted poll → `429 slow_down` and does not reset the timer.

### Account
- `DELETE /v1/me` deletes from every table explicitly in one batch (in addition to the foreign-key cascade), including
  the address's login challenges. Rate-limit rows hold only keyed hashes and expire within two days.
- Audit events in use: `account_created`, `identity_linked`, `login`, `claim_code_created`, `nest_claimed`,
  `nest_confirmed`, `nest_unlinked`, `nest_removed`, `nest_login`, `nest_login_on`, `nest_login_off`,
  `app_approved`, `app_denied`, `app_signed_in`, `notice_skipped`.

### Golden vectors (§8)
- Cases marked "—" (3, 4, 25, 26, 27, 30, 32) carry the `valid` JWS itself.
- `jwk-header` keeps `kid` `test-1` from `H0` and adds `test-2`'s public JWK as `{kty, crv, kid, x, y}`.
- The generator and the normal suite both run every case through a test-side reference implementation of V1–V21
  (`test/support/verifier.ts`), so the file is proven to mean what the §8.3 table says.
- `scripts/check-secrets.sh` accepts the file as it is: the JWS strings start `eyJhbGci…`, not the tunnel-token
  shape `eyJhIjoi…`. No exemption was added.

### Tooling
- `@cloudflare/vitest-pool-workers` 0.23.0 (as requested) prints a notice that it was renamed to
  `@cloudflare/vitest-plugin`; it works with vitest 4.1. Switching later is a rename (the maintainers ship a codemod).
- TypeScript is pinned to 6.0.3 (the last JavaScript-based compiler) rather than 7.x. There is no `@types/node`;
  `types/node-shim.d.ts` declares the few Node pieces the config files and the generator use.

# Version 2 (RELAY.md): what the Worker does where RELAY.md is silent

Nothing here changes a wire format of `RELAY.md`. Items marked **(choice)** are judgment calls the integrator may want to
fold into `RELAY.md` or reverse.

## Not changed on purpose

- **The login assertion's issuer stays `https://id.pairnets.app`** (`src/assertion.ts` `ISSUER`), although the service
  now lives at `https://sync.pairnets.app`. It is a fixed name the nest pins (CONTRACT §2.3, check V10) and the golden
  vectors carry; it is not an address. Changing it means changing the C# constant and regenerating the vectors together.
  The nest's `Sync:HostedServiceUrl` default (CONTRACT §4.7) still names `id.pairnets.app` on the C# side and needs to
  move to `https://sync.pairnets.app` there.
- `id.pairnets.app` stays refused as a nest's public URL; `sync.pairnets.app` is refused too.

## Adding a server (RELAY.md 2)

- `server_links.status` has one more value than the API shows: `approving`, held for the few seconds of Cloudflare work
  (so a second approval of the same code cannot make a second server). It is shown as `pending`; only the approving
  account sees it. When the Cloudflare work fails, the link goes back to `pending`, so the person can press the button
  again with the same code (RELAY.md does not say whether a failed approval burns the code).
- The nest row, the account limit (3, pending and own-domain nests included), the service limit (`MAX_NESTS`, counting
  every relay row including `broken` ones, since each holds a tunnel) and the link's state change are one D1 batch.
  Order of answers: `400 bad_request` (shape, label over 64) → `404` / `409 already_decided` / `400 expired` (the
  code) → `429 rate_limited` (20 approvals an hour per account, counted only for `approve: true`) → `409 nest_limit` →
  `503 full` → `502 cloudflare_failed`.
- The tunnel is created with exactly `{"name":"pairnets-<nestId>","config_src":"cloudflare"}` (no `tunnel_secret`:
  Cloudflare makes one). `nests.tunnel_id` is written only after the ingress is set, so a router deploy never names a
  half-made tunnel; when the undo fails, the row becomes `broken` with that tunnel id for the sweep. If the Worker dies
  between creating the tunnel and storing its id, the sweep finds the tunnel by its name (`pairnets-<nestId>`).
- Approving sends a notice email ("A nest was added to your account: <label>"), like version 1's "nest linked".
  Adding needs a session, not a recent sign-in (RELAY.md says session + Origin). **(choice; owner question: should adding
  a server need a sign-in within the last 15 minutes, like creating a claim code did?)**
- `POST /v1/servers/start` takes an empty body as `{}`. Every start counts against the 10 an hour, refused ones too.
- `POST /v1/servers/poll`: the 3 s interval is checked atomically. If reading the tunnel token from Cloudflare fails,
  the answer is `502 cloudflare_failed` ("Cloudflare did not answer. The installer tries again by itself.") and the
  link stays `approved`. A server removed before its installer collected the answer gives `400 expired`.
- `/add?code=` answers with HTTP status 404 (and the code form) for an unknown or expired code, as RELAY.md says; the
  `next` allow-list now also takes `/add` and `/add?code=`.

## The relay (RELAY.md 5)

- **Order:** the 600-a-minute limit comes first (before the nest is looked up), then steps 1-5. `/n/*` does not count
  against the 300-a-minute limit of the rest of the service. Everything under `/n` (also `/n` and `/n/`) answers JSON.
- **Step 2, admin paths:** `api/relay` and `api/relay/...` are refused in every spelling the nest's router would still
  match: percent-decoded, dot segments resolved, repeated slashes collapsed, any letter case. A malformed
  percent-encoding is refused too. The allowed prefixes (`api/`, `hub`, `hub/`) are case-sensitive.
- **Step 3, headers:** besides the listed ones, `Host` is not passed on (the runtime sets it; the router drops it too).
  `X-Pairnets-Client-IP` is `CF-Connecting-IP` (always set by Cloudflare in production).
- **Step 4, "the router answers 502/503 from the link":** read as: the router's own error answers (which carry the
  header `X-Pairnets-Router: nest_unknown|nest_offline`, internal between the two Workers), or a `502`, `503`, `504` or
  `530` that is **not** JSON. The nest's own JSON answers pass through unchanged, so its `503 busy` ("too many uploads")
  still reaches the app as such. The router's `404 nest_unknown` (the router has no link for a nest the service knows)
  also becomes `503 nest_offline` for the app.
- **Self-heal (choice):** when the router says `nest_unknown` for a linked server, the relay starts a router deploy in the
  background, unless the last deploy is under 2 minutes old (it may just not be live yet), at most once a minute for
  the whole service. This covers a deploy that was busy or failed, and a code deploy of the router with wrangler (which
  clears its links).
- **Step 5:** the first answer from the nest (anything that is not a link failure) turns `pending` into `active` with
  `confirmed_at`, in the background; the update only matches `pending` rows, so it is one write per server, ever (no
  other relay writes to the row). The signed admin calls (account page, app sign-in) also count as "reached", so a
  server that is only ever used through them is not swept as "never reached".
- **Answers made inert (choice, security):** relayed answers are served on the account service's own origin, and a
  server is run by whoever installed it. So the relay drops `Set-Cookie`, `Clear-Site-Data` and `Access-Control-*`
  from them, sets the usual security headers with the CSP `default-src 'none'; sandbox; frame-ancestors 'none'`, and
  `Cache-Control: no-store`. Without this, a server could plant a session cookie in a visitor's browser or run a script
  on `sync.pairnets.app`. Apps are not affected. A WebSocket upgrade (`101`) is handed back untouched.
- **Cost note:** every relayed request is one D1 read (the nest row) and one D1 write (the rate-limit counter). If that
  costs too much at scale, the per-IP limit can move to a Workers Rate Limiting binding without changing any answer.

## The router and its deploys (RELAY.md 5)

- The settings PATCH sends the part `settings` as a JSON blob (`Content-Type: application/json`, filename
  `settings.json`); the bindings are sorted by nest id. A server is in the list when it is `pending` or `active` and has
  a tunnel.
- The lease is `router_state.lease_id`/`lease_until` (2 minutes). A deploy that finds a server added or removed while its
  PATCH was going out runs another round (at most 3), so a server approved during someone else's deploy does not wait
  for the hourly cron. `routed_version` is set only while the lease is still held. A failed deploy keeps
  `router_state.last_error` (step, status, Cloudflare's codes; never the token).
- **Removal order (choice):** RELAY.md 7 lists "deletes the tunnel, redeploys, deletes the row". The Worker marks the row
  `broken` (out of the relay and of the next deploy at once), deploys, and only then drops the tunnel's connections and
  deletes the tunnel and the row, all under the lease. So the running router never points at a deleted tunnel, and no
  deploy ever names one. If the lease is busy, the deploy holding it (next round) or the hourly cron does it.
- The router answers its own errors as `{"error":"nest_unknown"}` (404) and `{"error":"nest_offline"}` (503) with
  `X-Pairnets-Router`; it drops `Host` along with `X-Pairnets-Route`. Its `wrangler.toml` also sets `preview_urls = false`.

## Signed admin calls (RELAY.md 4)

- Timeout 5 s for every call. Bodies are `JSON.stringify` of the object; the hash is over exactly those bytes.
- Fixed vectors (key = CONTRACT §5.6's derived key, nest `nst_testnest000000000000000001`), computed separately with
  Node's crypto and checked in `test/nestadmin.test.ts`; the C# side can use the same:

  | ts | nonce | request | body | `X-Pairnets-Sig` |
  |---|---|---|---|---|
  | `1791504000` | `TestNonce000000000000A` | `GET /api/relay/status` | (empty) | `dGVhzZGCRgzy9J5LNwbHrbfC9t0gpTh87O3sp5SprzY` |
  | `1791504001` | `TestNonce000000000000B` | `POST /api/relay/devices` | `{"name":"Laptop","system":"Windows 11","approvedBy":"m***@gmail.com"}` (69 bytes) | `CS_In_osTtvilvMxr0yOzlxNjkN8UCQGfs9hGNbb8-4` |

- `approvedBy` is the masked account address (`m***@gmail.com`, CONTRACT §4.2's masking). `system` is sent as `null`
  when the app did not give one.

## Apps (RELAY.md 6)

- `POST /v1/app/approve`: `nestId` missing or not a nest id → `400 bad_request`; a nest id that is not one of this
  account's relayed servers (`pending` or `active`; another account's, an own-domain nest, a removed one) →
  `404 not_found`. The chosen server is kept in `device_logins.nest_id` (a column added by migration 0002).
- `POST /v1/app/poll`: the 3 s interval is checked atomically; while the server is being asked for a key the slot is
  held for up to 10 s, so two polls at once never make two devices. Any answer other than a well-formed device
  (`{id, name, key}`), including the nest's `401 bad_signature`, is `503 nest_offline` and the login stays `approved`.
  A server removed between the approval and the poll gives `400 expired`.
- `/app?code=` lists the account's servers as radio buttons (the first one chosen). With no server, the page says so and
  offers only "Not me".

## Account page and removal (RELAY.md 7)

- `GET /v1/servers` is a JSON array as RELAY.md says, with one extra field `lastSeenAt` (the last time a status call was
  answered). When a server answers, `last_seen_at` and `last_version` are stored, so an offline server still shows its
  last version. `devices` keeps only the documented fields of the right types; it is `[]` while the server is offline.
  `status` is `pending` or `active`; removed servers are not listed.
- `DELETE /v1/servers/{id}/devices/{deviceId}` answers `204`; the nest's `404` is `404 not_found`, an unreachable nest or
  any other answer `503 nest_offline`. No recent sign-in needed.
- `DELETE /v1/servers/{id}` answers `204` at once and needs a sign-in within the last 15 minutes (`401
  reauth_required`), like removing a nest in version 1 (CONTRACT §6.6). **(choice)** A notice email goes out ("A server
  was removed from your account: <label>").
- `DELETE /v1/me` also removes the account's servers: their rows go with the account, then a router deploy drops their
  links and deletes their tunnels.
- The page renders the server list (labels) and its script fills in online, version, free space and the computers from
  `GET /v1/servers`. Own-domain nests are in a collapsed "Servers on their own address" section (open when there are
  some).

## Version 1 paths and relayed servers

- Relayed servers (`mode = 'relay'`) are not part of `GET/PATCH/DELETE /v1/nests` (404 for the latter two), the signed
  nest requests (`410 unlinked`: their key would verify, but those requests belong to own-domain nests) or
  `/nest-login` (the "not your nest" page). Relay nests are made with `hosted_login = 0` and `public_url = ''`.
- Audit events added: `server_added`, `server_denied`, `server_add_failed` (step, status, Cloudflare's codes, whether the
  undo worked), `server_delivered`, `server_active`, `server_removed`, `device_removed`; `app_approved` and
  `app_signed_in` now carry the nest id.

## Cron (RELAY.md 7)

- Order: the version 1 deletions (pending nests older than an hour only for `mode = 'url'`), expired server links, then
  relay servers still `pending` an hour after they were made become `broken`; then, under the lease, a deploy when a
  server is missing from the router or a `broken` one exists (that deploy deletes their tunnels and rows), and finally a
  **reconcile (choice):** any tunnel named `pairnets-nst_<26>` whose nest row no longer exists is deleted (left behind by
  an account deletion whose follow-up failed, or a request that died). Other tunnels in the account are never touched.
