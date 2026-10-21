// Friends on a nest (RELAY.md section 10). The owner invites someone with a link (or an email holding it); that person
// signs in and asks to join; the owner lets them in or declines. A link alone never lets anyone in. Once in, the
// friend's computers sign in like the owner's (apps.ts) and the nest gives them keys limited to what was shared.
//
//   POST   /v1/servers/{id}/invites               owner, session + Origin   -> {id, link, expiresAt, emailed}
//   DELETE /v1/servers/{id}/invites/{inv}         owner, session + Origin   -> the link stops working
//   POST   /v1/servers/{id}/invites/{inv}/approve owner, session + Origin, recent -> member row; the friend is emailed
//   POST   /v1/servers/{id}/invites/{inv}/decline owner, session + Origin   -> declined
//   GET    /v1/servers/{id}/people                owner, session            -> {members, invites, max}
//   PATCH  /v1/servers/{id}/people/{acc}          owner, session + Origin   -> the nest changes that person's computers
//   DELETE /v1/servers/{id}/people/{acc}          owner, session + Origin   -> the nest removes that person's computers
//   GET    /v1/shared                             session or app token      -> the nests shared with this account
//   DELETE /v1/shared/{nestId}                    session + Origin          -> leave (as if the owner removed you)
//   GET    /invite?t=pni_…                        browser page              -> "Join Home nest?" / where it stands
//   POST   /v1/invite/request {t}                 session + Origin          -> asks to join, once; the owner is emailed
//
// The nest hears about every change: a member's access and folders (PATCH /api/relay/people/{acc}), a removal (DELETE),
// and the people waiting for the owner's answer (PUT /api/relay/requests). A change it could not be told is kept with
// synced = 0 and the hourly cron tells it later (retryFriends).

import { audit } from "../audit";
import type { Ctx } from "../context";
import { B32_LOWER, newId, randomBase32 } from "../crypto";
import {
  ACCOUNT_ID_RE,
  cleanAccess,
  cleanFolders,
  dottedEmail,
  type FriendAccess,
  foldersJson,
  INVITE_ID_RE,
  INVITE_TOKEN_RE,
  iso,
  NEST_ID_RE,
  normaliseEmail,
  parseFolders,
  personName,
} from "../formats";
import { HttpError, htmlResponse, json, noContent, redirect } from "../http";
import { friendInEmail, inviteEmail, joinRequestEmail, reserveMail, sendLater, type ShareDetail } from "../mail";
import { type AdminTarget, adminCall, markReached, NestOffline } from "../nestadmin";
import { invitePage, type InvitePageView } from "../pages";
import { DAY, enforce, HOUR } from "../ratelimit";
import { jsonBody } from "../request";
import { loadBrowserSession, requireBrowser, requireBrowserOrApp, requireRecent } from "../sessions";
import type { NestRow } from "./account";
import { friendFeatures, obj, ownServer, relayUrl } from "./servers";

/** Friends per nest: members plus open invites (decided 2026-10-21). */
export const MAX_FRIENDS = 10;
/** An invite link works this long; asking to join must happen within it. */
export const INVITE_LIFETIME = 7 * DAY;
/** An invite someone asked to join with waits this long after it ended for the owner's answer, then it goes. */
export const REQUEST_KEEP = 30 * DAY;
const INVITES_PER_DAY = 20;
const REQUESTS_PER_HOUR = 20;
/** The nest keeps at most this many people waiting (PUT /api/relay/requests). */
const MAX_REQUESTS_TO_NEST = 20;
/** Member rows the cron tells their nest about in one sweep. */
const SYNC_PER_SWEEP = 50;

export interface MemberRow {
  nest_id: string;
  account_id: string;
  access: FriendAccess;
  folders: string | null;
  added_at: number;
  synced: number;
  removed_at: number | null;
}

export interface InviteRow {
  id: string;
  nest_id: string;
  token: string;
  email: string | null;
  access: FriendAccess;
  folders: string | null;
  created_by: string;
  created_at: number;
  expires_at: number;
  requested_by: string | null;
  requested_at: number | null;
  decided_at: number | null;
  decision: "in" | "declined" | null;
  cancelled_at: number | null;
}

interface AccountRow {
  id: string;
  email: string;
  username: string | null;
}

async function accountOf(ctx: Ctx, id: string): Promise<AccountRow | null> {
  return ctx.env.DB.prepare("SELECT id, email, username FROM accounts WHERE id = ?1").bind(id).first<AccountRow>();
}

