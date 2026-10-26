// The beta program (designs/beta-channels/CONTRACT.md): an account signs up here for Public beta, Developer beta or both,
// agreeing to the beta terms. A sign-up is approved at once when BETA_AUTO_APPROVE is on (and the account was not removed
// by an admin); otherwise it waits for an admin on /admin/beta. What an account may get reaches its computers only
// through its own nests, which the service tells (ra1-signed, like a rename).
//
//   GET  /v1/beta                              session                 -> signed up?, terms, approved and waiting betas
//   POST /v1/beta/join {acceptTerms, termsVersion, channels} session + Origin -> signs up (or for one more beta, or new terms)
//   POST /v1/beta/leave                        session + Origin        -> leaves; its own computers go back to Off
//   PUT  /v1/servers/{id}/devices/{d}/beta     session + Origin        -> the nest sets that computer's choice
//   GET  /admin/beta                           admins only             -> who signed up, and who uses it (read live)
//   POST /admin/beta/{account}/{approve|remove|let-in}  admins + Origin -> approves, removes or lets back in
//
// Each change bumps the member's rev and is pushed (PUT /api/relay/beta) to the account's own active relay nests at
// once; a nest that missed it (offline) is told by the hourly cron. A nest from before this answers 404: it is asked
// again at most once a day. Nothing about the computers is stored here: /admin/beta asks the nests each time.

import { audit } from "../audit";
import type { Ctx } from "../context";
import { ACCOUNT_ID_RE, DEVICE_ID_RE, emailName, iso } from "../formats";
import { escapeHtml as e, HttpError, htmlResponse, json, redirect } from "../http";
import { adminCall, NestOffline } from "../nestadmin";
import { type BetaPageView, layout } from "../pages";
import { DAY, hit, peek } from "../ratelimit";
import { jsonBody } from "../request";
import { requireBrowser } from "../sessions";
import type { NestRow } from "./account";
import { adminNav, adminOnly } from "./crash";
import { obj, ownServer, serversOf } from "./servers";

/** The beta terms the page links to (/beta-terms); signing up agrees to this version. */
export const BETA_TERMS_VERSION = "2026-10-26";
export const BETA_CHANNELS = ["public-beta", "developer-beta"] as const;
export type BetaChannel = (typeof BETA_CHANNELS)[number];
/** What a computer may be set to: Off (= stable) or one of the betas. */
const CHOICES = ["off", ...BETA_CHANNELS] as const;
/** Nests the cron tells per run; the next hour takes the rest. */
export const BETA_PUSHES_PER_RUN = 50;
/** Members whose nests /admin/beta asks when it opens. */
export const BETA_ADMIN_MEMBERS = 100;

export interface BetaRow {
  account_id: string;
  joined_at: number;
  left_at: number | null;
  terms_version: string;
  public_allowed: number;
  developer_allowed: number;
  rev: number;
  public_requested_at: number | null;
  developer_requested_at: number | null;
  blocked_at: number | null;
}

const COLUMNS: Record<BetaChannel, { requested: "public_requested_at" | "developer_requested_at"; allowed: "public_allowed" | "developer_allowed" }> = {
  "public-beta": { requested: "public_requested_at", allowed: "public_allowed" },
  "developer-beta": { requested: "developer_requested_at", allowed: "developer_allowed" },
};

const WORDS: Record<BetaChannel, string> = { "public-beta": "Public beta", "developer-beta": "Developer beta" };

export async function betaRow(ctx: Ctx, accountId: string): Promise<BetaRow | null> {
  return ctx.env.DB.prepare("SELECT * FROM beta_members WHERE account_id = ?1").bind(accountId).first<BetaRow>();
}

export function autoApprove(ctx: Ctx): boolean {
  return (ctx.env.BETA_AUTO_APPROVE ?? "1") === "1";
}

