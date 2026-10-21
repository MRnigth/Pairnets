// Linked nests and nests shared with an account (RELAY.md section 11; docs/DECISIONS.md, "4 nests per account, and
// linked nests"). Both ride on the friend invites of friends.ts: a link alone never lets anyone in, and the owner lets
// each one in.
//
// A: an invite of kind 'nest'. The friend asks to join with one of their own nests. Once let in, the friend is a member
//    as usual, and their nest gets one key on the owner's nest (POST /api/relay/devices with the member's access, folders
//    and person, so the nest's access gate and removal work as for a person). It then sets the link up on the joining
//    nest (POST /api/relay/links), whose link worker keeps the shared folders in step. The key goes from one nest to the
//    other in one go and is never stored here. Changing the member's access is passed on (PATCH), and removing them, or
//    either owner unlinking, ends the link on the joining nest (DELETE).
// B: a friend's own nests hear which nests are shared with the account (PUT /api/relay/offers) whenever that changes,
//    so each of the friend's computers can offer "Add it".
//
//   GET    /v1/links          session          -> {outgoing, incoming}: links from and to this account's nests
//   DELETE /v1/links/{id}     session + Origin -> unlink (either owner)

import { audit } from "../audit";
import type { Ctx } from "../context";
import { newId } from "../crypto";
import { type FriendAccess, iso, parseFolders, personName } from "../formats";
import { HttpError, json, noContent } from "../http";
import { type AdminTarget, adminCall, NestOffline } from "../nestadmin";
import type { LinkView } from "../pages";
import { requireBrowser } from "../sessions";
import type { NestRow } from "./account";
import { type InviteRow, memberOf, type MemberRow, nestFriendFeatures, removeMemberOf, sharedNestsOf } from "./friends";
import { obj, serversOf } from "./servers";

/** A nest can be linked to at most this many nests (the nest itself refuses a fifth too). */
export const MAX_LINKS = 4;

const LINK_ID_RE = /^lnk_[0-9a-hjkmnp-tv-z]{26}$/;

/** Pending links tried again per hourly sweep. */
const LINKS_PER_SWEEP = 20;

export interface LinkRow {
  id: string;
  from_nest: string;
  to_nest: string;
  account_id: string;
  device_id: string | null;
  status: "pending" | "active";
  created_at: number;
}

interface NestAndOwner extends NestRow {
  o_email: string;
  o_username: string | null;
}

async function nestWithOwner(ctx: Ctx, nestId: string): Promise<NestAndOwner | null> {
  return ctx.env.DB.prepare(
    `SELECT n.*, a.email AS o_email, a.username AS o_username FROM nests n JOIN accounts a ON a.id = n.account_id
       WHERE n.id = ?1 AND n.mode = 'relay' AND n.status IN ('pending', 'active')`,
  )
    .bind(nestId)
    .first<NestAndOwner>();
}

function ownerName(n: NestAndOwner): string {
  return personName({ email: n.o_email, username: n.o_username });
}

// ------------------------------------------------------------------ inviting a nest

/**
 * What the owner's nest says about links: the top-level folders that came in through links (never shared on), or null
 * when the nest is too old to have links. Throws NestOffline.
 */
export async function linkedInFolders(ctx: Ctx, n: AdminTarget): Promise<string[] | null> {
  const r = await adminCall(ctx.env, ctx.now, n, "GET", "/api/relay/links");
  if (r.status === 404 || r.status === 405) return null;
  const body = obj(r.json);
  if (r.status !== 200 || !body || !Array.isArray(body.linkedIn)) throw new NestOffline();
  return body.linkedIn.filter((f): f is string => typeof f === "string");
}

/**
 * Checks for an invite of kind 'nest' (friends.ts createInviteRoute): not an encrypted nest, a nest new enough for links,
 * room for one more link, and nothing shared on that came in through a link.
 */
