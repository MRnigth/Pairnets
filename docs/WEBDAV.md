# Files in Finder / Explorer over WebDAV (feature/webdav)

Status: built on `feature/webdav` (from v1.0.61.14), local only, not released. Judgment calls: DECISIONS.md, "WebDAV link (2026-10-30)".

A nest can hand out **WebDAV links**: an address, a username and a generated password that a Mac (Finder → Go → Connect to
Server), Windows (Map network drive) or any WebDAV client uses to browse the nest's files over HTTPS, through the
existing tunnel and relay. Files up to **25 MB** can be uploaded this way; bigger ones are read-only here (use the app).
Not available on an encrypted (sealed) nest.

## The address

- Nest reached through Pairnets: `https://pairnets.app/<nest-name>.<nest id>/` (e.g. `…/soro.nst_….`). The name is
  decoration (lower-case a-z, 0-9, "-", up to 40); only the id routes. A Pages Function on pairnets.app passes it to the
  Worker's `https://sync.pairnets.app/dav/<name>.<id>/…`, which relays to the nest's `/dav/…`.
- Nest at its own address: `<its address>/dav/`.

## Nest API (owner's computer only; `AccessGate.IsOwnerOnly`)

- `GET /api/webdav/links` → `{ "sealed": bool, "maxUploadBytes": 26214400, "links": [Link] }`
- `POST /api/webdav/links` `{ "label": "Mac mini", "access": "view"|"edit", "folders": ["Photos"]|null }` → `201 { Link + "password": "abcde-fghjk-mnpqr-stuvw" }`
  (shown once, never stored). `409 sealed` on an encrypted nest, `409 too_many_links` (max 10), `400` otherwise.
- `DELETE /api/webdav/links/{id}` → `204` (the password stops working at once), `404`.
- `Link` = `{ id, label, username, access, folders, created, lastUsed }`. `username` = the label as a lower-case slug
  (informational; the password is the credential).

## Seeing and removing links without a computer

An owner who lost every computer can still take a leaked password away:

- **The nest's website** (signed-in owner; same protection as `/web/api/devices`: the nest's own name, the owner's session
  cookie, and for DELETE a same-origin request): `GET /web/api/webdav/links` → the same answer as `GET /api/webdav/links`;
  `DELETE /web/api/webdav/links/{id}` → `204` (the password stops working at once), `404` for an unknown, removed or
  computer id. A computer's key, a link's password or a friend's key is no sign-in there (`401`). The Devices page shows the
  links in a "WebDAV links" section of their own (hidden when there are none) with **Remove…**, which asks first. Links are
  made only in the apps (the password is shown once, there). `/web/api/devices` never lists a link, and `DELETE
  /web/api/devices/{id}` refuses one.
- **The server's command line**: `pairnets-server devices list` prints the computers, then a "WebDAV links" section (label,
  view only or can change, folders, made, last used; never a password or hash). `devices remove <name or id>` removes a
  link too and says "Removed the WebDAV link …". A computer and a link may share a name; then a removal by that name is
  refused and the id decides. The running nest notices within its key cache's 30 seconds (another process).
- **Not on the account page** (pairnets.app) yet: `/api/relay/devices` lists computers only and refuses a link's id.

## On the wire (`/dav/…` on the nest)

Basic auth, the password alone is checked. Methods: OPTIONS, PROPFIND (Depth 0/1), GET/HEAD (ranges), PUT, DELETE,
MKCOL, MOVE, LOCK/UNLOCK (formal locks only), PROPPATCH (accepted, ignored). COPY → 501. Reads outside the link's folders → 404,
changes there → 403. A folder DELETE/MOVE that left files behind → 207 listing them. Folder metadata (`._*`, `.DS_Store`, `Thumbs.db`,
`desktop.ini`) is accepted and dropped; Office/LibreOffice temp files are stored (their saves rename them onto the document).
A MOVE that only changes the last name's letter case (`photo.jpg` → `Photo.jpg`, a folder `trip` → `Trip`) → 201, done in two moves
through a passing name `<old name>.pairnets-rename-<8 hex>`; a step that fails is undone and answered 412/409. Changing the case of a
folder above the name in the same MOVE → 409.
A nonsense `If-Match` → 412. Over 25 MB → 413 (the rest of the body is read first, up to 100 MB, so the client hears it).

## Worker side

- **Two doors, one relay.** `https://sync.pairnets.app/n/<id>/dav/…` (what the apps could use) and
  `https://sync.pairnets.app/dav/<name>.<id>/…` (what the website's function uses) both end in `relay.ts`. The relay lets
  `dav/` through next to `api/` and the hub, and still only inside `dav/` after dot-resolving (`dav/..%2Fapi/…` is 404).
- **Any method.** PROPFIND, MKCOL, MOVE, COPY, LOCK, UNLOCK, PROPPATCH, OPTIONS go to the nest with their body streamed.
  The nest's `DAV`, `MS-Author-Via` and `Lock-Token` answers pass. Its `WWW-Authenticate` never does (nor anywhere else
  in the relay): a 401 under `dav/` gets the fixed `Basic realm="Pairnets"`, and none at all when the request carries
  `Sec-Fetch-Mode` (a browser), so a nest owner's link can't draw a "sign in to pairnets.app" box.
- **Authorization only as the nest reads it.** Under `dav/` only a Basic password shaped like a link's (20 of
  `a-z2-9` without i, l, o, once "-" and spaces are gone) goes on; elsewhere only `Bearer` (the hub). Anything else is dropped.
- **The password is the key for limits.** For `dav/` the HTTP Basic password (lower-case, without "-") is hashed like a
  computer's key: its own 6,000 a minute, the nest's 30,000, and a refused password counts against the address.
- **25 MB.** A PUT whose `Content-Length` is over 26,214,400 gets `413 {"error":"too_large"}` before the nest is asked.
  A PUT without a length is stopped by the nest. The Worker's other refusals under `dav/` (404, 429, 503) are plain text.
- **Links in answers.** The Worker sends `X-Pairnets-Dav-Base` (a caller's own copy is dropped): `https://pairnets.app/<name>.<id>`
  when the website's function passed the request on, else `https://sync.pairnets.app/dav/<name>.<id>`, or
  `…/n/<id>/dav` on the first door. The nest writes every `href` under it (no trailing slash).
- **Trusted address.** `site/functions/[[path]].js` adds `X-Pairnets-Dav-Ip`, `-Ts` and `-Sig` = HMAC-SHA256 of
  `<ts>|<ip>|<name>.<id>` with `DAV_EDGE_SECRET` (the same secret on the Pages project and the Worker). A signature within
  60 s makes the Worker use that address (limits and the nest's throttle); anything else is ignored. No secret on the
  Pages project: 503. No secret on the Worker: the headers are ignored.
- **The website's function** passes only a first path segment shaped `<a-z0-9- up to 40>.nst_<26>`; every other path goes to
  `context.next()` untouched (a test lists the site's pages). `site/_routes.json` skips `/assets/*` and `/favicon.ico`.
- **Not deployed.** Needs, in this order: Worker secret `DAV_EDGE_SECRET`, Worker deploy, the same secret and a `SYNC`
  service binding on the Pages project, Pages deploy.
