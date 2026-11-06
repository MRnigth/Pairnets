// The relay (RELAY.md section 5): ANY /n/{nestId}/{rest} goes to that person's own server through the router Worker.
//
// Apps are not browsers: no cookie is read here (the Cookie header is not even passed on), the Origin rule does not
// apply, bodies stream through in both directions (not the 16 KiB reader, no JSON-only rule; Cloudflare's own 100 MB is
// the only limit), WebSocket upgrades pass through, and every error is JSON.

import { audit } from "./audit";
import type { Ctx } from "./context";
import { NEST_ID_RE } from "./formats";
import { apiError, finalizeRelay, HttpError, MESSAGES } from "./http";
import { markReached } from "./nestadmin";
import { hit, keyedHash, limitAddress, MINUTE } from "./ratelimit";
import { linkFailed, ROUTER_ERROR_HEADER, runRouterLogged, SELF_HEAL_AFTER } from "./router";

/**
 * Rate limits of the relay (RELAY.md 5.6). Four budgets, each one of Cloudflare's Rate Limiting bindings next to the
 * Worker (wrangler.toml `[[ratelimits]]`), so a relayed request costs no database round trip for counting:
 *
 * - RELAY_PER_MINUTE per address (RELAY_LIMIT): a ceiling for everything, only there to stop floods.
 * - RELAY_DEVICE_PER_MINUTE per server and computer key (RELAY_DEVICE_LIMIT): what a signed-in app sends. Each computer
 *   has its own, so two computers in one home do not share one budget during a big first sync.
 * - RELAY_IP_PER_MINUTE per address (RELAY_FAIL_LIMIT): everything that carries no computer's key (health checks,
 *   joining) and everything that fails (an unknown server, a refused path, a key the server turns away with 401/403, a
 *   server that is offline). An address over it gets 429 for every relayed request for RELAY_LIMIT_PERIOD: the guard
 *   against abuse, so a caller without a working key never gets more than this. Only the server can tell whether a key
 *   is good; a made-up key gets 401 from it and is counted here.
 * - RELAY_NEST_PER_MINUTE per server (RELAY_NEST_LIMIT): everything relayed to one server, from all its computers and
 *   addresses together. Five computers each at their full budget fit under it, so normal use never meets it; it only
 *   stops one server from taking the whole service's share of requests. The limiter sees only a keyed hash of the id.
 *
 * Each MUST match `simple.limit` of its binding in wrangler.toml (a test checks this): the binding enforces it, the
 * constant names it for the docs and the tests. The counts are kept per Cloudflare location and are approximate, which
 * is fine for limits that only stop floods and abuse.
 */
export const RELAY_PER_MINUTE = 20000;
export const RELAY_DEVICE_PER_MINUTE = 6000;
export const RELAY_IP_PER_MINUTE = 600;
export const RELAY_NEST_PER_MINUTE = 30000;
/** The bindings' period, and so the Retry-After of a refused request (they give no exact time of their own). */
export const RELAY_LIMIT_PERIOD = 60;
/** How long a nest row read for the relay is reused before it is read again (seconds). */
export const NEST_CACHE_SECONDS = 30;
/** At most this many nest rows (or blocked addresses) are kept; the whole map is emptied when it is full. */
export const NEST_CACHE_MAX = 1000;
/** What a computer is told when its server used up the server's own budget (the apps wait and send again). */
export const NEST_BUSY_MESSAGE =
  "This server got more requests through Pairnets this minute than one server may have. Syncing waits a minute and then goes on by itself.";
/** How often the relay writes down that a server answered (nests.last_seen_at): at most once a day for each server. */
export const SEEN_EVERY = 86400;

/** Where relayed requests are addressed; the router uses only the path and query. */
const ROUTER_BASE = "https://pairnets-router";

/**
 * Request headers that never reach the nest (plus every CF-* header). Host is the runtime's own. The hop-by-hop ones
 * belong to the caller's connection only: `Expect: 100-continue` (the apps send it with every single-request upload)
 * would make the nest answer "100 Continue" to the relay, which cannot pass a 1xx answer on.
 */
const STRIP = new Set([
  "cookie", "host", "x-pairnets-client-ip", "x-pairnets-route", "x-pairnets-sig", "x-pairnets-nonce", "x-pairnets-ts", "x-pairnets-nest",
  "x-pairnets-dav-base", "x-pairnets-dav-ip", "x-pairnets-dav-ts", "x-pairnets-dav-sig",
  "expect", "keep-alive", "proxy-connection", "te", "trailer", "transfer-encoding",
]);