export async function checkNestInvite(ctx: Ctx, n: AdminTarget, folders: string[] | null, sealed: boolean): Promise<void> {
  if (sealed) throw new HttpError(409, "sealed_cant_link", "An encrypted nest can't be linked with another nest. Invite a person instead.");
  const links = await ctx.env.DB.prepare("SELECT COUNT(*) AS n FROM nest_links WHERE from_nest = ?1").bind(n.id).first<{ n: number }>();
  if ((links?.n ?? 0) >= MAX_LINKS) throw new HttpError(409, "too_many_links", `A nest can be linked with up to ${MAX_LINKS} nests.`);
  let linkedIn: string[] | null;
  try {
    linkedIn = await linkedInFolders(ctx, n);
  } catch (e) {
    if (e instanceof NestOffline) throw new HttpError(409, "nest_offline", "Your nest is not connected right now. Inviting a nest needs it on.");
    throw e;
  }
  if (linkedIn === null) throw new HttpError(409, "nest_too_old", "Update your nest first. Linked nests need its newest version.");
  const passedOn = linkedIn.filter((f) => folders === null || folders.some((x) => x.toLowerCase() === f.toLowerCase()));
  if (passedOn.length > 0) {
    throw new HttpError(400, "linked_in", `${passedOn.join(", ")} came from another nest, so it can't be shared on. Choose your own folders.`);
  }
}

/** The friend's nests that could join (the invite page's choice): their own relayed nests, not the inviting one. */
export async function joinableNests(ctx: Ctx, accountId: string, invitingNest: string): Promise<{ id: string; label: string }[]> {
  return (await serversOf(ctx, accountId)).filter((n) => n.id !== invitingNest).map((n) => ({ id: n.id, label: n.label }));
}

/** For a request with an invite of kind 'nest': the chosen nest is one of the requester's own, else 400 bad_nest. */
export async function requireJoinable(ctx: Ctx, accountId: string, invitingNest: string, nestId: unknown): Promise<string> {
  const mine = await joinableNests(ctx, accountId, invitingNest);
  const pick = mine.find((n) => n.id === nestId);
  if (!pick) throw new HttpError(400, "bad_nest", mine.length ? "Choose which of your nests should join." : "You need a nest of your own to link it. Set one up first.");
  return pick.id;
}

// ------------------------------------------------------------------ the link's life

/** Let in with an invite of kind 'nest' (friends.ts approveInviteRoute): the link row, then the key handed over. */
export async function startLink(ctx: Ctx, invite: InviteRow): Promise<LinkRow | null> {
  if (!invite.joining_nest || !invite.requested_by) return null;
  const id = newId("lnk");
  await ctx.env.DB.prepare(
    `INSERT INTO nest_links (id, from_nest, to_nest, account_id, device_id, status, created_at) VALUES (?1, ?2, ?3, ?4, NULL, 'pending', ?5)
       ON CONFLICT (from_nest, to_nest) DO NOTHING`,
  )
    .bind(id, invite.nest_id, invite.joining_nest, invite.requested_by, ctx.now)
    .run();
  const row = await ctx.env.DB.prepare("SELECT * FROM nest_links WHERE from_nest = ?1 AND to_nest = ?2").bind(invite.nest_id, invite.joining_nest).first<LinkRow>();
  if (row && row.status === "pending") await deliverLink(ctx, row);
  return row;
}

/**
 * Hands the key over: one key on the sharing nest for the joining nest, straight to the joining nest. Both nests must be
 * on; if the second step fails the key is removed again, so a key never lingers without a link. Returns true when the
 * link is active (or gone for good), false to try again later.
 */
