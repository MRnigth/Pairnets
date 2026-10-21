// Servers reached through the service (RELAY.md sections 2 and 7): the installer's device-code flow, the account's
// servers and their computers, and removing either.
//
//   POST /v1/servers/start               installer, no auth          -> device code + user code
//   GET  /add?code=                      browser page                -> "Add this nest" / "Not mine"
//   GET  /v1/servers/requests/{code}     session                     -> what the page shows
//   POST /v1/servers/approve             session + Origin            -> nest row, tunnel, router deploy
//   POST /v1/servers/poll                installer, no auth          -> once: tunnel token + nest key
//   GET  /v1/servers                     session                     -> servers with status, version, computers
//   PATCH  /v1/servers/{id}/devices/{d}  session + Origin            -> the nest renames that computer
//   DELETE /v1/servers/{id}/devices/{d}  session + Origin            -> the nest revokes that computer
//   PATCH  /v1/servers/{id}              session + Origin            -> new label, passed on to the nest
//   DELETE /v1/servers/{id}              session + Origin, recent    -> router binding, tunnel and row go
//
// Friends on a server (its People, RELAY.md section 10) have their own routes in friends.ts.

import { b64urlEncode } from "../b64";
import { audit } from "../audit";
import { CloudflareApi, CloudflareError, teardown } from "../cloudflare";
import type { Ctx } from "../context";
import { B32_UPPER, newId, randomBase32, randomToken } from "../crypto";
import { intVar } from "../env";
import { ACCOUNT_ID_RE, cleanLabel, cleanText, DEVICE_ID_RE, stripHidden, displayUserCode, iso, NEST_ID_RE, normaliseUserCode, SERVER_DEVICE_CODE_RE } from "../formats";
import { HttpError, htmlResponse, json, noContent, redirect } from "../http";
import { sendNotice } from "../mail";
import { type AdminAnswer, type AdminTarget, adminCall, markReached, nestKey, NestOffline } from "../nestadmin";
import { addPage } from "../pages";
import { enforce, HOUR } from "../ratelimit";
import { jsonBody } from "../request";
import { runRouterLogged } from "../router";
import { isRecent, loadBrowserSession, requireBrowser, requireRecent, secretHash } from "../sessions";
import { maxNestsPerAccount, nestLimitError, type NestRow } from "./account";

export const SERVER_LINK_LIFETIME = 900;
export const SERVER_POLL_INTERVAL = 3;
export const DEFAULT_MAX_NESTS = 900;
/** A computer's or a nest's name, as the nest keeps it (AuthStore.MaxNameLength). */
export const MAX_NAME = 64;
const STARTS_PER_HOUR = 10;
const APPROVALS_PER_HOUR = 20;

export interface ServerLinkRow {
  id: string;
  user_code: string;
  hostname: string | null;
  server_version: string | null;
  status: "pending" | "approving" | "approved" | "denied" | "delivered";
  account_id: string | null;
  nest_id: string | null;
  created_at: number;
  expires_at: number;
  decided_at: number | null;
  last_poll_at: number | null;
}

/** The relay address of a nest: what apps store as their server URL. */
export function relayUrl(ctx: Ctx, nestId: string): string {
  return `${ctx.env.PUBLIC_ORIGIN}/n/${nestId}/`;
}

/** 'approving' is a few seconds of Cloudflare work; from the outside the request is still waiting. */
function shownStatus(s: ServerLinkRow["status"]): string {
  return s === "approving" ? "pending" : s;
}

// POST /v1/servers/start
export async function serverStartRoute(ctx: Ctx): Promise<Response> {
  await enforce(ctx, "server-start", ctx.ipKey, STARTS_PER_HOUR, HOUR);
  const b = jsonBody(ctx, true);
  const hostname = cleanText(b.hostname, 64);
  const serverVersion = cleanText(b.serverVersion, 64);
  if (hostname === "invalid" || serverVersion === "invalid") throw new HttpError(400, "bad_request");
  const deviceCode = `psd_${randomToken(32)}`;
  for (let attempt = 0; attempt < 5; attempt++) {
    const userCode = randomBase32(8, B32_UPPER);
    const r = await ctx.env.DB.prepare(
      `INSERT INTO server_links (id, device_code_hash, user_code, hostname, server_version, status, account_id, nest_id, created_at, expires_at, decided_at, last_poll_at)
       VALUES (?1, ?2, ?3, ?4, ?5, 'pending', NULL, NULL, ?6, ?7, NULL, NULL)
       ON CONFLICT DO NOTHING`,
    )
      .bind(newId("svl"), await secretHash(deviceCode), userCode, hostname, serverVersion, ctx.now, ctx.now + SERVER_LINK_LIFETIME)
      .run();
    if (r.meta.changes !== 1) continue; // that user code is taken right now: pick another
    const shown = displayUserCode(userCode);
    return json(200, {
      deviceCode,
      userCode: shown,
      verificationUri: `${ctx.env.PUBLIC_ORIGIN}/add`,
      verificationUriComplete: `${ctx.env.PUBLIC_ORIGIN}/add?code=${shown}`,
      expiresIn: SERVER_LINK_LIFETIME,
      interval: SERVER_POLL_INTERVAL,
    });
  }
  throw new Error("no free user code");
}

