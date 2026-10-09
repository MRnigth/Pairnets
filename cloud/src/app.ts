// Request pipeline and routing (CONTRACT.md section 6, RELAY.md). /n/* is the relay to people's own servers and has a
// pipeline of its own (relay.ts): it never reaches the body reader, the JSON rule or the Origin rule below.

import { ASSETS } from "./assets";
import { type Ctx, defaultDeps, type Deps } from "./context";
import { sweep } from "./cron";
import type { Env } from "./env";
import { apiError, finalize, HttpError, htmlResponse, MESSAGES, redirect } from "./http";
import { ResendMailer } from "./mail";
import { errorPage } from "./pages";
import { hit, MINUTE } from "./ratelimit";
import { relay } from "./relay";
import {
  accountPageRoute,
  cancelClaimCodesRoute,
  createClaimCodeRoute,
  deleteMeRoute,
  deleteNestRoute,
  deleteSessionRoute,
  listNestsRoute,
  listSessionsRoute,
  meRoute,
  patchNestRoute,
} from "./routes/account";
import { appApproveRoute, appLogoutRoute, appPageRoute, appPollRoute, appRequestRoute, appStartRoute } from "./routes/apps";
import { claimRoute } from "./routes/claim";
import {
  emailLandingRoute,
  googleCallbackRoute,
  googleStartRoute,
  loginEmailConfirmRoute,
  loginEmailRoute,
  loginPageRoute,
  logoutRoute,
} from "./routes/login";
import { heartbeatRoute, nestConfirmRoute, nestUnlinkRoute } from "./routes/nest";
import { nestLoginRoute } from "./routes/nestlogin";
import {
  addPageRoute,
  listServersRoute,
  removeDeviceRoute,
  removeServerRoute,
  serverApproveRoute,
  serverPollRoute,
  serverRequestRoute,
  serverStartRoute,
} from "./routes/servers";

export const MAX_BODY = 16 * 1024;

/**
 * These authenticate by code, HMAC or Bearer token, need no Origin and never read a cookie (section 6.3). The relay
 * (/n/*) is outside this rule altogether: it reads no cookie and passes none on.
 */
export const ORIGIN_EXEMPT = new Set([
  "/v1/claim",
  "/v1/nest/confirm",
  "/v1/nest/unlink",
  "/v1/heartbeat",
  "/v1/app/start",
  "/v1/app/poll",
  "/v1/app/logout",
  "/v1/servers/start",
  "/v1/servers/poll",
]);

const MUTATING = new Set(["POST", "PATCH", "PUT", "DELETE"]);

type Handler = (ctx: Ctx, param: string, param2: string) => Promise<Response> | Response;

interface Route {
  method: string;
  pattern: RegExp;
  handler: Handler;
}

