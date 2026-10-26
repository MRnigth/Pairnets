// The account page and account API (CONTRACT.md section 6.7). Servers reached through the service (RELAY.md) have
// their own API in servers.ts; the /v1/nests endpoints here are about nests on their own domain ('url' mode) only.

import { audit } from "../audit";
import type { Ctx } from "../context";
import { type Env, intVar } from "../env";
import { B32_UPPER, randomBase32, sha256 } from "../crypto";
import { cleanAccess, cleanFolders, cleanLabel, cleanText, iso, MAX_NEXT_INVITE, MAX_USERNAME, NEST_ID_RE, parseFolders, personName, safeNext, SESSION_ID_RE } from "../formats";
import { HttpError, htmlResponse, json, noContent, redirect } from "../http";
import { sendNotice } from "../mail";
import { accountPage, type AccountView, type NestView, type PeopleView } from "../pages";
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
  type Session,
} from "../sessions";
import { deleteAccountRows } from "../accounts";
import { inviteLink, peopleOf, sharedNestsOf } from "./friends";
import { linksOfAccount } from "./links";
import { serversOf } from "./servers";
import { betaRow, betaView } from "./beta";

/** Nests per account, both kinds together (pending ones count), unless wrangler.toml's MAX_NESTS_PER_ACCOUNT says otherwise. */
export const MAX_NESTS_PER_ACCOUNT = 4;

/** How many nests one account may have (MAX_NESTS_PER_ACCOUNT in wrangler.toml, default 4). Ones it has already stay. */
export function maxNestsPerAccount(env: Env): number {
  return intVar(env.MAX_NESTS_PER_ACCOUNT, MAX_NESTS_PER_ACCOUNT);
}

/** 409 nest_limit, naming the limit in force. */
export function nestLimitError(env: Env): HttpError {
  const max = maxNestsPerAccount(env);
  return new HttpError(409, "nest_limit", `This account already has ${max} ${max === 1 ? "nest" : "nests"}. Remove one first.`);
}

/** The account's own nests, both kinds and pending ones (what the limit counts), and the limit. Shared nests never count. */
export async function nestCount(ctx: Ctx, accountId: string): Promise<{ used: number; max: number }> {
  const r = await ctx.env.DB.prepare("SELECT COUNT(*) AS n FROM nests WHERE account_id = ?1").bind(accountId).first<{ n: number }>();
  return { used: r?.n ?? 0, max: maxNestsPerAccount(ctx.env) };
}
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
  /** The owner's beta rev this nest was last told (routes/beta.ts). */
  beta_rev_pushed: number;
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

interface AccountRow {
  id: string;
  email: string;
  username: string | null;
  created_at: number;
}

async function accountRow(ctx: Ctx, accountId: string): Promise<AccountRow | null> {
  return ctx.env.DB.prepare("SELECT id, email, username, created_at FROM accounts WHERE id = ?1").bind(accountId).first();
}


function addressWarning(n: NestRow): string | null {
  if (!n.last_public_host) return null;
  const u = new URL(n.public_url);
  if (n.last_public_host === u.hostname || n.last_public_host === u.host) return null;
  return `This nest now reports the address ${n.last_public_host}. Link it again (hosted unlink, then hosted link with a new code) to use the new address.`;
}

/**
 * /account?invite=nst_…&folders=Photos&folders=School&access=view: the apps' "Invite a friend" hands over to the website
 * (only a signed-in browser makes invites). Each folders= is one name, commas and all. A list that would not be accepted
 * leaves "Chosen folders" picked with nothing typed: never the whole nest, which is more than was asked for. So does a
 * list too long to come back through the sign-in page (MAX_NEXT_INVITE): it sends "." (a name never accepted) instead.
 */
function inviteAsked(ctx: Ctx): { nestId: string; folders: string[] | null; access: "view" | "edit"; next: string } | null {
  const q = ctx.url.searchParams;
  const nestId = q.get("invite");
  if (!nestId || !NEST_ID_RE.test(nestId)) return null;
  const typed = q.getAll("folders").filter((f) => f.length > 0);
  const folders = typed.length ? cleanFolders(typed) : null;
  const access = cleanAccess(q.get("access")) ?? "view";
  const kept = folders === "invalid" ? [] : folders;
  let own = `${(kept ?? []).map((f) => `&folders=${encodeURIComponent(f)}`).join("")}&access=${access}`;
  if (own.length > MAX_NEXT_INVITE) own = `&folders=.&access=${access}`;
  const next = `/account?invite=${nestId}${own}`;
  return { nestId, folders: kept, access, next };
}

