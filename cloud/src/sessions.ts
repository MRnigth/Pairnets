// Browser sessions (cookie __Host-pn_id) and app tokens (Bearer pca_...), CONTRACT.md sections 1.5, 6.2, 6.6, 6.9.

import { isStrictB64url } from "./b64";
import { audit } from "./audit";
import type { Ctx } from "./context";
import { clearCookie, readCookie, SESSION_COOKIE, setCookie } from "./cookies";
import { newId, randomToken, sha256 } from "./crypto";
import { HttpError } from "./http";
import { DAY } from "./ratelimit";

export const SESSION_IDLE = 30 * DAY;
export const SESSION_MAX = 90 * DAY;
export const SESSION_REFRESH = 3600;
export const APP_TOKEN_LIFETIME = 3600;
export const RECENT_AUTH = 900;

export interface Session {
  id: string;
  accountId: string;
  kind: "browser" | "app";
  amr: string[];
  authTime: number;
  createdAt: number;
  lastSeenAt: number;
  expiresAt: number;
}

interface SessionRow {
  id: string;
  account_id: string;
  kind: "browser" | "app";
  amr: string;
  auth_time: number;
  created_at: number;
  last_seen_at: number;
  expires_at: number;
}

/** A D1 BLOB parameter. */
export function blob(bytes: Uint8Array): ArrayBuffer {
  return bytes.slice().buffer as ArrayBuffer;
}

/** SHA-256 of the UTF-8 secret, whole string with prefix (section 1.5), as a D1 BLOB. */
export async function secretHash(secret: string): Promise<ArrayBuffer> {
  return blob(await sha256(secret));
}

function toSession(r: SessionRow): Session {
  let amr: string[] = [];
  try {
    const parsed: unknown = JSON.parse(r.amr);
    if (Array.isArray(parsed)) amr = parsed.filter((x): x is string => typeof x === "string");
  } catch {
    amr = [];
  }
  return {
    id: r.id,
    accountId: r.account_id,
    kind: r.kind,
    amr,
    authTime: r.auth_time,
    createdAt: r.created_at,
    lastSeenAt: r.last_seen_at,
    expiresAt: r.expires_at,
  };
}

const BROWSER_TOKEN_RE = /^pcs_[A-Za-z0-9_-]{43}$/;
const APP_TOKEN_RE = /^pca_[A-Za-z0-9_-]{43}$/;

const browserCache = new WeakMap<Ctx, Promise<Session | null>>();

function validToken(token: string | null, re: RegExp): token is string {
  return !!token && re.test(token) && isStrictB64url(token.slice(4), 32);
}

/** The browser session from the cookie, refreshed at most hourly (30 days idle, never past 90 days). */
export function loadBrowserSession(ctx: Ctx): Promise<Session | null> {
  let p = browserCache.get(ctx);
  if (!p) {
    p = loadBrowserSessionUncached(ctx);
    browserCache.set(ctx, p);
  }
  return p;
}

async function loadBrowserSessionUncached(ctx: Ctx): Promise<Session | null> {
  const token = readCookie(ctx.req, SESSION_COOKIE);
  if (!validToken(token, BROWSER_TOKEN_RE)) return null;
  const row = await ctx.env.DB.prepare(
    "SELECT * FROM sessions WHERE token_hash = ?1 AND kind = 'browser' AND expires_at > ?2 AND created_at + ?3 > ?2",
  )
    .bind(await secretHash(token), ctx.now, SESSION_MAX)
    .first<SessionRow>();
  if (!row) return null;
  const s = toSession(row);
  if (ctx.now - s.lastSeenAt >= SESSION_REFRESH) {
    const expires = Math.min(s.createdAt + SESSION_MAX, ctx.now + SESSION_IDLE);
    await ctx.env.DB.prepare("UPDATE sessions SET last_seen_at = ?1, expires_at = ?2 WHERE id = ?3").bind(ctx.now, expires, s.id).run();
    s.lastSeenAt = ctx.now;
    s.expiresAt = expires;
    ctx.setCookies.push(setCookie(SESSION_COOKIE, token, expires - ctx.now));
  }
  return s;
}

