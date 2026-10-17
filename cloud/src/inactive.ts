// Accounts nobody uses (privacy page, "How long we keep it"): an account with no sign-in and no other activity for a
// year is deleted, after a warning email 30 days before. Run by the hourly sweep (cron.ts).
//
// "Used" is the newest of: the account was made, a sign-in (identities.last_used_at), a signed-in browser or app
// seen (sessions.last_seen_at), and one of its nests online (nests.last_seen_at: heartbeats, and once a day for
// relayed servers, relay.ts). So an account whose nest or computers keep running is never deleted.

import { deleteAccountRows } from "./accounts";
import { audit } from "./audit";
import type { Ctx } from "./context";
import { DAY, hit } from "./ratelimit";
import { inactiveDeletedEmail, inactiveWarningEmail, reserveMail } from "./mail";

/** Unused this long (days): the account is deleted. */
export const INACTIVE_DAYS = 365;
/** The warning goes out this many days before. */
export const WARNING_DAYS = 30;
/** At most this many warnings a day, so they never use up the day's emails that sign-ins need. */
export const WARNINGS_PER_DAY = 20;
/** At most this many deletions in one sweep (each is a batch of deletes; the next hour takes the rest). */
export const DELETES_PER_SWEEP = 20;

interface UnusedRow {
  id: string;
  email: string;
  inactive_warned_at: number | null;
  last_used: number;
}

const LAST_USED = `MAX(a.created_at,
      COALESCE((SELECT MAX(last_used_at) FROM identities WHERE account_id = a.id), 0),
      COALESCE((SELECT MAX(last_seen_at) FROM sessions WHERE account_id = a.id), 0),
      COALESCE((SELECT MAX(last_seen_at) FROM nests WHERE account_id = a.id), 0))`;

/** The day an account warned at `warnedAt` is deleted, as "17 November 2026". */
export function deletionDay(warnedAt: number): string {
  return new Date((warnedAt + WARNING_DAYS * DAY) * 1000).toLocaleDateString("en-GB", { day: "numeric", month: "long", year: "numeric", timeZone: "UTC" });
}

/** Warns, forgives and deletes. Returns counts for the sweep's log line. */
export async function sweepInactive(ctx: Ctx): Promise<{ inactiveWarned: number; inactiveKept: number; inactiveDeleted: number; tunnels: string[] }> {
  const db = ctx.env.DB;
  const result = { inactiveWarned: 0, inactiveKept: 0, inactiveDeleted: 0, tunnels: [] as string[] };
  const warnBefore = ctx.now - (INACTIVE_DAYS - WARNING_DAYS) * DAY;

  // 1. Warned, then used again (someone signed in, a nest came online): the warning no longer holds.
  const kept = await db
    .prepare(`SELECT a.id FROM accounts a WHERE a.inactive_warned_at IS NOT NULL AND ${LAST_USED} > a.inactive_warned_at`)
    .all<{ id: string }>();
  for (const r of kept.results) {
    await db.prepare("UPDATE accounts SET inactive_warned_at = NULL WHERE id = ?1").bind(r.id).run();
    await audit(ctx, "inactive_kept", r.id);
    result.inactiveKept++;
  }

  // 2. Warned at least 30 days ago and still unused: deleted, like "Delete account" does it.
  const due = await db
    .prepare(
      `SELECT a.id, a.email, a.inactive_warned_at, ${LAST_USED} AS last_used FROM accounts a
         WHERE a.inactive_warned_at IS NOT NULL AND a.inactive_warned_at <= ?1 ORDER BY a.inactive_warned_at LIMIT ?2`,
    )
    .bind(ctx.now - WARNING_DAYS * DAY, DELETES_PER_SWEEP)
    .all<UnusedRow>();
  for (const r of due.results) {
    if (r.last_used > (r.inactive_warned_at ?? 0) || r.last_used > ctx.now - INACTIVE_DAYS * DAY) continue; // (used meanwhile)
    result.tunnels.push(...(await deleteAccountRows(ctx, r.id, r.email)));
    console.log("inactive: deleted an account unused since", new Date(r.last_used * 1000).toISOString().slice(0, 10));
    result.inactiveDeleted++;
    if (await reserveMail(ctx, "notice")) await send(ctx, inactiveDeletedEmail(r.email));
  }

  // 3. Unused for 11 months and not warned yet: the warning email. Only a sent warning counts, so no account is
  //    deleted without one; when the day's emails are used up, the rest wait for tomorrow.
  const unwarned = await db
    .prepare(
      `SELECT a.id, a.email, a.inactive_warned_at, ${LAST_USED} AS last_used FROM accounts a
         WHERE a.inactive_warned_at IS NULL AND ${LAST_USED} <= ?1 ORDER BY last_used LIMIT ?2`,
    )
    .bind(warnBefore, WARNINGS_PER_DAY)
    .all<UnusedRow>();
  for (const r of unwarned.results) {
    if ((await hit(ctx, "inactive-warning", "service", WARNINGS_PER_DAY, DAY)).limited) break;
    if (!(await reserveMail(ctx, "notice"))) break;
    if (!(await send(ctx, inactiveWarningEmail(r.email, deletionDay(ctx.now))))) continue;
    await db.prepare("UPDATE accounts SET inactive_warned_at = ?1 WHERE id = ?2 AND inactive_warned_at IS NULL").bind(ctx.now, r.id).run();
    await audit(ctx, "inactive_warned", r.id);
    result.inactiveWarned++;
  }
  return result;
}

/** Sends now (the sweep waits for it) and tells whether it went out; failures are logged without the address. */
async function send(ctx: Ctx, msg: Parameters<Ctx["mailer"]["send"]>[0]): Promise<boolean> {
  try {
    await ctx.mailer.send(msg);
    return true;
  } catch (e) {
    console.error("inactive: sending failed", e instanceof Error ? e.message : "unknown error");
    return false;
  }
}
