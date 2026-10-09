// The account page and account API (CONTRACT.md section 6.7). Servers reached through the service (RELAY.md) have
// their own API in servers.ts; the /v1/nests endpoints here are about nests on their own domain ('url' mode) only.

import { audit } from "../audit";
import type { Ctx } from "../context";
import { B32_UPPER, randomBase32, sha256 } from "../crypto";
import { cleanLabel, iso, NEST_ID_RE, SESSION_ID_RE } from "../formats";
import { HttpError, htmlResponse, json, noContent, redirect } from "../http";
import { sendNotice } from "../mail";
import { accountPage, type NestView } from "../pages";
import { runRouterLogged } from "../router";
import { DAY, hit } from "../ratelimit";
import { jsonBody } from "../request";
import {
  blob,
  clearSessionCookie,
  isRecent,
  loadBrowserSession,
  requireBrowser,
  requireBrowserOrApp,
  requireRecent,
} from "../sessions";
import { serversOf } from "./servers";

/** Nests per account, both kinds together (pending ones count). */
export const MAX_NESTS_PER_ACCOUNT = 3;
export const MAX_UNUSED_CODES = 5;
export const MAX_CODES_PER_DAY = 20;
export const CLAIM_CODE_LIFETIME = 600;
export const ONLINE_WINDOW = 25 * 60;

export interface NestRow {
  id: string;
  account_id: string;
  label: string;
  public_url: string;
  status: "pending" | "active" | "broken";
  key_version: number;
  hosted_login: number;
  created_at: number;
  confirmed_at: number | null;
  last_req_ts: number;
  last_seen_at: number | null;
  last_version: string | null;
  last_ready: number | null;
  last_public_host: string | null;
  last_hosted_login: number | null;
  mode: "url" | "relay";
  tunnel_id: string | null;
  routed_version: number | null;
}

export function isOnline(n: NestRow, now: number): boolean {
  return n.status === "active" && n.last_seen_at !== null && now - n.last_seen_at <= ONLINE_WINDOW;
}

/** The `Nest` JSON shape of section 6.7. */
export function nestJson(n: NestRow, now: number): Record<string, unknown> {
  return {
    nestId: n.id,
    label: n.label,
    publicUrl: n.public_url,
    status: n.status,
    online: isOnline(n, now),
    lastSeenAt: iso(n.last_seen_at),
    serverVersion: n.last_version,
    ready: n.last_ready === null ? null : n.last_ready === 1,
    publicHost: n.last_public_host,
    hostedLogin: n.hosted_login === 1,
    createdAt: iso(n.created_at),
    confirmedAt: iso(n.confirmed_at),
  };
}

/** The account's nests on their own domain (the version 1 kind). */
async function nestsOf(ctx: Ctx, accountId: string): Promise<NestRow[]> {
  const r = await ctx.env.DB.prepare("SELECT * FROM nests WHERE account_id = ?1 AND mode = 'url' ORDER BY created_at, id").bind(accountId).all<NestRow>();
  return r.results;
}

async function accountRow(ctx: Ctx, accountId: string): Promise<{ id: string; email: string; created_at: number } | null> {
  return ctx.env.DB.prepare("SELECT id, email, created_at FROM accounts WHERE id = ?1").bind(accountId).first();
}

function addressWarning(n: NestRow): string | null {
  if (!n.last_public_host) return null;
  const u = new URL(n.public_url);
  if (n.last_public_host === u.hostname || n.last_public_host === u.host) return null;
  return `This nest now reports the address ${n.last_public_host}. Link it again (hosted unlink, then hosted link with a new code) to use the new address.`;
}

// GET /account
export async function accountPageRoute(ctx: Ctx): Promise<Response> {
  const s = await loadBrowserSession(ctx);
  if (!s) return redirect("/login?next=/account");
  const account = await accountRow(ctx, s.accountId);
  if (!account) return redirect("/login?next=/account");
  const nests = await nestsOf(ctx, s.accountId);
  const servers = await serversOf(ctx, s.accountId);
  const sessions = await sessionRows(ctx, s.accountId);
  const view: NestView[] = nests.map((n) => ({
    nestId: n.id,
    label: n.label,
    publicUrl: n.public_url,
    status: n.status === "active" ? "active" : "pending",
    online: n.status !== "active" || n.last_seen_at === null ? "unknown" : isOnline(n, ctx.now) ? "online" : "offline",
    serverVersion: n.last_version,
    lastSeenAt: n.last_seen_at,
    hostedLogin: n.hosted_login === 1,
    addressWarning: addressWarning(n),
  }));
  return htmlResponse(
    200,
    accountPage({
      accountId: account.id,
      email: account.email,
      servers: servers.map((n) => ({ id: n.id, label: n.label, status: n.status === "active" ? "active" : "pending" })),
      nests: view,
      sessions: sessions.map((r) => ({
        id: r.id,
        kind: r.kind,
        createdAt: r.created_at,
        lastSeenAt: r.last_seen_at,
        userAgent: r.user_agent,
        deviceName: r.device_name,
        current: r.id === s.id,
      })),
    }),
  );
}