/** A link this account may see: pending (anyone with the code), or already decided by this account. */
async function visibleLink(ctx: Ctx, rawCode: string | null, accountId: string): Promise<ServerLinkRow | null> {
  const code = normaliseUserCode(rawCode);
  if (!code) return null;
  const row = await ctx.env.DB.prepare("SELECT * FROM server_links WHERE user_code = ?1").bind(code).first<ServerLinkRow>();
  if (!row || row.expires_at <= ctx.now) return null;
  if (row.status !== "pending" && row.account_id !== accountId) return null;
  return row;
}

// GET /v1/servers/requests/{userCode}
export async function serverRequestRoute(ctx: Ctx, rawCode: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  const row = await visibleLink(ctx, rawCode, s.accountId);
  if (!row) throw new HttpError(404, "not_found");
  return json(200, {
    userCode: displayUserCode(row.user_code),
    hostname: row.hostname,
    serverVersion: row.server_version,
    createdAt: iso(row.created_at),
    expiresAt: iso(row.expires_at),
    status: shownStatus(row.status),
  });
}

// GET /add?code=XXXX-XXXX
export async function addPageRoute(ctx: Ctx): Promise<Response> {
  const typed = ctx.url.searchParams.get("code");
  const s = await loadBrowserSession(ctx);
  if (!s) {
    const next = typed && /^[0-9A-Za-z-]{1,16}$/.test(typed) ? `/add?code=${typed}` : "/add";
    return redirect(`/login?next=${encodeURIComponent(next)}`);
  }
  const row = typed ? await visibleLink(ctx, typed, s.accountId) : null;
  // Adding a nest is a security step: an older sign-in is asked for again, then comes straight back here.
  if (row && row.status === "pending" && !isRecent(ctx, s)) {
    return redirect(`/login?reauth=1&next=${encodeURIComponent(`/add?code=${displayUserCode(row.user_code)}`)}`);
  }
  const view = row
    ? { userCode: row.user_code, hostname: row.hostname, serverVersion: row.server_version, createdAt: row.created_at, status: row.status }
    : null;
  return htmlResponse(typed && !row ? 404 : 200, addPage(view, typed));
}

// POST /v1/servers/approve
export async function serverApproveRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  const b = jsonBody(ctx);
  if (typeof b.userCode !== "string" || typeof b.approve !== "boolean") throw new HttpError(400, "bad_request");
  const label = cleanLabel(b.label);
  if (label === "invalid") throw new HttpError(400, "bad_request");
  // Adding a nest only right after a sign-in (refusing is always fine).
  if (b.approve) requireRecent(ctx, s);
  const code = normaliseUserCode(b.userCode);
  if (!code) throw new HttpError(404, "not_found");
  const link = await ctx.env.DB.prepare("SELECT * FROM server_links WHERE user_code = ?1").bind(code).first<ServerLinkRow>();
  if (!link) throw new HttpError(404, "not_found");
  if (link.status !== "pending") {
    if (link.account_id !== s.accountId) throw new HttpError(404, "not_found");
    throw new HttpError(409, "already_decided");
  }
  if (link.expires_at <= ctx.now) throw new HttpError(400, "expired");

  if (!b.approve) {
    const r = await ctx.env.DB.prepare(
      "UPDATE server_links SET status = 'denied', account_id = ?1, decided_at = ?2 WHERE id = ?3 AND status = 'pending' AND expires_at > ?2",
    )
      .bind(s.accountId, ctx.now, link.id)
      .run();
    if (r.meta.changes !== 1) throw new HttpError(409, "already_decided");
    await audit(ctx, "server_denied", s.accountId, null, { hostname: link.hostname });
    return json(200, { status: "denied" });
  }

  await enforce(ctx, "server-approve", s.accountId, APPROVALS_PER_HOUR, HOUR);
  const nestId = await createNestRow(ctx, s.accountId, link, label ?? link.hostname ?? "My server");
  await createTunnel(ctx, s.accountId, link, nestId);
  await audit(ctx, "server_added", s.accountId, nestId, { hostname: link.hostname });
  const account = await ctx.env.DB.prepare("SELECT email FROM accounts WHERE id = ?1").bind(s.accountId).first<{ email: string }>();
  if (account) await sendNotice(ctx, s.accountId, account.email, "server_added", label ?? link.hostname ?? "My server");
  ctx.defer(runRouterLogged(ctx.env, ctx.now, ctx.deps.fetch));
  return json(200, { status: "approved", nestId });
}

