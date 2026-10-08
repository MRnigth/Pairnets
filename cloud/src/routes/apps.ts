// Apps sign in with the account: a device-code flow (CONTRACT.md section 6.9).

import { audit } from "../audit";
import type { Ctx } from "../context";
import { B32_UPPER, newId, randomBase32, randomToken } from "../crypto";
import { cleanText, displayUserCode, iso, normaliseUserCode } from "../formats";
import { HttpError, htmlResponse, json, noContent, redirect } from "../http";
import { appPage } from "../pages";
import { enforce, HOUR } from "../ratelimit";
import { jsonBody } from "../request";
import { APP_TOKEN_LIFETIME, bearerToken, loadBrowserSession, requireBrowser, secretHash, startAppSession } from "../sessions";

export const DEVICE_LOGIN_LIFETIME = 600;
export const POLL_INTERVAL = 3;

interface DeviceLoginRow {
  id: string;
  user_code: string;
  name: string;
  system: string | null;
  app_version: string | null;
  status: "pending" | "approved" | "denied" | "delivered";
  account_id: string | null;
  created_at: number;
  expires_at: number;
  decided_at: number | null;
  last_poll_at: number | null;
}

// POST /v1/app/start
export async function appStartRoute(ctx: Ctx): Promise<Response> {
  await enforce(ctx, "app-start", ctx.ip, 10, HOUR);
  const b = jsonBody(ctx);
  const name = cleanText(b.name, 64);
  const system = cleanText(b.system, 64);
  const appVersion = cleanText(b.appVersion, 32);
  if (typeof b.name !== "string" || name === null || name === "invalid" || system === "invalid" || appVersion === "invalid") {
    throw new HttpError(400, "bad_request");
  }
  const deviceCode = `pcd_${randomToken(32)}`;
  for (let attempt = 0; attempt < 5; attempt++) {
    const userCode = randomBase32(8, B32_UPPER);
    const r = await ctx.env.DB.prepare(
      `INSERT INTO device_logins (id, device_code_hash, user_code, name, system, app_version, status, account_id, created_at, expires_at, decided_at, last_poll_at)
       VALUES (?1, ?2, ?3, ?4, ?5, ?6, 'pending', NULL, ?7, ?8, NULL, NULL)
       ON CONFLICT DO NOTHING`,
    )
      .bind(newId("dvl"), await secretHash(deviceCode), userCode, name, system, appVersion, ctx.now, ctx.now + DEVICE_LOGIN_LIFETIME)
      .run();
    if (r.meta.changes !== 1) continue; // that user code is taken right now: pick another
    const shown = displayUserCode(userCode);
    return json(200, {
      deviceCode,
      userCode: shown,
      verificationUri: `${ctx.env.PUBLIC_ORIGIN}/app`,
      verificationUriComplete: `${ctx.env.PUBLIC_ORIGIN}/app?code=${shown}`,
      expiresIn: DEVICE_LOGIN_LIFETIME,
      interval: POLL_INTERVAL,
    });
  }
  throw new Error("no free user code");
}

/** A request this account may see: pending (anyone with the code), or already decided by this account. */
async function visibleRequest(ctx: Ctx, rawCode: string | null, accountId: string): Promise<DeviceLoginRow | null> {
  const code = normaliseUserCode(rawCode);
  if (!code) return null;
  const row = await ctx.env.DB.prepare("SELECT * FROM device_logins WHERE user_code = ?1").bind(code).first<DeviceLoginRow>();
  if (!row || row.expires_at <= ctx.now) return null;
  if (row.status !== "pending" && row.account_id !== accountId) return null;
  return row;
}

// GET /v1/app/requests/{userCode}
export async function appRequestRoute(ctx: Ctx, rawCode: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  const row = await visibleRequest(ctx, rawCode, s.accountId);
  if (!row) throw new HttpError(404, "not_found");
  return json(200, {
    userCode: displayUserCode(row.user_code),
    name: row.name,
    system: row.system,
    appVersion: row.app_version,
    createdAt: iso(row.created_at),
    expiresAt: iso(row.expires_at),
    status: row.status,
  });
}