// GET /v1/me (session or app token)
export async function meRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowserOrApp(ctx);
  const account = await accountRow(ctx, s.accountId);
  if (!account) throw new HttpError(401, "unauthorized");
  const ids = await ctx.env.DB.prepare(
    "SELECT provider, email, created_at, last_used_at FROM identities WHERE account_id = ?1 ORDER BY created_at, provider",
  )
    .bind(s.accountId)
    .all<{ provider: string; email: string; created_at: number; last_used_at: number }>();
  return json(200, {
    accountId: account.id,
    email: account.email,
    createdAt: iso(account.created_at),
    identities: ids.results.map((i) => ({ provider: i.provider, email: i.email, createdAt: iso(i.created_at), lastUsedAt: iso(i.last_used_at) })),
    session: { id: s.id, kind: s.kind, authTime: iso(s.authTime), amr: s.amr, recentAuth: s.kind === "browser" && isRecent(ctx, s) },
  });
}

// DELETE /v1/me
export async function deleteMeRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  requireRecent(ctx, s);
  const account = await accountRow(ctx, s.accountId);
  if (account) await sendNotice(ctx, null, account.email, "account_deleted");
  const db = ctx.env.DB;
  const id = s.accountId;
  // The account's relayed servers: their rows go with the account; the router deploy that follows drops their links
  // and then deletes their tunnels (the hourly cron deletes any tunnel left over).
  const tunnels = (
    await db.prepare("SELECT tunnel_id FROM nests WHERE account_id = ?1 AND mode = 'relay' AND tunnel_id IS NOT NULL").bind(id).all<{ tunnel_id: string }>()
  ).results.map((r) => r.tunnel_id);
  // Foreign keys cascade from accounts; the explicit deletes make the result independent of that setting.
  await db.batch([
    db.prepare("DELETE FROM sessions WHERE account_id = ?1").bind(id),
    db.prepare("DELETE FROM identities WHERE account_id = ?1").bind(id),
    db.prepare("DELETE FROM nests WHERE account_id = ?1").bind(id),
    db.prepare("DELETE FROM claim_codes WHERE account_id = ?1").bind(id),
    db.prepare("DELETE FROM device_logins WHERE account_id = ?1").bind(id),
    db.prepare("DELETE FROM server_links WHERE account_id = ?1").bind(id),
    db.prepare("DELETE FROM audit WHERE account_id = ?1").bind(id),
    db.prepare("DELETE FROM login_challenges WHERE email = ?1").bind(account?.email ?? ""),
    db.prepare("DELETE FROM accounts WHERE id = ?1").bind(id),
  ]);
  clearSessionCookie(ctx);
  if (tunnels.length) ctx.defer(runRouterLogged(ctx.env, ctx.now, ctx.deps.fetch, { alsoDelete: tunnels }));
  return noContent();
}

// GET /v1/nests (session or app token)
export async function listNestsRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowserOrApp(ctx);
  const nests = await nestsOf(ctx, s.accountId);
  return json(200, { nests: nests.map((n) => nestJson(n, ctx.now)) });
}

export const LINK_COMMAND = "sudo -u pairnets /opt/pairnets/pairnets-server hosted link";

// POST /v1/nests/claim-codes
export async function createClaimCodeRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  requireRecent(ctx, s);
  const b = jsonBody(ctx, true);
  const label = cleanLabel(b.label);
  if (label === "invalid") throw new HttpError(400, "bad_request");

  const nests = await ctx.env.DB.prepare("SELECT COUNT(*) AS n FROM nests WHERE account_id = ?1").bind(s.accountId).first<{ n: number }>();
  if ((nests?.n ?? 0) >= MAX_NESTS_PER_ACCOUNT) throw new HttpError(409, "nest_limit");
  const unused = await ctx.env.DB.prepare("SELECT COUNT(*) AS n FROM claim_codes WHERE account_id = ?1 AND used_at IS NULL AND expires_at > ?2")
    .bind(s.accountId, ctx.now)
    .first<{ n: number }>();
  if ((unused?.n ?? 0) >= MAX_UNUSED_CODES) throw new HttpError(429, "too_many_codes", undefined, { "Retry-After": String(CLAIM_CODE_LIFETIME) });
  const daily = await hit(ctx, "codes-day", s.accountId, MAX_CODES_PER_DAY, DAY);
  if (daily.limited) throw new HttpError(429, "too_many_codes", undefined, { "Retry-After": String(daily.retryAfter) });

  const c = randomBase32(12, B32_UPPER);
  const code = `PN-${c.slice(0, 4)}-${c.slice(4, 8)}-${c.slice(8, 12)}`;
  const expires = ctx.now + CLAIM_CODE_LIFETIME;
  await ctx.env.DB.prepare(
    "INSERT INTO claim_codes (code_hash, account_id, label, created_at, expires_at, used_at, nest_id) VALUES (?1, ?2, ?3, ?4, ?5, NULL, NULL)",
  )
    .bind(blob(await sha256(code)), s.accountId, label, ctx.now, expires)
    .run();
  await audit(ctx, "claim_code_created", s.accountId);
  return json(201, { code, expiresAt: iso(expires), command: `${LINK_COMMAND} ${code}` });
}

