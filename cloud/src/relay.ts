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
import { hit, MINUTE } from "./ratelimit";
import { linkFailed, ROUTER_ERROR_HEADER, runRouterLogged, SELF_HEAL_AFTER } from "./router";

/**
 * Relayed requests a minute per address. It MUST match `simple.limit` of the `RELAY_LIMIT` binding in wrangler.toml
 * (a test checks this): the binding enforces it, this constant only names it for the docs and the tests.
 */
export const RELAY_PER_MINUTE = 6000;
/** The binding's period, and so the Retry-After of a refused request (it gives no exact time of its own). */
export const RELAY_LIMIT_PERIOD = 60;
/** How long a nest row read for the relay is reused before it is read again (seconds). */
export const NEST_CACHE_SECONDS = 30;
/** At most this many nest rows are kept; the whole cache is emptied when it is full. */
export const NEST_CACHE_MAX = 1000;

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
}

/**
 * Nest rows by id (null = no such relay nest), each kept until `until` (Unix seconds, the request's clock). Per Worker
 * instance, so a nest that is removed or unlinked can still be relayed for up to NEST_CACHE_SECONDS by an instance that
 * read it just before (sooner once the router has dropped its link: see step 4).
 */
const nestCache = new Map<string, { nest: RelayNest | null; until: number }>();
let warnedNoLimit = false;

/** Tests: forget the cached nest rows and the "no RELAY_LIMIT binding" warning. */
export function resetRelayState(): void {
  nestCache.clear();
  warnedNoLimit = false;
}

/** The relay nest row for `nestId`, from the cache while it is fresh (or always from D1 when `fresh`). */
async function lookupNest(ctx: Ctx, nestId: string, fresh = false): Promise<RelayNest | null> {
  const cached = fresh ? undefined : nestCache.get(nestId);
  if (cached && cached.until > ctx.now) return cached.nest;
  const nest = await ctx.env.DB.prepare("SELECT id, account_id, status, routed_version FROM nests WHERE id = ?1 AND mode = 'relay'")
    .bind(nestId)
    .first<RelayNest>();
  if (nestCache.size >= NEST_CACHE_MAX && !nestCache.has(nestId)) nestCache.clear();
  nestCache.set(nestId, { nest, until: ctx.now + NEST_CACHE_SECONDS });
  return nest;
}

function usable(nest: RelayNest | null): nest is RelayNest {
  return nest !== null && (nest.status === "pending" || nest.status === "active");
}

/** True when the address may make another relayed request now. Without the binding (local runs): always true. */
async function underLimit(ctx: Ctx): Promise<boolean> {
  const limiter = ctx.env.RELAY_LIMIT;
  if (!limiter) {
    if (!warnedNoLimit) {
      warnedNoLimit = true;
      console.warn("relay: no RELAY_LIMIT binding, so relayed requests are not rate limited");
    }
    return true;
  }
  try {
    return (await limiter.limit({ key: ctx.ip })).success;
  } catch (e) {
    // The limit only stops floods; a hiccup of the limiter must not stop people's syncing.
    console.warn("relay: the rate limiter failed, request let through", e instanceof Error ? e.message : "unknown");
    return true;
  }
}

/** ANY /n/... : never throws; JSON errors, relayed answers made inert for browsers (http.ts finalizeRelay). */
export async function relay(ctx: Ctx): Promise<Response> {
  try {
    return finalizeRelay(await forward(ctx));
  } catch (e) {
    if (e instanceof HttpError) return finalizeRelay(apiError(e.status, e.code, e.message, e.headers));
    console.error("relay: unexpected error", e instanceof Error ? `${e.name}: ${e.message}` : "unknown");
    return finalizeRelay(apiError(500, "server_error", MESSAGES.server_error));
  }
}

async function forward(ctx: Ctx): Promise<Response> {
  // 6. 6,000 requests a minute per address (the relay's own limit, instead of the 300 of the account pages). Two
  //    computers in one home share one address, and every file synced between them costs a request on each (one
  //    uploads it, the other downloads it), so 600 capped a whole home at about 5 files a second. The count is kept by
  //    Cloudflare's Rate Limiting binding next to the Worker, not in D1: no database round trip per request. It is
  //    counted per Cloudflare location and is approximate, which is fine for a limit that only stops floods.
  if (!(await underLimit(ctx))) throw new HttpError(429, "rate_limited", undefined, { "Retry-After": String(RELAY_LIMIT_PERIOD) });

  // 1. A relayed nest that is linked. The row is reused for NEST_CACHE_SECONDS (the relay reads it on every request
  //    otherwise), so a nest that was removed or unlinked stops being relayed within 30 s at most.
  const m = RELAY_PATH_RE.exec(ctx.url.pathname);
  const nestId = m?.[1] ?? "";
  const rest = m?.[2] ?? "";
  const nest = NEST_ID_RE.test(nestId) ? await lookupNest(ctx, nestId) : null;
  if (!usable(nest)) throw new HttpError(404, "nest_unknown");

  // 2. Only api/* and the hub.
  if (!relayAllowed(rest)) throw new HttpError(404, "not_found");

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
    throw new HttpError(503, "nest_offline");
  }

  // 4. The router or the link failed. When the router does not know the nest, the cached row may be stale: read it
  //    again. Gone or no longer linked: 404 nest_unknown, as step 1 would answer now. Still linked: self-heal with the
  //    fresh row (its routed_version) and 503.
  if (linkFailed(resp)) {
    await resp.body?.cancel().catch(() => undefined);
    if (resp.headers.get(ROUTER_ERROR_HEADER) === "nest_unknown") {
      const current = await lookupNest(ctx, nest.id, true);
      if (!usable(current)) throw new HttpError(404, "nest_unknown");
      await selfHeal(ctx, current);
    }
    throw new HttpError(503, "nest_offline");
  }

  // 5. The nest answered: a pending nest is now active. The cached row says so too, so this Worker instance does not
  //    write again for every request of the next 30 s (the update only matches 'pending' rows anyway).
  if (nest.status === "pending") {
    const cached = nestCache.get(nest.id);
    if (cached?.nest === nest) cached.nest = { ...nest, status: "active" };
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
