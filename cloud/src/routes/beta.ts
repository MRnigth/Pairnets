// The beta program (designs/beta-channels/CONTRACT.md): an account joins here, agreeing to the beta terms; what it may get
// reaches its computers only through its own nests, which the service tells (ra1-signed, like a rename).
//
//   GET  /v1/beta                              session                 -> member?, terms, the channels it may use
//   POST /v1/beta/join {acceptTerms, termsVersion} session + Origin    -> joins, or agrees to new terms
//   POST /v1/beta/leave                        session + Origin        -> leaves; its own computers go back to Off
//   PUT  /v1/servers/{id}/devices/{d}/beta     session + Origin        -> the nest sets that computer's choice
//   GET  /admin/beta, POST /admin/beta/{account}/{action}  admins only -> members, and who may get which beta
//
// Each change bumps the member's rev and is pushed (PUT /api/relay/beta) to the account's own active relay nests at
// once; a nest that missed it (offline) is told by the hourly cron. A nest from before this answers 404: it is asked
// again at most once a day.

import { audit } from "../audit";
import type { Ctx } from "../context";
import { ACCOUNT_ID_RE, DEVICE_ID_RE, iso } from "../formats";
import { escapeHtml as e, HttpError, htmlResponse, json, redirect } from "../http";
import { adminCall, NestOffline } from "../nestadmin";
import { type BetaPageView, layout } from "../pages";
import { DAY, hit, peek } from "../ratelimit";
import { jsonBody } from "../request";
import { requireBrowser } from "../sessions";
import type { NestRow } from "./account";
import { adminNav, adminOnly } from "./crash";
import { obj, ownServer, serversOf } from "./servers";

/** The beta terms the page links to (/beta-terms); joining agrees to this version. */
export const BETA_TERMS_VERSION = "2026-10-26";
export const BETA_CHANNELS = ["public-beta", "developer-beta"] as const;
export type BetaChannel = (typeof BETA_CHANNELS)[number];
/** What a computer may be set to: Off (= stable) or one of the betas. */
const CHOICES = ["off", ...BETA_CHANNELS] as const;
/** Nests the cron tells per run; the next hour takes the rest. */
export const BETA_PUSHES_PER_RUN = 50;

export interface BetaRow {
  account_id: string;
  joined_at: number;
  left_at: number | null;
  terms_version: string;
  public_allowed: number;
  developer_allowed: number;
  rev: number;
}

export async function betaRow(ctx: Ctx, accountId: string): Promise<BetaRow | null> {
  return ctx.env.DB.prepare("SELECT * FROM beta_members WHERE account_id = ?1").bind(accountId).first<BetaRow>();
}

const isMember = (r: BetaRow | null): r is BetaRow => !!r && r.left_at === null;
const termsCurrent = (r: BetaRow) => r.terms_version >= BETA_TERMS_VERSION;

/** The channels a member may use: none when not a member or on older terms. */
export function allowedChannels(ctx: Ctx, r: BetaRow | null): BetaChannel[] {
  if (!isMember(r) || !termsCurrent(r)) return [];
  const out: BetaChannel[] = [];
  if (r.public_allowed === 1 || (ctx.env.BETA_PUBLIC_FOR_ALL ?? "1") === "1") out.push("public-beta");
  if (r.developer_allowed === 1) out.push("developer-beta");
  return out;
}

/** What the account page and GET /v1/beta show. */
export type BetaView = BetaPageView;

export function betaView(ctx: Ctx, r: BetaRow | null): BetaView {
  const member = isMember(r);
  return {
    member,
    joinedAt: member ? r.joined_at : null,
    termsVersion: member ? r.terms_version : null,
    termsCurrent: BETA_TERMS_VERSION,
    channels: allowedChannels(ctx, r),
    needsNewTerms: member && !termsCurrent(r),
  };
}

function betaJson(v: BetaView): Record<string, unknown> {
  return { ...v, joinedAt: iso(v.joinedAt) };
}

/** What a nest is told: PUT /api/relay/beta. */
function nestBody(ctx: Ctx, r: BetaRow | null): Record<string, unknown> {
  return { member: isMember(r), termsVersion: r?.terms_version ?? null, channels: allowedChannels(ctx, r), rev: r?.rev ?? 0 };
}

type Pushed = "ok" | "offline" | "too_old" | "failed";