/**
 * The nest row ('relay', 'pending', no address) and the link's 'approving' state, in one batch (one transaction): the
 * row is made only while the link is still pending, the account has fewer than 3 nests and the service has room.
 */
async function createNestRow(ctx: Ctx, accountId: string, link: ServerLinkRow, label: string): Promise<string> {
  const db = ctx.env.DB;
  const nestId = newId("nst");
  const max = intVar(ctx.env.MAX_NESTS, DEFAULT_MAX_NESTS);
  const [insert] = await db.batch([
    db
      .prepare(
        `INSERT INTO nests (id, account_id, label, public_url, status, key_version, hosted_login, created_at, last_req_ts, mode)
           SELECT ?1, ?2, ?3, '', 'pending', 1, 0, ?4, 0, 'relay'
           WHERE EXISTS (SELECT 1 FROM server_links WHERE id = ?5 AND status = 'pending' AND expires_at > ?4)
             AND (SELECT COUNT(*) FROM nests WHERE account_id = ?2) < ?6
             AND (SELECT COUNT(*) FROM nests WHERE mode = 'relay') < ?7`,
      )
      .bind(nestId, accountId, label, ctx.now, link.id, maxNestsPerAccount(ctx.env), max),
    db
      .prepare(
        `UPDATE server_links SET status = 'approving', account_id = ?2, decided_at = ?3, nest_id = ?1
           WHERE id = ?4 AND status = 'pending' AND EXISTS (SELECT 1 FROM nests WHERE id = ?1)`,
      )
      .bind(nestId, accountId, ctx.now, link.id),
  ]);
  if (insert.meta.changes === 1) return nestId;

  const again = await db.prepare("SELECT status, expires_at FROM server_links WHERE id = ?1").bind(link.id).first<{ status: string; expires_at: number }>();
  if (!again) throw new HttpError(404, "not_found");
  if (again.status !== "pending") throw new HttpError(409, "already_decided");
  if (again.expires_at <= ctx.now) throw new HttpError(400, "expired");
  const mine = await db.prepare("SELECT COUNT(*) AS n FROM nests WHERE account_id = ?1").bind(accountId).first<{ n: number }>();
  if ((mine?.n ?? 0) >= maxNestsPerAccount(ctx.env)) throw nestLimitError(ctx.env);
  throw new HttpError(503, "full");
}

/**
 * The nest's tunnel, with no hostname routed. Any failure deletes what was made and puts the link back to pending (the
 * person can press the button again); a failed undo leaves the row 'broken' with its tunnel for the sweep.
 */
async function createTunnel(ctx: Ctx, accountId: string, link: ServerLinkRow, nestId: string): Promise<void> {
  const db = ctx.env.DB;
  const cf = new CloudflareApi(ctx.env, ctx.deps.fetch);
  let tunnelId: string | null = null;
  try {
    tunnelId = await cf.createTunnel(nestId);
    await cf.setIngress(tunnelId);
  } catch (e) {
    const err = e instanceof CloudflareError ? e : new CloudflareError("unknown", 0, []);
    const undo = tunnelId ? await teardown(cf, tunnelId, nestId) : { done: true, failed: [] };
    await db.batch([
      undo.done
        ? db.prepare("DELETE FROM nests WHERE id = ?1").bind(nestId)
        : db.prepare("UPDATE nests SET status = 'broken', tunnel_id = ?1 WHERE id = ?2").bind(tunnelId, nestId),
      db
        .prepare("UPDATE server_links SET status = 'pending', account_id = NULL, decided_at = NULL, nest_id = NULL WHERE id = ?1 AND status = 'approving'")
        .bind(link.id),
    ]);
    await audit(ctx, "server_add_failed", accountId, nestId, { step: err.step, status: err.status, codes: err.codes, undone: undo.done });
    throw new HttpError(502, "cloudflare_failed");
  }
  // Only now does the row carry its tunnel: a router deploy never names a tunnel that is not fully set up.
  await db.batch([
    db.prepare("UPDATE nests SET tunnel_id = ?1 WHERE id = ?2").bind(tunnelId, nestId),
    db.prepare("UPDATE server_links SET status = 'approved' WHERE id = ?1 AND status = 'approving'").bind(link.id),
  ]);
}