/** The address of an invite: what the owner copies or Pairnets emails. */
export function inviteLink(ctx: Ctx, token: string): string {
  return `${ctx.env.PUBLIC_ORIGIN}/invite?t=${token}`;
}

/** An invite that still holds a place: not cancelled or decided, and either still working or asked with and waiting. */
const OPEN_INVITE = "cancelled_at IS NULL AND decided_at IS NULL AND (expires_at > ?2 OR (requested_by IS NOT NULL AND expires_at > ?3))";

/** Members plus open invites of a nest: what the 10-friend limit counts. Binds ?1 nest, ?2 now, ?3 now - REQUEST_KEEP. */
const FRIEND_COUNT = `(SELECT COUNT(*) FROM nest_members WHERE nest_id = ?1 AND removed_at IS NULL)
  + (SELECT COUNT(*) FROM nest_invites WHERE nest_id = ?1 AND ${OPEN_INVITE})`;

async function friendCount(ctx: Ctx, nestId: string): Promise<number> {
  const r = await ctx.env.DB.prepare(`SELECT ${FRIEND_COUNT} AS n`).bind(nestId, ctx.now, ctx.now - REQUEST_KEEP).first<{ n: number }>();
  return r?.n ?? 0;
}

function tooManyFriends(): HttpError {
  return new HttpError(409, "too_many_friends");
}

/** "Photos, School" or "Whole nest", and "view only" or "can change": the words the pages use. */
export function foldersWords(folders: string[] | null): string {
  return folders === null ? "Whole nest" : folders.join(", ");
}

export function accessWords(access: FriendAccess): string {
  return access === "view" ? "view only" : "can change";
}

// ------------------------------------------------------------------ the nest's side

/**
 * What the nest says about friends (a live status call): whether it can have them, and whether it is encrypted.
 * Throws NestOffline when it cannot be reached or does not answer its status.
 */
export async function nestFriendFeatures(ctx: Ctx, n: AdminTarget & { status?: string }): Promise<{ friends: boolean; sealed: boolean }> {
  const r = await adminCall(ctx.env, ctx.now, n, "GET", "/api/relay/status");
  const st = r.status === 200 ? obj(r.json) : null;
  if (!st) throw new NestOffline();
  if (n.status === "pending") await markReached(ctx.env, ctx.now, n.id);
  return friendFeatures(st);
}

/** For inviting and changing: the nest is online and can have friends, else 409 nest_offline or nest_too_old. */
async function requireFriends(ctx: Ctx, n: AdminTarget & { status?: string }): Promise<{ sealed: boolean }> {
  let f;
  try {
    f = await nestFriendFeatures(ctx, n);
  } catch (e) {
    if (e instanceof NestOffline) throw new HttpError(409, "nest_offline", "Your nest is not connected right now. Inviting a friend needs it on.");
    throw e;
  }
  if (!f.friends) throw new HttpError(409, "nest_too_old", "Update your nest first. Friends need its newest version.");
  return f;
}

/**
 * Tells the nest who is waiting for the owner's answer (PUT /api/relay/requests), so the owner's apps can show them.
 * Best effort: an offline or older nest is told the next time something changes.
 */
export async function pushRequests(ctx: Ctx, n: AdminTarget): Promise<void> {
  const rows = await ctx.env.DB.prepare(
    `SELECT i.id, i.access, i.folders, i.requested_at, a.email, a.username FROM nest_invites i JOIN accounts a ON a.id = i.requested_by
       WHERE i.nest_id = ?1 AND i.decided_at IS NULL AND i.cancelled_at IS NULL ORDER BY i.requested_at, i.id LIMIT ?2`,
  )
    .bind(n.id, MAX_REQUESTS_TO_NEST)
    .all<{ id: string; access: FriendAccess; folders: string | null; requested_at: number; email: string; username: string | null }>();
  const requests = rows.results.map((r) => ({
    id: r.id,
    name: personName(r),
    email: r.email,
    access: r.access,
    folders: parseFolders(r.folders),
    at: iso(r.requested_at),
  }));
  try {
    await adminCall(ctx.env, ctx.now, n, "PUT", "/api/relay/requests", { requests });
  } catch (e) {
    if (!(e instanceof NestOffline)) console.error("friends: telling the nest who is waiting failed", e instanceof Error ? e.message : "unknown");
  }
}