const RELAY_PATH_RE = /^\/n\/([^/]*)(?:\/(.*))?$/s;

/** The decoded, dot-resolved, lower-cased, slash-collapsed form of `rest`, as the nest's router would see it; null if malformed. */
function resolvedPath(rest: string): string | null {
  let decoded: string;
  try {
    decoded = decodeURIComponent(rest);
  } catch {
    return null; // malformed percent-encoding: refuse rather than guess what the nest would make of it
  }
  let path: string;
  try {
    path = new URL(`/${decoded}`, "http://nest.invalid").pathname;
  } catch {
    return null;
  }
  return path.replace(/\/{2,}/g, "/").toLowerCase();
}

/** Whether a path is an admin one (api/relay, in every spelling). */
function isAdminPath(rest: string): boolean {
  const path = resolvedPath(rest);
  return path === null || path === "/api/relay" || path.startsWith("/api/relay/");
}

/** The nest's WebDAV part (WEBDAV.md): everything under dav/, as `rest` after the id. */
export function isDavRest(rest: string): boolean {
  return rest === "dav" || rest.startsWith("dav/");
}

/** The biggest file a WebDAV client may PUT (WEBDAV.md: 25 MB); the nest checks again, this one only saves the trip. */
export const DAV_MAX_UPLOAD = 26214400;

/** Step 2: only the API, the hub and WebDAV, and never the admin endpoints (RELAY.md 5.2). `rest` is the path after the id. */
export function relayAllowed(rest: string): boolean {
  if (isDavRest(rest)) {
    // WebDAV must stay inside dav/ after dot-resolving ("dav/../api/x" is not a way to reach the API with a WebDAV password).
    const path = resolvedPath(rest);
    return path !== null && (path === "/dav" || path.startsWith("/dav/"));
  }
  if (!(rest.startsWith("api/") || rest === "hub" || rest.startsWith("hub/"))) return false;
  return !isAdminPath(rest);
}

interface RelayNest {
  id: string;
  account_id: string;
  status: "pending" | "active" | "broken";
  routed_version: number | null;
  last_seen_at: number | null;
}

/**
 * Nest rows by id (null = no such relay nest), each kept until `until` (Unix seconds, the request's clock). Per Worker
 * instance, so a nest that is removed or unlinked can still be relayed for up to NEST_CACHE_SECONDS by an instance that
 * read it just before (sooner once the router has dropped its link: see step 4).
 */
const nestCache = new Map<string, { nest: RelayNest | null; until: number }>();
/**
 * Addresses that went over RELAY_IP_PER_MINUTE, until when (Unix seconds). A binding cannot be read without counting, so
 * the refusal is remembered here, per Worker instance like the binding's own counts.
 */
const blockedAddresses = new Map<string, number>();
const warnedNoBinding = new Set<string>();

/** Tests: forget the cached nest rows, the blocked addresses and the "no binding" warnings. */
export function resetRelayState(): void {
  nestCache.clear();
  blockedAddresses.clear();
  warnedNoBinding.clear();
}

/** The relay nest row for `nestId`, from the cache while it is fresh (or always from D1 when `fresh`). */
async function lookupNest(ctx: Ctx, nestId: string, fresh = false): Promise<RelayNest | null> {
  const cached = fresh ? undefined : nestCache.get(nestId);
  if (cached && cached.until > ctx.now) return cached.nest;
  const nest = await ctx.env.DB.prepare("SELECT id, account_id, status, routed_version, last_seen_at FROM nests WHERE id = ?1 AND mode = 'relay'")
    .bind(nestId)
    .first<RelayNest>();
  if (nestCache.size >= NEST_CACHE_MAX && !nestCache.has(nestId)) nestCache.clear();
  nestCache.set(nestId, { nest, until: ctx.now + NEST_CACHE_SECONDS });
  return nest;
}

function usable(nest: RelayNest | null): nest is RelayNest {
  return nest !== null && (nest.status === "pending" || nest.status === "active");
}

type LimitBinding = "RELAY_LIMIT" | "RELAY_DEVICE_LIMIT" | "RELAY_FAIL_LIMIT" | "RELAY_NEST_LIMIT";