// POST /v1/servers/poll
export async function serverPollRoute(ctx: Ctx): Promise<Response> {
  const b = jsonBody(ctx);
  if (typeof b.deviceCode !== "string") throw new HttpError(400, "bad_request");
  if (!SERVER_DEVICE_CODE_RE.test(b.deviceCode)) throw new HttpError(400, "expired");
  const row = await ctx.env.DB.prepare("SELECT * FROM server_links WHERE device_code_hash = ?1")
    .bind(await secretHash(b.deviceCode))
    .first<ServerLinkRow>();
  if (!row || row.expires_at <= ctx.now || row.status === "delivered") throw new HttpError(400, "expired");
  // One counted poll per interval (an atomic check, so two polls at once cannot both pass).
  const slot = await ctx.env.DB.prepare("UPDATE server_links SET last_poll_at = ?1 WHERE id = ?2 AND (last_poll_at IS NULL OR last_poll_at <= ?3)")
    .bind(ctx.now, row.id, ctx.now - SERVER_POLL_INTERVAL)
    .run();
  if (slot.meta.changes !== 1) throw new HttpError(429, "slow_down", undefined, { "Retry-After": String(SERVER_POLL_INTERVAL) });
  if (row.status === "approved" && row.nest_id) return deliver(ctx, row, row.nest_id);
  return json(200, { status: row.status === "denied" ? "denied" : "pending" });
}

/** The approved answer, once: the tunnel token is read from Cloudflare now and the nest key derived now. */
async function deliver(ctx: Ctx, row: ServerLinkRow, nestId: string): Promise<Response> {
  const nest = await ctx.env.DB.prepare("SELECT * FROM nests WHERE id = ?1 AND mode = 'relay'").bind(nestId).first<NestRow>();
  if (!nest || nest.status === "broken" || !nest.tunnel_id) throw new HttpError(400, "expired");
  let tunnelToken: string;
  try {
    tunnelToken = await new CloudflareApi(ctx.env, ctx.deps.fetch).tunnelToken(nest.tunnel_id);
  } catch (e) {
    console.error("servers: reading the tunnel token failed", e instanceof Error ? e.message : "unknown");
    throw new HttpError(502, "cloudflare_failed", "Cloudflare did not answer. The installer tries again by itself.");
  }
  const flip = await ctx.env.DB.prepare("UPDATE server_links SET status = 'delivered' WHERE id = ?1 AND status = 'approved'").bind(row.id).run();
  if (flip.meta.changes !== 1) throw new HttpError(400, "expired");
  await audit(ctx, "server_delivered", nest.account_id, nest.id);
  return json(200, {
    status: "approved",
    nestId: nest.id,
    label: nest.label,
    relayUrl: relayUrl(ctx, nest.id),
    tunnelToken,
    nestKey: b64urlEncode(await nestKey(ctx.env, nest.id, nest.key_version)),
    keyVersion: nest.key_version,
    serviceUrl: ctx.env.PUBLIC_ORIGIN,
  });
}

/** The account's relayed servers that are in use (removed ones are 'broken' until the sweep has deleted them). */
export async function serversOf(ctx: Ctx, accountId: string): Promise<NestRow[]> {
  const r = await ctx.env.DB.prepare(
    "SELECT * FROM nests WHERE account_id = ?1 AND mode = 'relay' AND status IN ('pending', 'active') ORDER BY created_at, id",
  )
    .bind(accountId)
    .all<NestRow>();
  return r.results;
}

/** One of the account's relayed servers in use, or 404 not_found (another account's answers like an unknown one). */
export async function ownServer(ctx: Ctx, accountId: string, nestId: string): Promise<NestRow> {
  if (!NEST_ID_RE.test(nestId)) throw new HttpError(404, "not_found");
  const n = await ctx.env.DB.prepare(
    "SELECT * FROM nests WHERE id = ?1 AND account_id = ?2 AND mode = 'relay' AND status IN ('pending', 'active')",
  )
    .bind(nestId, accountId)
    .first<NestRow>();
  if (!n) throw new HttpError(404, "not_found");
  return n;
}