/**
 * Tells the nest about one member row: a removal (DELETE /api/relay/people/{acc}) or its access and folders (PATCH).
 * Returns true when that is settled: the nest did it, or refused for good (then there is nothing more to tell it; an
 * older nest has no friends' computers, and a sealed nest refuses folders). False when it should be tried again.
 */
export async function syncMember(ctx: Ctx, n: AdminTarget, m: MemberRow): Promise<boolean> {
  const path = `/api/relay/people/${encodeURIComponent(m.account_id)}`;
  let r;
  try {
    r = m.removed_at !== null
      ? await adminCall(ctx.env, ctx.now, n, "DELETE", path)
      : await adminCall(ctx.env, ctx.now, n, "PATCH", path, { access: m.access, folders: parseFolders(m.folders) });
  } catch (e) {
    if (e instanceof NestOffline) return false;
    throw e;
  }
  if (r.status >= 500) return false;
  if (r.status !== 200) await audit(ctx, "friend_sync_refused", null, m.nest_id, { status: r.status, removed: m.removed_at !== null });
  const db = ctx.env.DB;
  if (m.removed_at !== null) {
    await db.prepare("DELETE FROM nest_members WHERE nest_id = ?1 AND account_id = ?2 AND removed_at IS NOT NULL").bind(m.nest_id, m.account_id).run();
  } else {
    // Only if nothing changed meanwhile (a later change keeps synced = 0 and is sent on its own).
    await db
      .prepare("UPDATE nest_members SET synced = 1 WHERE nest_id = ?1 AND account_id = ?2 AND removed_at IS NULL AND access = ?3 AND folders IS ?4")
      .bind(m.nest_id, m.account_id, m.access, m.folders)
      .run();
  }
  return true;
}

/** The hourly cron: member rows whose nest has not heard of a change or a removal yet. Returns how many were settled. */
export async function retryFriends(ctx: Ctx): Promise<number> {
  const rows = await ctx.env.DB.prepare(
    `SELECT m.*, n.key_version FROM nest_members m JOIN nests n ON n.id = m.nest_id AND n.mode = 'relay' AND n.status IN ('pending', 'active')
       WHERE m.synced = 0 ORDER BY m.added_at, m.account_id LIMIT ?1`,
  )
    .bind(SYNC_PER_SWEEP)
    .all<MemberRow & { key_version: number }>();
  let settled = 0;
  for (const m of rows.results) {
    try {
      if (await syncMember(ctx, { id: m.nest_id, key_version: m.key_version }, m)) settled++;
    } catch (e) {
      console.error("friends: telling a nest about a friend failed", e instanceof Error ? e.message : "unknown");
    }
  }
  return settled;
}

/** Marks a member removed and tells the nest at once; the cron retries when the nest is offline. */
async function removeMember(ctx: Ctx, n: AdminTarget, accountId: string): Promise<MemberRow | null> {
  const db = ctx.env.DB;
  const row = await db
    .prepare("UPDATE nest_members SET removed_at = COALESCE(removed_at, ?3), synced = 0 WHERE nest_id = ?1 AND account_id = ?2 RETURNING *")
    .bind(n.id, accountId, ctx.now)
    .first<MemberRow>();
  if (row) await syncMember(ctx, n, row);
  return row;
}

// ------------------------------------------------------------------ the owner's routes

function memberJson(m: MemberRow, a: AccountRow | null): Record<string, unknown> {
  return {
    accountId: m.account_id,
    name: a ? personName(a) : "Someone",
    email: a?.email ?? null,
    access: m.access,
    folders: parseFolders(m.folders),
    addedAt: iso(m.added_at),
    synced: m.synced === 1,
  };
}

export interface PeopleOfNest {
  members: { row: MemberRow; account: AccountRow | null }[];
  invites: { row: InviteRow; requester: AccountRow | null }[];
}