/** The app session from `Authorization: Bearer pca_...` (1 hour, no refresh). */
export async function loadAppSession(ctx: Ctx): Promise<Session | null> {
  const token = bearerToken(ctx.req);
  if (!validToken(token, APP_TOKEN_RE)) return null;
  const row = await ctx.env.DB.prepare("SELECT * FROM sessions WHERE token_hash = ?1 AND kind = 'app' AND expires_at > ?2")
    .bind(await secretHash(token), ctx.now)
    .first<SessionRow>();
  return row ? toSession(row) : null;
}

export function bearerToken(req: Request): string | null {
  const h = req.headers.get("Authorization");
  if (!h) return null;
  const m = /^Bearer ([^\s]+)$/.exec(h.trim());
  return m ? m[1] : null;
}

/** A browser session or 401 unauthorized. App tokens never count here. */
export async function requireBrowser(ctx: Ctx): Promise<Session> {
  const s = await loadBrowserSession(ctx);
  if (!s) throw new HttpError(401, "unauthorized");
  return s;
}

/** For GET /v1/me and GET /v1/nests: a Bearer app token when the header is present, else the browser cookie. */
export async function requireBrowserOrApp(ctx: Ctx): Promise<Session> {
  if (ctx.req.headers.has("Authorization")) {
    const s = await loadAppSession(ctx);
    if (!s) throw new HttpError(401, "unauthorized");
    return s;
  }
  return requireBrowser(ctx);
}

export function isRecent(ctx: Ctx, s: Session): boolean {
  return ctx.now - s.authTime <= RECENT_AUTH;
}

export function requireRecent(ctx: Ctx, s: Session): void {
  if (s.kind !== "browser" || !isRecent(ctx, s)) throw new HttpError(401, "reauth_required");
}

function userAgent(req: Request): string | null {
  const ua = req.headers.get("User-Agent");
  return ua ? ua.slice(0, 200) : null;
}

/** Deletes the session named by this browser's cookie, if any (sign-in replaces it; sign-out removes it). */
export async function dropCookieSession(ctx: Ctx): Promise<void> {
  const token = readCookie(ctx.req, SESSION_COOKIE);
  if (!validToken(token, BROWSER_TOKEN_RE)) return;
  await ctx.env.DB.prepare("DELETE FROM sessions WHERE token_hash = ?1 AND kind = 'browser'").bind(await secretHash(token)).run();
}

/** A new browser session after a sign-in; the old one of this browser is deleted (a re-auth replaces it). */
export async function startBrowserSession(ctx: Ctx, accountId: string, method: "google" | "email"): Promise<void> {
  await dropCookieSession(ctx);
  const token = `pcs_${randomToken(32)}`;
  await ctx.env.DB.prepare(
    `INSERT INTO sessions (id, token_hash, account_id, kind, amr, auth_time, created_at, last_seen_at, expires_at, user_agent, device_name)
     VALUES (?1, ?2, ?3, 'browser', ?4, ?5, ?5, ?5, ?6, ?7, NULL)`,
  )
    .bind(newId("ses"), await secretHash(token), accountId, JSON.stringify([method]), ctx.now, ctx.now + SESSION_IDLE, userAgent(ctx.req))
    .run();
  ctx.setCookies.push(setCookie(SESSION_COOKIE, token, SESSION_IDLE));
  await audit(ctx, "login", accountId, null, { method });
}

/** A new app token (`pca_...`, 1 hour) for an approved app sign-in. */
export async function startAppSession(ctx: Ctx, accountId: string, deviceName: string, authTime: number): Promise<string> {
  const token = `pca_${randomToken(32)}`;
  await ctx.env.DB.prepare(
    `INSERT INTO sessions (id, token_hash, account_id, kind, amr, auth_time, created_at, last_seen_at, expires_at, user_agent, device_name)
     VALUES (?1, ?2, ?3, 'app', ?4, ?5, ?6, ?6, ?7, ?8, ?9)`,
  )
    .bind(newId("ses"), await secretHash(token), accountId, JSON.stringify(["app"]), authTime, ctx.now, ctx.now + APP_TOKEN_LIFETIME, userAgent(ctx.req), deviceName)
    .run();
  return token;
}

export function clearSessionCookie(ctx: Ctx): void {
  ctx.setCookies.push(clearCookie(SESSION_COOKIE));
}