const isMember = (r: BetaRow | null): r is BetaRow => !!r && r.left_at === null;
const termsCurrent = (r: BetaRow) => r.terms_version >= BETA_TERMS_VERSION;
const requested = (r: BetaRow) => BETA_CHANNELS.filter((c) => r[COLUMNS[c].requested] !== null);
const approved = (r: BetaRow) => BETA_CHANNELS.filter((c) => r[COLUMNS[c].allowed] === 1);

/** The channels a member may use: its approved betas, none when not a member, removed, or on older terms. */
export function allowedChannels(r: BetaRow | null): BetaChannel[] {
  if (!isMember(r) || r.blocked_at !== null || !termsCurrent(r)) return [];
  return approved(r);
}

/** What the account page and GET /v1/beta show. */
export type BetaView = BetaPageView;

export function betaView(ctx: Ctx, r: BetaRow | null): BetaView {
  const member = isMember(r);
  const asked = member ? requested(r) : [];
  return {
    member,
    joinedAt: member ? r.joined_at : null,
    termsVersion: member ? r.terms_version : null,
    termsCurrent: BETA_TERMS_VERSION,
    channels: allowedChannels(r),
    requested: asked,
    pending: member ? asked.filter((c) => r[COLUMNS[c].allowed] !== 1) : [],
    blocked: member && r.blocked_at !== null,
    // A sign-up sent after an admin removed the account: it waits for them.
    blockedSignup: member && r.blocked_at !== null ? asked.filter((c) => (r[COLUMNS[c].requested] ?? 0) > (r.blocked_at ?? 0)) : [],
    autoApprove: autoApprove(ctx),
    needsNewTerms: member && !termsCurrent(r),
  };
}

function betaJson(v: BetaView): Record<string, unknown> {
  return { ...v, joinedAt: iso(v.joinedAt) };
}

/** What a nest is told: PUT /api/relay/beta. A removed account is not a member there. */
function nestBody(r: BetaRow | null): Record<string, unknown> {
  return { member: isMember(r) && r.blocked_at === null, termsVersion: r?.terms_version ?? null, channels: allowedChannels(r), rev: r?.rev ?? 0 };
}

type Pushed = "ok" | "offline" | "too_old" | "failed";

/** Tells one nest the owner's beta state; on 200 notes the rev it now has. A 404 (too old) is noted for a day. */
async function pushNest(ctx: Ctx, n: Pick<NestRow, "id" | "key_version">, r: BetaRow | null): Promise<Pushed> {
  let res;
  try {
    res = await adminCall(ctx.env, ctx.now, n, "PUT", "/api/relay/beta", nestBody(r));
  } catch (err) {
    if (err instanceof NestOffline) return "offline";
    console.error("beta: telling a nest failed", err instanceof Error ? err.message : "unknown");
    return "failed";
  }
  if (res.status === 404 || res.status === 405) {
    await hit(ctx, "beta-old", n.id, 1, DAY);
    return "too_old";
  }
  if (res.status !== 200) return "failed";
  const rev = r?.rev ?? 0;
  await ctx.env.DB.prepare("UPDATE nests SET beta_rev_pushed = ?1 WHERE id = ?2 AND beta_rev_pushed < ?1").bind(rev, n.id).run();
  return "ok";
}

/** The account's own nests that are in use and have connected. */
async function activeNests(ctx: Ctx, accountId: string): Promise<NestRow[]> {
  return (await serversOf(ctx, accountId)).filter((n) => n.status === "active");
}

/** Tells every own active nest, best effort (an offline one is told by the cron). */
async function pushAll(ctx: Ctx, accountId: string): Promise<void> {
  const r = await betaRow(ctx, accountId);
  for (const n of await activeNests(ctx, accountId)) await pushNest(ctx, n, r);
}