/** A nest's members (not removed) and open invites, with the people behind them: GET …/people and the account page. */
export async function peopleOf(ctx: Ctx, nestId: string): Promise<PeopleOfNest> {
  const db = ctx.env.DB;
  const [members, invites] = await db.batch([
    db
      .prepare(
        `SELECT m.*, a.email AS a_email, a.username AS a_username FROM nest_members m LEFT JOIN accounts a ON a.id = m.account_id
           WHERE m.nest_id = ?1 AND m.removed_at IS NULL ORDER BY m.added_at, m.account_id`,
      )
      .bind(nestId),
    db
      .prepare(
        `SELECT i.*, a.email AS a_email, a.username AS a_username FROM nest_invites i LEFT JOIN accounts a ON a.id = i.requested_by
           WHERE i.nest_id = ?1 AND ${OPEN_INVITE} ORDER BY i.created_at, i.id`,
      )
      .bind(nestId, ctx.now, ctx.now - REQUEST_KEEP),
  ]);
  type Joined = { a_email: string | null; a_username: string | null };
  const who = (id: string | null, r: Joined): AccountRow | null => (id && r.a_email ? { id, email: r.a_email, username: r.a_username } : null);
  return {
    members: (members.results as (MemberRow & Joined)[]).map((r) => ({ row: r, account: who(r.account_id, r) })),
    invites: (invites.results as (InviteRow & Joined)[]).map((r) => ({ row: r, requester: who(r.requested_by, r) })),
  };
}

// GET /v1/servers/{id}/people
export async function listPeopleRoute(ctx: Ctx, nestId: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  const n = await ownServer(ctx, s.accountId, nestId);
  const p = await peopleOf(ctx, n.id);
  return json(200, {
    members: p.members.map((m) => memberJson(m.row, m.account)),
    invites: p.invites.map(({ row: i, requester }) => ({
      id: i.id,
      link: inviteLink(ctx, i.token),
      email: i.email,
      access: i.access,
      folders: parseFolders(i.folders),
      createdAt: iso(i.created_at),
      expiresAt: iso(i.expires_at),
      status: i.requested_by ? "requested" : "open",
      requester: requester ? { accountId: requester.id, name: personName(requester), email: requester.email, at: iso(i.requested_at) } : null,
    })),
    max: MAX_FRIENDS,
  });
}

/** `access` and `folders` from a body, or 400 bad_access. */
function shareFrom(b: Record<string, unknown>): { access: FriendAccess; folders: string[] | null } {
  const access = cleanAccess(b.access);
  const folders = cleanFolders(b.folders);
  if (access === null || folders === "invalid") throw new HttpError(400, "bad_access");
  return { access, folders };
}

// POST /v1/servers/{id}/invites {access, folders, email?}
export async function createInviteRoute(ctx: Ctx, nestId: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  const n = await ownServer(ctx, s.accountId, nestId);
  const b = jsonBody(ctx);
  const { access, folders } = shareFrom(b);
  let email: string | null = null;
  if (b.email !== undefined && b.email !== null && b.email !== "") {
    email = normaliseEmail(b.email);
    if (!email) throw new HttpError(400, "bad_request", "That isn't an email address. Check it, or copy the link instead.");
  }
  const owner = await accountOf(ctx, s.accountId);
  if (!owner) throw new HttpError(401, "unauthorized");
  if (email === owner.email) throw new HttpError(409, "own_nest", "That is your own address. Type your friend's.");
  if ((await friendCount(ctx, n.id)) >= MAX_FRIENDS) throw tooManyFriends();
  await enforce(ctx, "invite-create", s.accountId, INVITES_PER_DAY, DAY);
  const { sealed } = await requireFriends(ctx, n);
  if (sealed && folders) throw new HttpError(400, "sealed_whole_only");

  const emailed = email !== null && (await reserveMail(ctx, "invite"));
  const id = newId("inv");
  const token = `pni_${randomBase32(32, B32_LOWER)}`;
  const expires = ctx.now + INVITE_LIFETIME;
  // The limit is checked again in the insert itself, so two invites made at once cannot both take the last place.
  const r = await ctx.env.DB.prepare(
    `INSERT INTO nest_invites (id, nest_id, token, email, access, folders, created_by, created_at, expires_at)
       SELECT ?4, ?1, ?5, ?6, ?7, ?8, ?9, ?2, ?10 WHERE (${FRIEND_COUNT}) < ?11`,
  )
    .bind(n.id, ctx.now, ctx.now - REQUEST_KEEP, id, token, emailed ? email : null, access, foldersJson(folders), s.accountId, expires, MAX_FRIENDS)
    .run();
  if (r.meta.changes !== 1) throw tooManyFriends();
  await audit(ctx, "invite_created", s.accountId, n.id, { access, folders: folders?.length ?? null, emailed, mailSkipped: email !== null && !emailed });
  const link = inviteLink(ctx, token);
  if (emailed) sendLater(ctx, inviteEmail(email!, { name: personName(owner), email: owner.email }, { nest: n.label, folders, access }, link, expires));
  return json(201, { id, link, expiresAt: iso(expires), emailed });
}