export function obj(v: unknown): Record<string, unknown> | null {
  return v && typeof v === "object" && !Array.isArray(v) ? (v as Record<string, unknown>) : null;
}

function str(v: unknown, max = 200): string | null {
  return typeof v === "string" && v.length <= max ? v : null;
}

function count(v: unknown): number | null {
  return typeof v === "number" && Number.isSafeInteger(v) && v >= 0 ? v : null;
}

/**
 * A computer as the nest lists it, with only the documented fields (and only of the documented types). A friend's
 * computer (RELAY.md 10) has its person's account id, so the page lists it under that person, not with the owner's.
 */
function deviceJson(v: unknown): Record<string, unknown> | null {
  const d = obj(v);
  if (!d || typeof d.id !== "string" || !DEVICE_ID_RE.test(d.id)) return null;
  const access = d.access === "view" || d.access === "edit" ? d.access : "owner";
  const personId = access !== "owner" && typeof d.personId === "string" && ACCOUNT_ID_RE.test(d.personId) ? d.personId : null;
  return { id: d.id, name: str(d.name) ?? "", system: str(d.system), createdAt: str(d.createdAt, 64), lastSeen: str(d.lastSeen, 64), access, personId };
}

/** What a nest's status says about friends (RELAY.md 10): whether it can have them, and whether it is encrypted. */
export function friendFeatures(st: Record<string, unknown>): { friends: boolean; sealed: boolean } {
  return { friends: Array.isArray(st.features) && st.features.includes("friends"), sealed: st.sealed === true };
}

/** One server for the account page: a live status call and the computers list (5 s each, in parallel). */
async function serverJson(ctx: Ctx, n: NestRow): Promise<Record<string, unknown>> {
  const call = (path: string) => adminCall(ctx.env, ctx.now, n, "GET", path).catch((e: unknown) => (e instanceof NestOffline ? null : Promise.reject(e)));
  const [status, devices] = await Promise.all([call("/api/relay/status"), call("/api/relay/devices")]);
  const st = status && status.status === 200 ? obj(status.json) : null;
  const online = st !== null;
  const version = st ? str(st.serverVersion, 32) : null;
  if (online) {
    if (n.status === "pending") await markReached(ctx.env, ctx.now, n.id);
    await ctx.env.DB.prepare("UPDATE nests SET last_seen_at = ?1, last_version = COALESCE(?2, last_version) WHERE id = ?3").bind(ctx.now, version, n.id).run();
  }
  // A nest that missed a rename while it was offline (or was set up before nests had names) is told its name now. An
  // older nest has no "name" in its status and no way to keep one.
  if (st && "name" in st && st.name !== n.label) ctx.defer(tellName(ctx, n, n.label));
  const list = devices && devices.status === 200 ? obj(devices.json)?.devices : null;
  const features = st ? friendFeatures(st) : null;
  return {
    id: n.id,
    label: n.label,
    status: online && n.status === "pending" ? "active" : n.status,
    online,
    serverVersion: version ?? n.last_version,
    freeBytes: st ? count(st.freeBytes) : null,
    devices: Array.isArray(list) ? list.map(deviceJson).filter((d) => d !== null) : [],
    lastSeenAt: iso(online ? ctx.now : n.last_seen_at),
    // Unknown (null) while the nest is offline.
    friends: features ? features.friends : null,
    sealed: features ? features.sealed : null,
  };
}

// GET /v1/servers
export async function listServersRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  const servers = await serversOf(ctx, s.accountId);
  return json(200, await Promise.all(servers.map((n) => serverJson(ctx, n))));
}

/** A new name from the account page: control and invisible characters dropped, trimmed, 1-64 characters, or 400 bad_name. */
function newName(v: unknown): string {
  if (v !== undefined && v !== null && typeof v !== "string") throw new HttpError(400, "bad_request");
  const name = typeof v === "string" ? stripHidden(v).trim() : "";
  if (name.length === 0) throw new HttpError(400, "bad_name", "Type a name.");
  if ([...name].length > MAX_NAME) throw new HttpError(400, "bad_name", `A name is at most ${MAX_NAME} letters.`);
  return name;
}

/** An older nest has no rename route: 405 for the path it knows for DELETE only, or a 404 that is not its own JSON error. */
function tooOld(r: AdminAnswer): boolean {
  return r.status === 405 || (r.status === 404 && typeof obj(r.json)?.error !== "string");
}