/** Every own computer back to Off on every own nest (after leaving or being removed), best effort. */
async function allOff(ctx: Ctx, accountId: string): Promise<void> {
  for (const n of await activeNests(ctx, accountId)) {
    try {
      const list = await adminCall(ctx.env, ctx.now, n, "GET", "/api/relay/devices");
      const devices = list.status === 200 ? obj(list.json)?.devices : null;
      if (!Array.isArray(devices)) continue;
      for (const v of devices) {
        const d = obj(v);
        if (!d || typeof d.id !== "string" || !DEVICE_ID_RE.test(d.id) || d.personId) continue;
        const choice = obj(d.beta)?.channel;
        if (!choice || choice === "off") continue;
        await adminCall(ctx.env, ctx.now, n, "PUT", `/api/relay/devices/${encodeURIComponent(d.id)}/beta`, { channel: "off" });
      }
    } catch (err) {
      if (!(err instanceof NestOffline)) console.error("beta: turning computers off failed", err instanceof Error ? err.message : "unknown");
    }
  }
}

// GET /v1/beta
export async function betaGetRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  return json(200, betaJson(betaView(ctx, await betaRow(ctx, s.accountId))));
}

/** The betas a sign-up names: at least one, each known, or 400. */
function chosenChannels(v: unknown): BetaChannel[] {
  if (!Array.isArray(v) || v.length === 0 || v.some((c) => typeof c !== "string" || !(BETA_CHANNELS as readonly string[]).includes(c))) {
    throw new HttpError(400, "bad_request", "Choose Public beta, Developer beta or both.");
  }
  return BETA_CHANNELS.filter((c) => v.includes(c));
}

// POST /v1/beta/join {acceptTerms: true, termsVersion, channels: ["public-beta" | "developer-beta", ...]}
export async function betaJoinRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  const b = jsonBody(ctx);
  if (b.acceptTerms !== true || typeof b.termsVersion !== "string") throw new HttpError(400, "bad_request", "Tick the box to agree to the beta terms first.");
  if (b.termsVersion !== BETA_TERMS_VERSION) throw new HttpError(400, "terms_changed");
  const chosen = chosenChannels(b.channels);
  const old = await betaRow(ctx, s.accountId);
  // Signing up after leaving (or for the first time) starts afresh; signing up again adds a beta and keeps the date.
  const fresh = !isMember(old);
  const auto = autoApprove(ctx) && !old?.blocked_at;
  const next = (c: BetaChannel) => {
    const was = { requested: old?.[COLUMNS[c].requested] ?? null, allowed: old?.[COLUMNS[c].allowed] ?? 0 };
    if (!chosen.includes(c)) return fresh ? { requested: null, allowed: 0 } : was;
    return { requested: ctx.now, allowed: auto ? 1 : fresh ? 0 : was.allowed };
  };
  const pub = next("public-beta");
  const dev = next("developer-beta");
  await ctx.env.DB.prepare(
    `INSERT INTO beta_members (account_id, joined_at, left_at, terms_version, public_allowed, developer_allowed, rev, public_requested_at, developer_requested_at)
       VALUES (?1, ?2, NULL, ?3, ?4, ?5, 1, ?6, ?7)
       ON CONFLICT (account_id) DO UPDATE SET joined_at = excluded.joined_at, left_at = NULL, terms_version = excluded.terms_version,
         public_allowed = excluded.public_allowed, developer_allowed = excluded.developer_allowed, rev = beta_members.rev + 1,
         public_requested_at = excluded.public_requested_at, developer_requested_at = excluded.developer_requested_at`,
  )
    .bind(s.accountId, fresh ? ctx.now : old.joined_at, BETA_TERMS_VERSION, pub.allowed, dev.allowed, pub.requested, dev.requested)
    .run();
  await audit(ctx, "beta_signup", s.accountId, null, { terms: BETA_TERMS_VERSION, channels: chosen, approved: auto });
  ctx.defer(pushAll(ctx, s.accountId));
  return json(200, betaJson(betaView(ctx, await betaRow(ctx, s.accountId))));
}