async function ownInvite(ctx: Ctx, nestId: string, invId: string): Promise<InviteRow> {
  if (!INVITE_ID_RE.test(invId)) throw new HttpError(404, "not_found");
  const i = await ctx.env.DB.prepare("SELECT * FROM nest_invites WHERE id = ?1 AND nest_id = ?2").bind(invId, nestId).first<InviteRow>();
  if (!i) throw new HttpError(404, "not_found");
  return i;
}

/** An invite the owner can answer: someone asked to join with it, and it is neither cancelled nor answered. */
function requireWaiting(i: InviteRow): void {
  if (i.cancelled_at !== null) throw new HttpError(409, "cancelled");
  if (i.decided_at !== null) throw new HttpError(409, "already_decided", "This request was already answered.");
  if (!i.requested_by) throw new HttpError(400, "bad_request", "Nobody has asked to join with this invite yet.");
}

// POST /v1/servers/{id}/invites/{inv}/approve
export async function approveInviteRoute(ctx: Ctx, nestId: string, invId: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  // Letting someone in gives their computers a way into the nest: only right after a sign-in (declining is always fine).
  requireRecent(ctx, s);
  const n = await ownServer(ctx, s.accountId, nestId);
  const i = await ownInvite(ctx, n.id, invId);
  requireWaiting(i);
  const friend = await accountOf(ctx, i.requested_by!);
  if (!friend) throw new HttpError(404, "not_found", "That account no longer exists.");
  const db = ctx.env.DB;
  const before = await db.prepare("SELECT * FROM nest_members WHERE nest_id = ?1 AND account_id = ?2").bind(n.id, friend.id).first<MemberRow>();
  // Answered and let in, in one batch: the member row is written only if this request was still waiting.
  const [flip] = await db.batch([
    db
      .prepare(
        "UPDATE nest_invites SET decided_at = ?2, decision = 'in' WHERE id = ?1 AND decided_at IS NULL AND cancelled_at IS NULL AND requested_by IS NOT NULL",
      )
      .bind(i.id, ctx.now),
    // Someone let in again while the nest still has their computers (a removal not yet heard, or a second invite):
    // synced = 0, so the nest is told the new access below or by the cron.
    db
      .prepare(
        `INSERT INTO nest_members (nest_id, account_id, access, folders, added_at, synced, removed_at)
           SELECT ?1, ?2, ?3, ?4, ?5, 1, NULL WHERE EXISTS (SELECT 1 FROM nest_invites WHERE id = ?6 AND decided_at = ?5 AND decision = 'in')
           ON CONFLICT (nest_id, account_id) DO UPDATE SET access = excluded.access, folders = excluded.folders, removed_at = NULL, synced = 0`,
      )
      .bind(n.id, friend.id, i.access, i.folders, ctx.now, i.id),
  ]);
  if (flip.meta.changes !== 1) throw new HttpError(409, "already_decided", "This request was already answered.");
  const member = await db.prepare("SELECT * FROM nest_members WHERE nest_id = ?1 AND account_id = ?2").bind(n.id, friend.id).first<MemberRow>();
  if (before && member) await syncMember(ctx, n, member);
  await audit(ctx, "invite_approved", s.accountId, n.id, { friend: friend.id, access: i.access });
  const owner = await accountOf(ctx, s.accountId);
  const share: ShareDetail = { nest: n.label, folders: parseFolders(i.folders), access: i.access };
  if (await reserveMail(ctx, "invite")) sendLater(ctx, friendInEmail(friend.email, owner ? personName(owner) : "The owner", share));
  else await audit(ctx, "invite_mail_skipped", s.accountId, n.id);
  ctx.defer(pushRequests(ctx, n));
  return json(200, { status: "in", member: member ? memberJson(member, friend) : null });
}