/** Counts one request under `key` and tells whether it is within the binding's limit. Without the binding (local runs): always true. */
async function allowed(ctx: Ctx, binding: LimitBinding, key: string): Promise<boolean> {
  const limiter = ctx.env[binding];
  if (!limiter) {
    if (!warnedNoBinding.has(binding)) {
      warnedNoBinding.add(binding);
      console.warn(`relay: no ${binding} binding, so these relayed requests are not rate limited`);
    }
    return true;
  }
  try {
    return (await limiter.limit({ key })).success;
  } catch (e) {
    // The limits only stop floods and abuse; a hiccup of the limiter must not stop people's syncing.
    console.warn("relay: the rate limiter failed, request let through", e instanceof Error ? e.message : "unknown");
    return true;
  }
}

function tooMany(retryAfter: number, message?: string): HttpError {
  return new HttpError(429, "rate_limited", message, { "Retry-After": String(Math.max(1, retryAfter)) });
}

/** ANY /n/... : never throws; JSON errors, relayed answers made inert for browsers (http.ts finalizeRelay). */
export async function relay(ctx: Ctx, dav?: DavOptions): Promise<Response> {
  const nestId = ctx.url.pathname.split("/")[2] ?? "";
  const where = NEST_ID_RE.test(nestId) ? { url: ctx.url, nestId } : undefined;
  const isDav = dav !== undefined || isDavRest(RELAY_PATH_RE.exec(ctx.url.pathname)?.[2] ?? "");
  try {
    const resp = finalizeRelay(await forward(ctx, dav), where);
    // A WebDAV client only asks for a password when the 401 says how (Finder and Windows both need the challenge). The
    // nest's own challenge never passes (http.ts): this one is fixed, only on WebDAV, and never to a browser, which
    // would show it as a "sign in to pairnets.app" box on any link a nest owner sends round.
    if (isDav && resp.status === 401 && !fromBrowser(ctx.req)) resp.headers.set("WWW-Authenticate", DAV_CHALLENGE);
    return resp;
  } catch (e) {
    if (e instanceof HttpError) return finalizeRelay(isDav ? davError(e) : apiError(e.status, e.code, e.message, e.headers));
    console.error("relay: unexpected error", e instanceof Error ? `${e.name}: ${e.message}` : "unknown");
    return finalizeRelay(isDav ? davError(new HttpError(500, "server_error", MESSAGES.server_error)) : apiError(500, "server_error", MESSAGES.server_error));
  }
}

export const DAV_CHALLENGE = 'Basic realm="Pairnets"';

/**
 * Whether a request came from a web browser: every current browser sends Sec-Fetch-Mode on every request (navigate for a
 * page, no-cors for an image, cors for a script's fetch), and WebDAV clients (Finder, Windows, rclone, Cyberduck) never do.
 */
export function fromBrowser(req: Request): boolean {
  return req.headers.has("sec-fetch-mode");
}

/** WebDAV clients read the status and a line of text, not JSON: the Worker's own refusals under dav/ are plain text. */
function davError(e: HttpError): Response {
  if (e.status === 413) return apiError(413, e.code, e.message, e.headers); // the one JSON answer (the apps and tests read it)
  const headers = new Headers(e.headers);
  headers.set("Content-Type", "text/plain; charset=utf-8");
  return new Response(`${e.message || e.code}
`, { status: e.status, headers });
}

/** What the /dav/<name>.<id>/ route adds to a relayed WebDAV request (davRoute below). */
export interface DavOptions {
  /** The public address of this nest's WebDAV folder, without a trailing slash: the nest writes its links under it. */
  base: string;
}

/**
 * A WebDAV link's password: 20 of these characters once "-" and spaces are gone and it is lower-case (AuthStore.cs
 * NewWebDavPassword / WebDavKeyOf). Anything else someone types into a sign-in box never reaches a nest.
 */
const LINK_PASSWORD_RE = /^[a-hjkmnp-z2-9]{20}$/;

/**
 * The password of an HTTP Basic "Authorization" header when it is shaped like a WebDAV link's, or null. Lower-case and
 * without "-" or spaces: how the nest stores it and how passwords are counted.
 */