async function peopleView(ctx: Ctx, nestId: string): Promise<PeopleView> {
  const p = await peopleOf(ctx, nestId);
  const name = (a: { email: string; username: string | null } | null) => (a ? personName(a) : "Someone");
  return {
    members: p.members.map((m) => ({
      accountId: m.row.account_id,
      name: name(m.account),
      email: m.account?.email ?? "",
      access: m.row.access,
      folders: parseFolders(m.row.folders),
      applied: m.row.synced === 1,
    })),
    waiting: p.invites
      .filter((i) => i.row.requested_by !== null)
      .map((i) => ({ id: i.row.id, name: name(i.requester), email: i.requester?.email ?? "", access: i.row.access, folders: parseFolders(i.row.folders) })),
    links: p.invites
      .filter((i) => i.row.requested_by === null)
      .map((i) => ({
        id: i.row.id,
        link: inviteLink(ctx, i.row.token),
        email: i.row.email,
        expiresAt: i.row.expires_at,
        access: i.row.access,
        folders: parseFolders(i.row.folders),
      })),
  };
}

// GET /account
export async function accountPageRoute(ctx: Ctx): Promise<Response> {
  const asked = inviteAsked(ctx);
  const back = asked ? `/login?next=${encodeURIComponent(safeNext(asked.next))}` : "/login?next=/account";
  const s = await loadBrowserSession(ctx);
  if (!s) return redirect(back);
  const account = await accountRow(ctx, s.accountId);
  if (!account) return redirect(back);
  const nests = await nestsOf(ctx, s.accountId);
  const servers = await serversOf(ctx, s.accountId);
  const people = await Promise.all(servers.map((n) => peopleView(ctx, n.id)));
  const shared = await sharedNestsOf(ctx, s.accountId);
  const invite: AccountView["invite"] = asked && servers.some((n) => n.id === asked.nestId) ? { nestId: asked.nestId, folders: asked.folders, access: asked.access } : null;
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
      username: account.username,
      nestCount: await nestCount(ctx, s.accountId),
      links: await linksOfAccount(ctx, s.accountId),
      servers: servers.map((n, i) => ({ id: n.id, label: n.label, status: n.status === "active" ? "active" : "pending", people: people[i] })),
      nests: view,
      shared: shared.map((x) => ({
        nestId: x.nest.id,
        label: x.nest.label,
        sharedBy: x.sharedBy,
        access: x.member.access,
        folders: parseFolders(x.member.folders),
      })),
      invite,
      beta: betaView(ctx, await betaRow(ctx, s.accountId)),
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
  return json(200, await meJson(ctx, s, account));
}

// PATCH /v1/me {username}: set (1 to 32 characters) or clear (null or "") the username.
export async function patchMeRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  const b = jsonBody(ctx);
  if (!("username" in b)) throw new HttpError(400, "bad_request");
  const username = cleanText(b.username, MAX_USERNAME);
  if (username === "invalid") throw new HttpError(400, "bad_request", `A username is at most ${MAX_USERNAME} characters.`);
  await ctx.env.DB.prepare("UPDATE accounts SET username = ?1, updated_at = ?2 WHERE id = ?3").bind(username, ctx.now, s.accountId).run();
  const account = await accountRow(ctx, s.accountId);
  if (!account) throw new HttpError(401, "unauthorized");
  await audit(ctx, "username_changed", s.accountId);
  return json(200, await meJson(ctx, s, account));
}

// GET /v1/signed-in: lets pairnets.app show who is signed in (the only route with CORS, see http.ts).
export async function signedInRoute(ctx: Ctx): Promise<Response> {
  const s = await loadBrowserSession(ctx);
  const account = s ? await accountRow(ctx, s.accountId) : null;
  return json(200, account ? { signedIn: true, name: personName(account) } : { signedIn: false });
}

async function meJson(ctx: Ctx, s: Session, account: AccountRow): Promise<Record<string, unknown>> {
  const ids = await ctx.env.DB.prepare(
    "SELECT provider, email, created_at, last_used_at FROM identities WHERE account_id = ?1 ORDER BY created_at, provider",
  )
    .bind(s.accountId)
    .all<{ provider: string; email: string; created_at: number; last_used_at: number }>();
  return {
    accountId: account.id,
    email: account.email,
    username: account.username,
    createdAt: iso(account.created_at),
    identities: ids.results.map((i) => ({ provider: i.provider, email: i.email, createdAt: iso(i.created_at), lastUsedAt: iso(i.last_used_at) })),
    session: { id: s.id, kind: s.kind, authTime: iso(s.authTime), amr: s.amr, recentAuth: s.kind === "browser" && isRecent(ctx, s) },
    nests: await nestCount(ctx, s.accountId),
  };
}

/**
 * GET /v1/me/export: "Download my data" (GDPR articles 15 and 20). Everything stored here about the signed-in account,
 * as one JSON file. Left out on purpose: token, code and key hashes (secrets, not information about the person), and
 * crash and bug reports (they hold nothing that says who sent them). Other people's accounts appear only as the name
 * and email this account already sees on its account page.
 */