// POST /v1/servers/{id}/invites/{inv}/decline
export async function declineInviteRoute(ctx: Ctx, nestId: string, invId: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  const n = await ownServer(ctx, s.accountId, nestId);
  const i = await ownInvite(ctx, n.id, invId);
  requireWaiting(i);
  const r = await ctx.env.DB.prepare(
    "UPDATE nest_invites SET decided_at = ?2, decision = 'declined' WHERE id = ?1 AND decided_at IS NULL AND cancelled_at IS NULL AND requested_by IS NOT NULL",
  )
    .bind(i.id, ctx.now)
    .run();
  if (r.meta.changes !== 1) throw new HttpError(409, "already_decided", "This request was already answered.");
  await audit(ctx, "invite_declined", s.accountId, n.id, { friend: i.requested_by });
  ctx.defer(pushRequests(ctx, n));
  return json(200, { status: "declined" });
}

// DELETE /v1/servers/{id}/invites/{inv}
export async function cancelInviteRoute(ctx: Ctx, nestId: string, invId: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  const n = await ownServer(ctx, s.accountId, nestId);
  const i = await ownInvite(ctx, n.id, invId);
  if (i.cancelled_at !== null) return noContent();
  if (i.decided_at !== null) throw new HttpError(409, "already_decided", "This invite was already answered.");
  await ctx.env.DB.prepare("UPDATE nest_invites SET cancelled_at = ?2 WHERE id = ?1 AND cancelled_at IS NULL AND decided_at IS NULL").bind(i.id, ctx.now).run();
  await audit(ctx, "invite_cancelled", s.accountId, n.id);
  if (i.requested_by) ctx.defer(pushRequests(ctx, n));
  return noContent();
}

async function activeMember(ctx: Ctx, nestId: string, accountId: string): Promise<MemberRow> {
  if (!ACCOUNT_ID_RE.test(accountId)) throw new HttpError(404, "not_found");
  const m = await ctx.env.DB.prepare("SELECT * FROM nest_members WHERE nest_id = ?1 AND account_id = ?2 AND removed_at IS NULL")
    .bind(nestId, accountId)
    .first<MemberRow>();
  if (!m) throw new HttpError(404, "not_found");
  return m;
}

// PATCH /v1/servers/{id}/people/{accountId} {access?, folders?}: a field left out keeps its value; folders null = whole.
export async function changePersonRoute(ctx: Ctx, nestId: string, accountId: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  const n = await ownServer(ctx, s.accountId, nestId);
  const m = await activeMember(ctx, n.id, accountId);
  const b = jsonBody(ctx);
  const access = "access" in b ? cleanAccess(b.access) : m.access;
  const folders = "folders" in b ? cleanFolders(b.folders) : parseFolders(m.folders);
  if (access === null || folders === "invalid") throw new HttpError(400, "bad_access");
  // The nest first: a sealed nest refuses folders, and then nothing changes here.
  let synced = 1;
  try {
    const r = await adminCall(ctx.env, ctx.now, n, "PATCH", `/api/relay/people/${encodeURIComponent(m.account_id)}`, { access, folders });
    const error = obj(r.json)?.error;
    if (r.status === 400 && error === "sealed_whole_only") throw new HttpError(400, "sealed_whole_only");
    if (r.status === 400) throw new HttpError(400, "bad_access");
    if (r.status === 404 || r.status === 405) throw new HttpError(409, "nest_too_old", "Update your nest first. Friends need its newest version.");
    if (r.status !== 200) synced = 0;
  } catch (e) {
    if (!(e instanceof NestOffline)) throw e;
    synced = 0; // the cron tells it later
  }
  const db = ctx.env.DB;
  await db
    .prepare("UPDATE nest_members SET access = ?3, folders = ?4, synced = ?5 WHERE nest_id = ?1 AND account_id = ?2 AND removed_at IS NULL")
    .bind(n.id, m.account_id, access, foldersJson(folders), synced)
    .run();
  await audit(ctx, "friend_changed", s.accountId, n.id, { friend: m.account_id, access, folders: folders?.length ?? null, synced: synced === 1 });
  const friend = await accountOf(ctx, m.account_id);
  return json(200, memberJson({ ...m, access, folders: foldersJson(folders), synced }, friend));
}

// DELETE /v1/servers/{id}/people/{accountId}
export async function removePersonRoute(ctx: Ctx, nestId: string, accountId: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  const n = await ownServer(ctx, s.accountId, nestId);
  if (!ACCOUNT_ID_RE.test(accountId)) throw new HttpError(404, "not_found");
  const removed = await removeMember(ctx, n, accountId);
  if (!removed) throw new HttpError(404, "not_found");
  await audit(ctx, "friend_removed", s.accountId, n.id, { friend: accountId });
  return noContent();
}