// GET /app?code=XXXX-XXXX
export async function appPageRoute(ctx: Ctx): Promise<Response> {
  const typed = ctx.url.searchParams.get("code");
  const s = await loadBrowserSession(ctx);
  if (!s) {
    const next = typed && /^[0-9A-Za-z-]{1,16}$/.test(typed) ? `/app?code=${typed}` : "/app";
    return redirect(`/login?next=${encodeURIComponent(next)}`);
  }
  const row = typed ? await visibleRequest(ctx, typed, s.accountId) : null;
  return htmlResponse(
    200,
    appPage(
      row ? { userCode: row.user_code, name: row.name, system: row.system, appVersion: row.app_version, createdAt: row.created_at, status: row.status } : null,
      typed,
    ),
  );
}

// POST /v1/app/approve
export async function appApproveRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  const b = jsonBody(ctx);
  if (typeof b.userCode !== "string" || typeof b.approve !== "boolean") throw new HttpError(400, "bad_request");
  const code = normaliseUserCode(b.userCode);
  if (!code) throw new HttpError(404, "not_found");
  const row = await ctx.env.DB.prepare("SELECT * FROM device_logins WHERE user_code = ?1").bind(code).first<DeviceLoginRow>();
  if (!row) throw new HttpError(404, "not_found");
  if (row.status !== "pending") {
    if (row.account_id !== s.accountId) throw new HttpError(404, "not_found");
    throw new HttpError(409, "already_decided");
  }
  if (row.expires_at <= ctx.now) throw new HttpError(400, "expired");
  const status = b.approve ? "approved" : "denied";
  const r = await ctx.env.DB.prepare(
    "UPDATE device_logins SET status = ?1, account_id = ?2, decided_at = ?3 WHERE id = ?4 AND status = 'pending' AND expires_at > ?3",
  )
    .bind(status, s.accountId, ctx.now, row.id)
    .run();
  if (r.meta.changes !== 1) throw new HttpError(409, "already_decided");
  await audit(ctx, b.approve ? "app_approved" : "app_denied", s.accountId, null, { name: row.name });
  return json(200, { status });
}

// POST /v1/app/poll
export async function appPollRoute(ctx: Ctx): Promise<Response> {
  const b = jsonBody(ctx);
  if (typeof b.deviceCode !== "string") throw new HttpError(400, "bad_request");
  if (!/^pcd_[A-Za-z0-9_-]{43}$/.test(b.deviceCode)) throw new HttpError(400, "expired");
  const row = await ctx.env.DB.prepare("SELECT * FROM device_logins WHERE device_code_hash = ?1")
    .bind(await secretHash(b.deviceCode))
    .first<DeviceLoginRow>();
  if (!row || row.expires_at <= ctx.now || row.status === "delivered") throw new HttpError(400, "expired");
  if (row.last_poll_at !== null && ctx.now - row.last_poll_at < POLL_INTERVAL) {
    throw new HttpError(429, "slow_down", undefined, { "Retry-After": String(POLL_INTERVAL) });
  }
  if (row.status === "approved" && row.account_id) {
    // Delivered once: only the poll that flips approved -> delivered gets the token.
    const r = await ctx.env.DB.prepare("UPDATE device_logins SET status = 'delivered', last_poll_at = ?1 WHERE id = ?2 AND status = 'approved'")
      .bind(ctx.now, row.id)
      .run();
    if (r.meta.changes !== 1) throw new HttpError(400, "expired");
    const account = await ctx.env.DB.prepare("SELECT email FROM accounts WHERE id = ?1").bind(row.account_id).first<{ email: string }>();
    if (!account) throw new HttpError(400, "expired");
    const appToken = await startAppSession(ctx, row.account_id, row.name, row.decided_at ?? ctx.now);
    await audit(ctx, "app_signed_in", row.account_id, null, { name: row.name });
    return json(200, { status: "approved", appToken, expiresIn: APP_TOKEN_LIFETIME, email: account.email });
  }
  await ctx.env.DB.prepare("UPDATE device_logins SET last_poll_at = ?1 WHERE id = ?2").bind(ctx.now, row.id).run();
  return json(200, { status: row.status === "denied" ? "denied" : "pending" });
}

// POST /v1/app/logout (Bearer; 204 also for unknown tokens)
export async function appLogoutRoute(ctx: Ctx): Promise<Response> {
  const token = bearerToken(ctx.req);
  if (token && /^pca_[A-Za-z0-9_-]{43}$/.test(token)) {
    await ctx.env.DB.prepare("DELETE FROM sessions WHERE token_hash = ?1 AND kind = 'app'").bind(await secretHash(token)).run();
  }
  return noContent();
}