// POST /v1/beta/leave
export async function betaLeaveRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  const r = await ctx.env.DB.prepare("UPDATE beta_members SET left_at = ?1, rev = rev + 1 WHERE account_id = ?2 AND left_at IS NULL")
    .bind(ctx.now, s.accountId)
    .run();
  if (r.meta.changes === 1) {
    await audit(ctx, "beta_left", s.accountId);
    ctx.defer(pushAll(ctx, s.accountId).then(() => allOff(ctx, s.accountId)));
  }
  return json(200, betaJson(betaView(ctx, await betaRow(ctx, s.accountId))));
}

/** An older nest has no beta route: 405, or a 404 that is not its own "no_device". */
function tooOld(status: number, error: unknown): boolean {
  return status === 405 || (status === 404 && error !== "no_device");
}

// PUT /v1/servers/{id}/devices/{deviceId}/beta {channel}
export async function deviceBetaRoute(ctx: Ctx, nestId: string, deviceId: string): Promise<Response> {
  const s = await requireBrowser(ctx);
  const n = await ownServer(ctx, s.accountId, nestId);
  if (!DEVICE_ID_RE.test(deviceId)) throw new HttpError(404, "not_found");
  const channel = jsonBody(ctx).channel;
  if (typeof channel !== "string" || !(CHOICES as readonly string[]).includes(channel)) throw new HttpError(400, "bad_request");
  const member = await betaRow(ctx, s.accountId);
  if (channel !== "off" && !allowedChannels(member).includes(channel as BetaChannel)) throw new HttpError(409, "not_allowed");
  // A nest that missed the last change would refuse a channel it does not know is allowed yet: tell it first.
  if (member && n.beta_rev_pushed < member.rev) await pushNest(ctx, n, member);
  let r;
  try {
    r = await adminCall(ctx.env, ctx.now, n, "PUT", `/api/relay/devices/${encodeURIComponent(deviceId)}/beta`, { channel });
  } catch (err) {
    if (err instanceof NestOffline) throw new HttpError(503, "nest_offline");
    throw err;
  }
  const error = obj(r.json)?.error;
  if (tooOld(r.status, error)) throw new HttpError(409, "nest_too_old", "Update your nest to choose beta updates here.");
  if (r.status === 404) throw new HttpError(404, "not_found", "That computer is not on this nest any more.");
  if (r.status === 409 && (error === "not_owner_device" || error === "not_allowed")) throw new HttpError(409, error);
  const kept = r.status === 200 ? obj(r.json) : null;
  if (!kept || typeof kept.channel !== "string") throw new HttpError(503, "nest_offline");
  await audit(ctx, "device_beta", s.accountId, n.id, { channel: kept.channel });
  return json(200, { id: deviceId, channel: kept.channel, at: kept.at ?? null, by: kept.by ?? "web" });
}

/** The hourly cron: nests whose owner changed something they have not heard yet (not more than once a day for a too-old one). */
export async function betaCron(ctx: Ctx): Promise<number> {
  const due = await ctx.env.DB.prepare(
    `SELECT n.id, n.key_version, n.account_id FROM nests n JOIN beta_members b ON b.account_id = n.account_id
       WHERE n.mode = 'relay' AND n.status = 'active' AND b.rev > n.beta_rev_pushed ORDER BY n.id LIMIT ?1`,
  )
    .bind(BETA_PUSHES_PER_RUN)
    .all<{ id: string; key_version: number; account_id: string }>();
  let told = 0;
  for (const n of due.results) {
    if ((await peek(ctx, "beta-old", n.id, 1, DAY)).limited) continue;
    if ((await pushNest(ctx, n, await betaRow(ctx, n.account_id))) === "ok") told++;
  }
  return told;
}

// ------------------------------------------------------------------ /admin/beta

type MemberRow = BetaRow & { email: string; username: string | null };

/** "26.10.2026". */
function shortDay(sec: number): string {
  const d = new Date(sec * 1000);
  const two = (n: number) => String(n).padStart(2, "0");
  return `${two(d.getUTCDate())}.${two(d.getUTCMonth() + 1)}.${d.getUTCFullYear()}`;
}