/** Tells the nest the name its owner gave it, best effort: an offline nest is told on a later GET /v1/servers. */
async function tellName(ctx: Ctx, n: AdminTarget, name: string): Promise<void> {
  try {
    await adminCall(ctx.env, ctx.now, n, "PUT", "/api/relay/name", { name });
  } catch (e) {
    if (!(e instanceof NestOffline)) console.error("servers: telling the nest its name failed", e instanceof Error ? e.message : "unknown");
  }
}

// PATCH /v1/servers/{id}/devices/{deviceId} {name}
export async function renameDeviceRoute(ctx: Ctx, nestId: string, deviceId: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  const n = await ownServer(ctx, s.accountId, nestId);
  if (!DEVICE_ID_RE.test(deviceId)) throw new HttpError(404, "not_found");
  const name = newName(jsonBody(ctx).name);
  let r;
  try {
    r = await adminCall(ctx.env, ctx.now, n, "PATCH", `/api/relay/devices/${encodeURIComponent(deviceId)}`, { name });
  } catch (e) {
    if (e instanceof NestOffline) throw new HttpError(503, "nest_offline");
    throw e;
  }
  if (tooOld(r)) throw new HttpError(409, "nest_too_old");
  if (r.status === 404) throw new HttpError(404, "not_found", "That computer is not on this nest any more.");
  if (r.status === 400) throw new HttpError(400, "bad_name", "The nest did not take that name. Try another one.");
  // The name the nest settled on: "Laptop (2)" when another computer is already called "Laptop".
  const stored = r.status === 200 ? str(obj(r.json)?.name, MAX_NAME * 2) : null;
  if (!stored) throw new HttpError(503, "nest_offline");
  await audit(ctx, "device_renamed", s.accountId, n.id, { to: stored });
  return json(200, { id: deviceId, name: stored, requested: name });
}

// PATCH /v1/servers/{id} {label}
export async function renameServerRoute(ctx: Ctx, nestId: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  const n = await ownServer(ctx, s.accountId, nestId);
  const label = newName(jsonBody(ctx).label);
  await ctx.env.DB.prepare("UPDATE nests SET label = ?1 WHERE id = ?2 AND account_id = ?3").bind(label, n.id, s.accountId).run();
  await audit(ctx, "nest_renamed", s.accountId, n.id, { from: n.label, to: label });
  ctx.defer(tellName(ctx, n, label));
  return json(200, { id: n.id, label });
}

// DELETE /v1/servers/{id}/devices/{deviceId}
export async function removeDeviceRoute(ctx: Ctx, nestId: string, deviceId: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  requireRecent(ctx, s);
  const n = await ownServer(ctx, s.accountId, nestId);
  if (!DEVICE_ID_RE.test(deviceId)) throw new HttpError(404, "not_found");
  let r;
  try {
    r = await adminCall(ctx.env, ctx.now, n, "DELETE", `/api/relay/devices/${encodeURIComponent(deviceId)}`);
  } catch (e) {
    if (e instanceof NestOffline) throw new HttpError(503, "nest_offline");
    throw e;
  }
  if (r.status === 404) throw new HttpError(404, "not_found");
  if (r.status !== 200) throw new HttpError(503, "nest_offline");
  await audit(ctx, "device_removed", s.accountId, n.id);
  return noContent();
}

// DELETE /v1/servers/{id}
export async function removeServerRoute(ctx: Ctx, nestId: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  requireRecent(ctx, s);
  const n = await ownServer(ctx, s.accountId, nestId);
  // 'broken' takes it out of the relay and of the next router deploy at once; that deploy then deletes the tunnel and
  // the row (or the hourly cron does, if a deploy is busy or Cloudflare is down).
  // Its friends and invites go with it (their computers lose the nest with the link).
  const db = ctx.env.DB;
  await db.batch([
    db.prepare("UPDATE nests SET status = 'broken' WHERE id = ?1 AND account_id = ?2").bind(n.id, s.accountId),
    db.prepare("DELETE FROM nest_members WHERE nest_id = ?1").bind(n.id),
    db.prepare("DELETE FROM nest_invites WHERE nest_id = ?1").bind(n.id),
  ]);
  await audit(ctx, "server_removed", s.accountId, n.id);
  const account = await ctx.env.DB.prepare("SELECT email FROM accounts WHERE id = ?1").bind(s.accountId).first<{ email: string }>();
  if (account) await sendNotice(ctx, s.accountId, account.email, "server_removed", n.label);
  ctx.defer(runRouterLogged(ctx.env, ctx.now, ctx.deps.fetch));
  return noContent();
}
