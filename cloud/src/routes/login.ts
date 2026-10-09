// Browser sign-in: the login page, email links and Google (CONTRACT.md sections 6.4-6.6).

import { accountForIdentity } from "../accounts";
import { b64urlEncode, isStrictB64url } from "../b64";
import type { Ctx } from "../context";
import { clearCookie, EMAIL_COOKIE, GOOGLE_COOKIE, readCookie, setCookie } from "../cookies";
import { newId, randomToken, sha256, timingSafeEqual, timingSafeEqualText } from "../crypto";
import { seamUrl } from "../env";
import { normaliseEmail, safeNext } from "../formats";
import {
  DEFAULT_GOOGLE_AUTH_URL,
  DEFAULT_GOOGLE_TOKEN_URL,
  GOOGLE_FLOW_LIFETIME,
  readFlowCookie,
  signFlowCookie,
  verifyGoogleIdToken,
} from "../google";
import { HttpError, htmlResponse, json, noContent, redirect } from "../http";
import { loginEmail, loginMailLimit, reserveMail, sendLater } from "../mail";
import { emailLandingPage, loginPage } from "../pages";
import { DAY, enforce, hit, HOUR, peek } from "../ratelimit";
import { blobBytes, jsonBody } from "../request";
import { clearSessionCookie, dropCookieSession, loadBrowserSession, secretHash, startBrowserSession } from "../sessions";
import { verifyTurnstile } from "../turnstile";

export const EMAIL_CODE_LIFETIME = 900;
export const EMAIL_BINDING_LIFETIME = 900;

// GET /login?next=&reauth=&error=
export async function loginPageRoute(ctx: Ctx): Promise<Response> {
  const p = ctx.url.searchParams;
  const next = safeNext(p.get("next"));
  const reauth = p.get("reauth") === "1";
  const error = p.get("error");
  if (!reauth && !error && (await loadBrowserSession(ctx))) return redirect(next);
  return htmlResponse(200, loginPage({ next, reauth, error, siteKey: ctx.env.TURNSTILE_SITE_KEY ?? "" }));
}

// GET /login/email (the emailed link's landing page; the code stays in the fragment)
export function emailLandingRoute(): Response {
  return htmlResponse(200, emailLandingPage());
}

// POST /v1/login/email
export async function loginEmailRoute(ctx: Ctx): Promise<Response> {
  await enforce(ctx, "email-ip", ctx.ip, 10, HOUR);
  const b = jsonBody(ctx);
  const email = normaliseEmail(b.email);
  const nextOk = b.next === undefined || b.next === null || typeof b.next === "string";
  if (!email || typeof b.turnstile !== "string" || !nextOk) throw new HttpError(400, "bad_request");
  if (!(await verifyTurnstile(ctx, b.turnstile))) throw new HttpError(400, "turnstile_failed");
  if ((await peek(ctx, "mail-login", "service", loginMailLimit(ctx), DAY)).limited) throw new HttpError(503, "email_unavailable");

  // The browser binding: a fresh cookie on every answer. A browser that already holds a well-formed one keeps its value,
  // so the links of two requests in a row both work in it (see CONTRACT-NOTES-worker.md).
  const existing = readCookie(ctx.req, EMAIL_COOKIE);
  const binding = existing && existing.length === 43 && isStrictB64url(existing, 32) ? existing : randomToken(32);
  ctx.setCookies.push(setCookie(EMAIL_COOKIE, binding, EMAIL_BINDING_LIFETIME));

  // Per-address limits fail silently: the answer is the same 202 whatever happens from here on.
  const hourly = await hit(ctx, "email-addr-h", email, 3, HOUR);
  const daily = await hit(ctx, "email-addr-d", email, 10, DAY);
  if (!hourly.limited && !daily.limited && (await reserveMail(ctx, "login"))) {
    const code = randomToken(24);
    await ctx.env.DB.prepare(
      `INSERT INTO login_challenges (id, kind, email, code_hash, binding_hash, next, created_at, expires_at, used_at)
       VALUES (?1, 'email', ?2, ?3, ?4, ?5, ?6, ?7, NULL)`,
    )
      .bind(newId("chl"), email, await secretHash(code), await secretHash(binding), safeNext(b.next), ctx.now, ctx.now + EMAIL_CODE_LIFETIME)
      .run();
    sendLater(ctx, loginEmail(email, `${ctx.env.PUBLIC_ORIGIN}/login/email#code=${code}`));
  }
  return json(202, { status: "sent" });
}

