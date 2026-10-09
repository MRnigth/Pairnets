// Request pipeline and routing.
//
// There are no cookies and no sign-in: the API is for the installer (curl), and it only takes JSON, so a page on
// another site cannot make a visitor's browser call it (a JSON POST needs a CORS preflight, which is never answered).

import { CloudflareApi } from "./cloudflare";
import { type Ctx, defaultDeps, type Deps } from "./context";
import { sweep } from "./cron";
import type { Env } from "./env";
import { apiError, finalize, HttpError, htmlResponse, MESSAGES } from "./http";
import { ResendMailer } from "./mail";
import { infoPage } from "./page";
import { hit, MINUTE } from "./ratelimit";
import { availableRoute } from "./routes/available";
import { claimRoute, claimStartRoute } from "./routes/claim";
import { releaseRoute, rotateRoute } from "./routes/manage";

export const MAX_BODY = 16 * 1024;

const WITH_BODY = new Set(["POST", "PATCH", "PUT", "DELETE"]);

type Handler = (ctx: Ctx) => Promise<Response> | Response;

const API_ROUTES: Record<string, Handler> = {
  "GET /v1/available": availableRoute,
  "POST /v1/claim/start": claimStartRoute,
  "POST /v1/claim": claimRoute,
  "POST /v1/rotate": rotateRoute,
  "DELETE /v1/name": releaseRoute,
};

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

async function route(ctx: Ctx, isApi: boolean): Promise<Response> {
  const method = ctx.req.method.toUpperCase();
  const path = ctx.url.pathname;

  // Any request: 120 a minute per IP.
  const global = await hit(ctx, "req", ctx.ip, 120, MINUTE);
  if (global.limited) throw new HttpError(429, "rate_limited", undefined, { "Retry-After": String(global.retryAfter) });

  if (isApi) {
    const handler = API_ROUTES[`${method} ${path}`];
    if (!handler) throw new HttpError(404, "not_found");
    if (WITH_BODY.has(method)) {
      if (!isJsonType(ctx.req.headers.get("Content-Type"))) {
        throw new HttpError(415, "bad_request", "Send JSON (Content-Type: application/json).");
      }
      ctx.body = await readBody(ctx.req);
    }
    return handler(ctx);
  }
  if (method === "GET" && path === "/") return htmlResponse(200, infoPage(ctx.env));
  throw new HttpError(404, "not_found");
}

/** Builds the Worker. Tests pass their own mailer, outbound fetch and clock. */
export function createWorker(overrides: Partial<Deps> = {}): ExportedHandler<Env> {
  const deps: Deps = { ...defaultDeps(), ...overrides };
  return {
    async fetch(req: Request, env: Env): Promise<Response> {
      const url = new URL(req.url);
      const isApi = url.pathname.startsWith("/v1/");
      const ctx: Ctx = {
        req,
        url,
        env,
        now: deps.now(),
        ip: req.headers.get("CF-Connecting-IP") || "unknown",
        body: new Uint8Array(0),
        mailer: deps.mailer ?? new ResendMailer(env.RESEND_API_KEY ?? "", env.MAIL_FROM ?? "", deps.fetch),
        cf: new CloudflareApi(env, deps.fetch),
      };
      let resp: Response;
      try {
        resp = await route(ctx, isApi);
      } catch (e) {
        let err: HttpError;
        if (e instanceof HttpError) {
          err = e;
        } else {
          console.error("unexpected error", e instanceof Error ? `${e.name}: ${e.message}` : "unknown");
          err = new HttpError(500, "server_error", MESSAGES.server_error);
        }
        resp = isApi
          ? apiError(err.status, err.code, err.message, err.headers)
          : htmlResponse(err.status, `<!doctype html><title>Pairnets names</title><p>${err.status === 404 ? "There is no such page." : "That did not work."}</p>`, err.headers);
      }
      return finalize(resp);
    },

    async scheduled(_controller: ScheduledController, env: Env): Promise<void> {
      const counts = await sweep(env, deps.now(), deps.fetch);
      console.log("sweep", JSON.stringify(counts));
    },
  };
}