// ------------------------------------------------------------------ the friend's routes

export interface SharedNest {
  nest: NestRow;
  member: MemberRow;
  /** The owner's name (username, or the part of the email before the @). */
  sharedBy: string;
}

/** The nests this account was let in to (relayed, in use), oldest first. */
export async function sharedNestsOf(ctx: Ctx, accountId: string): Promise<SharedNest[]> {
  const r = await ctx.env.DB.prepare(
    `SELECT n.*, m.access AS m_access, m.folders AS m_folders, m.added_at AS m_added_at, m.synced AS m_synced, a.email AS o_email, a.username AS o_username
       FROM nest_members m JOIN nests n ON n.id = m.nest_id JOIN accounts a ON a.id = n.account_id
       WHERE m.account_id = ?1 AND m.removed_at IS NULL AND n.mode = 'relay' AND n.status IN ('pending', 'active')
       ORDER BY m.added_at, n.id`,
  )
    .bind(accountId)
    .all<NestRow & { m_access: FriendAccess; m_folders: string | null; m_added_at: number; m_synced: number; o_email: string; o_username: string | null }>();
  return r.results.map((x) => ({
    nest: x,
    member: { nest_id: x.id, account_id: accountId, access: x.m_access, folders: x.m_folders, added_at: x.m_added_at, synced: x.m_synced, removed_at: null },
    sharedBy: personName({ email: x.o_email, username: x.o_username }),
  }));
}

/** This account's membership of a nest (not removed), or null. */
export async function memberOf(ctx: Ctx, nestId: string, accountId: string): Promise<MemberRow | null> {
  return ctx.env.DB.prepare("SELECT * FROM nest_members WHERE nest_id = ?1 AND account_id = ?2 AND removed_at IS NULL")
    .bind(nestId, accountId)
    .first<MemberRow>();
}

// GET /v1/shared (session or app token)
export async function listSharedRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowserOrApp(ctx);
  const shared = await sharedNestsOf(ctx, s.accountId);
  return json(200, {
    nests: shared.map((x) => ({
      id: x.nest.id,
      label: x.nest.label,
      sharedBy: x.sharedBy,
      access: x.member.access,
      folders: parseFolders(x.member.folders),
      addedAt: iso(x.member.added_at),
      serverUrl: relayUrl(ctx, x.nest.id),
    })),
  });
}

// DELETE /v1/shared/{nestId}: leaving is the same as the owner removing you.
export async function leaveSharedRoute(ctx: Ctx, nestId: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  if (!NEST_ID_RE.test(nestId)) throw new HttpError(404, "not_found");
  const n = await ctx.env.DB.prepare("SELECT id, key_version FROM nests WHERE id = ?1 AND mode = 'relay'").bind(nestId).first<AdminTarget>();
  const m = n ? await memberOf(ctx, n.id, s.accountId) : null;
  if (!n || !m) throw new HttpError(404, "not_found");
  await removeMember(ctx, n, s.accountId);
  await audit(ctx, "friend_left", s.accountId, n.id);
  return noContent();
}

type InviteState = "unknown" | "own" | "open" | "waiting" | "in" | "declined" | "cancelled" | "expired" | "used" | "member";

interface InviteLookup {
  state: InviteState;
  invite: InviteRow | null;
  nest: NestRow | null;
  owner: AccountRow | null;
}

/** Where an invite stands for the account looking at it (the page and the request answer the same). */
async function lookUpInvite(ctx: Ctx, token: unknown, accountId: string): Promise<InviteLookup> {
  const none: InviteLookup = { state: "unknown", invite: null, nest: null, owner: null };
  if (typeof token !== "string" || !INVITE_TOKEN_RE.test(token)) return none;
  const invite = await ctx.env.DB.prepare("SELECT * FROM nest_invites WHERE token = ?1").bind(token).first<InviteRow>();
  const nest = invite
    ? await ctx.env.DB.prepare("SELECT * FROM nests WHERE id = ?1 AND mode = 'relay' AND status IN ('pending', 'active')").bind(invite.nest_id).first<NestRow>()
    : null;
  const owner = nest ? await accountOf(ctx, nest.account_id) : null;
  if (!invite || !nest || !owner) return none;
  const at = (state: InviteState): InviteLookup => ({ state, invite, nest, owner });
  if (nest.account_id === accountId) return at("own");
  if (invite.requested_by === accountId) {
    // Let in, and still in (someone removed since, or who left, reads it as taken back).
    if (invite.decision === "in") return at((await memberOf(ctx, nest.id, accountId)) ? "in" : "cancelled");
    if (invite.decision === "declined") return at("declined");
    return at(invite.cancelled_at !== null ? "cancelled" : "waiting");
  }
  if (invite.requested_by !== null) return at("used");
  if (invite.cancelled_at !== null) return at("cancelled");
  if (invite.expires_at <= ctx.now) return at("expired");
  if (await memberOf(ctx, nest.id, accountId)) return at("member");
  return at("open");
}

