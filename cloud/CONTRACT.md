# Pairnets Cloud: interface contract

Status: **draft for building, nothing deployed.** Version 1 (2026-10-08).

> **Version 2 (`RELAY.md`) takes precedence:** the service moves to `https://sync.pairnets.app`, and nests are reached
> through the service instead of on names of their own. Version 1's nest login round trip (§3) and claim codes (§4)
> remain only for nests on their own domain.

This file is the single agreement between the two sides of Pairnets Cloud:

- **the service**: a Cloudflare Worker `pairnets-id` at `https://id.pairnets.app` (TypeScript in `cloud/`, D1 database, Cron);
- **the nest**: the C# server in `src/Pairnets.Server` (plus its command line).

The two sides are built in parallel by different people. Everything one side needs from the other is written here:
names, formats, byte strings, status codes, error codes, order of checks. If something is not in this file, it is internal
to one side and the other side must not depend on it. If a builder finds a gap or a contradiction, they write it down in
their report rather than guessing; the integrator fixes this file first.

Words: **MUST / MUST NOT** are hard requirements that a test on the other side may rely on. "Internal" marks advice for
one side only. `[1A]` = Phase 1A (accounts, nest login, claim). `[1B]` = Phase 1B (heartbeat, server list, apps).

Files never travel through the service. The service only ever tells a nest "this browser belongs to account X", with a
signed, short-lived assertion, and only for nests that account owns.

```
 browser / app ──> id.pairnets.app (Worker + D1 + Cron) <── claim / confirm / unlink / heartbeat ── nest (C#)
                       │ assertion (ES256, 90 s) ──302──> <nest public url>/hosted-return#assertion=<jws>
 app <════════ files + device keys: through the owner's OWN tunnel, never via the Worker ════════> nest
```

---

## Contents

1. Identifiers and formats
2. The login assertion (service → nest)
3. Nest login round trip
4. Linking a nest (claim) and the nest's command line
5. Nest-signed requests: confirm, unlink, heartbeat
6. Service HTTP API (browsers and apps)
7. D1 schema (migration 0001)
8. Golden vectors
9. Threats and which check stops them
10. Open questions for the owner

Appendix A: nest-side changes in one list. Appendix B: service configuration.

---

## 1. Identifiers and formats

### 1.1 Alphabets

| Name | Characters | Used for |
|---|---|---|
| **base32-lower** | `0123456789abcdefghjkmnpqrstvwxyz` (Crockford, lowercase; no `i l o u`) | account, nest and other ids |
| **base32-upper** | `0123456789ABCDEFGHJKMNPQRSTVWXYZ` (Crockford, uppercase; no `I L O U`) | claim codes, app user codes |
| **base64url** | `A-Z a-z 0-9 - _`, RFC 4648 §5, **never padded** | tokens, keys, signatures, JWS parts |

**Random base32 characters** are made one per random byte: `alphabet[byte & 31]` (256 is a multiple of 32, so this is
uniform). Ids and codes are only ever *generated* by the service; the nest only *checks their format*.

### 1.2 Ids

| Id | Format | Regex (whole string) | Entropy |
|---|---|---|---|
| Account id | `acc_` + 26 base32-lower | `^acc_[0-9a-hjkmnp-tv-z]{26}$` | 130 bits |
| Nest id | `nst_` + 26 base32-lower | `^nst_[0-9a-hjkmnp-tv-z]{26}$` | 130 bits |
| Session id (public handle) | `ses_` + 26 base32-lower | `^ses_[0-9a-hjkmnp-tv-z]{26}$` | internal to the service |
| Login challenge id | `chl_` + 26 base32-lower | | internal |
| App login id | `dvl_` + 26 base32-lower | | internal |

Ids are compared as exact, case-sensitive strings. Example shapes used in tests: `nst_testnest000000000000000001`,
`acc_testacct000000000000000001`.

### 1.3 Claim code (links a nest to an account)

- Shown as `PN-XXXX-XXXX-XXXX`: the fixed prefix `PN-` and 12 base32-upper characters in three groups of four.
  12 × 5 = **60 bits**. Lives **10 minutes**, works **once**.
- **Normalising typed input** (the nest's command line does this before sending; the service does it again and MUST
  get the same result):
  1. Remove every space, tab and `-`.
  2. Uppercase (ASCII).
  3. If the result is exactly 14 characters and starts with `PN`, drop those 2 characters.
  4. Map look-alikes: `I` → `1`, `L` → `1`, `O` → `0`.
  5. The result MUST be exactly 12 characters, all in base32-upper (so `U` and anything else is refused).
  6. Canonical form: `PN-` + c[0..4] + `-` + c[4..8] + `-` + c[8..12].
- Examples: `pn-abcd-efgh-jkmn`, `ABCDEFGHJKMN`, `PN ABCD EFGH JKMN`, `PNABCDEFGHJKMN` → `PN-ABCD-EFGH-JKMN`;
  `PN-ABCD-EFGH-JKMO` → `PN-ABCD-EFGH-JKM0`; `PN-ABCD-EFGH-JKMU` → invalid; `PN-ABCD-EFGH-JKM` → invalid.
- Stored (service): `SHA-256(UTF-8 of the canonical form)`.

### 1.4 App user code `[1B]`

8 base32-upper characters, shown `XXXX-XXXX` (40 bits), 10 minutes. Normalising: steps 1, 2, 4 above, then exactly 8
characters in base32-upper. Stored in canonical form without the hyphen (it is not a secret on its own: the app's
device code is).

### 1.5 Secrets and tokens

Every secret is random from a CSPRNG (`crypto.getRandomValues` / `RandomNumberGenerator`). The service stores only
`SHA-256(UTF-8 of the whole string, prefix included)` as a 32-byte BLOB.

| Secret | Format | Where it lives | Lifetime |
|---|---|---|---|
| Browser session | `pcs_` + base64url(32 bytes) (47 chars) | cookie `__Host-pn_id` | 30 days idle, 90 days max |
| App token `[1B]` | `pca_` + base64url(32 bytes) | app memory only, `Authorization: Bearer` | 1 hour, no refresh |
| App device code `[1B]` | `pcd_` + base64url(32 bytes) | app memory only | 10 minutes |
| Email login code | base64url(24 bytes) (32 chars) | `#code=` fragment of the emailed link | 15 minutes, once |
| Email binding | base64url(32 bytes) (43 chars) | cookie `__Host-pn_el` | 15 minutes |
| Google flow state | signed cookie `__Host-pn_g` (§6.2) | cookie | 10 minutes |
| Heartbeat key (per nest) | 32 bytes, sent once as base64url (43 chars) | nest's auth.db, **derived** (never stored) on the service | until unlinked |
| Nest login nonce | base64url(16 bytes) (22 chars) | nest memory + the assertion | 10 minutes |
| Nest binding | base64url(32 bytes) (43 chars) | cookie `__Host-pn_hosted` on the nest | 10 minutes |
| Assertion `jti` | base64url(16 bytes) (22 chars) | the assertion | 90 seconds |

The prefixes `pcs_`, `pca_`, `pcd_` exist so a leaked value is recognisable. They are not the nest's `pn_` device keys.

### 1.6 Times and JSON

- JSON field names are **camelCase** everywhere (both sides), except inside the JWS claims (§2.3), which use JWT names.
- Times in the JSON API are ISO 8601 UTC strings with seconds, `2026-10-09T00:00:00Z`. Times inside the assertion, in
  signed nest requests and in D1 are **Unix seconds** (integers). The nest's own auth.db keeps Unix milliseconds, as it
  already does.
- An "integer" in a JSON document is a number token without fraction or exponent that fits a signed 64-bit integer
  (.NET `JsonElement.TryGetInt64` semantics: `1.0` and `1e3` are not integers).

### 1.7 Strict base64url (both sides)

A string `s` is valid strict base64url when **all** of these hold:

1. every character is in `A-Z a-z 0-9 - _` (no `+`, `/`, `=`, whitespace or anything else);
2. `s.length % 4 != 1`;
3. decoding it and re-encoding the bytes (no padding) gives exactly `s` back (this refuses non-zero unused bits, the
   classic "two strings, same bytes" trick).

The nest MUST use a new strict helper for everything in this contract. `WebAuthnVerifier.FromBase64Url` is lenient
(it accepts padding) and MUST NOT be used for these checks.

### 1.8 Normalised public URL (the nest's address)

Both sides use the same form for a nest's address: `https://` + lowercase ASCII host (IDN as punycode) + `:port` only
when the port is not 443. No path (not even `/`), no query, no fragment, no user info.

- Nest: from `Uri` → scheme must be `https`; `IdnHost.ToLowerInvariant()`; `IsDefaultPort` → omit port.
- Service: `new URL(input)`; the input MUST already equal `url.origin` exactly, else it is refused (`bad_url`).
  Additionally the service refuses: IP-literal hosts, hosts without a dot, `localhost`, `pairnets.app`,
  `www.pairnets.app`, `id.pairnets.app`, and anything longer than 200 characters.
- Example: `https://nest.example.com`. The return address is that + `/hosted-return`.

---

## 2. The login assertion (service → nest)

### 2.1 Shape

A JWS in compact serialization: `BASE64URL(header) "." BASE64URL(payload) "." BASE64URL(signature)`.
Signing input = the ASCII bytes of `BASE64URL(header) "." BASE64URL(payload)`. Total length at most 4096 characters.

### 2.2 Header

Exactly these three members, in this order when the service writes it, no whitespace:

```json
{"alg":"ES256","typ":"pn-login+jwt","kid":"<kid>"}
```

- `kid`: 1 to 64 characters from `A-Z a-z 0-9 . _ -`. Production kids look like `pnid-2026-10`; tests use `test-1`
  and `test-2`.
- Anything else in the header (`crit`, `jku`, `jwk`, `x5u`, `x5c`, `x5t`, `cty`, `b64`, `zip`, …) makes the nest refuse
  the assertion.

### 2.3 Claims

The service writes them in this order with `JSON.stringify` (no whitespace). The nest MUST NOT depend on the order and
ignores unknown claims.