// POST /v1/login/email/confirm
export async function loginEmailConfirmRoute(ctx: Ctx): Promise<Response> {
  await enforce(ctx, "email-confirm-ip", ctx.ip, 30, HOUR);
  const b = jsonBody(ctx);
  if (typeof b.code !== "string") throw new HttpError(400, "bad_request");
  if (b.code.length !== 32 || !isStrictB64url(b.code, 24)) throw new HttpError(400, "invalid_code");
  const row = await ctx.env.DB.prepare(
    "SELECT id, email, binding_hash, next FROM login_challenges WHERE code_hash = ?1 AND used_at IS NULL AND expires_at > ?2",
  )
    .bind(await secretHash(b.code), ctx.now)
    .first<{ id: string; email: string; binding_hash: unknown; next: string | null }>();
  if (!row) throw new HttpError(400, "invalid_code");

  const cookie = readCookie(ctx.req, EMAIL_COOKIE);
  if (!cookie || cookie.length !== 43 || !timingSafeEqual(await sha256(cookie), blobBytes(row.binding_hash))) {
    throw new HttpError(400, "wrong_browser"); // the code stays usable
  }
  const used = await ctx.env.DB.prepare("UPDATE login_challenges SET used_at = ?1 WHERE id = ?2 AND used_at IS NULL").bind(ctx.now, row.id).run();
  if (used.meta.changes !== 1) throw new HttpError(400, "invalid_code");

  const accountId = await accountForIdentity(ctx, "email", row.email, row.email);
  await startBrowserSession(ctx, accountId, "email");
  ctx.setCookies.push(clearCookie(EMAIL_COOKIE));
  return json(200, { next: safeNext(row.next) });
}

// GET /login/google?next=&reauth=1
export async function googleStartRoute(ctx: Ctx): Promise<Response> {
  await enforce(ctx, "google-ip", ctx.ip, 30, 600);
  const next = safeNext(ctx.url.searchParams.get("next"));
  const reauth = ctx.url.searchParams.get("reauth") === "1";
  const state = randomToken(16);
  const nonce = randomToken(16);
  const verifier = randomToken(32);
  const challenge = b64urlEncode(await sha256(verifier));
  const cookie = await signFlowCookie(ctx.env, { s: state, n: nonce, v: verifier, r: next, e: ctx.now + GOOGLE_FLOW_LIFETIME, ra: reauth ? 1 : 0 });
  ctx.setCookies.push(setCookie(GOOGLE_COOKIE, cookie, GOOGLE_FLOW_LIFETIME));

  const u = new URL(seamUrl(ctx.env.GOOGLE_AUTH_URL, DEFAULT_GOOGLE_AUTH_URL));
  u.searchParams.set("client_id", ctx.env.GOOGLE_CLIENT_ID ?? "");
  u.searchParams.set("redirect_uri", `${ctx.env.PUBLIC_ORIGIN}/login/google/callback`);
  u.searchParams.set("response_type", "code");
  u.searchParams.set("scope", "openid email");
  u.searchParams.set("state", state);
  u.searchParams.set("nonce", nonce);
  u.searchParams.set("code_challenge", challenge);
  u.searchParams.set("code_challenge_method", "S256");
  u.searchParams.set("prompt", "select_account");
  if (reauth) u.searchParams.set("max_age", "0");
  return redirect(u.toString());
}

// GET /login/google/callback?state=&code=
export async function googleCallbackRoute(ctx: Ctx): Promise<Response> {
  const p = ctx.url.searchParams;
  ctx.setCookies.push(clearCookie(GOOGLE_COOKIE));
  const fail = (code: "google_failed" | "google_denied" | "google_expired") => redirect(`/login?error=${code}`);

  const flow = await readFlowCookie(ctx.env, readCookie(ctx.req, GOOGLE_COOKIE));
  if (!flow || flow.e <= ctx.now) return fail("google_expired");
  const state = p.get("state");
  if (!state || !timingSafeEqualText(state, flow.s)) return fail("google_failed");
  const error = p.get("error");
  if (error) return fail(error === "access_denied" ? "google_denied" : "google_failed");
  const code = p.get("code");
  if (!code || code.length > 2048) return fail("google_failed");

  let idToken: unknown;
  try {
    const resp = await ctx.deps.fetch(seamUrl(ctx.env.GOOGLE_TOKEN_URL, DEFAULT_GOOGLE_TOKEN_URL), {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded", Accept: "application/json" },
      body: new URLSearchParams({
        code,
        client_id: ctx.env.GOOGLE_CLIENT_ID ?? "",
        client_secret: ctx.env.GOOGLE_CLIENT_SECRET ?? "",
        redirect_uri: `${ctx.env.PUBLIC_ORIGIN}/login/google/callback`,
        grant_type: "authorization_code",
        code_verifier: flow.v,
      }).toString(),
    });
    if (!resp.ok) return fail("google_failed");
    idToken = ((await resp.json()) as { id_token?: unknown }).id_token;
  } catch {
    return fail("google_failed");
  }
  if (typeof idToken !== "string") return fail("google_failed");
  const who = await verifyGoogleIdToken(ctx, idToken, flow);
  if (!who) return fail("google_failed");

  const accountId = await accountForIdentity(ctx, "google", who.sub, who.email);
  await startBrowserSession(ctx, accountId, "google");
  return redirect(safeNext(flow.r));
}

// POST /v1/logout
export async function logoutRoute(ctx: Ctx): Promise<Response> {
  await dropCookieSession(ctx);
  clearSessionCookie(ctx);
  return noContent();
}
