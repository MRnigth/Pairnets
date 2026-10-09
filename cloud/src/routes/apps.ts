// Apps sign in with the account: a device-code flow (CONTRACT.md section 6.9, extended by RELAY.md section 6). The
// person picks which of their servers the computer joins; the first poll after that asks the server for a device key
// (a signed admin call) and hands it to the app once. The service never stores the device key.

import { audit } from "../audit";
import type { Ctx } from "../context";
import { B32_UPPER, newId, randomBase32, randomToken } from "../crypto";
import { cleanText, DEVICE_ID_RE, displayUserCode, iso, maskEmail, NEST_ID_RE, normaliseUserCode } from "../formats";
import { HttpError, htmlResponse, json, noContent, redirect } from "../http";
import { adminCall, markReached, NestOffline } from "../nestadmin";
import { appPage } from "../pages";
import { enforce, HOUR } from "../ratelimit";
import { jsonBody } from "../request";
import { APP_TOKEN_LIFETIME, bearerToken, loadBrowserSession, requireBrowser, secretHash, startAppSession } from "../sessions";
import type { NestRow } from "./account";
import { relayUrl, serversOf } from "./servers";

export const DEVICE_LOGIN_LIFETIME = 600;
export const POLL_INTERVAL = 3;
/** While the server is being asked for a key, further polls of that login are told to slow down (at most this long). */
const KEY_REQUEST_HOLD = 10;

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
  nest_id: string | null;
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
  const servers = row?.status === "pending" ? await serversOf(ctx, s.accountId) : [];
  return htmlResponse(
    200,
    appPage(
      row ? { userCode: row.user_code, name: row.name, system: row.system, appVersion: row.app_version, createdAt: row.created_at, status: row.status } : null,
      typed,
      servers.map((n) => ({ id: n.id, label: n.label })),
    ),
  );
}

// POST /v1/app/approve
export async function appApproveRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  const b = jsonBody(ctx);
  if (typeof b.userCode !== "string" || typeof b.approve !== "boolean") throw new HttpError(400, "bad_request");
  // The server this computer joins: required when approving.
  if (b.approve && (typeof b.nestId !== "string" || !NEST_ID_RE.test(b.nestId))) throw new HttpError(400, "bad_request");
  const code = normaliseUserCode(b.userCode);
  if (!code) throw new HttpError(404, "not_found");
  const row = await ctx.env.DB.prepare("SELECT * FROM device_logins WHERE user_code = ?1").bind(code).first<DeviceLoginRow>();
  if (!row) throw new HttpError(404, "not_found");
  if (row.status !== "pending") {
    if (row.account_id !== s.accountId) throw new HttpError(404, "not_found");
    throw new HttpError(409, "already_decided");
  }
  if (row.expires_at <= ctx.now) throw new HttpError(400, "expired");
  let nestId: string | null = null;
  if (b.approve) {
    // It MUST be one of this account's relayed servers (another account's answers like an unknown one).
    const nest = await ctx.env.DB.prepare(
      "SELECT id FROM nests WHERE id = ?1 AND account_id = ?2 AND mode = 'relay' AND status IN ('pending', 'active')",
    )
      .bind(b.nestId, s.accountId)
      .first<{ id: string }>();
    if (!nest) throw new HttpError(404, "not_found");
    nestId = nest.id;
  }
  const status = b.approve ? "approved" : "denied";
  const r = await ctx.env.DB.prepare(
    "UPDATE device_logins SET status = ?1, account_id = ?2, decided_at = ?3, nest_id = ?5 WHERE id = ?4 AND status = 'pending' AND expires_at > ?3",
  )
    .bind(status, s.accountId, ctx.now, row.id, nestId)
    .run();
  if (r.meta.changes !== 1) throw new HttpError(409, "already_decided");
  await audit(ctx, b.approve ? "app_approved" : "app_denied", s.accountId, nestId, { name: row.name });
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
  // One counted poll per interval (an atomic check, so two polls at once cannot both pass). An approved login holds
  // the slot a little longer while its server is asked for a key, so that key is asked for once at a time.
  const approved = row.status === "approved" && row.account_id !== null;
  const slot = await ctx.env.DB.prepare("UPDATE device_logins SET last_poll_at = ?1 WHERE id = ?2 AND (last_poll_at IS NULL OR last_poll_at <= ?3)")
    .bind(approved ? ctx.now + KEY_REQUEST_HOLD : ctx.now, row.id, ctx.now - POLL_INTERVAL)
    .run();
  if (slot.meta.changes !== 1) throw new HttpError(429, "slow_down", undefined, { "Retry-After": String(POLL_INTERVAL) });
  if (approved) {
    try {
      return await deliver(ctx, row);
    } catch (e) {
      // Not delivered: the next poll (after the interval) tries again.
      await ctx.env.DB.prepare("UPDATE device_logins SET last_poll_at = ?1 WHERE id = ?2 AND status = 'approved'").bind(ctx.now, row.id).run();
      throw e;
    }
  }
  return json(200, { status: row.status === "denied" ? "denied" : "pending" });
}