/** "26.10.2026, 10.12" (UTC), or "today, 10.12" on the day the page is drawn. */
function dayTime(sec: number, now: number): string {
  const d = new Date(sec * 1000);
  const time = `${String(d.getUTCHours()).padStart(2, "0")}.${String(d.getUTCMinutes()).padStart(2, "0")}`;
  return `${shortDay(sec) === shortDay(now) ? "today" : shortDay(sec)}, ${time}`;
}

const nameOf = (m: { username: string | null; email: string }) => m.username ?? emailName(m.email);

/** "<b>Public beta</b> and <b>Developer beta</b>". */
function betaWords(list: BetaChannel[]): string {
  return list.map((c) => `<b>${WORDS[c]}</b>`).join(" and ");
}

/** A computer on a beta, as its owner's nest lists it right now. */
interface InUse {
  accountId: string;
  name: string;
  computer: string;
  system: string | null;
  nest: string;
  channel: BetaChannel;
  app: string | null;
  lastSeen: string | null;
}

/**
 * Asks every member's active nests for their computers (all at once, ADMIN_TIMEOUT_MS each): the owner's own computers
 * that are on a beta, and the members whose nest could not be reached. Nothing of it is kept.
 */
async function liveUsage(ctx: Ctx, members: MemberRow[]): Promise<{ rows: InUse[]; offline: string[] }> {
  const byId = new Map(members.map((m) => [m.account_id, m]));
  const ids = members.map((m) => m.account_id);
  const nests = ids.length
    ? (
        await ctx.env.DB.prepare(
          `SELECT * FROM nests WHERE mode = 'relay' AND status = 'active' AND account_id IN (${ids.map((_, i) => `?${i + 1}`).join(", ")}) ORDER BY created_at, id`,
        )
          .bind(...ids)
          .all<NestRow>()
      ).results
    : [];
  const answers = await Promise.all(
    nests.map((n) =>
      adminCall(ctx.env, ctx.now, n, "GET", "/api/relay/devices").catch((err: unknown) => {
        if (!(err instanceof NestOffline)) console.error("beta: reading a nest's computers failed", err instanceof Error ? err.message : "unknown");
        return null;
      }),
    ),
  );
  const rows: InUse[] = [];
  const offline: string[] = [];
  nests.forEach((n, i) => {
    const m = byId.get(n.account_id)!;
    const a = answers[i];
    const devices = a && a.status === 200 ? obj(a.json)?.devices : null;
    if (!Array.isArray(devices)) {
      if (!offline.includes(nameOf(m))) offline.push(nameOf(m));
      return;
    }
    for (const v of devices) {
      const d = obj(v);
      const channel = obj(d?.beta)?.channel;
      if (!d || d.personId || (channel !== "public-beta" && channel !== "developer-beta")) continue;
      const text = (x: unknown) => (typeof x === "string" && x.length <= 200 ? x : null);
      rows.push({
        accountId: m.account_id,
        name: nameOf(m),
        computer: text(d.name) ?? "Computer",
        system: text(d.system),
        nest: n.label,
        channel,
        app: text(d.app),
        lastSeen: text(d.lastSeen),
      });
    }
  });
  return { rows, offline };
}

function adminButton(accountId: string, action: "approve" | "remove" | "let-in", cls: string, text: string): string {
  return `<form method="post" action="/admin/beta/${e(accountId)}/${action}" class="inline"><button type="submit" class="${cls}" data-action="beta-${action}">${text}</button></form>`;
}