| Claim | Type | Value |
|---|---|---|
| `iss` | string | exactly `https://id.pairnets.app` (not configurable, also in tests) |
| `aud` | string (never an array) | the nest id, `nst_…` |
| `sub` | string | the account id, `acc_…` |
| `iat` | integer | issue time (service clock), Unix seconds |
| `nbf` | integer | = `iat` |
| `exp` | integer | = `iat + 90` |
| `nonce` | string | the nonce the nest sent to `/nest-login`, echoed unchanged |
| `jti` | string | base64url(16 random bytes), 22 chars |
| `amr` | array of strings | how the account signed in for this session: `["google"]` or `["email"]` |
| `auth_time` | integer | when that sign-in happened (the session's `auth_time`), Unix seconds, `<= iat` |
| `ver` | integer | `1` |

There is no email address, name or other personal data in the assertion.

### 2.4 Signature

ECDSA on P-256 with SHA-256 (ES256). The signature is **raw `r || s`, 64 bytes** (IEEE P1363), as WebCrypto produces
it. DER (`SEQUENCE { INTEGER r, INTEGER s }`, 70–72 bytes) is refused.

- Service: `crypto.subtle.importKey("jwk", privateJwk, {name:"ECDSA", namedCurve:"P-256"}, false, ["sign"])`, then
  `crypto.subtle.sign({name:"ECDSA", hash:"SHA-256"}, key, signingInput)`.
- Nest: `ecdsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)`.
  (The repo's passkey code uses `Rfc3279DerSequence`; that is a different format and MUST NOT be reused here.)

### 2.5 Keys, pinning and the test override

The nest verifies **offline**: no key download, no `jku`. Public keys are compiled into the server in
`src/Pairnets.Server/Auth/HostedKeys.cs` as JWK coordinates:

```csharp
public sealed record HostedKey(string Kid, string X, string Y);   // X, Y: base64url, exactly 32 bytes each

public static readonly IReadOnlyList<HostedKey> Pinned =
[
    // new("pnid-YYYY-MM", "<x base64url>", "<y base64url>"),   // current
    // new("pnid-YYYY-MM", "<x base64url>", "<y base64url>"),   // next (rotation)
];
```

- **Pinned starts empty** until the owner generates the production keys (open question 1). With no effective key, the
  nest treats hosted login as unavailable: `hosted link` refuses, `/auth/hosted/start` redirects with `hosted-none`.
- Each key is imported with `ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X, Y } })`.
  Import MUST succeed (a point off the curve throws); a bad pinned key is a build error caught by a unit test.
- At most 4 keys; kids are unique.
- **Override** `Sync:HostedPublicKeys` (environment `Sync__HostedPublicKeys`): a string of entries separated by `;`,
  each `kid:x:y` (kid as in §2.2; x, y strict base64url of 32 bytes). Whitespace around entries is ignored; empty
  entries are skipped. When the setting is non-empty it **replaces** the pinned list entirely. An invalid value stops
  the server at start-up with a clear message (and makes the command line exit 2). When it is set, the server logs one
  warning at start: `Pairnets Cloud keys come from Sync:HostedPublicKeys (meant for tests)`.
- Tests generate key pairs at test time (`ECDsa.Create(ECCurve.NamedCurves.nistP256)`, `ExportParameters(false)`) and
  pass `test-1:<x>:<y>`. No private key is ever committed, on either side.
- Production private key: a private JWK `{"kty":"EC","crv":"P-256","x":…,"y":…,"d":…,"kid":…}` stored only as the
  Worker secret `SIGNING_KEY`. The service takes `kid` from that JWK.
- **Rotation:** ship nests that pin *current* and *next*; watch nest versions via heartbeats; swap `SIGNING_KEY` to the
  next key; later ship nests that pin (new current, new next) and drop the old one.

### 2.6 How the nest checks an assertion (ordered)

The check is a **pure function** (no I/O, clock passed in) so it can be unit-tested and fed the golden vectors (§8):

```
Verify(jws, keys, nestId, sub, expectedNonce, nowUnix, jtiCache) -> (Outcome, Reason, Claims?)
Outcome: Ok | Failed | Clock
```

(Name and shape are a suggestion; the outcome/reason pairs are the contract.) Steps run in this order; the **first**
failing step decides the reason. All comparisons are ordinal and case-sensitive.

| # | Check | Reason on failure |
|---|---|---|
| V1 | Length ≤ 4096 and exactly two `.` (three segments) | `shape` |
| V2 | Header segment: non-empty, strict base64url (§1.7), decoded ≤ 512 bytes | `b64` |
| V3a | Header bytes are valid UTF-8 JSON, an object, no member name twice | `header` |
| V3b | `alg` present and exactly `"ES256"` | `alg` |
| V3c | `typ` present and exactly `"pn-login+jwt"` | `typ` |
| V3d | Members are exactly `{alg, typ, kid}`; `kid` is a string matching §2.2 | `header` |
| V4 | `kid` is one of the effective keys | `kid` |
| V5 | Signature segment: strict base64url and decodes to **exactly 64 bytes** | `sig-format` |
| V6 | Payload segment: non-empty, strict base64url, decoded ≤ 2048 bytes | `b64` |
| V7 | ES256 signature verifies with that kid's key (P1363) | `sig` |
| V8 | Payload is valid UTF-8 JSON, an object, no member name twice; `iss aud sub nonce jti` are strings, `iat nbf exp auth_time ver` are integers, `amr` is an array | `claims` |
| V9 | `ver == 1` | `ver` |
| V10 | `iss == "https://id.pairnets.app"` | `iss` |
| V11 | `aud == nestId` (`hosted.nestId`) | `aud` |
| V12 | `sub == sub` (`hosted.sub`), compared in constant time (`CryptographicOperations.FixedTimeEquals` over UTF-8) | `sub` |
| V13 | `abs(now - iat) <= 300` | **Outcome `Clock`**, reason `clock` |
| V14 | `iat <= now + 60` | `iat` |
| V15 | `exp > iat` and `exp - iat <= 300` (max lifetime 5 min) | `lifetime` |
| V16 | `nbf <= now + 60` and `nbf <= exp` | `nbf` |
| V17 | `now < exp + 60` (60 s skew) | `exp` |
| V18 | `auth_time <= iat` | `auth_time` |
| V19 | `nonce == expectedNonce`, constant time | `nonce` |
| V20 | `amr` has 1 to 4 entries, each a string matching `^[a-z0-9_-]{1,32}$` (unknown values allowed) | `amr` |
| V21 | `jti` matches `^[A-Za-z0-9_-]{16,64}$` and is not in `jtiCache`; then add it until `exp + 60` | `jti` |
| — | all passed | Outcome `Ok`, reason `null`, claims returned |

Notes: skew allowed is **60 s**; max lifetime **5 min**; a clock that is off by more than **5 min** gets its own outcome
because only the owner can fix it (it is checked only after the signature, so it reveals nothing to a forger). The jti
cache is in memory, at most 1000 entries, expired entries swept on insert.

### 2.7 What the endpoint does around the check

`POST /auth/hosted/finish` (§3.4) adds the parts that need the request:

| # | Check | Answer |
|---|---|---|
| E1 | `OwnerAuth.IsNestRequest` | 404 `{code:"not-found"}` |
| E2 | `OwnerAuth.IsSameOrigin` | 403 `{code:"bad-request"}` |
| E3 | Hosted login usable (§4.6) | 404 `{code:"hosted-none"}` |
| E4 | Wait `FailureThrottle.CurrentDelay(client address)` | (delay, then continue) |
| E5 | Cookie `__Host-pn_hosted` present, 43 chars strict base64url; **remove** the pending entry keyed by base64url(SHA-256(cookie)); it existed and has not expired | `binding` → 400 `hosted-failed` |
| E6 | Body is a JSON object with a string `assertion` of 1–4096 chars | `shape` → 400 `hosted-failed` |
| V1–V21 | §2.6 with `expectedNonce` = the pending entry's nonce | `Failed` → 400 `hosted-failed`; `Clock` → 400 `hosted-clock` |

- The pending entry is removed at E5 whatever happens next (one try per start). The response **always** deletes the
  `__Host-pn_hosted` cookie.
- Every `Failed` (not `Clock`) calls `FailureThrottle.RecordFailure(address)` and logs a warning with the reason code and
  the client address. Success calls `RecordSuccess`. The JWS, nonce, binding and heartbeat key are never logged.
- Outward messages (the reason codes stay in the log):
  - `hosted-failed`: "That sign-in did not work. Try again."
  - `hosted-clock`: "This nest's clock is more than 5 minutes off, so the sign-in could not be checked. Fix the time on
    the server (timedatectl) and try again."
  - `hosted-none`: "Sign-in with a Pairnets account is not set up (or is turned off) on this nest."

---

## 3. Nest login round trip `[1A]`

```
browser                      nest (https://nest.example.com)                 service (https://id.pairnets.app)
   │ GET /auth/hosted/start?next=/link?code=…  ─────>│
   │ <── 302 + Set-Cookie __Host-pn_hosted ──────────│  pending[hash(binding)] = {nonce, next, +10 min}
   │ GET /nest-login?nest=…&nonce=…&return=… ──────────────────────────────────────>│ session? owner? return exact?
   │ <── 302 https://nest.example.com/hosted-return#assertion=<jws> ────────────────│ (or /login first)
   │ GET /hosted-return  (fragment stays in the browser) ─>│
   │ POST /auth/hosted/finish {assertion} (same origin, cookie) ─>│ E1–E6, V1–V21
   │ <── 200 {next} + Set-Cookie __Host-pn_session ───────│
   │ location.replace(next)
```

### 3.1 Nest: `GET /auth/hosted/start?next=<path>`

- Not `IsNestRequest` → **404** (empty). Rate limited with `PairingEndpoints.RateLimitPolicy`.
- Hosted login not usable (§4.6) → **302** `/signin?error=hosted-none`.
- Already signed in to the nest → **302** to `next` (or `/devices`).
- Otherwise:
  1. `next` = `WebEndpoints.SafeNextPath(next)` (null → `/devices` later).
  2. `nonce` = base64url(16 random bytes); `binding` = base64url(32 random bytes).
  3. Pending map (in memory): key base64url(SHA-256(UTF-8 binding)) → `{Nonce, Next, Expires = now + 10 min}`. Expired
     entries are swept on each start; at most 200 entries, the oldest go first (as in `GoogleSignIn.Start`).
  4. `Set-Cookie: __Host-pn_hosted=<binding>; Path=/; Secure; HttpOnly; SameSite=Lax; Max-Age=600`.
  5. **302** to
     `{Sync:HostedServiceUrl}/nest-login?nest={hosted.nestId}&nonce={nonce}&return={Uri.EscapeDataString(publicUrl + "/hosted-return")}`
     where `publicUrl` is the nest's running `Sync:PublicUrl` in normalised form (§1.8).

### 3.2 Service: `GET /nest-login?nest=&nonce=&return=`

Checks in this order. Every failure is an HTML error page on `id.pairnets.app`; **the service never redirects to a nest
except with a valid assertion to the registered address.**

| # | Check | Failure |
|---|---|---|
| 1 | `nest` matches the nest-id regex; `nonce` matches `^[A-Za-z0-9_-]{22,64}$`; `return` present, ≤ 300 chars | 400 page |
| 2 | Valid browser session cookie `__Host-pn_id` | **302** `/login?next=<URL-encoded "/nest-login?" + original query>` |
| 3 | Nest row exists **and** belongs to the session's account (same page for both, so other accounts learn nothing) | 404 page "This nest is not linked to your Pairnets account." |
| 4 | Nest `status = 'active'` | 409 page "This nest has not finished linking." |
| 5 | `nests.hosted_login = 1` and the emergency switch `HOSTED_LOGIN_DISABLED` is not `"1"` | 403 page |
| 6 | `return === nests.public_url + "/hosted-return"` (exact string, after normal query decoding) | 400 page "This nest's address changed. Link it again." |
| 7 | Rate limit: 60 per hour per account | 429 page |
| 8 | Sign the assertion (§2): `aud` = nest id, `sub` = account id, `nonce` = the given nonce, `amr` and `auth_time` from the session; write audit `nest_login` | — |

Answer: **302** `Location: <nests.public_url>/hosted-return#assertion=<jws>` with `Cache-Control: no-store` and
`Referrer-Policy: no-referrer`. The fragment is exactly `assertion=` + the JWS (base64url and dots need no escaping).

### 3.3 Nest: `GET /hosted-return` (a website page)

- New page `WebUi/pages/hosted-return.html` (`<body data-page="hosted-return">`), added to `WebUi.Pages` as
  `["/hosted-return"] = "hosted-return"` (this also makes it public in `TokenAuthMiddleware`). Served like the other pages
  (same CSP, `Referrer-Policy: no-referrer`, `Cache-Control: no-store`).
- `hostedReturnPage` in `nest.js`:
  1. `const assertion = new URLSearchParams(location.hash.slice(1)).get("assertion")`.
  2. **Immediately** `history.replaceState(null, "", location.pathname)` (the JWS leaves the address bar and history).
  3. No assertion → show "This page needs to be opened from the Pairnets sign-in. Go back to sign in." with a link to
     `/signin`.
  4. Otherwise, without a button press: `fetch("/auth/hosted/finish", {method:"POST", credentials:"same-origin",
     headers:{"Content-Type":"application/json"}, body: JSON.stringify({assertion})})`. (Not the `api()` helper: that
     one prefixes `/web/api`.)
  5. 200 → `location.replace(body.next)`. Error → show `body.message` and a "Try again" link to `/signin`.

### 3.4 Nest: `POST /auth/hosted/finish`

- Path is under `/auth` (already public in `TokenAuthMiddleware`). Rate limited with `PairingEndpoints.RateLimitPolicy`.
  `Cache-Control: no-store`.
- Request body: `{"assertion": "<compact JWS>"}`.
- Checks: §2.7.
- Success: `owner.SignIn(ctx, "pairnets account")` (the session method shown on the Security page), then
  **200** `{"next": "<pending.Next ?? "/devices">"}`. `next` was checked at start and kept on the server; nothing from
  the service or the fragment can choose it.
- Errors use the nest's existing `ErrorBody` shape `{code, message}`:

| Status | `code` | When |
|---|---|---|
| 404 | `not-found` | not the nest's own name |
| 403 | `bad-request` | not same origin |
| 404 | `hosted-none` | hosted login not usable |
| 400 | `hosted-failed` | E5, E6, any `Failed` outcome |
| 400 | `hosted-clock` | `Clock` outcome |
| 429 | (rate limiter) | too many requests |

### 3.5 Nest website pieces that go with it

- `/signin`: a button `<a class="button wide" id="hosted-signin" href="/auth/hosted/start" hidden>Continue with Pairnets account</a>`;
  its `href` gets `?next=<encoded next>` like the Google one; shown when `state.methods.hosted`. When the `next` link's
  own query has `method=hosted` (the apps' "Continue with Pairnets account", §6.9), the page goes straight to it, as it
  already does for `method=google`.
- `/signin?error=` messages: `hosted-none`, `hosted-failed`, `hosted-clock` (texts in §2.7).
- See Appendix A for `/web/api/state`, `/api/hello` and the Security page.

---

## 4. Linking a nest (claim) and the nest's command line `[1A]`

### 4.1 Overview

1. Signed in at `https://id.pairnets.app/account` (and signed in within the last 15 minutes), the owner presses
   **Add a nest**. The page shows a claim code and the command to run on the server.
2. On the server: `sudo -u pairnets /opt/pairnets/pairnets-server hosted link PN-XXXX-XXXX-XXXX`.
3. The command claims the code, **shows which account it belongs to (masked email) and asks to confirm**.
4. Only after "yes" does it confirm to the service (signed) and store the link in auth.db.

The nest row on the service is `pending` from step 3 until the signed confirm (step 4). A pending nest cannot be signed
in to and disappears after 1 hour. This is why a code someone else gave you cannot quietly make your nest trust their
account: you see their (masked) address first, and nothing is stored on the nest unless you say yes.

### 4.2 Service: `POST /v1/claim`

No cookies are read on this endpoint and no Origin is required (it is called by servers). `Content-Type:
application/json`; the nest sends `User-Agent: pairnets-server/<version>`.

Request:

```json
{"code":"PN-ABCD-EFGH-JKMN","publicUrl":"https://nest.example.com","serverVersion":"1.0.47","label":"Home nest"}
```

- `code`: any typed form (§1.3 normalises it). `publicUrl`: normalised (§1.8). `serverVersion`: ≤ 32 chars.
  `label`: optional, 1–64 chars after trimming and dropping control characters; default = the URL's host.

Checks in order: per-IP rate limit → JSON shape → code format → `publicUrl` rules (checked **before** the code is
used, so a typo in the URL does not burn the code) → look up `SHA-256(canonical code)` → atomic redeem → nest limit.

**Atomic redeem** (one D1 `batch`, which is one transaction):

```sql
INSERT INTO nests (id, account_id, label, public_url, status, key_version, hosted_login, created_at, last_req_ts)
  SELECT ?1, account_id, COALESCE(?2, label, ?3), ?4, 'pending', 1, 1, ?5, 0
  FROM claim_codes
  WHERE code_hash = ?6 AND used_at IS NULL AND expires_at > ?5
    AND (SELECT COUNT(*) FROM nests n WHERE n.account_id = claim_codes.account_id) < 3;
UPDATE claim_codes SET used_at = ?5, nest_id = ?1
  WHERE code_hash = ?6 AND used_at IS NULL AND expires_at > ?5
    AND EXISTS (SELECT 1 FROM nests WHERE id = ?1);
```

Success = the INSERT changed 1 row. If not, read the code row to pick the error (`used`, `expired`, `nest_limit`,
`invalid_code`). Two parallel claims of one code: exactly one wins.

Success **201**:

```json
{"nestId":"nst_…","accountId":"acc_…","maskedEmail":"y***@gmail.com","heartbeatKey":"<base64url, 32 bytes>","keyVersion":1}
```

- `maskedEmail`: first character of the account email + `***` + `@` + the domain (ASCII only, so any terminal shows it).
- `heartbeatKey`: §5.1 derivation for this nest and `keyVersion`.

Errors (body `{error, message}`):

| Status | `error` | When |
|---|---|---|
| 400 | `bad_request` | not JSON / wrong types |
| 400 | `invalid_code` | malformed, or no such code |
| 400 | `expired` | code older than 10 minutes |
| 409 | `used` | code already redeemed |
| 400 | `bad_url` | `publicUrl` fails §1.8 |
| 409 | `nest_limit` | the account already has 3 nests (pending ones count) |
| 429 | `rate_limited` | §6.10, with `Retry-After` (seconds) |

### 4.3 Nest: `pairnets-server hosted link <CODE> [--yes] [--label <text>] [--data-dir <dir>]`

Works while the service runs (auth.db is shared safely). Steps:

1. Normalise the code (§1.3). Invalid → stderr `That is not a Pairnets link code (PN-XXXX-XXXX-XXXX).`, **exit 2**.
2. Already linked (`hosted.nestId` present) → `This nest is already linked (run "hosted unlink" first).`, **exit 1**.
3. No effective key (§2.5) → `This server version cannot check Pairnets account sign-ins yet.`, **exit 1**.
4. `server.publicUrl` from auth.db, normalised (§1.8); missing or not https → `This nest has no https name yet: give it one first with  sudo ./install.sh --public-url https://nest.example.com`, **exit 1**.
5. No `--yes` and stdin is not a terminal (`Console.IsInputRedirected`) → `Add --yes to link without asking (only with a code from your own account).`, **exit 1**. (Checked before anything is sent, so the code is not used up.)
6. `POST {Sync:HostedServiceUrl}/v1/claim` (20 s timeout, redirects not followed). Error → print the service's
   `message` (or a generic network message), **exit 1**.
7. Check the answer: `nestId` and `accountId` match their regexes, `heartbeatKey` is strict base64url of 32 bytes,
   `keyVersion` is an integer ≥ 1. Otherwise send unlink `declined` (best effort), **exit 1**.
8. Unless `--yes`, print and ask:
   ```
   This links https://nest.example.com to the Pairnets account y***@gmail.com.
   Anyone who can sign in to that account can then sign in to this nest's website.
   Link it? [y/N]
   ```
   Anything but `y`/`yes` (case-insensitive) → send unlink `declined` (§5.3, best effort), print `Not linked.`, **exit 1**.
9. Send confirm (§5.3). It MUST answer 200 with a valid signed response. Otherwise send unlink `declined` (best effort),
   print the problem, **exit 1**.
10. Store the rows of §4.4 **in one transaction**, then print
    `Linked. Sign in to https://nest.example.com with your Pairnets account (y***@gmail.com).`, **exit 0**.

With `--yes`, step 8 still prints the first line (so logs show which account), without asking. `install.sh --link CODE`
`[1B]` runs `runuser -u pairnets -- /opt/pairnets/pairnets-server hosted link CODE --yes` late in the install.

### 4.4 What the nest stores (auth.db `settings` rows, all values are strings)

| Key | Value | Written by |
|---|---|---|
| `hosted.nestId` | `nst_…` | link |
| `hosted.sub` | `acc_…` (the `accountId` from the claim) | link |
| `hosted.heartbeatKey` | base64url, 32 bytes | link |
| `hosted.keyVersion` | decimal, e.g. `1` | link |
| `hosted.linkedAt` | Unix **milliseconds**, decimal (like `owner.passwordSetMs`) | link |
| `hosted.enabled` | `true` or `false` | link (`true`), enable/disable, website toggle, heartbeat |
| `hosted.maskedEmail` | `y***@gmail.com` (display only) | link |
| `hosted.lastTs` | last `X-Pairnets-Ts` sent, Unix seconds (§5.2) | every signed request |
| `hosted.disabledBy` | `owner`, `service` or `service-unlinked` (only while `enabled=false`) | disable / heartbeat |
| `hosted.lastBeatAt` `[1B]` | Unix ms of the last heartbeat attempt | heartbeat |
| `hosted.lastBeatResult` `[1B]` | `ok`, `http-<status>`, `network` or `bad-signature` | heartbeat |

The heartbeat key cannot sign anyone in; it only lets its holder send this nest's heartbeats and unlink it.
`hosted status` never prints it. Every `hosted.*` row is read from auth.db **on each use** (no caching), so command-line
changes apply to the running server at once.

### 4.5 Other subcommands

- `hosted unlink`: not linked → `Not linked.`, exit 0. Otherwise: send unlink `owner` (§5.3, 10 s timeout, best effort);
  then delete every `hosted.%` row (one statement) **and every website session whose method is `pairnets account`**;
  print `Unlinked.` and, if the service could not be told,
  `Could not tell the account service; remove the nest on https://id.pairnets.app/account.`; exit 0.
- `hosted status`: first line exactly `Pairnets account: not linked` or
  `Pairnets account: linked (y***@gmail.com)`; then informational lines (nest id, linked time, sign-in on/off and
  `disabledBy`, heartbeat on/off and last result). Exit 0.
- `hosted enable` / `hosted disable`: set `hosted.enabled`; disable writes `hosted.disabledBy=owner`; enable deletes
  `hosted.disabledBy`. Enable when not linked → exit 1. No network.
- `IsCliCommand` accepts `hosted`; `Usage` gains:
  ```
    pairnets-server hosted link <CODE> [--yes] [--label <name>]
                                            link this nest to a Pairnets account (code from id.pairnets.app/account)
    pairnets-server hosted unlink           stop accepting sign-ins from the Pairnets account
    pairnets-server hosted status           show whether this nest is linked
    pairnets-server hosted enable|disable   turn sign-in with the Pairnets account on or off
  ```

### 4.6 "Hosted login usable" (the nest)

All of: `hosted.nestId`, `hosted.sub`, `hosted.heartbeatKey` present and well-formed; `hosted.enabled == "true"`;
at least one effective key (§2.5); `Sync:PublicUrl` set.

- It **never counts as the last way in**: `OwnerAuth.HasOtherWayThan` and the passkey-delete check ignore it, so the
  owner cannot remove their last password/passkey/email/Google method because hosted login exists.
- It **does** count for "is there any way to sign in" (`AuthStore.HasSignInMethod`: linked and enabled), so `/` sends
  a fresh linked nest to `/signin` (not `/setup`) and `owner-link --if-new` stays quiet.

### 4.7 Configuration on the nest

| Setting | Default | Meaning |
|---|---|---|
| `Sync:HostedServiceUrl` | `https://id.pairnets.app` | base URL for `/nest-login`, `/v1/claim`, `/v1/nest/*`, `/v1/heartbeat`. Must be `https`, except `http://127.0.0.1:<port>` / `http://localhost:<port>` (tests). |
| `Sync:HostedPublicKeys` | empty | §2.5 override |
| `Sync:HostedHeartbeat` | `true` | `[1B]` `false` = never send heartbeats |

The command line reads only `appsettings.json` and its own environment, so these can be set as `Sync__…` variables
there too; everything about the link itself is in auth.db.

---

## 5. Nest-signed requests: confirm, unlink, heartbeat

### 5.1 The per-nest key (service side only)

```
heartbeatKey = HMAC-SHA256(key = HB_MASTER, message = UTF-8("pn-hb-key\n" + nestId + "\n" + keyVersion))
```

`HB_MASTER` is the Worker secret: base64url of 32 random bytes; the HMAC key is the **decoded 32 bytes**.
`keyVersion` is written in decimal. The service never stores per-nest keys; it derives them when needed. (A D1 leak
does not leak them.)

### 5.2 Signing (both directions)

Request (nest → service), method POST, `Content-Type: application/json`, body ≤ 1024 bytes:

| Header | Value |
|---|---|
| `X-Pairnets-Nest` | the nest id |
| `X-Pairnets-Ts` | Unix seconds, decimal, no sign, no leading zeros |
| `X-Pairnets-Sig` | `base64url(HMAC-SHA256(K, M))`, 43 chars |

```
K = the 32 bytes of hosted.heartbeatKey
M = UTF-8( P + "\n" + nestId + "\n" + ts + "\n" + lowercase_hex(SHA-256(exact body bytes)) )
```

`P` per endpoint: `hb1` heartbeat, `nc1` confirm, `nu1` unlink. `ts` is the header's exact text. Every body has
`"v":1` and `"ts"` equal (as a number) to the header.

- **The nest picks** `ts = max(nowUnix, hosted.lastTs + 1)` and writes `hosted.lastTs` before sending (the command line
  keeps it in memory until the rows exist). So no two signed requests from one nest share a `ts`.
- **The service checks, in order:**
  1. `X-Pairnets-Nest` matches the regex and the nest row exists → else **410** `{error:"unlinked"}`.
  2. Signature: 43-char strict base64url, HMAC verifies (constant time, `crypto.subtle.verify`) → else **401** `bad_signature`.
  3. Body is JSON with `v == 1` and `ts` equal to the header → else **400** `bad_request`.
  4. `abs(now - ts) <= 300` → else **401** `stale`.
  5. `UPDATE nests SET last_req_ts = ?ts WHERE id = ?id AND last_req_ts < ?ts` changes 1 row → else **401** `stale`
     (replay, or an older request).
  6. Per-endpoint rules below.
- On `401 stale` the nest may retry **once**, after 1.1 s, with a new `ts`.

Response (service → nest), only for **200** answers:

| Header | Value |
|---|---|
| `X-Pairnets-Ts` | service time, Unix seconds |
| `X-Pairnets-Sig` | `base64url(HMAC-SHA256(K, UTF-8(P + "-resp\n" + nestId + "\n" + respTs + "\n" + hex(SHA-256(response body)))))` |

The response body has `"v":1`, `"ts"` (= `respTs`) and `"reqTs"` (= the request's `ts`). The nest accepts a 200 only if
the signature verifies, `abs(now - respTs) <= 300` and `reqTs` equals what it sent; otherwise it treats the answer as a
failure (`bad-signature`) and acts on nothing in it. Answers other than 200 are not signed; the nest acts on them only as
listed below (and 410 can only ever turn hosted login **off**).

### 5.3 The endpoints

| Endpoint | Phase | `P` | Request body | 200 body | Service does |
|---|---|---|---|---|---|
| `POST /v1/nest/confirm` | 1A | `nc1` | `{"v":1,"ts":…,"serverVersion":"1.0.47"}` | `{"v":1,"ts":…,"reqTs":…,"status":"active"}` | `pending` → `active`, `confirmed_at`; notice email "A nest was linked to your account: <url>"; audit `nest_confirmed`. Already active → same 200. |
| `POST /v1/nest/unlink` | 1A | `nu1` | `{"v":1,"ts":…,"reason":"owner"}` (or `"declined"`) | `{"v":1,"ts":…,"reqTs":…,"status":"removed"}` | Deletes the nest row; notice email if it was active; audit `nest_unlinked`. |
| `POST /v1/heartbeat` | 1B | `hb1` | §5.4 | `{"v":1,"ts":…,"reqTs":…,"disableHostedLogin":false}` | §5.4 |

Extra errors: heartbeat for a `pending` nest → **409** `not_confirmed`; more than one heartbeat per 60 s or more than 10
confirm/unlink per hour per nest → **429** `rate_limited` with `Retry-After`.

### 5.4 Heartbeat `[1B]` (specified now)

Request body (exact field order as written by the nest, ≤ 1024 bytes):

```json
{"v":1,"ts":1791504000,"serverVersion":"1.0.47","ready":true,"publicHost":"nest.example.com","hostedLogin":true}
```

- `serverVersion`: `PairnetsInfo.ProductVersion`. `ready`: the store is initialised and `Sync:PublicUrl` is set.
  `publicHost`: `SyncOptions.PublicHost` or `null`. `hostedLogin`: `hosted.enabled == "true"`.
- Service on success: `last_seen_at = now`, `last_version`, `last_ready`, `last_public_host`, `last_hosted_login`.
  `publicHost` is informational: it never changes `public_url` (a changed address needs a new link; the account page
  shows a warning when they differ). Online = `last_seen_at` within **25 minutes**.
- Response `disableHostedLogin` is `true` only while the Worker variable `HOSTED_LOGIN_DISABLED` is `"1"` (emergency
  switch). It can only turn the feature off.

Nest behaviour (`Services/HostedHeartbeatService.cs`, a `BackgroundService`):

- Wakes at least every 60 s and reads `Sync:HostedHeartbeat` and the `hosted.*` rows. Not linked, disabled by
  `Sync:HostedHeartbeat=false`, or `hosted.disabledBy=service-unlinked` → **no network call at all**.
- First beat **30 s** after start (or 30 s after it first sees a new `hosted.nestId`).
- Then every `600 s × U(0.9, 1.1)`. After `f` failures in a row: `min(3600, 600 × 2^f) × U(0.9, 1.1)` (20, 40, 60, 60…
  minutes). Success resets `f`. A 429's `Retry-After` is honoured (capped at 3600 s).
- Timeout **10 s**; redirects are not followed.
- Valid signed 200 → `lastBeatResult=ok`; if `disableHostedLogin` → `hosted.enabled=false`, `hosted.disabledBy=service`,
  log a warning.
- **410** → `hosted.enabled=false`, `hosted.disabledBy=service-unlinked`, log a warning ("the account service no longer
  knows this nest; run hosted unlink, or hosted link with a new code"), stop beating until the link changes.
- Anything else → failure, back off.

### 5.5 Service-side status for the account page

`online` = active and `last_seen_at` within 25 min. A nest that has never sent a heartbeat (Phase 1A, or the opt-out)
shows status "unknown", not "offline".

### 5.6 HMAC test vectors (both sides MUST have a unit test with these)

`HB_MASTER` bytes `00 01 02 … 1f` (32 bytes, base64url `AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8`),
nest id `nst_testnest000000000000000001`, key version `1`. These values are public test data, not secrets.

| Vector | Input | Expected (base64url) |
|---|---|---|
| Derived heartbeat key | §5.1 | `n4Tr_MkavyYVcV4JYFqyLo4V_3SdAf5UYSdjq0Gzw9g` |
| Heartbeat request sig, `P=hb1`, `ts=1791504000` | body `{"v":1,"ts":1791504000,"serverVersion":"1.0.47","ready":true,"publicHost":"nest.example.com","hostedLogin":true}` (112 bytes) | `bHpoOXDoG0p3j0swx0Agcxo8zl7KhnwU1Qug6JP93ZE` |
| Heartbeat response sig, `P=hb1-resp`, `ts=1791504001` | body `{"v":1,"ts":1791504001,"reqTs":1791504000,"disableHostedLogin":false}` | `rGRf5tEBz034Ylc8b96O1mRgG4JxmqqSG7fwPdELpNk` |
| Confirm request sig, `P=nc1`, `ts=1791504005` | body `{"v":1,"ts":1791504005,"serverVersion":"1.0.47"}` | `Zm0rOOd9GSK6RchlkdDwrk7NYUzTCjOgiajMTRMHNEk` |

The bodies are the exact bytes shown (no trailing newline). The nest only needs the last three rows (it is given the
key); the service needs all four.

---

## 6. Service HTTP API (browsers and apps)

### 6.1 General rules

- Origin `https://id.pairnets.app` (Worker variable `PUBLIC_ORIGIN`; tests use the same value).
- `/v1/*` speaks JSON only. Requests with a body MUST have `Content-Type: application/json` (else 415 `bad_request`);
  bodies over 16 KiB → 413 `bad_request`.
- **Uniform error body** on every 4xx/5xx under `/v1/*`: `{"error":"<snake_case code>","message":"<sentence for people>"}`.
  HTML pages show errors as pages.
- **Every response** (pages, JSON, redirects) carries these headers (the CSP is one line; it is wrapped here only for
  reading):
  ```
  Content-Security-Policy: default-src 'none'; script-src 'self' https://challenges.cloudflare.com; style-src 'self';
    img-src 'self' data:; connect-src 'self'; frame-src https://challenges.cloudflare.com; form-action 'self';
    base-uri 'none'; frame-ancestors 'none'
  Strict-Transport-Security: max-age=31536000
  X-Content-Type-Options: nosniff
  X-Frame-Options: DENY
  Referrer-Policy: no-referrer
  Cross-Origin-Opener-Policy: same-origin
  Permissions-Policy: camera=(), microphone=(), geolocation=()
  ```
  and `Cache-Control: no-store` on everything except `/assets/*`. No CORS headers anywhere.
- No inline scripts or styles; page scripts are plain JavaScript files under `/assets/`.

### 6.2 Cookies

All: `Secure; HttpOnly; Path=/; SameSite=Lax`, no `Domain` (that is what `__Host-` requires). Because nests often live
on other `*.pairnets.app` names (same site), these cookies, the exact Origin check and the CSP are what keep the
account safe from a sibling site.

| Cookie | Value | Max-Age |
|---|---|---|
| `__Host-pn_id` | browser session `pcs_…` | 2592000 (30 days), refreshed at most hourly while used; never past 90 days from `created_at` |
| `__Host-pn_g` | Google flow, signed (§6.3) | 600 |
| `__Host-pn_el` | email binding | 900 |

Internal (service): `__Host-pn_g` = `base64url(JSON payload) + "." + base64url(HMAC-SHA256(COOKIE_KEY, "pn-g1\n" + payloadPart))`,
payload `{s: state, n: nonce, v: PKCE verifier, r: next, e: expiry, ra: reauth flag}`; checked and cleared at the callback.

### 6.3 CSRF rule

Every `POST`, `PATCH`, `PUT` or `DELETE` MUST carry `Origin` exactly equal to `PUBLIC_ORIGIN`; missing or different →
**403** `{error:"bad_origin"}`. Only these paths are exempt, and they MUST NOT read any cookie (they authenticate by
code, HMAC or Bearer token): `/v1/claim`, `/v1/nest/confirm`, `/v1/nest/unlink`, `/v1/heartbeat`, `/v1/app/start`,
`/v1/app/poll`, `/v1/app/logout`. Version 2 (`RELAY.md`) adds `/v1/servers/start` and `/v1/servers/poll` (the
installer, authenticated by its device code). The relay `/n/*` is outside this rule altogether: it reads no cookie, passes
none on (the `Cookie` header is removed) and is meant for apps, not browsers.

`GET` never changes account state, except `GET /nest-login` (issues an assertion, delivered only to the registered
address) and `GET /login/google/callback` (protected by state, nonce, PKCE and the signed cookie).

### 6.4 Pages (HTML)

| Path | Phase | What |
|---|---|---|
| `GET /` | 1A | 302 `/account` |
| `GET /login?next=&reauth=&error=` | 1A | "Continue with Google" + email form with Turnstile |
| `GET /login/email` | 1A | landing page of the emailed link: reads `#code`, `replaceState`, button "Continue" → `POST /v1/login/email/confirm` |
| `GET /account` | 1A | nests (label, address, status, online, version, last seen), Add a nest, remove, on/off per nest, sessions, delete account; no session → 302 `/login?next=/account` |
| `GET /app?code=XXXX-XXXX` | 1B | approve an app sign-in |
| `GET /privacy`, `/terms`, `/guidelines`, `/cookies`, `/security`, `/faq`, `/help`, `/contact`, `/delete-account`, `/licenses` (with or without a trailing `/`) | 1A | 302 to the same page on the website, `https://pairnets.app/<page>/`. Every page's footer links Privacy, Terms, Guidelines, Cookie settings and Help; the sign-in page also says that continuing means agreeing to the Terms and the Privacy Policy |
| `GET /nest-login` | 1A | §3.2 |

`next` (everywhere on the service) MUST match `^/(account|app(\?code=[0-9A-Za-z-]{1,16})?|nest-login\?[^#\s]{1,600})$`;
anything else becomes `/account`. Version 2 adds `/add` and `/add?code=` (same code pattern as `/app`).

### 6.5 Browser sign-in `[1A]`

**Google**

- `GET /login/google?next=&reauth=1` → sets `__Host-pn_g`, **302** to Google's authorization URL with `client_id`,
  `redirect_uri=https://id.pairnets.app/login/google/callback`, `response_type=code`, `scope=openid email`, `state`,
  `nonce`, `code_challenge` (S256), `prompt=select_account` (and `max_age=0` when `reauth=1`). Per-IP limit.
- `GET /login/google/callback?state=&code=` → cookie and state match and not expired; exchange the code (with the PKCE
  verifier) at Google's token endpoint; verify the `id_token` **signature** (RS256) against Google's JWKS (cached ≤ 1 h),
  `iss` ∈ {`https://accounts.google.com`, `accounts.google.com`}, `aud == GOOGLE_CLIENT_ID`, `exp > now - 60`,
  `nonce` matches, `email_verified == true`, `sub` present (and `auth_time >= flow start` when `reauth`). Then the
  account (§6.6), a new session, **302** `next`. Failure → **302** `/login?error=google_failed|google_denied|google_expired`.
- Test seams (internal): `GOOGLE_AUTH_URL`, `GOOGLE_TOKEN_URL`, `GOOGLE_JWKS_URL` variables; tests mock `fetch`.

**Email**

- `POST /v1/login/email` `{"email":"you@example.com","turnstile":"<widget response>","next":"/account"}`
  - Turnstile fails → 400 `turnstile_failed`. Not an address → 400 `bad_request`. Per-IP limit → 429 `rate_limited`.
    Global daily breaker → 503 `email_unavailable`.
  - Otherwise **always 202** `{"status":"sent"}` and a new `__Host-pn_el` cookie, **whether or not an email was sent**
    (the per-address limits fail silently, so the answer never tells whether an address exists or was limited).
  - The email holds `https://id.pairnets.app/login/email#code=<code>` (15 minutes, once). `next` is kept in the
    challenge row, not in the email.
- `POST /v1/login/email/confirm` `{"code":"…"}`
  - Unknown, used or expired → 400 `invalid_code`. Valid but `__Host-pn_el` missing or not matching → 400
    `wrong_browser` ("Open the link in the browser where you asked for it."); the code stays usable.
  - Success → mark used, account (§6.6), new session cookie, **200** `{"next":"/account"}`.

### 6.6 Accounts and identities

- Email addresses are compared and stored ASCII-lowercased.
- Sign-in with identity (`google`, sub) or (`email`, address):
  1. Identity exists → its account (update `email`, `last_used_at`).
  2. Else an account has that **verified** email → add the identity to it, send a notice email to the account address
     ("Google sign-in was added to your Pairnets account"), audit `identity_linked`.
  3. Else create an account (`acc_…`, email = that address) with the identity, audit `account_created`.
- New session: `amr = ["google"]` or `["email"]`, `auth_time = now`. A re-auth replaces the session (old row deleted).
- **Recent sign-in** = `now - auth_time <= 900`. Required for: creating claim codes, removing a nest, deleting the
  account. Otherwise **401** `reauth_required` (the page sends you to `/login?reauth=1&next=/account`).

### 6.7 Account API `[1A]` (cookie session; mutations Origin-checked)

| Method and path | Request | Success | Errors |
|---|---|---|---|
| `GET /v1/me` | — | 200 `{accountId, email, createdAt, identities:[{provider, email, createdAt, lastUsedAt}], session:{id, kind, authTime, amr, recentAuth}}` | 401 `unauthorized` |
| `DELETE /v1/me` | — | 204, cookie cleared, everything cascades | 401 `unauthorized`, 401 `reauth_required` |
| `GET /v1/nests` | — (session **or** app token) | 200 `{nests:[Nest]}` | 401 `unauthorized` |
| `POST /v1/nests/claim-codes` | `{label?}` | 201 `{code:"PN-XXXX-XXXX-XXXX", expiresAt, command}` | 401 `reauth_required`, 409 `nest_limit`, 429 `too_many_codes` |
| `DELETE /v1/nests/claim-codes` | — | 204 (cancels all unused codes) | 401 |
| `PATCH /v1/nests/{nestId}` | `{label?, hostedLogin?}` | 200 `Nest` | 404 `not_found` |
| `DELETE /v1/nests/{nestId}` | — | 204; notice email; audit `nest_removed` | 401 `reauth_required`, 404 `not_found` |
| `GET /v1/sessions` | — | 200 `{sessions:[{id, kind, createdAt, lastSeenAt, userAgent, deviceName, current}]}` | 401 |
| `DELETE /v1/sessions/{id}` | — | 204 | 404 `not_found` |
| `POST /v1/logout` | — | 204, cookie cleared | — |

`command` = `sudo -u pairnets /opt/pairnets/pairnets-server hosted link PN-XXXX-XXXX-XXXX`.

`Nest` = `{nestId, label, publicUrl, status:"pending"|"active", online, lastSeenAt|null, serverVersion|null,
ready|null, publicHost|null, hostedLogin, createdAt, confirmedAt|null}`. `hostedLogin` here is the service-side
per-nest switch (`nests.hosted_login`). A nest of another account answers 404 `not_found`, never 403.

Limits: at most **3 nests** per account (pending included); at most **5 unused, unexpired claim codes** and 20 per day.

### 6.8 Uniform error codes (service)

| `error` | Status | Meaning |
|---|---|---|
| `bad_request` | 400 / 413 / 415 | malformed request |
| `bad_origin` | 403 | CSRF rule (§6.3) |
| `unauthorized` | 401 | no or invalid session / app token |
| `reauth_required` | 401 | needs a recent sign-in |
| `not_found` | 404 | no such thing (or not yours) |
| `rate_limited` | 429 | with `Retry-After` |
| `turnstile_failed` | 400 | |
| `email_unavailable` | 503 | daily email breaker |
| `invalid_code`, `expired`, `used`, `wrong_browser` | 400 / 400 / 409 / 400 | codes |
| `bad_url`, `nest_limit`, `too_many_codes` | 400 / 409 / 429 | claim |
| `already_decided`, `slow_down` | 409 / 429 | app sign-in |
| `unlinked`, `bad_signature`, `stale`, `not_confirmed` | 410 / 401 / 401 / 409 | nest-signed requests |
| `server_error` | 500 | anything unexpected (no details) |

### 6.9 Apps: sign in with the account `[1B]`

The app never keeps the account token: it uses it to list nests, pairs with the chosen nest the normal way, then throws
it away.

1. `POST /v1/app/start` `{name, system?, appVersion?}` (no auth; per-IP limit) → **200**
   `{deviceCode:"pcd_…", userCode:"XXXX-XXXX", verificationUri:"https://id.pairnets.app/app", verificationUriComplete:"https://id.pairnets.app/app?code=XXXX-XXXX", expiresIn:600, interval:3}`.
   The app opens `verificationUriComplete` in the browser and shows the user code.
2. `/app?code=` page (session required, else `/login?next=/app?code=…`) shows the computer's name, system, version
   and the code, with "Allow" / "Not me". It uses:
   - `GET /v1/app/requests/{userCode}` → 200 `{userCode, name, system, appVersion, createdAt, expiresAt, status}` / 404.
   - `POST /v1/app/approve` `{userCode, approve: true|false}` → 200 `{status:"approved"|"denied"}` / 404 `not_found` /
     409 `already_decided` / 400 `expired`.
3. `POST /v1/app/poll` `{deviceCode}` → 200 `{status:"pending"}` · 200 `{status:"denied"}` ·
   200 `{status:"approved", appToken:"pca_…", expiresIn:3600, email}` (**once**; the login becomes `delivered`) ·
   400 `expired` (unknown, expired or already delivered) · 429 `slow_down` (polled faster than `interval`).
4. `GET /v1/nests` with `Authorization: Bearer pca_…` → the list. App tokens work **only** on `GET /v1/nests`,
   `GET /v1/me` and `POST /v1/app/logout`; anywhere else 401 `unauthorized`.
5. The app pairs with the chosen nest using the existing `PairingFlow` with `method="hosted"`; the approval link
   becomes `<nest>/link?code=…&method=hosted`, and the nest's sign-in page starts hosted login by itself (§3.5). The
   browser is already signed in to the service from step 2, so it comes straight back.
6. `POST /v1/app/logout` with the Bearer token → 204 (also for unknown tokens). The app forgets the token.

With more than one nest the app MUST let the person choose (never pick one silently), showing label and address.

### 6.10 Rate limits

Fixed windows in D1 (`rate_counters`); the key is the event plus the first 22 characters of
`base64url(HMAC-SHA256(COOKIE_KEY, "pn-rl\n" + value))` (value = IP from `CF-Connecting-IP`, or the lowercase email),
so no raw IP or address is stored there.

| What | Per | Limit | Answer when exceeded |
|---|---|---|---|
| any request | IP | 300 / minute | 429 `rate_limited` |
| `GET /login/google` | IP | 30 / 10 min | 429 page |
| `POST /v1/login/email` | IP | 10 / hour | 429 `rate_limited` |
| `POST /v1/login/email` | address | 3 / hour and 10 / day | **silent** (202, nothing sent) |
| login emails | whole service | `EMAIL_DAILY_LIMIT` (80) per UTC day | 503 `email_unavailable` |
| all emails incl. notices | whole service | 95 per UTC day (Resend free plan is 100) | notice skipped, audit `notice_skipped` |
| `POST /v1/login/email/confirm` | IP | 30 / hour | 429 |
| `POST /v1/claim` | IP | 20 / hour, and 10 failures / hour | 429 `rate_limited` |
| claim codes | account | 5 unused at once, 20 / day | 429 `too_many_codes` |
| `GET /nest-login` | account | 60 / hour | 429 page |
| `POST /v1/app/start` | IP | 10 / hour | 429 `rate_limited` |
| `POST /v1/app/poll` | device code | once per `interval` (3 s) | 429 `slow_down` |
| `POST /v1/heartbeat` | nest | 1 / 60 s | 429 `rate_limited` |
| `POST /v1/nest/confirm`, `/unlink` | nest | 10 / hour | 429 `rate_limited` |

### 6.11 Email (internal to the service)

A small `Mailer` interface (`send({to, subject, text, html})`) with a Resend implementation
(`POST https://api.resend.com/emails`, `Authorization: Bearer RESEND_API_KEY`, from `MAIL_FROM`) and a fake for tests.
Emails: sign-in link; notices for identity added, nest linked (confirmed), nest removed or unlinked, account deleted.

### 6.12 Cron (internal)

Hourly (`17 * * * *`): delete expired sessions, challenges, app logins, claim codes, `pending` nests older than 1 hour,
old `rate_counters`, and `audit` rows older than 90 days.

---

## 7. D1 schema (migration `cloud/migrations/0001_init.sql`)

All times are Unix seconds. `*_hash` columns are 32-byte BLOBs: SHA-256 of the UTF-8 secret (whole string, prefix
included). D1 enforces foreign keys, so deleting an account removes everything that belongs to it.

```sql
-- Pairnets Cloud, migration 0001. Additive migrations only from here on.

CREATE TABLE accounts (
  id            TEXT PRIMARY KEY,                 -- acc_ + 26 base32-lower
  email         TEXT NOT NULL,                    -- verified, lowercase; where notices go
  created_at    INTEGER NOT NULL,
  updated_at    INTEGER NOT NULL
);
CREATE UNIQUE INDEX accounts_email ON accounts(email);

CREATE TABLE identities (
  provider      TEXT NOT NULL CHECK (provider IN ('google', 'email')),
  subject       TEXT NOT NULL,                    -- google: id_token sub; email: the lowercase address
  account_id    TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  email         TEXT NOT NULL,                    -- verified address seen at the last sign-in
  created_at    INTEGER NOT NULL,
  last_used_at  INTEGER NOT NULL,
  PRIMARY KEY (provider, subject)
);
CREATE INDEX identities_account ON identities(account_id);

CREATE TABLE sessions (
  id            TEXT PRIMARY KEY,                 -- ses_ + 26: public handle for lists and DELETE
  token_hash    BLOB NOT NULL UNIQUE,             -- SHA-256 of "pcs_..." (browser) or "pca_..." (app)
  account_id    TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  kind          TEXT NOT NULL CHECK (kind IN ('browser', 'app')),
  amr           TEXT NOT NULL,                    -- JSON array, e.g. ["google"]
  auth_time     INTEGER NOT NULL,
  created_at    INTEGER NOT NULL,
  last_seen_at  INTEGER NOT NULL,
  expires_at    INTEGER NOT NULL,
  user_agent    TEXT,                             -- at most 200 characters
  device_name   TEXT                              -- app sessions: the computer's name
);
CREATE INDEX sessions_account ON sessions(account_id);
CREATE INDEX sessions_expires ON sessions(expires_at);

CREATE TABLE login_challenges (
  id            TEXT PRIMARY KEY,                 -- chl_ + 26
  kind          TEXT NOT NULL CHECK (kind IN ('email')),
  email         TEXT NOT NULL,
  code_hash     BLOB NOT NULL UNIQUE,             -- SHA-256 of the emailed code
  binding_hash  BLOB NOT NULL,                    -- SHA-256 of the __Host-pn_el cookie value
  next          TEXT,                             -- allow-listed path (section 6.4)
  created_at    INTEGER NOT NULL,
  expires_at    INTEGER NOT NULL,
  used_at       INTEGER
);
CREATE INDEX login_challenges_expires ON login_challenges(expires_at);

CREATE TABLE device_logins (
  id                TEXT PRIMARY KEY,             -- dvl_ + 26
  device_code_hash  BLOB NOT NULL UNIQUE,         -- SHA-256 of "pcd_..."
  user_code         TEXT NOT NULL UNIQUE,         -- 8 base32-upper, no hyphen
  name              TEXT NOT NULL,
  system            TEXT,
  app_version       TEXT,
  status            TEXT NOT NULL CHECK (status IN ('pending', 'approved', 'denied', 'delivered')),
  account_id        TEXT REFERENCES accounts(id) ON DELETE CASCADE,
  created_at        INTEGER NOT NULL,
  expires_at        INTEGER NOT NULL,
  decided_at        INTEGER,
  last_poll_at      INTEGER
);
CREATE INDEX device_logins_expires ON device_logins(expires_at);

CREATE TABLE claim_codes (
  code_hash     BLOB PRIMARY KEY,                 -- SHA-256 of the canonical "PN-XXXX-XXXX-XXXX"
  account_id    TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  label         TEXT,
  created_at    INTEGER NOT NULL,
  expires_at    INTEGER NOT NULL,
  used_at       INTEGER,
  nest_id       TEXT                              -- the nest it created
);
CREATE INDEX claim_codes_account ON claim_codes(account_id, used_at, expires_at);

CREATE TABLE nests (
  id                 TEXT PRIMARY KEY,            -- nst_ + 26
  account_id         TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  label              TEXT NOT NULL,
  public_url         TEXT NOT NULL,               -- normalised (section 1.8)
  status             TEXT NOT NULL CHECK (status IN ('pending', 'active')),
  key_version        INTEGER NOT NULL DEFAULT 1,  -- heartbeat key = f(HB_MASTER, id, key_version); key not stored
  hosted_login       INTEGER NOT NULL DEFAULT 1 CHECK (hosted_login IN (0, 1)),
  created_at         INTEGER NOT NULL,
  confirmed_at       INTEGER,
  last_req_ts        INTEGER NOT NULL DEFAULT 0,  -- highest accepted X-Pairnets-Ts (replay guard)
  last_seen_at       INTEGER,                     -- last valid heartbeat
  last_version       TEXT,
  last_ready         INTEGER,
  last_public_host   TEXT,
  last_hosted_login  INTEGER
);
CREATE INDEX nests_account ON nests(account_id);
CREATE INDEX nests_status_created ON nests(status, created_at);

CREATE TABLE audit (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  at            INTEGER NOT NULL,
  account_id    TEXT REFERENCES accounts(id) ON DELETE CASCADE,
  event         TEXT NOT NULL,                    -- account_created, identity_linked, login, nest_claimed, ...
  nest_id       TEXT,
  detail        TEXT                              -- small JSON; never codes, tokens, keys or assertions
);
CREATE INDEX audit_account ON audit(account_id, at);
CREATE INDEX audit_at ON audit(at);

CREATE TABLE rate_counters (
  bucket        TEXT NOT NULL,                    -- event + ":" + keyed hash (section 6.10)
  window_start  INTEGER NOT NULL,
  count         INTEGER NOT NULL,
  PRIMARY KEY (bucket, window_start)
);
```

What is stored about people: the account email, Google `sub`, the nest's address and label, last seen / version,
session metadata (user agent, device name, times), and a 90-day audit. `DELETE /v1/me` removes all of it.

---

## 8. Golden vectors

The point: the **real Worker signing code** produces assertions, and the **real C# verifier** checks them, so the two
sides are proven to agree byte for byte.

### 8.1 Procedure

1. The Worker repo has a generator that runs under Node ≥ 20 (WebCrypto is built in): a vitest file with its own config,
   `cloud/vitest.vectors.config.ts` (Node environment, not the Workers pool, because it writes a file), run by
   `npm run vectors`. It imports the Worker's own `signAssertion` (no second implementation).
2. It generates two P-256 key pairs at run time (`test-1`, `test-2`), builds the cases of §8.3 from the fixed values of
   §8.2, and writes `cloud/test/vectors/assertion-v1.json` (format §8.4) containing **only public keys**, the JWS
   strings, the fixed `now` and the expected outcome. Private keys exist only in memory and are dropped. It asserts that
   no key object has a `d` member before writing.
3. The file is committed. Re-running the generator changes every key and signature (ECDSA is randomised); that is fine,
   it is only re-run on purpose.
4. Worker CI (normal suite) loads the committed file and checks that every `ok` case verifies with WebCrypto against
   its key, and that the header and claims of `valid` are exactly the §8.2 values.
5. The C# test `tests/Pairnets.Tests/Unit/HostedGoldenVectorTests.cs` finds the repo root (walk up from
   `AppContext.BaseDirectory` to the folder with `Pairnets.sln`), reads the file, builds the key set from `keys`, and
   for **each case, with a fresh jti cache**, calls the pure verifier with `nestId`, `sub`, `case.nonce` and
   `case.now` (fake clock); asserts `expect` and `reason`; for `ok` cases also asserts `sub` and `amr == ["google"]`.
   It also asserts no key has `d`, and that the file's `issuer` equals the nest's constant. While the file does not
   exist yet, the test class is marked `[Fact(Skip = "until cloud/test/vectors/assertion-v1.json exists")]`; the
   integrator removes the skip when both sides are merged. (A missing file must never pass silently.)

### 8.2 Fixed values

| Name | Value |
|---|---|
| `T` | `1791504000` (2026-10-09T00:00:00Z) |
| `NEST` | `nst_testnest000000000000000001` |
| `NEST2` | `nst_testnest000000000000000002` |
| `ACC` | `acc_testacct000000000000000001` |
| `ACC2` | `acc_testacct000000000000000002` |
| `N` | `TestNonce000000000000A` |
| `N2` | `OtherNonce00000000000A` |
| `J` | `TestJti00000000000000A` |
| `H0` | `{"alg":"ES256","typ":"pn-login+jwt","kid":"test-1"}` |
| `C0` | `{"iss":"https://id.pairnets.app","aud":NEST,"sub":ACC,"iat":T,"nbf":T,"exp":T+90,"nonce":N,"jti":J,"amr":["google"],"auth_time":T-600,"ver":1}` |

Unless a case says otherwise: header `H0`, claims `C0`, signed with `test-1`'s key, `now = T`, `nonce = N`.

### 8.3 Cases

| # | `name` | How it differs from the default | `now` | `expect` | `reason` |
|---|---|---|---|---|---|
| 1 | `valid` | — | T | ok | null |
| 2 | `valid-next-key` | `kid` `test-2`, signed with `test-2` | T | ok | null |
| 3 | `skew-late-ok` | — | T+149 | ok | null |
| 4 | `skew-early-ok` | — | T−60 | ok | null |
| 5 | `wrong-kid` | `kid` `test-9` (not in `keys`), signed with `test-1` | T | failed | `kid` |
| 6 | `alg-none` | header `alg` `none`; signature segment empty (JWS ends with `.`) | T | failed | `alg` |
| 7 | `alg-hs256` | header `alg` `HS256`; signature = HMAC-SHA256(key = `test-1`'s 65-byte uncompressed point `04‖x‖y`, signing input) | T | failed | `alg` |
| 8 | `typ-jwt` | header `typ` `JWT` | T | failed | `typ` |
| 9 | `crit-header` | header `{"alg":"ES256","typ":"pn-login+jwt","kid":"test-1","crit":["exp"]}`, validly signed | T | failed | `header` |
| 10 | `jwk-header` | header also has `"jwk":{…test-2's public JWK…}`, signed with `test-2` | T | failed | `header` |
| 11 | `sig-der` | signature segment = base64url(DER of the same r, s) | T | failed | `sig-format` |
| 12 | `sig-noncanonical` | last character `c` of `valid`'s signature segment replaced by `ALPHABET[index(c) OR 1]` (bitwise OR: sets an unused bit; same bytes to a lenient decoder) | T | failed | `sig-format` |
| 13 | `sig-std-alphabet` | `valid`'s signature segment with `-`→`+` and `_`→`/` (the generator re-signs `valid` until its signature segment has at least one `-` or `_`) | T | failed | `sig-format` |
| 14 | `payload-padding` | `valid` with `=` appended to the payload segment | T | failed | `b64` |
| 15 | `payload-whitespace` | `valid` with one space inserted after the 10th character of the payload segment | T | failed | `b64` |
| 16 | `tampered-payload` | payload segment of `C0` with `sub = ACC2`, header and signature from `valid` | T | failed | `sig` |
| 17 | `wrong-key-same-kid` | `kid` `test-1`, signed with `test-2`'s key | T | failed | `sig` |
| 18 | `duplicate-claim` | payload text = `C0`'s JSON with `,"sub":"<ACC2>"` inserted before the final `}` | T | failed | `claims` |
| 19 | `aud-array` | `aud` = `[NEST]` | T | failed | `claims` |
| 20 | `iat-string` | `iat` = `"1791504000"` (a string) | T | failed | `claims` |
| 21 | `ver-2` | `ver` = 2 | T | failed | `ver` |
| 22 | `iss-trailing-slash` | `iss` = `https://id.pairnets.app/` | T | failed | `iss` |
| 23 | `bad-aud` | `aud` = NEST2 | T | failed | `aud` |
| 24 | `bad-sub` | `sub` = ACC2 | T | failed | `sub` |
| 25 | `clock-behind` | — | T+301 | clock | `clock` |
| 26 | `clock-ahead` | — | T−301 | clock | `clock` |
| 27 | `iat-future` | — | T−61 | failed | `iat` |
| 28 | `lifetime-too-long` | `exp` = T+301 | T | failed | `lifetime` |
| 29 | `nbf-future` | `nbf` = T+61 | T | failed | `nbf` |
| 30 | `expired` | — | T+150 | failed | `exp` |
| 31 | `auth-time-future` | `auth_time` = T+1 | T | failed | `auth_time` |
| 32 | `nonce-mismatch` | case `nonce` = N2 (the JWS still carries N) | T | failed | `nonce` |
| 33 | `amr-empty` | `amr` = `[]` | T | failed | `amr` |
| 34 | `jti-short` | `jti` = `short` | T | failed | `jti` |

`ALPHABET` is the base64url alphabet in RFC 4648 order (`A`–`Z`, `a`–`z`, `0`–`9`, `-`, `_`). Unless stated, a modified
header or payload is re-signed with the stated key, so only the intended check fails. The required negatives (wrong kid,
alg none, HS256 confusion, bad aud, bad sub, expired, nbf in the future, lifetime over 5 min, bad base64url, DER instead
of raw, extra `crit`) are cases 5, 6, 7, 23, 24, 30, 29, 28, 12–15, 11 and 9.

### 8.4 File format (`cloud/test/vectors/assertion-v1.json`)

UTF-8, 2-space indentation, cases in the order of §8.3:

```json
{
  "format": "pairnets-login-assertion-vectors",
  "version": 1,
  "generator": "cloud/test/vectors.gen.test.ts",
  "issuer": "https://id.pairnets.app",
  "nestId": "nst_testnest000000000000000001",
  "sub": "acc_testacct000000000000000001",
  "keys": [
    { "kty": "EC", "crv": "P-256", "kid": "test-1", "x": "<base64url>", "y": "<base64url>" },
    { "kty": "EC", "crv": "P-256", "kid": "test-2", "x": "<base64url>", "y": "<base64url>" }
  ],
  "cases": [
    {
      "name": "valid",
      "description": "the reference assertion",
      "jws": "<compact JWS>",
      "now": 1791504000,
      "nonce": "TestNonce000000000000A",
      "expect": "ok",
      "reason": null
    }
  ]
}
```

- `keys` objects have exactly `kty`, `crv`, `kid`, `x`, `y`. `expect` ∈ `ok | failed | clock`. `reason` is `null` for
  `ok`, otherwise one of the §2.6 reason codes.
- Both readers ignore unknown top-level members (so the format can grow), but reject the file if `format` or `version`
  differ.

---

## 9. Threats and which check stops them

"Nest" tests are C# (`tests/Pairnets.Tests/…`); "Worker" tests are vitest files in `cloud/test/`. Test names are the
ones each builder should write (C# in the repo's sentence style).

| # | Threat | Stopped by (side: check) | Proven by |
|---|---|---|---|
| 1 | Forged assertion: `alg:none`, HS256 with the public key, unknown `kid`, own key in `jwk`/`jku` header | Nest: V3–V4 strict header, pinned keys only | Vectors 5, 6, 7, 9, 10; `HostedAssertionTests.OnlyTheExactHeaderIsAccepted` |
| 2 | Parser tricks: padded / non-canonical / standard-alphabet base64, DER signature, duplicate claims | Nest: §1.7 strict base64url, V5 64-byte signature, V3a/V8 duplicate names | Vectors 11–15, 18 |
| 3 | Assertion for nest A replayed at nest B | Nest: V11 `aud == hosted.nestId` | Vector 23; Worker `nest-login.test.ts › aud is the nest id` |
| 4 | Someone else's account signs in to my nest | Worker: §3.2 step 3 ownership. Nest: V12 `sub == hosted.sub` | Vector 24; Worker `nest-login.test.ts › another account gets the not-linked page`; `HostedSignInTests.AnotherAccountDoesNotGetIn` |
| 5 | Stolen assertion replayed later or twice | Nest: E5 one-time pending entry, V17 90 s expiry, V21 jti cache | Vector 30; `HostedSignInTests.AnAssertionWorksOnce` |
| 6 | Assertion pushed into another browser (session swap / login CSRF) | Nest: E5 binding cookie `__Host-pn_hosted` + V19 nonce | Vector 32; `HostedSignInTests.OnlyTheBrowserThatStartedCanFinish` |
| 7 | Open redirect: `/nest-login` sends the assertion to an attacker's address | Worker: §3.2 step 6 exact `return == public_url + "/hosted-return"`; errors never redirect | Worker `nest-login.test.ts › refuses any other return address` |
| 8 | Assertion leaks via logs, Referer or history | Both: fragment delivery, `Referrer-Policy: no-referrer`, `replaceState`, `no-store`; nest never logs the JWS | Worker `nest-login.test.ts › assertion only in the fragment`; `HostedSignInTests.TheAssertionNeverReachesTheLog` |
| 9 | Cross-site POST to the nest's finish endpoint | Nest: E2 same-origin check | `HostedSignInTests.FinishFromAnotherSiteIsRefused` |
| 10 | Claim-code phishing: "run this command" links my nest to the attacker's account | Nest CLI: masked-email confirm before anything is stored (§4.3 step 8); Worker: pending until signed confirm | `HostedCliTests.DecliningStoresNothingAndTellsTheService`; Worker `claim.test.ts › pending nest cannot be signed in to` |
| 11 | Stolen claim code links the attacker's nest to my account (my apps might pair with it) | Worker: 10 min, single use, recent sign-in to create, notice email on confirm; apps never auto-pick among several nests | Worker `claim.test.ts › codes expire / work once / need recent sign-in`; `notice email on confirm` |
| 12 | Claim code guessing | Worker: 60 bits, 10 min, per-IP limits incl. failures | Worker `claim.test.ts › rate limited after failures` |
| 13 | Two parallel claims of one code | Worker: atomic batch (§4.2) | Worker `claim.test.ts › parallel redeem: exactly one wins` |
| 14 | Account takeover at the service = owner access to nests | Worker: re-auth for claim/remove/delete, notice emails, session list, per-nest switch, emergency switch. Nest: hosted login never the last way in, local on/off, `hosted unlink` signs out hosted sessions | Worker `account.test.ts › reauth required`; `HostedSignInTests.TheLastWayInIgnoresHostedLogin`; `HostedCliTests.UnlinkSignsOutHostedSessions` |
| 15 | Forged Google identity / code injection | Worker: id_token signature via JWKS, iss/aud/exp/nonce/email_verified, PKCE, signed state cookie | Worker `login-google.test.ts` (each claim wrong → refused) |
| 16 | Email bombing, address probing | Worker: Turnstile, identical 202, per-address/IP limits, daily breaker | Worker `login-email.test.ts › same answer for limited and unknown addresses`, `breaker` |
| 17 | Email link opened by a mail scanner or in another browser | Worker: code in fragment, button press to confirm, `__Host-pn_el` binding | Worker `login-email.test.ts › wrong browser is refused and the code survives` |
| 18 | A sibling `*.pairnets.app` site (another nest) reads or rides the account session | Worker: `__Host-` cookies (no Domain), exact Origin check, CSP | Worker `csrf.test.ts › every mutation needs the exact Origin`; `headers.test.ts › cookie attributes` |
| 19 | Forged or replayed heartbeat / confirm / unlink | Worker: HMAC per nest, 5 min window, strictly increasing `ts` | §5.6 vectors on both sides; Worker `nest-signed.test.ts › replay refused`, `wrong key refused` |
| 20 | Forged service answer turns things on or off at the nest | Nest: signed 200 bound to `reqTs`; only "off" actions exist; 410 only disables | `HostedHeartbeatTests.AnUnsignedAnswerChangesNothing`, `GoneOnlyTurnsHostedLoginOff` |
| 21 | Service down or gone | Nest: password, passkeys, email, Google, `owner-link` unaffected; computers keep syncing | `HostedSignInTests.LocalSignInWorksWhenTheServiceIsDown` |
| 22 | Privacy: an unlinked nest talks to the service | Nest: heartbeat loop makes no call unless linked and not opted out | `HostedHeartbeatTests.AnUnlinkedNestMakesNoCalls`, `OptOutMakesNoCalls` |
| 23 | Signing key leak | Both: `kid` rotation with current + next pinned; per-nest and emergency switches; `hosted disable` | Vector 2 (next key works), vector 5 (unknown kid fails) |
| 24 | Nest clock wrong → confusing failures | Nest: V13 distinct `hosted-clock` answer | Vectors 25, 26 |
| 25 | D1 dump leaks secrets | Worker: only hashes of tokens/codes; heartbeat keys derived, not stored; no email in assertions | Worker `schema.test.ts › no secret is stored in clear` (after a full login + claim, no row contains a token, code or key) |
| 26 | App sign-in phishing (victim approves the attacker's app) | Worker: app token can only list nests; approve page shows computer and code. Nest: pairing still needs the owner's Allow | Worker `app-login.test.ts › app token cannot create claim codes` |
| 27 | Flood of starts fills the nest's pending map | Nest: 200-entry cap, oldest first, rate limiter | `HostedSignInTests.ManyStartsDoNotPushOutTheNewest` |
| 28 | Test key override left on in production | Nest: start-up warning; changing it needs the nest's own config (root) | `HostedSignInTests.OverriddenKeysAreLogged` |

Note: tests that use the nest's HTTPS test website fail on the owner's PC only (TLS chain), so every check that can be is
also covered by unit tests of the pure verifier and the HMAC helpers.

---

## 10. Open questions for the owner

1. **Production signing keys.** Proposal: you run `npm run keygen` (provided in `cloud/`) once; it prints the private key
   for `wrangler secret put SIGNING_KEY` and the public halves for `HostedKeys.cs`. A second "next" key is kept offline
   (password manager). Until then hosted login stays switched off in every build. OK?
2. **Nest address changes.** This contract requires `hosted unlink` + a new `hosted link`. Should the account page
   instead offer "accept the new address" when a heartbeat reports one?
3. **Unlink from the nest's website?** Here only the command line unlinks; the website has an on/off switch.
4. **Limits:** 3 nests per account, codes 10 minutes, account sessions 30 days idle / 90 days max. OK?
5. **Releases:** a push to `main` that only changes `cloud/` still rebuilds "Latest build". Add `paths-ignore: cloud/**`
   to the release workflow before this merges?

---

## Appendix A: nest-side changes in one list

- `Auth/HostedKeys.cs` (§2.5), `Auth/HostedAssertion.cs` or similar (pure verifier §2.6, strict base64url §1.7),
  `Auth/HostedSignIn.cs` (pending map §3.1, jti cache), `Auth/HostedClient.cs` (claim, signed requests §5.2,
  `HttpMessageHandler` injectable), `Web/HostedSignInEndpoints.cs` (mapped right after `LinkedSignInEndpoints.Map` in
  `WebEndpoints.Map`), `Storage/AuthStore.Hosted.cs` (§4.4 rows; set-all-in-one-transaction; delete `hosted.%`; delete
  sessions by method), `Services/HostedHeartbeatService.cs` `[1B]`.
- Registration with `TryAddSingleton` so tests can register fakes first.
- `SyncOptions`: `HostedServiceUrl`, `HostedPublicKeys`, `HostedHeartbeat` (§4.7).
- `Pairnets.Core/Models.cs`: `SignInMethods(bool Password, bool Passkeys, bool Email, bool Google, bool Hosted = false)`;
  `/api/hello` fills `Hosted` with "usable" (§4.6).
- `OwnerAuth.UsableMethods()` gains `Hosted` (best as a small record instead of the tuple; its users are
  `OwnerAuth.HasOtherWayThan`, `Endpoints` (`/api/hello`), `PasskeyEndpoints` (passkey delete), `WebEndpoints.Methods`).
  `HasOtherWayThan` and the passkey-delete rule ignore `Hosted`.
- `AuthStore.HasSignInMethod` also true when linked and enabled (§4.6).
- `/web/api/state`: `methods.hosted`. `/web/api/security`: `hosted: {linked, enabled, account, nestId, disabledBy}`
  (`account` = `hosted.maskedEmail`). New `POST /web/api/hosted` `{enabled: bool}` (signed in, same origin): sets
  `hosted.enabled` (`disabledBy=owner` when off); enabling when not linked → 409 `{code:"conflict"}`. No network.
- Website: `hosted-return` page (§3.3), the sign-in button and messages (§3.5), a "Pairnets account" row on the Security
  page with the on/off switch.
- Command line: `hosted link|unlink|status|enable|disable` (§4.3–4.5).
- Test infrastructure: `tests/Pairnets.Tests/Infrastructure/FakeHostedService.cs` — a small Kestrel app (like
  `FakeGoogle`) implementing `GET /nest-login`, `POST /v1/claim`, `/v1/nest/confirm`, `/v1/nest/unlink`, `/v1/heartbeat`
  **exactly as in this contract**, signing with a key generated at test time (it may accept `https://localhost` as a
  public URL, which the real service refuses). Unit tests: `HostedAssertionTests`, `HostedSigningTests` (§5.6),
  `HostedGoldenVectorTests` (§8). Integration: `HostedSignInTests`, `HostedCliTests`, `HostedHeartbeatTests` `[1B]`.
- Out of bounds for this work: `LinkedSignInEndpoints.cs`, `Pairnets.Server.csproj`, `FakeMail.cs`,
  `NestWebsiteTests.cs` (someone else's unfinished email-template work touches them).

## Appendix B: service configuration (Worker)

| Name | Kind | Value / meaning |
|---|---|---|
| `DB` | D1 binding | the database (§7) |
| `SIGNING_KEY` | secret | private JWK with `kid` (§2.5) |
| `HB_MASTER` | secret | base64url, 32 bytes (§5.1) |
| `COOKIE_KEY` | secret | base64url, 32 bytes: signs `__Host-pn_g`, keys rate-limit buckets |
| `GOOGLE_CLIENT_SECRET` | secret | |
| `TURNSTILE_SECRET` | secret | |
| `RESEND_API_KEY` | secret | |
| `PUBLIC_ORIGIN` | variable | `https://id.pairnets.app` |
| `GOOGLE_CLIENT_ID`, `TURNSTILE_SITE_KEY` | secrets | public values, kept in the secret store so no id is in the code |
| `MAIL_FROM` | variable | `Pairnets <noreply@pairnets.app>` |
| `EMAIL_DAILY_LIMIT` | variable | `80` |
| `HOSTED_LOGIN_DISABLED` | variable | `"1"` = emergency: `/nest-login` refuses, heartbeats answer `disableHostedLogin: true` |
| `GOOGLE_AUTH_URL`, `GOOGLE_TOKEN_URL`, `GOOGLE_JWKS_URL` | variables | Google's real URLs by default; tests point them at mocks |

Secrets are set only in the Cloudflare dashboard or with `wrangler secret put`; locally in `cloud/.dev.vars` (ignored by
git). Tests generate every key at run time. Tools: TypeScript, zero runtime dependencies (WebCrypto only); dev
dependencies `wrangler`, `vitest`, `@cloudflare/vitest-pool-workers`, `typescript`.