export function basicPasswordOf(req: Request): string | null {
  const auth = req.headers.get("authorization")?.trim() ?? "";
  const m = /^basic\s+([A-Za-z0-9+/=_-]+)$/i.exec(auth);
  if (!m) return null;
  let text: string;
  try {
    const bin = atob(m[1].replace(/-/g, "+").replace(/_/g, "/"));
    text = new TextDecoder().decode(Uint8Array.from(bin, (c) => c.charCodeAt(0)));
  } catch {
    return null;
  }
  const password = text.slice(text.indexOf(":") + 1).toLowerCase().replace(/[- ]/g, "");
  return LINK_PASSWORD_RE.test(password) ? password : null;
}

/**
 * The computer's key a request carries, or null: the apps send it as X-Sync-Token, their push channel as
 * "Authorization: Bearer" (or, from a browser-like client, ?access_token= on the hub). Only ever hashed (ratelimit.ts).
 */
export function deviceKeyOf(req: Request, url: URL): string | null {
  const token = req.headers.get("x-sync-token")?.trim();
  if (token) return token;
  // Only Bearer: the nest reads no other kind on its API, and the relay drops the rest (forward below).
  const bearer = /^bearer\s+(.+)$/i.exec(req.headers.get("authorization")?.trim() ?? "")?.[1].trim();
  if (bearer) return bearer;
  return url.searchParams.get("access_token")?.trim() || null;
}

/** Counts one request against the address's budget for keyless and failing traffic; over it, the address is blocked. */
async function countAgainstAddress(ctx: Ctx): Promise<boolean> {
  if (await allowed(ctx, "RELAY_FAIL_LIMIT", ctx.ipKey)) return true;
  if (blockedAddresses.size >= NEST_CACHE_MAX && !blockedAddresses.has(ctx.ipKey)) blockedAddresses.clear();
  blockedAddresses.set(ctx.ipKey, ctx.now + RELAY_LIMIT_PERIOD);
  return false;
}