/**
 * The approved answer, once: asks the chosen server for a device key (POST /api/relay/devices, signed), then flips the
 * login to delivered. A server that cannot be reached answers 503 nest_offline and the login stays approved.
 */
async function deliver(ctx: Ctx, row: DeviceLoginRow): Promise<Response> {
  const accountId = row.account_id!;
  const nest = row.nest_id
    ? await ctx.env.DB.prepare("SELECT * FROM nests WHERE id = ?1 AND account_id = ?2 AND mode = 'relay' AND status IN ('pending', 'active')")
        .bind(row.nest_id, accountId)
        .first<NestRow>()
    : null;
  const account = await ctx.env.DB.prepare("SELECT email FROM accounts WHERE id = ?1").bind(accountId).first<{ email: string }>();
  if (!nest || !account) throw new HttpError(400, "expired"); // the server was removed meanwhile

  let answer;
  try {
    answer = await adminCall(ctx.env, ctx.now, nest, "POST", "/api/relay/devices", { name: row.name, system: row.system, approvedBy: maskEmail(account.email) });
  } catch (e) {
    if (e instanceof NestOffline) throw new HttpError(503, "nest_offline");
    throw e;
  }
  const d = answer.status === 200 && answer.json && typeof answer.json === "object" ? (answer.json as Record<string, unknown>) : null;
  if (!d || typeof d.id !== "string" || !DEVICE_ID_RE.test(d.id) || typeof d.name !== "string" || typeof d.key !== "string" || d.key.length === 0) {
    console.error("apps: the server did not make a device", answer.status);
    throw new HttpError(503, "nest_offline");
  }
  if (nest.status === "pending") await markReached(ctx.env, ctx.now, nest.id);

  const r = await ctx.env.DB.prepare("UPDATE device_logins SET status = 'delivered' WHERE id = ?1 AND status = 'approved'").bind(row.id).run();
  if (r.meta.changes !== 1) throw new HttpError(400, "expired");
  const appToken = await startAppSession(ctx, accountId, row.name, row.decided_at ?? ctx.now);
  await audit(ctx, "app_signed_in", accountId, nest.id, { name: row.name });
  return json(200, {
    status: "approved",
    appToken,
    expiresIn: APP_TOKEN_LIFETIME,
    email: account.email,
    nest: { id: nest.id, label: nest.label, serverUrl: relayUrl(ctx, nest.id) },
    device: { id: d.id, name: d.name, key: d.key },
  });
}

// POST /v1/app/logout (Bearer; 204 also for unknown tokens)
export async function appLogoutRoute(ctx: Ctx): Promise<Response> {
  const token = bearerToken(ctx.req);
  if (token && /^pca_[A-Za-z0-9_-]{43}$/.test(token)) {
    await ctx.env.DB.prepare("DELETE FROM sessions WHERE token_hash = ?1 AND kind = 'app'").bind(await secretHash(token)).run();
  }
  return noContent();
}