const API_ROUTES: Route[] = [
  { method: "POST", pattern: /^\/v1\/login\/email$/, handler: loginEmailRoute },
  { method: "POST", pattern: /^\/v1\/login\/email\/confirm$/, handler: loginEmailConfirmRoute },
  { method: "GET", pattern: /^\/v1\/me$/, handler: meRoute },
  { method: "DELETE", pattern: /^\/v1\/me$/, handler: deleteMeRoute },
  { method: "GET", pattern: /^\/v1\/nests$/, handler: listNestsRoute },
  { method: "POST", pattern: /^\/v1\/nests\/claim-codes$/, handler: createClaimCodeRoute },
  { method: "DELETE", pattern: /^\/v1\/nests\/claim-codes$/, handler: cancelClaimCodesRoute },
  { method: "PATCH", pattern: /^\/v1\/nests\/([^/]+)$/, handler: patchNestRoute },
  { method: "DELETE", pattern: /^\/v1\/nests\/([^/]+)$/, handler: deleteNestRoute },
  { method: "GET", pattern: /^\/v1\/sessions$/, handler: listSessionsRoute },
  { method: "DELETE", pattern: /^\/v1\/sessions\/([^/]+)$/, handler: deleteSessionRoute },
  { method: "POST", pattern: /^\/v1\/logout$/, handler: logoutRoute },
  { method: "POST", pattern: /^\/v1\/claim$/, handler: claimRoute },
  { method: "POST", pattern: /^\/v1\/nest\/confirm$/, handler: nestConfirmRoute },
  { method: "POST", pattern: /^\/v1\/nest\/unlink$/, handler: nestUnlinkRoute },
  { method: "POST", pattern: /^\/v1\/heartbeat$/, handler: heartbeatRoute },
  { method: "POST", pattern: /^\/v1\/app\/start$/, handler: appStartRoute },
  { method: "GET", pattern: /^\/v1\/app\/requests\/([^/]+)$/, handler: appRequestRoute },
  { method: "POST", pattern: /^\/v1\/app\/approve$/, handler: appApproveRoute },
  { method: "POST", pattern: /^\/v1\/app\/poll$/, handler: appPollRoute },
  { method: "POST", pattern: /^\/v1\/app\/logout$/, handler: appLogoutRoute },
  { method: "POST", pattern: /^\/v1\/servers\/start$/, handler: serverStartRoute },
  { method: "GET", pattern: /^\/v1\/servers\/requests\/([^/]+)$/, handler: serverRequestRoute },
  { method: "POST", pattern: /^\/v1\/servers\/approve$/, handler: serverApproveRoute },
  { method: "POST", pattern: /^\/v1\/servers\/poll$/, handler: serverPollRoute },
  { method: "GET", pattern: /^\/v1\/servers$/, handler: listServersRoute },
  { method: "DELETE", pattern: /^\/v1\/servers\/([^/]+)\/devices\/([^/]+)$/, handler: removeDeviceRoute },
  { method: "DELETE", pattern: /^\/v1\/servers\/([^/]+)$/, handler: removeServerRoute },
];

const PAGE_ROUTES: Route[] = [
  { method: "GET", pattern: /^\/$/, handler: () => redirect("/account") },
  { method: "GET", pattern: /^\/login$/, handler: loginPageRoute },
  { method: "GET", pattern: /^\/login\/email$/, handler: emailLandingRoute },
  { method: "GET", pattern: /^\/login\/google$/, handler: googleStartRoute },
  { method: "GET", pattern: /^\/login\/google\/callback$/, handler: googleCallbackRoute },
  { method: "GET", pattern: /^\/account$/, handler: accountPageRoute },
  { method: "GET", pattern: /^\/app$/, handler: appPageRoute },
  { method: "GET", pattern: /^\/add$/, handler: addPageRoute },
  { method: "GET", pattern: /^\/privacy$/, handler: () => redirect("https://pairnets.app/privacy") },
  { method: "GET", pattern: /^\/nest-login$/, handler: nestLoginRoute },
];

function match(routes: Route[], method: string, path: string): { handler: Handler; param: string; param2: string } | null {
  for (const r of routes) {
    if (r.method !== method) continue;
    const m = r.pattern.exec(path);
    if (!m) continue;
    try {
      return { handler: r.handler, param: m[1] ? decodeURIComponent(m[1]) : "", param2: m[2] ? decodeURIComponent(m[2]) : "" };
    } catch {
      return null; // malformed percent-encoding: no such thing
    }
  }
  return null;
}

function isJsonType(ct: string | null): boolean {
  return !!ct && ct.split(";")[0].trim().toLowerCase() === "application/json";
}

async function readBody(req: Request): Promise<Uint8Array> {
  const declared = req.headers.get("Content-Length");
  if (declared !== null && Number(declared) > MAX_BODY) throw new HttpError(413, "bad_request", "The request body is too large.");
  if (!req.body) return new Uint8Array(0);
  const reader = req.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.length;
    if (total > MAX_BODY) {
      await reader.cancel().catch(() => undefined);
      throw new HttpError(413, "bad_request", "The request body is too large.");
    }
    chunks.push(value);
  }
  const out = new Uint8Array(total);
  let o = 0;
  for (const c of chunks) {
    out.set(c, o);
    o += c.length;
  }
  return out;
}