/** Tells one nest the owner's beta state; on 200 notes the rev it now has. A 404 (too old) is noted for a day. */
async function pushNest(ctx: Ctx, n: Pick<NestRow, "id" | "key_version">, r: BetaRow | null): Promise<Pushed> {
  let res;
  try {
    res = await adminCall(ctx.env, ctx.now, n, "PUT", "/api/relay/beta", nestBody(ctx, r));
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

/** Every own computer back to Off on every own nest (after leaving), best effort. */
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

// POST /v1/beta/join {acceptTerms: true, termsVersion}
export async function betaJoinRoute(ctx: Ctx): Promise<Response> {
  const s = await requireBrowser(ctx);
  const b = jsonBody(ctx);
  if (b.acceptTerms !== true || typeof b.termsVersion !== "string") throw new HttpError(400, "bad_request", "Tick the box to agree to the beta terms first.");
  if (b.termsVersion !== BETA_TERMS_VERSION) throw new HttpError(400, "terms_changed");
  // Joining again after leaving starts a new joined date; agreeing to new terms while a member keeps it.
  await ctx.env.DB.prepare(
    `INSERT INTO beta_members (account_id, joined_at, left_at, terms_version, rev) VALUES (?1, ?2, NULL, ?3, 1)
       ON CONFLICT (account_id) DO UPDATE SET
         joined_at = CASE WHEN beta_members.left_at IS NOT NULL THEN excluded.joined_at ELSE beta_members.joined_at END,
         left_at = NULL, terms_version = excluded.terms_version, rev = beta_members.rev + 1`,
  )
    .bind(s.accountId, ctx.now, BETA_TERMS_VERSION)
    .run();
  await audit(ctx, "beta_joined", s.accountId, null, { terms: BETA_TERMS_VERSION });
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
  if (channel !== "off" && !allowedChannels(ctx, member).includes(channel as BetaChannel)) throw new HttpError(409, "not_allowed");
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

const ADMIN_ACTIONS: Record<string, { column: "public_allowed" | "developer_allowed"; value: 0 | 1 }> = {
  "allow-public": { column: "public_allowed", value: 1 },
  "disallow-public": { column: "public_allowed", value: 0 },
  "allow-developer": { column: "developer_allowed", value: 1 },
  "disallow-developer": { column: "developer_allowed", value: 0 },
};

function day(sec: number): string {
  return new Date(sec * 1000).toISOString().slice(0, 10);
}

function toggle(accountId: string, on: number, what: "public" | "developer"): string {
  const action = `${on ? "disallow" : "allow"}-${what}`;
  return `<form method="post" action="/admin/beta/${e(accountId)}/${action}" class="inline"><button type="submit" class="${on ? "danger" : "secondary"}" data-action="${action}">${on ? "Take away" : "Allow"}</button></form>`;
}

// GET /admin/beta
export async function betaAdminRoute(ctx: Ctx): Promise<Response> {
  const login = await adminOnly(ctx, "/admin/beta");
  if (login) return login;
  const rows = (
    await ctx.env.DB.prepare(
      "SELECT b.*, a.email FROM beta_members b JOIN accounts a ON a.id = b.account_id ORDER BY b.left_at IS NOT NULL, b.joined_at DESC LIMIT 500",
    ).all<BetaRow & { email: string }>()
  ).results;
  const forAll = (ctx.env.BETA_PUBLIC_FOR_ALL ?? "1") === "1";
  const items = rows
    .map(
      (r) => `<li data-member="${e(r.account_id)}">
<div><strong>${e(r.email)}</strong> <span class="muted">· joined ${day(r.joined_at)}${r.left_at !== null ? ` · left ${day(r.left_at)}` : ""} · terms ${e(r.terms_version)}${r.terms_version < BETA_TERMS_VERSION ? " (old)" : ""}</span></div>
<div class="actions"><span>Public beta: <b>${r.public_allowed ? "allowed" : forAll ? "open to all" : "no"}</b></span> ${toggle(r.account_id, r.public_allowed, "public")}
<span>Developer beta: <b>${r.developer_allowed ? "allowed" : "no"}</b></span> ${toggle(r.account_id, r.developer_allowed, "developer")}</div>
</li>`,
    )
    .join("\n");
  return htmlResponse(
    200,
    layout(
      "Beta program",
      `<section class="card"><h1>Beta program</h1>${adminNav("beta")}<p class="muted">${rows.length} member${rows.length === 1 ? "" : "s"} (left ones last). Terms version ${BETA_TERMS_VERSION}. Public beta is ${forAll ? "open to every member (BETA_PUBLIC_FOR_ALL)" : "only for those allowed"}.</p>
<ul class="list" id="beta-members">
${items || `<li class="muted">Nobody has joined yet.</li>`}
</ul></section>`,
    ),
  );
}

// POST /admin/beta/{accountId}/{allow|disallow}-{public|developer}
export async function betaAdminSetRoute(ctx: Ctx, accountId: string, action: string): Promise<Response> {
  const login = await adminOnly(ctx, "/admin/beta");
  if (login) return login;
  const a = ADMIN_ACTIONS[action];
  if (!a || !ACCOUNT_ID_RE.test(accountId)) throw new HttpError(404, "not_found");
  const r = await ctx.env.DB.prepare(`UPDATE beta_members SET ${a.column} = ?1, rev = rev + 1 WHERE account_id = ?2 AND ${a.column} <> ?1`)
    .bind(a.value, accountId)
    .run();
  if (r.meta.changes === 1) {
    await audit(ctx, "beta_allowed", accountId, null, { [a.column]: a.value });
    ctx.defer(pushAll(ctx, accountId));
  }
  return redirect("/admin/beta", 303);
}
