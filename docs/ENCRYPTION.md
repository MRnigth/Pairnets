# End-to-end encryption (plan)

> **Status (1.0.61.1, in progress):** the encryption core (`src/Pairnets.Core/Crypto`) and the nest's encrypted
> mode and key mailbox are built and tested; the apps' sync engine, screens and moving existing nests are not yet, so
> nothing is encrypted for users and none of §3 may be said yet. The decisions are in [DECISIONS.md](DECISIONS.md)
> under "End-to-end encryption plan (2026-10-19)" and "Encryption core (1.0.61.1, 2026-10-20)", which also lists
> where the build differs from this plan (segments bound to a random file id, tags from the plain SHA-256, signed
> envelopes).

The goal: everything that passes through or sits on machines Pairnets runs is unreadable to Pairnets, and only the
user's own computers hold the keys. Built only with .NET 8's own `System.Security.Cryptography` (no new packages).

## 1. Where user data goes today

| Place | What is there | Readable by Pairnets' side today? |
|---|---|---|
| **Relay** (sync.pairnets.app: `pairnets-sync` → `pairnets-router` → Workers VPC → the nest's tunnel → `127.0.0.1:5075`) | File contents (bodies are streamed, `cloud/src/relay.ts`), **file paths** in query strings (`api/file?path=`, `api/upload?path=`, `api/history?path=`), content hashes (`X-Pairnets-Hash`, `ETag`), modified times, the computer's key (`X-Sync-Token`, the hub's `access_token`), the computer's name (`X-Device-Id`), app version and system (`X-Pairnets-Client`), the caller's IP | **Yes.** HTTPS ends at Cloudflare. Nothing is logged or kept today (no `[observability]` or logpush in `wrangler.toml`, and no `console.*` prints paths), but a changed Worker, `wrangler tail` or Workers Logs could see all of it. Cloudflare sees it too. |
| **The service can let computers in** | The Worker derives every linked nest's admin key from `HB_MASTER` (`cloud/src/nestadmin.ts`) and can create a computer key on any nest (`POST /api/relay/devices`, `cloud/src/routes/apps.ts`). It can fetch any nest's tunnel token again (`cloud/src/cloudflare.ts`). | **Yes.** A key the service creates can download every file. This is the biggest gap. |
| **D1** (`pairnets-sync`; no R2, KV, Durable Objects or Queues) | `accounts` (email, username), `identities` (Google `sub` or email), `sessions` (user agent, device name), `device_logins` (computer name, system, app version, the request's city and country), `server_links` (server host name), `nests` (label, tunnel id, last seen, version), `audit` for 90 days (computer and host names in `detail`), `rate_counters` (HMAC of IP or email, 2 days). Raw IPs, tunnel tokens and computer keys are not stored. | **Yes**: account data, not files. D1 Time Travel keeps deleted rows for up to 30 days. |
| **Sign-in and mail** | Google gives `sub` and email. Email links go through Cloudflare Email Sending, with Resend as the fallback; the provider sees the address and the link. Notice emails name the computer, the nest's label and the city or country. | **Yes**: account data. |
| **The nest** (the user's own server) | `files/` and `history/` under the real paths; `manifest.db` (path, size, modified time, SHA-256, version); `auth.db` (computer names, join requests' IPs, the owner's email, passkeys); `devices.json`; journald lines with **every file path** (`RequestLoggingMiddleware`, `SyncStore`) | Not directly (it is the user's machine), **but** a key the service creates reads everything, and so does anyone with root on the machine. |
| **The apps** (`Pairnets.Core`, `Pairnets.Client`, `Pairnets.Desktop`) | The plain files; `state.db` (paths, hashes); the computer's key under DPAPI, the Keychain or the keyring; logs in `%LocalAppData%\Pairnets\logs` with the folder path and every file path, kept 14 days | **No.** Stays on the computer. "Report a bug" copies text to the clipboard and saves a file locally; nothing is sent. |
| **Crash cards** (`feature/crash-cards`, not merged) | Cleaned crash records in `%LocalAppData%\Pairnets\crashes`; `PrivacyCleaner` removes paths, names, emails and IPs | **No.** Nothing is sent. Sending is a separate, undecided step. |
| **Update check** | GitHub sees the IP and the app version | GitHub, not Pairnets. |

## 2. Design: encrypt on the computer, the nest keeps only encrypted data

The apps encrypt every file **and its path** before it leaves the computer. The nest stores opaque blobs under
meaningless ids and never holds a key. This closes all three gaps at once:
- the relay only carries encrypted data;
- a key the service creates only downloads encrypted data;
- root on the nest (or a stolen nest disk) gets nothing readable.

An inner encrypted channel to a nest that still keeps plain files was rejected, because the service could still
create a key and read everything.

### 2.1 Keys

- **Folder key ring.** It is made on the first computer when encryption is turned on. It holds a generation number
  and 32 random bytes per generation.
  - Sub-keys come from `HKDF` (SHA-256) with fixed labels: `content-wrap`, `meta` and `tag` (per generation), and
    `id` (from generation 0, never changes).
- **Computer key pair.** Each computer gets one (`ECDiffieHellman`, P-256) when it unlocks.
  - The ring is kept beside the computer's nest key (DPAPI, the Keychain or the keyring).
  - It is never in plain text and never logged.
- **The ring lists the trusted computers' public keys.** The ring is encrypted and authenticated, so neither the nest
  nor the service can add a computer to it.
- **Envelopes on the nest.** The nest stores these as opaque blobs:
  - one per trusted computer: the ring sealed to that computer's public key (ECDH with an ephemeral P-256 key → HKDF
    → `AesGcm`);
  - one sealed with the recovery key.

### 2.2 File format (`PNE1`)

- **Header:**
  - magic and version;
  - the ring generation;
  - the file's own random 32-byte data key, wrapped with `content-wrap` (AES-GCM);
  - a 4-byte random nonce prefix.
- **Body:** 1 MiB segments.
  - Each segment is AES-256-GCM with nonce = prefix ‖ 8-byte counter.
  - The associated data binds the file id, the generation, the segment number and a "last segment" flag. The nest
    therefore cannot reorder, cut, swap or graft files without the app noticing.
- **A new data key and prefix for every upload.** Two versions of a file never share keys.
- **Overhead:** about 80 bytes plus 16 bytes per MiB (≈ 0.002 %). The encrypted size follows from the plain size.

### 2.3 Paths and folders

- **Flat ids.** The nest stores each file under `base32lower(HMAC-SHA256(id, NFC(full path)))`, first 32 characters,
  in two-letter shard folders.
  - The same path always gives the same id.
  - So these keep working unchanged: the nest's base check (409), per-path history, the `since` manifest and resumable
    pieces.
  - Lower-case base32 never trips the nest's own case check.
- **The nest sees no folder tree:** no names, no depth, no "these files sit together".
- **The real path, modified time and plain size** go into an encrypted `meta` blob (AES-GCM with `meta`, associated
  data = the id).
  - It is stored in a new opaque manifest column.
  - The other computers read paths from it without downloading files.
- **Name rules move to the app.** The nest can no longer check names that differ only in letter case, Windows-hostile
  names or lengths. The app runs `PathRules` and the case check itself before uploading.

### 2.4 Hashes and the sync decision

- **Keyed tag.** L, S and B become `HMAC-SHA256(tag, id ‖ content)` instead of a plain SHA-256.
  - `SyncDecision.Decide` and its exhaustive tests are untouched.
  - The nest stores the tag it is given in the `hash` column and still compares on it under its lock.
- **Transport checks.** The nest checks a SHA-256 of the **encrypted** bytes for the commit check, `ETag` and
  `If-Range`.
  - It never sees a plain-content hash, so it cannot test "is this the well-known file X?".
- **After a download** the app decrypts (AES-GCM proves the data is genuine), recomputes the tag and compares it with
  the manifest, as the existing "verify before replace" step does.
- **The id is part of the tag**, so two identical files in different places do not look alike to the nest.

### 2.5 Big files and resuming

- **Uploads.**
  - Pieces stay 4–50 MiB of encrypted bytes; `PieceSizer` is unchanged.
  - The app keeps the wrapped data key and the nonce prefix in `state.db`'s `uploads` row. After a cut it remakes
    exactly the same bytes and continues at the nest's offset.
  - `PieceReader` hashes the encrypted stream.
- **Downloads.**
  - The app decrypts segment by segment while it downloads into the existing `.pairnets-tmp` part file (plain).
  - It resumes at encrypted offset `header + k × (1 MiB + 16)`, where `k = plain length / 1 MiB`. No extra disk space
    is needed.

### 2.6 A new computer

1. **It signs in as today.** That only lets it talk to the nest, so a key the service creates opens nothing.
2. **The nest says the folder is encrypted**, and the app shows **Unlock** with two ways:
   - **With another computer.** New nest endpoints (`/api/keys/requests`) act as a mailbox, and a `KeyRequested`
     push tells the other computers.
     - The two computers do a P-256 key exchange with a commitment: the new computer first sends only a hash of its
       key and nonce, then reveals them.
     - Both screens show the same 6-digit code, made from both keys and nonces. The user presses **Yes, same code**
       on the old computer.
     - A man in the middle has a one-in-a-million chance and one try.
     - The old computer then adds the new public key to the ring and sends the ring sealed to it.
   - **With the recovery key.** The app opens the recovery envelope and adds itself to the ring.

A QR code is optional for later; both computers can already show the 6-digit code.

### 2.7 Losing every computer

- **The recovery key** is 160 random bits, shown once as eight groups of four letters (base32 plus a check letter).
  - The user writes it down or prints it, and the wizard asks for two groups back.
  - It is random rather than a password, so it needs no slow hashing.
  - It opens only the recovery envelope.
- **Honest rule:** lose every computer **and** the recovery key, and the files on the nest are gone for good.
  Pairnets cannot help, by design. The app and the site say so before encryption is turned on.
- **Settings → Make a new recovery key** replaces the envelope; the old key stops working.

### 2.8 A stolen computer

1. **Remove it as today** (on the nest's website or the Pairnets account page). Its nest key stops at once.
2. **The app then rotates the ring:**
   - it makes generation *n+1* without the stolen computer;
   - it seals new envelopes for the remaining computers and the recovery key;
   - it uses *n+1* for every new upload.
3. **New and changed files stay closed to a later key for the thief**, even one the service creates.
4. **Old files stay under their old generation.** The ring keeps every generation, and an optional "encrypt everything
   again" can re-upload them later.
5. **A computer refuses a ring older than the newest generation it has seen.** The nest or the service therefore
   cannot hand back the ring that still trusts the stolen computer.
6. **Honest limit:** what the stolen computer had already synced is on it in plain form.

### 2.9 Speed

- **AES-GCM is fast.** It runs at several GB/s per core on any recent PC or Mac (AES-NI, ARMv8), far above any
  internet line.
- **The keyed tag costs the same as today's SHA-256.**
- **Turning encryption on costs one full read of the folder** (new tags) and one full upload.
- **A Raspberry Pi 4 used as a computer** (no AES instructions) manages roughly 50–100 MB/s. The nest encrypts
  nothing.
- **To check first:** `AesGcm.IsSupported` must be true on the Windows, macOS and Linux CI runners. If one lacks it,
  the app says so clearly instead of syncing unencrypted.

### 2.10 Old and new versions; moving existing users

- **Capability flags.**
  - The nest advertises `Sealed` in `ServerInfo` / `ServerHello` (the optional-field pattern of `Models.cs`).
  - Apps add `sealed` to `X-Pairnets-Client`.
  - `ApiVersion` stays 1.
- **An encrypted nest refuses apps without `sealed`** with `426 update_required`. Old apps update themselves from
  "Latest build".
  - Before switching, the app lists computers on older versions (from `devices.json`) and asks for them to be
    updated first.
- **Order:**
  1. Release apps and nest that understand encrypted nests but change nothing.
  2. Encryption for new nests.
  3. "Turn on encryption" for existing nests.
- **Moving an existing nest:**
  1. A fully synced computer makes the ring and the recovery key.
  2. It uploads every file encrypted into a separate `sealed/` area (its own manifest, files and history) while normal
     sync goes on.
  3. It catches up the changes and asks the nest to switch. The nest switches only if the plain manifest version is
     still the one the app caught up to; otherwise the app catches up again.
  4. The other computers see "encrypted now" and unlock.
     - They tag their local files and match them.
     - Identical files transfer nothing (the "same on both sides → record base" case).
  5. The plain `files/` and `history/` are deleted when every computer has switched, or after 7 days, whichever comes
     first.
     - The apps show "Finishing encryption…" until then.
     - The claims below hold only after that.
     - Versions from before the switch are not kept.

### 2.11 Other leaks closed in the same work

- **Nest log lines** then hold ids only; the remaining name and email lines go too (`LinkedSignInEndpoints`).
- **The push** `Changed(device, path)` carries ids.
- **"Report a bug"** text goes through `PrivacyCleaner`, so pasted reports do not carry file names.
- **Optional:** computer names hidden from the service. A neutral label goes to the service and the nest's headers,
  and the real names live in the ring.

## 3. What may be said afterwards

**May be said** (for a nest that is encrypted and whose plain copy is gone):
- Files are encrypted on the user's computers before they leave, and only those computers hold the key.
- Pairnets and Cloudflare cannot see the files, the file names or the folders.
- The nest stores only encrypted data, so a stolen server reveals nothing readable.
- A computer that Pairnets or anyone else lets reach the nest gets only encrypted data. Unlocking needs one of the
  user's computers or the recovery key.

**Still visible to Pairnets** (must be said):
- Account email, Google id, username, sign-in times, browser type, and the rough city and country of a sign-in
  request.
- Computer names and systems (unless the optional step hides them), when they are online, and app versions.
- How many files there are, roughly how big each is, how much space is used, when files change and which computer
  changed them.
- IP addresses while traffic passes (not stored).

**Must not be said:**
- "Zero-knowledge", "military-grade", "unbreakable", or "nobody can ever access your data".
- "We can't see anything" (the account and usage data above).
- "We can recover your files": not without the recovery key.
- That it protects against a hacked computer of the user's, a malicious app update, or someone holding the recovery
  key.
- That files are encrypted where encryption is not on, or during the switch-over.
- That Pairnets cannot delete or block data: the service and the nest's owner can still delete or refuse to serve.
  History and the other computers' copies are the safety net.
- "Audited", until someone independent has reviewed it.

**Privacy page wording** (to replace the "no end-to-end encryption yet" lines when it ships):

> **End-to-end encryption.** When encryption is on (it is for every new nest), the Pairnets app scrambles each file,
> its name and its folder on your computer before sending it, with a key that only your computers have. Your nest
> stores only scrambled data, and sync.pairnets.app and Cloudflare only ever pass scrambled data along. We can't read
> your files, their names or your folders. If we or anyone else let a computer reach your nest, it gets nothing
> readable without one of your computers or your recovery key.
>
> **What we can still see:** your account (email, username, how you sign in), your computers' names and systems and
> when they're online, and how many files you have, roughly how big they are and when they change.
>
> **Your recovery key** is shown once, when you turn encryption on. We never get a copy. If you lose all your
> computers and your recovery key, nobody, including us, can open your files.

## 4. Steps (most valuable first), each a yes/no for the owner

| # | Step | For users | Rough effort |
|---|---|---|---|
| 1 | Approach: encrypt on the computer, the nest keeps only encrypted data | Only their own computers can read files or names | decision |
| 2 | Recovery key written down by the user, no copy at Pairnets | Lose every computer and the key → files are gone; told beforehand | decision |
| 3 | Encryption core + encrypted nest mode + sync engine (branch `feature/e2e`, with tamper tests) | Nothing visible yet | 8–10 days |
| 4 | Screens in both apps (pictures first): turn on + recovery key, Unlock, "Remove and lock", new recovery key | The new steps | 3 days + pictures |
| 5 | Encryption for every new nest | New users protected from day one | 1 day + release |
| 6 | "Turn on encryption" for existing nests; plain copy deleted after all computers switch or 7 days | One big re-upload; old versions from before are dropped | 3 days |
| 7 | Privacy and Security pages, same release as 5 | Clear, honest statement | half a day |
| 8 | Hide computer names from Pairnets | Account page shows "Computer 1" | 1–2 days |
| 9 | File names cleaned out of "Report a bug" | Pasted reports don't leak names | half a day |
| 10 | Later: encryption required for every nest | One way to run | 1 day |

## 5. How the build will be checked

- **Fixed test vectors** for the file format, ids, tags, envelopes and the recovery key.
- **Tamper tests** that must fail closed: flip a byte, swap segments, cut the last segment, swap two files' blobs, and
  replay an older envelope.
- **The existing decision table and convergence suites** pass unchanged on tags.
- **An end-to-end run through the relay** finds a known file name and content **nowhere**: not on the nest's disk,
  in its journal, or in a Worker tail.
- **Mixed versions:**
  - an old app against an encrypted nest gets `update_required`;
  - a new app against a plain nest syncs as today.
- **`scripts/pre-deploy.ps1`** before any release.