async function forward(ctx: Ctx, dav?: DavOptions): Promise<Response> {
  // 6. An address that used up its budget for keyless and failing requests gets nothing more for a while, and no
  //    address gets more than the flood ceiling (the relay's own limits, instead of the 300 of the account pages).
  const blocked = blockedAddresses.get(ctx.ipKey);
  if (blocked !== undefined) {
    if (blocked > ctx.now) throw tooMany(blocked - ctx.now);
    blockedAddresses.delete(ctx.ipKey);
  }
  if (!(await allowed(ctx, "RELAY_LIMIT", ctx.ipKey))) throw tooMany(RELAY_LIMIT_PERIOD);

  // 1. A relayed nest that is linked. The row is reused for NEST_CACHE_SECONDS (the relay reads it on every request
  //    otherwise), so a nest that was removed or unlinked stops being relayed within 30 s at most.
  const m = RELAY_PATH_RE.exec(ctx.url.pathname);
  const nestId = m?.[1] ?? "";
  const rest = m?.[2] ?? "";
  const nest = NEST_ID_RE.test(nestId) ? await lookupNest(ctx, nestId) : null;
  if (!usable(nest)) {
    await countAgainstAddress(ctx);
    throw new HttpError(404, "nest_unknown");
  }

  // 2. Only api/* and the hub.
  if (!relayAllowed(rest)) {
    await countAgainstAddress(ctx);
    throw new HttpError(404, "not_found");
  }

  // 6. A request with a computer's key counts against that computer's own budget at this nest (the key only ever as a
  //    keyed hash); one without, against the address.
  //    A WebDAV client sends its password as HTTP Basic: that is its key here, so each link has its own budget (and the
  //    nest's /dav reads nothing else, so nothing else counts as a key there).
  const isDav = isDavRest(rest);
  const key = isDav ? basicPasswordOf(ctx.req) : deviceKeyOf(ctx.req, ctx.url);
  const method = ctx.req.method.toUpperCase();
  if (isDav && method === "PUT" && Number(ctx.req.headers.get("content-length") ?? "0") > DAV_MAX_UPLOAD) {
    // Before anything else is counted or sent: the file is too big for WebDAV (the apps take bigger ones).
    throw new HttpError(413, "too_large", "Files over 25 MB can't be uploaded this way. Use the Pairnets app for bigger files.");
  }
  if (key === null) {
    if (!(await countAgainstAddress(ctx))) throw tooMany(RELAY_LIMIT_PERIOD);
  } else if (!(await allowed(ctx, "RELAY_DEVICE_LIMIT", await keyedHash(ctx.env, `${nest.id}\n${key}`)))) {
    throw tooMany(RELAY_LIMIT_PERIOD);
  }
  //    Then the server's own budget, for everything that would now go to it (the limiter sees only a keyed hash of its
  //    id). Going over it is not the caller's doing, so it is not counted against the address.
  if (!(await allowed(ctx, "RELAY_NEST_LIMIT", await keyedHash(ctx.env, `nest\n${nest.id}`)))) {
    throw tooMany(RELAY_LIMIT_PERIOD, NEST_BUSY_MESSAGE);
  }

  // 3. To the router: same method, streamed body, the caller's headers minus ours and Cloudflare's.
  const headers = new Headers(ctx.req.headers);
  for (const name of [...headers.keys()]) {
    const n = name.toLowerCase();
    if (STRIP.has(n) || n.startsWith("cf-")) headers.delete(name);
  }
  //    Authorization only as the nest reads it: on dav/ a link-shaped Basic password, elsewhere Bearer (the hub). A
  //    password a browser or a person offers for this service's name (a saved one, or one typed into a sign-in box)
  //    never reaches a nest.
  const auth = headers.get("authorization");
  if (auth !== null && (isDav ? key === null : !/^\s*bearer\s/i.test(auth))) headers.delete("authorization");
  headers.set("X-Pairnets-Client-IP", ctx.ip);
  headers.set("X-Pairnets-Route", nest.id);
  if (isDav) headers.set("X-Pairnets-Dav-Base", dav?.base ?? `${ctx.url.origin}/n/${nest.id}/dav`);
  let resp: Response;
  try {
    resp = await ctx.env.ROUTER.fetch(`${ROUTER_BASE}/${rest}${ctx.url.search}`, {
      method,
      headers,
      body: method === "GET" || method === "HEAD" ? undefined : ctx.req.body,
      redirect: "manual",
    });
  } catch {
    if (key !== null) await countAgainstAddress(ctx);
    throw new HttpError(503, "nest_offline");
  }

  // 4. The router or the link failed (counted against the address: a made-up key must not make an offline server a free
  //    target). When the router does not know the nest, the cached row may be stale: read it again. Gone or no longer
  //    linked: 404 nest_unknown, as step 1 would answer now. Still linked: self-heal with the fresh row (its
  //    routed_version) and 503.
  if (linkFailed(resp)) {
    await resp.body?.cancel().catch(() => undefined);
    if (key !== null) await countAgainstAddress(ctx);
    if (resp.headers.get(ROUTER_ERROR_HEADER) === "nest_unknown") {
      const current = await lookupNest(ctx, nest.id, true);
      if (!usable(current)) throw new HttpError(404, "nest_unknown");
      await selfHeal(ctx, current);
    }
    throw new HttpError(503, "nest_offline");
  }

  // The server turned the key away: from now on it counts against the address, like a request without a key.
  if (key !== null && (resp.status === 401 || resp.status === 403)) await countAgainstAddress(ctx);

  // 5. The nest answered: it is online, which keeps its account in use (cron.ts deletes accounts unused for a year).
  //    That is written down at most once a day per server; the cached row says so at once, so this Worker instance
  //    does not write again for every request of the next 30 s.
  if (nest.last_seen_at === null || nest.last_seen_at <= ctx.now - SEEN_EVERY) {
    const cached = nestCache.get(nest.id);
    if (cached?.nest) cached.nest = { ...cached.nest, last_seen_at: ctx.now };
    ctx.defer(
      ctx.env.DB.prepare("UPDATE nests SET last_seen_at = ?1 WHERE id = ?2 AND (last_seen_at IS NULL OR last_seen_at <= ?3)")
        .bind(ctx.now, nest.id, ctx.now - SEEN_EVERY)
        .run()
        .catch((e: unknown) => console.error("relay: noting the server as seen failed", e instanceof Error ? e.message : "unknown")),
    );
  }
  //    A pending nest is now active. The cached row says so too, so this Worker instance does not write again for
  //    every request of the next 30 s (the update only matches 'pending' rows anyway).
  if (nest.status === "pending") {
    const cached = nestCache.get(nest.id);
    if (cached?.nest) cached.nest = { ...cached.nest, status: "active" };
    ctx.defer(
      markReached(ctx.env, ctx.now, nest.id).then(
        (changed) => (changed ? audit(ctx, "server_active", nest.account_id, nest.id) : undefined),
        (e: unknown) => console.error("relay: marking active failed", e instanceof Error ? e.message : "unknown"),
      ),
    );
  }
  return resp;
}