export async function deliverLink(ctx: Ctx, link: LinkRow): Promise<boolean> {
  const db = ctx.env.DB;
  const from = await nestWithOwner(ctx, link.from_nest);
  const to = await nestWithOwner(ctx, link.to_nest);
  const member = from ? await memberOf(ctx, from.id, link.account_id) : null;
  if (!from || !to || !member || to.account_id !== link.account_id) {
    await db.prepare("DELETE FROM nest_links WHERE id = ?1").bind(link.id).run();
    return true;
  }
  let made: { id: string; key: string } | null = null;
  try {
    const f = await nestFriendFeatures(ctx, to);
    if (f.sealed) {
      await audit(ctx, "link_refused_sealed", link.account_id, to.id);
      await removeMemberOf(ctx, from, link.account_id);
      return true;
    }
    const minted = await adminCall(ctx.env, ctx.now, from, "POST", "/api/relay/devices", {
      name: `${to.label} (linked nest)`,
      system: "Pairnets nest",
      approvedBy: "a linked nest",
      access: member.access,
      folders: parseFolders(member.folders),
      person: `${ownerName(to)} · ${to.label}`,
      personId: link.account_id,
    });
    const d = minted.status === 200 ? obj(minted.json) : null;
    if (!d || typeof d.id !== "string" || typeof d.key !== "string" || d.key.length === 0) return false;
    made = { id: d.id, key: d.key };
    const r = await adminCall(ctx.env, ctx.now, to, "POST", "/api/relay/links", {
      id: link.id,
      remoteNestId: from.id,
      deviceId: made.id,
      key: made.key,
      access: member.access,
      folders: parseFolders(member.folders),
      ownerName: ownerName(from),
      nestName: from.label,
    });
    if (r.status !== 200) {
      await audit(ctx, "link_refused", link.account_id, to.id, { status: r.status, error: obj(r.json)?.error ?? null });
      await adminCall(ctx.env, ctx.now, from, "DELETE", `/api/relay/devices/${encodeURIComponent(made.id)}`);
      if (r.status >= 500) return false;
      await removeMemberOf(ctx, from, link.account_id); // refused for good (encrypted, too old, full): nothing was shared
      return true;
    }
  } catch (e) {
    if (!(e instanceof NestOffline)) throw e;
    if (made) {
      // The joining nest went away halfway: the key is taken back (best effort; the member's removal would too).
      try {
        await adminCall(ctx.env, ctx.now, from, "DELETE", `/api/relay/devices/${encodeURIComponent(made.id)}`);
      } catch {
        // The sharing nest is gone too: nothing was handed over, and the hourly sweep makes a new key.
      }
    }
    return false;
  }
  await db.prepare("UPDATE nest_links SET status = 'active', device_id = ?2 WHERE id = ?1").bind(link.id, made.id).run();
  await audit(ctx, "link_active", link.account_id, from.id, { to: to.id });
  return true;
}

/** The owner changed what a member may do: the joining nest of each of their links hears it (best effort). */
export async function changeLinks(ctx: Ctx, fromNest: string, accountId: string, access: FriendAccess, folders: string[] | null): Promise<void> {
  const rows = await ctx.env.DB.prepare("SELECT * FROM nest_links WHERE from_nest = ?1 AND account_id = ?2 AND status = 'active'").bind(fromNest, accountId).all<LinkRow>();
  for (const l of rows.results) {
    const to = await nestWithOwner(ctx, l.to_nest);
    if (!to) continue;
    try {
      await adminCall(ctx.env, ctx.now, to, "PATCH", `/api/relay/links/${l.id}`, { access, folders });
    } catch (e) {
      if (!(e instanceof NestOffline)) throw e;
    }
  }
}

/**
 * A member was removed or left (friends.ts removeMember): their links from that nest end. The key on the sharing nest goes
 * with the member's computers; the joining nest forgets its side (best effort: without the key it can't sync anyway).
 */
export async function endLinks(ctx: Ctx, fromNest: string, accountId: string): Promise<void> {
  const rows = await ctx.env.DB.prepare("SELECT * FROM nest_links WHERE from_nest = ?1 AND account_id = ?2").bind(fromNest, accountId).all<LinkRow>();
  for (const l of rows.results) {
    await ctx.env.DB.prepare("DELETE FROM nest_links WHERE id = ?1").bind(l.id).run();
    const to = await nestWithOwner(ctx, l.to_nest);
    if (!to || l.status !== "active") continue;
    try {
      await adminCall(ctx.env, ctx.now, to, "DELETE", `/api/relay/links/${l.id}`);
    } catch (e) {
      if (!(e instanceof NestOffline)) throw e;
    }
  }
}

/** The hourly cron: links let in while a nest was offline get their key now. Returns how many were settled. */
export async function retryLinks(ctx: Ctx): Promise<number> {
  const rows = await ctx.env.DB.prepare("SELECT * FROM nest_links WHERE status = 'pending' ORDER BY created_at LIMIT ?1").bind(LINKS_PER_SWEEP).all<LinkRow>();
  let settled = 0;
  for (const l of rows.results) {
    try {
      if (await deliverLink(ctx, l)) settled++;
    } catch (e) {
      console.error("links: handing a link over failed", e instanceof Error ? e.message : "unknown");
    }
  }
  return settled;
}