// DELETE /v1/nests/claim-codes
export async function cancelClaimCodesRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  await ctx.env.DB.prepare("DELETE FROM claim_codes WHERE account_id = ?1 AND used_at IS NULL").bind(s.accountId).run();
  return noContent();
}

async function ownNest(ctx: Ctx, accountId: string, nestId: string): Promise<NestRow> {
  if (!NEST_ID_RE.test(nestId)) throw new HttpError(404, "not_found");
  const n = await ctx.env.DB.prepare("SELECT * FROM nests WHERE id = ?1 AND account_id = ?2 AND mode = 'url'").bind(nestId, accountId).first<NestRow>();
  if (!n) throw new HttpError(404, "not_found");
  return n;
}

// PATCH /v1/nests/{nestId}
export async function patchNestRoute(ctx: Ctx, nestId: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  const b = jsonBody(ctx);
  const label = cleanLabel(b.label);
  if (label === "invalid") throw new HttpError(400, "bad_request");
  if (b.hostedLogin !== undefined && typeof b.hostedLogin !== "boolean") throw new HttpError(400, "bad_request");
  const n = await ownNest(ctx, s.accountId, nestId);
  const newLabel = label ?? n.label;
  const newHosted = b.hostedLogin === undefined ? n.hosted_login : b.hostedLogin ? 1 : 0;
  await ctx.env.DB.prepare("UPDATE nests SET label = ?1, hosted_login = ?2 WHERE id = ?3 AND account_id = ?4")
    .bind(newLabel, newHosted, n.id, s.accountId)
    .run();
  if (newHosted !== n.hosted_login) await audit(ctx, newHosted ? "nest_login_on" : "nest_login_off", s.accountId, n.id);
  return json(200, nestJson({ ...n, label: newLabel, hosted_login: newHosted }, ctx.now));
}

// DELETE /v1/nests/{nestId}
export async function deleteNestRoute(ctx: Ctx, nestId: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  requireRecent(ctx, s);
  const n = await ownNest(ctx, s.accountId, nestId);
  await ctx.env.DB.prepare("DELETE FROM nests WHERE id = ?1 AND account_id = ?2").bind(n.id, s.accountId).run();
  await audit(ctx, "nest_removed", s.accountId, n.id);
  const account = await accountRow(ctx, s.accountId);
  if (account) await sendNotice(ctx, s.accountId, account.email, "nest_removed", n.public_url);
  return noContent();
}

interface SessionListRow {
  id: string;
  kind: "browser" | "app";
  created_at: number;
  last_seen_at: number;
  user_agent: string | null;
  device_name: string | null;
}

async function sessionRows(ctx: Ctx, accountId: string): Promise<SessionListRow[]> {
  const r = await ctx.env.DB.prepare(
    "SELECT id, kind, created_at, last_seen_at, user_agent, device_name FROM sessions WHERE account_id = ?1 AND expires_at > ?2 ORDER BY created_at DESC, id",
  )
    .bind(accountId, ctx.now)
    .all<SessionListRow>();
  return r.results;
}

// GET /v1/sessions
export async function listSessionsRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  const rows = await sessionRows(ctx, s.accountId);
  return json(200, {
    sessions: rows.map((r) => ({
      id: r.id,
      kind: r.kind,
      createdAt: iso(r.created_at),
      lastSeenAt: iso(r.last_seen_at),
      userAgent: r.user_agent,
      deviceName: r.device_name,
      current: r.id === s.id,
    })),
  });
}

// DELETE /v1/sessions/{id}
export async function deleteSessionRoute(ctx: Ctx, id: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  if (!SESSION_ID_RE.test(id)) throw new HttpError(404, "not_found");
  const r = await ctx.env.DB.prepare("DELETE FROM sessions WHERE id = ?1 AND account_id = ?2").bind(id, s.accountId).run();
  if (r.meta.changes !== 1) throw new HttpError(404, "not_found");
  if (id === s.id) clearSessionCookie(ctx);
  return noContent();
}