function memberItem(ctx: Ctx, m: MemberRow, inUse: number): string {
  const who = `<div><strong>${e(nameOf(m))}</strong> <span class="muted">${e(m.email)}</span></div>`;
  if (m.left_at !== null) {
    return `<li class="member gone" data-member="${e(m.account_id)}">${who}
<div class="what">Left the beta on ${shortDay(m.left_at)}.</div></li>`;
  }
  if (m.blocked_at !== null) {
    const again = requested(m).filter((c) => (m[COLUMNS[c].requested] ?? 0) > (m.blocked_at ?? 0));
    const said = again.length ? `Signed up again for ${betaWords(again)}; it waits for you.` : "A new sign-up from this account waits for you.";
    return `<li class="member gone" data-member="${e(m.account_id)}">${who}
<div class="what">Removed by you on ${shortDay(m.blocked_at)}. ${said}</div>
<div class="actions">${adminButton(m.account_id, "let-in", "secondary", "Let back in")}</div></li>`;
  }
  const waiting = requested(m).filter((c) => m[COLUMNS[c].allowed] !== 1);
  const state = waiting.length ? (waiting.length < requested(m).length ? `${WORDS[waiting[0]]} waiting` : "waiting") : autoApprove(ctx) ? "approved at once" : "approved";
  const computers = inUse === 0 ? "no computer on a beta yet" : `${inUse} computer${inUse === 1 ? "" : "s"} on a beta`;
  const terms = m.terms_version < BETA_TERMS_VERSION ? " · on older beta terms" : "";
  return `<li class="member" data-member="${e(m.account_id)}">${who}
<div class="what">${betaWords(requested(m))} · signed up ${dayTime(m.joined_at, ctx.now)} · ${state}${terms} · ${computers}</div>
<div class="actions">${waiting.length ? adminButton(m.account_id, "approve", "secondary", "Approve") : ""}${adminButton(m.account_id, "remove", "danger", "Remove from the beta")}</div></li>`;
}

/** "jo's nest is offline right now." / "The nests of jo and kim are offline right now." */
function offlineLine(names: string[]): string {
  if (!names.length) return "";
  if (names.length === 1) return ` ${e(names[0])}'s nest is offline right now.`;
  return ` The nests of ${names.map(e).join(", ").replace(/, ([^,]*)$/, " and $1")} are offline right now.`;
}

// GET /admin/beta
export async function betaAdminRoute(ctx: Ctx): Promise<Response> {
  const login = await adminOnly(ctx, "/admin/beta");
  if (login) return login;
  const db = ctx.env.DB;
  const members = (
    await db
      .prepare(
        `SELECT b.*, a.email, a.username FROM beta_members b JOIN accounts a ON a.id = b.account_id
           ORDER BY b.left_at IS NOT NULL, b.blocked_at IS NOT NULL, b.joined_at DESC LIMIT ?1`,
      )
      .bind(BETA_ADMIN_MEMBERS)
      .all<MemberRow>()
  ).results;
  const { rows, offline } = await liveUsage(ctx, members.filter((m) => m.left_at === null && m.blocked_at === null));
  const signedUp = (await db.prepare("SELECT COUNT(*) AS n FROM beta_members WHERE left_at IS NULL AND blocked_at IS NULL").first<{ n: number }>())?.n ?? 0;
  const reports =
    (
      await db
        .prepare("SELECT COUNT(*) AS n FROM bug_reports WHERE channel IN ('public-beta', 'developer-beta') AND created_at >= ?1")
        .bind(ctx.now - 7 * DAY)
        .first<{ n: number }>()
    )?.n ?? 0;
  const auto = autoApprove(ctx);
  const items = members.map((m) => memberItem(ctx, m, rows.filter((r) => r.accountId === m.account_id).length)).join("\n");
  const table = rows
    .map(
      (r) => `<tr><td>${e(r.name)}</td><td>${e(r.computer)}<div class="muted">${e([r.system, r.nest].filter((x) => x).join(" · "))}</div></td><td class="${r.channel === "public-beta" ? "ch-public" : "ch-dev"}">${WORDS[r.channel]}</td><td>${e(r.app ?? "unknown")}</td><td>${r.lastSeen && !Number.isNaN(Date.parse(r.lastSeen)) ? e(dayTime(Math.floor(Date.parse(r.lastSeen) / 1000), ctx.now)) : "not yet"}</td></tr>`,
    )
    .join("\n");
  return htmlResponse(
    200,
    layout(
      "Beta program",
      `<section class="card">
<h1>Beta program</h1>
${adminNav("beta")}
<div class="facts">
<div><b>${signedUp}</b>signed up</div>
<div><b>${rows.length}</b>computer${rows.length === 1 ? "" : "s"} on a beta</div>
<div><b>${reports}</b>beta report${reports === 1 ? "" : "s"} this week</div>
</div>
<p class="small-note">${auto ? "Sign-ups are approved at once (BETA_AUTO_APPROVE is on)." : "Sign-ups wait for you to approve them (BETA_AUTO_APPROVE is off)."} Beta terms of ${e(termsWords(BETA_TERMS_VERSION))}.</p>
</section>
<section class="card">
<h2>Who signed up</h2>
<ul class="list" id="beta-members">
${items || `<li class="muted">Nobody has signed up yet.</li>`}
</ul>
</section>
<section class="card">
<h2>Who is using it</h2>
${
  rows.length
    ? `<table id="beta-usage">
<thead><tr><th>Account</th><th>Computer</th><th>Beta</th><th>App</th><th>Last seen</th></tr></thead>
<tbody>
${table}
</tbody>
</table>`
    : `<p class="muted" id="beta-usage">No computer is on a beta right now.</p>`
}
<p class="small-note">Read from each member's nest when this page opens; nothing about the computers is stored here.${offlineLine(offline)}</p>
</section>`,
      { bodyClass: "admin" },
    ),
  );
}

