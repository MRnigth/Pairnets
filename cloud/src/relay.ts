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
import { hit, keyedHash, MINUTE } from "./ratelimit";
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
  "expect", "keep-alive", "proxy-connection", "te", "trailer", "transfer-encoding",
]);

const RELAY_PATH_RE = /^\/n\/([^/]*)(?:\/(.*))?$/s;

/** Whether a path (the decoded, dot-resolved, lower-cased form, as the nest's router would see it) is an admin one. */
function isAdminPath(rest: string): boolean {
  let decoded: string;
  try {
    decoded = decodeURIComponent(rest);
  } catch {
    return true; // malformed percent-encoding: refuse rather than guess what the nest would make of it
  }
  let path: string;
  try {
    path = new URL(`/${decoded}`, "http://nest.invalid").pathname;
  } catch {
    return true;
  }
  path = path.replace(/\/{2,}/g, "/").toLowerCase();
  return path === "/api/relay" || path.startsWith("/api/relay/");
}

/** Step 2: only the API and the hub, and never the admin endpoints (RELAY.md 5.2). `rest` is the path after the id. */
export function relayAllowed(rest: string): boolean {
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
export async function relay(ctx: Ctx): Promise<Response> {
  const nestId = ctx.url.pathname.split("/")[2] ?? "";
  const where = NEST_ID_RE.test(nestId) ? { url: ctx.url, nestId } : undefined;
  try {
    return finalizeRelay(await forward(ctx), where);
  } catch (e) {
    if (e instanceof HttpError) return finalizeRelay(apiError(e.status, e.code, e.message, e.headers));
    console.error("relay: unexpected error", e instanceof Error ? `${e.name}: ${e.message}` : "unknown");
    return finalizeRelay(apiError(500, "server_error", MESSAGES.server_error));
  }
}

/**
 * The computer's key a request carries, or null: the apps send it as X-Sync-Token, their push channel as
 * "Authorization: Bearer" (or, from a browser-like client, ?access_token= on the hub). Only ever hashed (ratelimit.ts).
 */
export function deviceKeyOf(req: Request, url: URL): string | null {
  const token = req.headers.get("x-sync-token")?.trim();
  if (token) return token;
  const auth = req.headers.get("authorization")?.trim();
  if (auth) return (/^bearer\s+(.+)$/i.exec(auth)?.[1] ?? auth).trim() || null;
  return url.searchParams.get("access_token")?.trim() || null;
}

/** Counts one request against the address's budget for keyless and failing traffic; over it, the address is blocked. */
async function countAgainstAddress(ctx: Ctx): Promise<boolean> {
  if (await allowed(ctx, "RELAY_FAIL_LIMIT", ctx.ipKey)) return true;
  if (blockedAddresses.size >= NEST_CACHE_MAX && !blockedAddresses.has(ctx.ipKey)) blockedAddresses.clear();
  blockedAddresses.set(ctx.ipKey, ctx.now + RELAY_LIMIT_PERIOD);
  return false;
}

async function forward(ctx: Ctx): Promise<Response> {
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
  const key = deviceKeyOf(ctx.req, ctx.url);
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
  headers.set("X-Pairnets-Client-IP", ctx.ip);
  headers.set("X-Pairnets-Route", nest.id);
  const method = ctx.req.method.toUpperCase();
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