function errorFor(e: HttpError, isApi: boolean): Response {
  if (isApi) return apiError(e.status, e.code, e.message, e.headers);
  const title = e.status === 429 ? "Too many requests" : e.status === 403 ? "Not allowed" : e.status === 404 ? "Not found" : "That did not work";
  return htmlResponse(e.status, errorPage(title, e.message), e.headers);
}

async function route(ctx: Ctx, isApi: boolean): Promise<Response> {
  const { req, url } = ctx;
  const method = req.method.toUpperCase();
  const path = url.pathname;

  // Any request: 300 a minute per IP.
  const global = await hit(ctx, "req", ctx.ip, 300, MINUTE);
  if (global.limited) throw new HttpError(429, "rate_limited", undefined, { "Retry-After": String(global.retryAfter) });

  // CSRF rule: every mutation carries exactly our Origin, except the server/app endpoints that never read cookies.
  if (MUTATING.has(method) && !ORIGIN_EXEMPT.has(path) && req.headers.get("Origin") !== ctx.env.PUBLIC_ORIGIN) {
    throw new HttpError(403, "bad_origin");
  }

  if (isApi) {
    if (MUTATING.has(method)) {
      ctx.body = await readBody(req);
      if (ctx.body.length > 0 && !isJsonType(req.headers.get("Content-Type"))) {
        throw new HttpError(415, "bad_request", "Send JSON (Content-Type: application/json).");
      }
    }
    const m = match(API_ROUTES, method, path);
    if (!m) throw new HttpError(404, "not_found");
    return m.handler(ctx, m.param, m.param2);
  }

  if (method === "GET" && path.startsWith("/assets/")) {
    const asset = ASSETS[path.slice("/assets/".length)];
    if (!asset) throw new HttpError(404, "not_found", "There is no such page.");
    return new Response(asset.body, { status: 200, headers: { "Content-Type": asset.type, "Cache-Control": "public, max-age=3600" } });
  }
  const m = match(PAGE_ROUTES, method, path);
  if (!m) throw new HttpError(404, "not_found", "There is no such page.");
  return m.handler(ctx, m.param, m.param2);
}

/** /n/<nestId>/... (and /n itself, so every answer under it is JSON). */
export function isRelayPath(path: string): boolean {
  return path === "/n" || path.startsWith("/n/");
}

/** Builds the Worker. Tests pass their own mailer, outbound fetch and clock. */
export function createWorker(overrides: Partial<Deps> = {}): ExportedHandler<Env> {
  const deps: Deps = { ...defaultDeps(), ...overrides };
  return {
    async fetch(req: Request, env: Env, exec: ExecutionContext): Promise<Response> {
      const url = new URL(req.url);
      const isApi = url.pathname.startsWith("/v1/");
      const isAsset = url.pathname.startsWith("/assets/");
      const ctx: Ctx = {
        req,
        url,
        env,
        deps,
        exec,
        now: deps.now(),
        ip: req.headers.get("CF-Connecting-IP") || "unknown",
        body: new Uint8Array(0),
        setCookies: [],
        mailer: deps.mailer ?? new ResendMailer(env.RESEND_API_KEY ?? "", env.MAIL_FROM ?? "", deps.fetch),
        defer: (p) => exec.waitUntil(p),
      };
      if (isRelayPath(url.pathname)) return relay(ctx);
      let resp: Response;
      try {
        resp = await route(ctx, isApi);
      } catch (e) {
        if (e instanceof HttpError) {
          resp = errorFor(e, isApi);
        } else {
          console.error("unexpected error", e instanceof Error ? `${e.name}: ${e.message}` : "unknown");
          resp = errorFor(new HttpError(500, "server_error", MESSAGES.server_error), isApi);
        }
      }
      return finalize(resp, isAsset && resp.status === 200, ctx.setCookies);
    },

    async scheduled(_controller: ScheduledController, env: Env): Promise<void> {
      const counts = await sweep(env, deps.now(), deps.fetch);
      console.log("sweep", JSON.stringify(counts));
    },
  };
}