// GET /invite?t=pni_…
export async function invitePageRoute(ctx: Ctx): Promise<Response> {
  const t = ctx.url.searchParams.get("t");
  const s = await loadBrowserSession(ctx);
  if (!s) {
    const next = t && INVITE_TOKEN_RE.test(t) ? `/invite?t=${t}` : "/account";
    return redirect(`/login?next=${encodeURIComponent(next)}`);
  }
  const me = await accountOf(ctx, s.accountId);
  if (!me) return redirect("/login?next=/account");
  const v = await lookUpInvite(ctx, t, s.accountId);
  const view: InvitePageView = {
    state: v.state,
    token: v.invite?.token ?? "",
    nestId: v.nest?.id ?? "",
    nest: v.nest?.label ?? "",
    owner: v.owner ? personName(v.owner) : "",
    ownerEmail: v.owner ? dottedEmail(v.owner.email) : "",
    folders: v.invite ? parseFolders(v.invite.folders) : null,
    access: v.invite?.access ?? "view",
    me: personName(me),
    myEmail: me.email,
  };
  return htmlResponse(v.state === "unknown" ? 404 : 200, invitePage(view));
}

// POST /v1/invite/request {t}
export async function inviteRequestRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  const b = jsonBody(ctx);
  if (typeof b.t !== "string") throw new HttpError(400, "bad_request");
  await enforce(ctx, "invite-request", s.accountId, REQUESTS_PER_HOUR, HOUR);
  let v = await lookUpInvite(ctx, b.t, s.accountId);
  if (v.state === "open") {
    // Once, by one account: whoever asks first holds it.
    const r = await ctx.env.DB.prepare(
      `UPDATE nest_invites SET requested_by = ?2, requested_at = ?3
         WHERE id = ?1 AND requested_by IS NULL AND cancelled_at IS NULL AND decided_at IS NULL AND expires_at > ?3`,
    )
      .bind(v.invite!.id, s.accountId, ctx.now)
      .run();
    if (r.meta.changes === 1) {
      const nest = v.nest!;
      const me = await accountOf(ctx, s.accountId);
      await audit(ctx, "invite_requested", s.accountId, nest.id);
      const share: ShareDetail = { nest: nest.label, folders: parseFolders(v.invite!.folders), access: v.invite!.access };
      if (me && (await reserveMail(ctx, "notice"))) sendLater(ctx, joinRequestEmail(v.owner!.email, { name: personName(me), email: me.email }, share, nest.id));
      else await audit(ctx, "notice_skipped", nest.account_id, nest.id, { kind: "join_request" });
      ctx.defer(pushRequests(ctx, nest));
      return json(200, { status: "requested", nest: nest.label, owner: personName(v.owner!) });
    }
    v = await lookUpInvite(ctx, b.t, s.accountId);
  }
  const owner = v.owner ? personName(v.owner) : "the owner";
  switch (v.state) {
    case "waiting":
      return json(200, { status: "requested", nest: v.nest!.label, owner });
    case "in":
      return json(200, { status: "in", nest: v.nest!.label, owner });
    case "own":
      throw new HttpError(409, "own_nest", "This is your own nest. Send the link to the friend you want to invite.");
    case "member":
      throw new HttpError(409, "already_member");
    case "declined":
      throw new HttpError(409, "already_decided", `${owner} said no to this invite.`);
    case "used":
      throw new HttpError(409, "used", `Someone else already used this invite. Ask ${owner} for a new one.`);
    case "cancelled":
      throw new HttpError(409, "cancelled");
    case "expired":
      throw new HttpError(400, "expired", `This invite has run out. Ask ${owner} for a new one.`);
    default:
      throw new HttpError(404, "not_found", "This invite link doesn't work. Check that the whole link was copied.");
  }
}
