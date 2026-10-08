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
