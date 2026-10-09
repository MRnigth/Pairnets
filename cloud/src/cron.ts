// Hourly sweep (CONTRACT.md section 6.12, RELAY.md section 7): expired sessions, challenges, app logins, claim codes,
// server links, pending own-domain nests older than 1 hour, old rate counters and audit rows older than 90 days. Then
// the relayed servers: one still pending after an hour (never reached) is given up like a removed one, and the router
// is deployed when a server is missing from it or a removed one still has a tunnel; the deploy deletes those tunnels and
// rows, and finally any tunnel of a nest that no longer exists at all.

import type { Env } from "./env";
import { DAY, HOUR } from "./ratelimit";
import { runRouter } from "./router";

export const AUDIT_DAYS = 90;
/** The longest rate-limit window is one day; counters are kept two. */
const COUNTER_KEEP = 2 * DAY;

export async function sweep(env: Env, now: number, fetchFn: typeof fetch): Promise<Record<string, number | string | null>> {
  const db = env.DB;
  const results = await db.batch([
    db.prepare("DELETE FROM sessions WHERE expires_at <= ?1").bind(now),
    db.prepare("DELETE FROM login_challenges WHERE expires_at <= ?1").bind(now),
    db.prepare("DELETE FROM device_logins WHERE expires_at <= ?1").bind(now),
    db.prepare("DELETE FROM claim_codes WHERE expires_at <= ?1").bind(now),
    db.prepare("DELETE FROM nests WHERE mode = 'url' AND status = 'pending' AND created_at <= ?1").bind(now - HOUR),
    db.prepare("DELETE FROM rate_counters WHERE window_start < ?1").bind(now - COUNTER_KEEP),
    db.prepare("DELETE FROM audit WHERE at < ?1").bind(now - AUDIT_DAYS * DAY),
    db.prepare("DELETE FROM server_links WHERE expires_at <= ?1").bind(now),
    db.prepare("UPDATE nests SET status = 'broken' WHERE mode = 'relay' AND status = 'pending' AND created_at <= ?1").bind(now - HOUR),
  ]);
  const names = ["sessions", "loginChallenges", "deviceLogins", "claimCodes", "pendingNests", "rateCounters", "audit", "serverLinks", "stalePendingServers"];
  const counts: Record<string, number | string | null> = {};
  results.forEach((r, i) => (counts[names[i]] = r.meta.changes ?? 0));

  const need = await db
    .prepare(
      `SELECT (EXISTS (SELECT 1 FROM nests WHERE mode = 'relay' AND status IN ('pending', 'active') AND tunnel_id IS NOT NULL AND routed_version IS NULL)
            OR EXISTS (SELECT 1 FROM nests WHERE mode = 'relay' AND status = 'broken')) AS need`,
    )
    .first<{ need: number }>();
  const run = await runRouter(env, now, fetchFn, { deploy: !!need?.need, reconcile: true });
  counts.routerBusy = run.busy ? 1 : 0;
  counts.routerDeploys = run.deployed;
  counts.serversCleaned = run.cleaned;
  counts.orphanTunnels = run.orphans;
  counts.routerError = run.error;
  return counts;
}