// ------------------------------------------------------------------ B: shared nests reach the friend's computers

/**
 * Tells every nest of `accountId` which nests are shared with that account (PUT /api/relay/offers, the whole list), so
 * the account's computers can offer "Add it". Best effort: an offline or older nest hears it at the next change.
 */
export async function pushOffers(ctx: Ctx, accountId: string): Promise<void> {
  // A nest that already comes in through a link is not offered again as a nest of its own.
  const linked = await ctx.env.DB.prepare("SELECT from_nest FROM nest_links WHERE account_id = ?1").bind(accountId).all<{ from_nest: string }>();
  const skip = new Set(linked.results.map((r) => r.from_nest));
  const shared = (await sharedNestsOf(ctx, accountId)).filter((x) => !skip.has(x.nest.id));
  const offers = shared.map((x) => ({
    nestId: x.nest.id,
    nestName: x.nest.label,
    ownerName: x.sharedBy,
    access: x.member.access,
    folders: parseFolders(x.member.folders),
  }));
  for (const n of await serversOf(ctx, accountId)) {
    try {
      await adminCall(ctx.env, ctx.now, n, "PUT", "/api/relay/offers", { offers });
    } catch (e) {
      if (!(e instanceof NestOffline)) console.error("links: telling a nest about shared nests failed", e instanceof Error ? e.message : "unknown");
    }
  }
}

// ------------------------------------------------------------------ routes

function linkJson(l: LinkRow, from: NestAndOwner | null, to: NestAndOwner | null, member: MemberRow | null): LinkView {
  return {
    id: l.id,
    status: l.status,
    from: { nestId: l.from_nest, nest: from?.label ?? "", owner: from ? ownerName(from) : "" },
    to: { nestId: l.to_nest, nest: to?.label ?? "", owner: to ? ownerName(to) : "" },
    access: member?.access ?? "view",
    folders: member ? parseFolders(member.folders) : null,
    createdAt: iso(l.created_at),
  };
}

/** The links from and to this account's nests, for the account page. */
export async function linksOfAccount(ctx: Ctx, accountId: string): Promise<{ outgoing: LinkView[]; incoming: LinkView[] }> {
  const rows = await ctx.env.DB.prepare(
    `SELECT l.* FROM nest_links l
       WHERE l.from_nest IN (SELECT id FROM nests WHERE account_id = ?1) OR l.to_nest IN (SELECT id FROM nests WHERE account_id = ?1)
       ORDER BY l.created_at, l.id`,
  )
    .bind(accountId)
    .all<LinkRow>();
  const outgoing: LinkView[] = [];
  const incoming: LinkView[] = [];
  for (const l of rows.results) {
    const from = await nestWithOwner(ctx, l.from_nest);
    const to = await nestWithOwner(ctx, l.to_nest);
    const member = from ? await memberOf(ctx, from.id, l.account_id) : null;
    (from?.account_id === accountId ? outgoing : incoming).push(linkJson(l, from, to, member));
  }
  return { outgoing, incoming };
}

// GET /v1/links
export async function listLinksRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  return json(200, await linksOfAccount(ctx, s.accountId));
}

// DELETE /v1/links/{id}: either owner. The same as the sharing owner removing that member.
export async function unlinkRoute(ctx: Ctx, id: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  if (!LINK_ID_RE.test(id)) throw new HttpError(404, "not_found");
  const l = await ctx.env.DB.prepare("SELECT * FROM nest_links WHERE id = ?1").bind(id).first<LinkRow>();
  const from = l ? await nestWithOwner(ctx, l.from_nest) : null;
  if (!l || !from || (from.account_id !== s.accountId && l.account_id !== s.accountId)) throw new HttpError(404, "not_found");
  await removeMemberOf(ctx, from, l.account_id);
  await audit(ctx, "link_ended", s.accountId, from.id, { to: l.to_nest });
  return noContent();
}