export async function exportMeRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  const db = ctx.env.DB;
  const id = s.accountId;
  const all = async <T = Record<string, unknown>>(sql: string, ...args: unknown[]): Promise<T[]> =>
    (await db.prepare(sql).bind(...args).all<T>()).results;
  const account = await db.prepare("SELECT id, email, username, created_at, updated_at, inactive_warned_at, terms_version, terms_accepted_at FROM accounts WHERE id = ?1").bind(id).first<Record<string, unknown>>();
  if (!account) throw new HttpError(401, "unauthorized");
  const times = (rows: Record<string, unknown>[]) =>
    rows.map((r) => Object.fromEntries(Object.entries(r).map(([k, v]) => [k, k.endsWith("_at") && typeof v === "number" ? iso(v) : v])));
  const person = async (accountId: string | null) => {
    if (!accountId) return null;
    const a = await accountRow(ctx, accountId);
    return a ? { name: personName(a), email: a.email } : { name: "A deleted account", email: null };
  };
  const ownNests = await all<{ id: string }>("SELECT id FROM nests WHERE account_id = ?1", id);
  const ownIds = new Set(ownNests.map((n) => n.id));
  const members = await all<Record<string, unknown> & { nest_id: string; account_id: string }>(
    "SELECT nest_id, account_id, access, folders, added_at, removed_at FROM nest_members WHERE account_id = ?1 OR nest_id IN (SELECT id FROM nests WHERE account_id = ?1) ORDER BY added_at",
    id,
  );
  const invites = await all<Record<string, unknown> & { requested_by: string | null }>(
    `SELECT id, nest_id, kind, email, access, folders, created_at, expires_at, requested_by, requested_at, decided_at, decision, cancelled_at
     FROM nest_invites WHERE created_by = ?1 OR requested_by = ?1 OR nest_id IN (SELECT id FROM nests WHERE account_id = ?1) ORDER BY created_at`,
    id,
  );
  const body = {
    about:
      "Everything Pairnets (sync.pairnets.app) stores about your account. Times are UTC. Your files are not here: they are only on your own nest and computers. See https://pairnets.app/privacy/",
    exportedAt: iso(ctx.now),
    account: times([account])[0],
    signInMethods: times(await all("SELECT provider, subject, email, created_at, last_used_at FROM identities WHERE account_id = ?1 ORDER BY created_at", id)),
    signedInBrowsersAndApps: times(
      await all("SELECT id, kind, amr, auth_time AS signed_in_at, created_at, last_seen_at, expires_at, user_agent, device_name FROM sessions WHERE account_id = ?1 ORDER BY created_at", id),
    ),
    nests: times(
      await all(
        "SELECT id, label, mode, public_url, status, hosted_login, created_at, confirmed_at, last_seen_at, last_version, last_public_host FROM nests WHERE account_id = ?1 ORDER BY created_at",
        id,
      ),
    ),
    computersAskingToJoin: times(
      await all("SELECT name, system, app_version, status, nest_id, request_country, request_city, created_at, expires_at, decided_at FROM device_logins WHERE account_id = ?1", id),
    ),
    nestsBeingLinked: times(await all("SELECT hostname, server_version, status, nest_id, created_at, expires_at, decided_at FROM server_links WHERE account_id = ?1", id)),
    linkCodes: times(await all("SELECT label, created_at, expires_at, used_at, nest_id FROM claim_codes WHERE account_id = ?1", id)),
    sharing: await Promise.all(
      members.map(async (m) => ({
        ...times([m])[0],
        account_id: undefined,
        direction: ownIds.has(m.nest_id) ? "someone on your nest" : "you on someone's nest",
        person: m.account_id === id ? "you" : await person(m.account_id),
        folders: parseFolders(m.folders as string | null),
      })),
    ),
    invites: await Promise.all(
      invites.map(async (i) => ({
        ...times([i])[0],
        requested_by: i.requested_by === null ? null : i.requested_by === id ? "you" : await person(i.requested_by),
        folders: parseFolders(i.folders as string | null),
      })),
    ),
    linkedNests: times(await all("SELECT id, from_nest, to_nest, status, created_at FROM nest_links WHERE account_id = ?1 OR from_nest IN (SELECT id FROM nests WHERE account_id = ?1)", id)),
    activityLog: (await all<{ at: number; event: string; nest_id: string | null; detail: string | null }>("SELECT at, event, nest_id, detail FROM audit WHERE account_id = ?1 ORDER BY at", id)).map((a) => ({
      at: iso(a.at),
      event: a.event,
      nest_id: a.nest_id,
      detail: a.detail ? JSON.parse(a.detail) : null,
    })),
  };
  await audit(ctx, "data_exported", id);
  const day = new Date(ctx.now * 1000).toISOString().slice(0, 10);
  return new Response(JSON.stringify(body, null, 2), {
    status: 200,
    headers: { "Content-Type": "application/json; charset=utf-8", "Content-Disposition": `attachment; filename="pairnets-my-data-${day}.json"` },
  });
}

// DELETE /v1/me
export async function deleteMeRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  requireRecent(ctx, s);
  const account = await accountRow(ctx, s.accountId);
  if (account) await sendNotice(ctx, null, account.email, "account_deleted");
  const tunnels = await deleteAccountRows(ctx, s.accountId, account?.email ?? "");
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
  if ((nests?.n ?? 0) >= maxNestsPerAccount(ctx.env)) throw nestLimitError(ctx.env);
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
  if (newLabel !== n.label) await audit(ctx, "nest_renamed", s.accountId, n.id, { from: n.label, to: newLabel });
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
