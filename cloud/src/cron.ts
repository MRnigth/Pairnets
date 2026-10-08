// Hourly sweep (CONTRACT.md section 6.12): expired sessions, challenges, app logins, claim codes, pending nests older
// than 1 hour, old rate counters and audit rows older than 90 days.

import type { Env } from "./env";
import { DAY } from "./ratelimit";

export const AUDIT_DAYS = 90;
/** The longest rate-limit window is one day; counters are kept two. */
const COUNTER_KEEP = 2 * DAY;

export async function sweep(env: Env, now: number): Promise<Record<string, number>> {
  const db = env.DB;
  const results = await db.batch([
    db.prepare("DELETE FROM sessions WHERE expires_at <= ?1").bind(now),
    db.prepare("DELETE FROM login_challenges WHERE expires_at <= ?1").bind(now),
    db.prepare("DELETE FROM device_logins WHERE expires_at <= ?1").bind(now),
    db.prepare("DELETE FROM claim_codes WHERE expires_at <= ?1").bind(now),
    db.prepare("DELETE FROM nests WHERE status = 'pending' AND created_at <= ?1").bind(now - 3600),
    db.prepare("DELETE FROM rate_counters WHERE window_start < ?1").bind(now - COUNTER_KEEP),
    db.prepare("DELETE FROM audit WHERE at < ?1").bind(now - AUDIT_DAYS * DAY),
  ]);
  const names = ["sessions", "loginChallenges", "deviceLogins", "claimCodes", "pendingNests", "rateCounters", "audit"];
  const counts: Record<string, number> = {};
  results.forEach((r, i) => (counts[names[i]] = r.meta.changes ?? 0));
  return counts;
}