/** "26 October 2026" for a terms version "2026-10-26". */
function termsWords(version: string): string {
  const d = new Date(`${version}T00:00:00Z`);
  return Number.isNaN(d.getTime()) ? version : d.toLocaleDateString("en-GB", { day: "numeric", month: "long", year: "numeric", timeZone: "UTC" });
}

// POST /admin/beta/{accountId}/{approve|remove|let-in}
export async function betaAdminSetRoute(ctx: Ctx, accountId: string, action: string): Promise<Response> {
  const login = await adminOnly(ctx, "/admin/beta");
  if (login) return login;
  if (!ACCOUNT_ID_RE.test(accountId) || !["approve", "remove", "let-in"].includes(action)) throw new HttpError(404, "not_found");
  const db = ctx.env.DB;
  const approveAsked = `public_allowed = CASE WHEN public_requested_at IS NOT NULL THEN 1 ELSE public_allowed END,
    developer_allowed = CASE WHEN developer_requested_at IS NOT NULL THEN 1 ELSE developer_allowed END`;
  let r: D1Result;
  if (action === "approve") {
    r = await db
      .prepare(
        `UPDATE beta_members SET ${approveAsked}, rev = rev + 1 WHERE account_id = ?1 AND left_at IS NULL AND blocked_at IS NULL
           AND ((public_requested_at IS NOT NULL AND public_allowed = 0) OR (developer_requested_at IS NOT NULL AND developer_allowed = 0))`,
      )
      .bind(accountId)
      .run();
  } else if (action === "remove") {
    r = await db
      .prepare("UPDATE beta_members SET blocked_at = ?1, public_allowed = 0, developer_allowed = 0, rev = rev + 1 WHERE account_id = ?2 AND blocked_at IS NULL")
      .bind(ctx.now, accountId)
      .run();
  } else {
    // Let back in: the betas it asked for are approved again when sign-ups are approved at once; otherwise they wait.
    r = await db
      .prepare(`UPDATE beta_members SET blocked_at = NULL${autoApprove(ctx) ? `, ${approveAsked}` : ""}, rev = rev + 1 WHERE account_id = ?1 AND blocked_at IS NOT NULL`)
      .bind(accountId)
      .run();
  }
  if (r.meta.changes === 1) {
    await audit(ctx, action === "approve" ? "beta_approved" : action === "remove" ? "beta_removed" : "beta_let_in", accountId);
    ctx.defer(action === "remove" ? pushAll(ctx, accountId).then(() => allOff(ctx, accountId)) : pushAll(ctx, accountId));
  }
  return redirect("/admin/beta", 303);
}