/**
 * The router does not know a nest that is linked here. Either it was never deployed with it (a deploy was busy or
 * failed), or the router lost its bindings (its code was redeployed with wrangler): deploy again, unless the last deploy
 * is so recent that it may simply not be live yet (about 30 s). At most one such try a minute for the whole service
 * (an installer waiting for its server polls every few seconds), and the lease keeps it to one deploy at a time.
 */
async function selfHeal(ctx: Ctx, nest: RelayNest): Promise<void> {
  if (nest.routed_version !== null) {
    const state = await ctx.env.DB.prepare("SELECT deployed_at FROM router_state WHERE id = 1").first<{ deployed_at: number | null }>();
    if (state && state.deployed_at !== null && ctx.now - state.deployed_at < SELF_HEAL_AFTER) return;
  }
  if ((await hit(ctx, "router-heal", "service", 1, MINUTE)).limited) return;
  ctx.defer(runRouterLogged(ctx.env, ctx.now, ctx.deps.fetch));
}

/** /dav/<name>.<nst id>/<rest>: the name is for people to read, only the id routes. */
const DAV_ROUTE_RE = /^\/dav\/([a-z0-9-]{0,40})\.(nst_[0-9a-hjkmnp-tv-z]{26})(?:\/(.*))?$/s;

export function isDavRoute(path: string): boolean {
  return path === "/dav" || path.startsWith("/dav/");
}

/** How far a signed header from the Pages function (site/functions) may be from this Worker's clock, seconds. */
const EDGE_SIGNATURE_WINDOW = 60;

async function hmacHex(secret: string, message: string): Promise<string> {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = new Uint8Array(await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(message)));
  return [...mac].map((x) => x.toString(16).padStart(2, "0")).join("");
}

/** The caller's address as the Pages function saw it, when its signature is good and fresh; otherwise null. */
async function edgeAddress(ctx: Ctx, target: string): Promise<string | null> {
  const secret = ctx.env.DAV_EDGE_SECRET;
  const ip = ctx.req.headers.get("x-pairnets-dav-ip") ?? "";
  const ts = ctx.req.headers.get("x-pairnets-dav-ts") ?? "";
  const sig = ctx.req.headers.get("x-pairnets-dav-sig") ?? "";
  if (!secret || !ip || ip.length > 64 || !/^[0-9]{1,12}$/.test(ts) || !/^[0-9a-f]{64}$/.test(sig)) return null;
  if (Math.abs(ctx.now - Number(ts)) > EDGE_SIGNATURE_WINDOW) return null;
  const expected = await hmacHex(secret, `${ts}|${ip}|${target}`);
  // Equal-length hex strings, compared without stopping at the first difference.
  let diff = 0;
  for (let i = 0; i < expected.length; i++) diff |= expected.charCodeAt(i) ^ sig.charCodeAt(i);
  return diff === 0 ? ip : null;
}

/**
 * The WebDAV address (WEBDAV.md): any method on /dav/<name>.<nst id>/<rest> is relayed as /n/<id>/dav/<rest>. A request
 * the Pages function on pairnets.app passed on carries a signed copy of the caller's address (so the per-address limits
 * and the nest's throttle see the person, not Cloudflare's Pages) and the nest writes its links under pairnets.app;
 * a request that came here directly gets links under this address.
 */
export async function davRoute(ctx: Ctx): Promise<Response> {
  const m = DAV_ROUTE_RE.exec(ctx.url.pathname);
  if (!m) return finalizeRelay(davError(new HttpError(404, "not_found", MESSAGES.not_found)));
  const [, name, id, rest] = m;
  const edgeIp = await edgeAddress(ctx, `${name}.${id}`);
  const url = new URL(ctx.url.toString());
  url.pathname = `/n/${id}/dav${rest === undefined ? "" : "/" + rest}`; // a trailing slash stays
  const origin = edgeIp !== null ? (ctx.env.SITE_ORIGIN || "https://pairnets.app").replace(/\/+$/, "") : ctx.url.origin;
  const base = edgeIp !== null ? `${origin}/${name}.${id}` : `${origin}/dav/${name}.${id}`;
  const inner: Ctx = edgeIp !== null ? { ...ctx, url, ip: edgeIp, ipKey: limitAddress(edgeIp) } : { ...ctx, url };
  return relay(inner, { base });
}
