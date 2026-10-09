// Hourly sweep: expired email codes, old rate counters and events, and the Cloudflare leftovers of any name that could
// not be undone or given back (status 'broken', or a 'creating'/'releasing' row older than an hour: the request that
// owned it died). A leftover's row is deleted only once its DNS record and tunnel are gone.

import { CloudflareApi, teardown } from "./cloudflare";
import type { Env } from "./env";
import { logEvent } from "./events";
import { DAY, HOUR } from "./ratelimit";
import type { NameRow } from "./store";

export const EVENT_DAYS = 90;
/** The longest rate-limit window is one day; counters are kept two. */
const COUNTER_KEEP = 2 * DAY;
/** Leftovers handled per run (each takes a few Cloudflare calls). */
const LEFTOVERS_PER_RUN = 20;

export async function sweep(env: Env, now: number, fetchFn: typeof fetch): Promise<Record<string, number>> {
  const db = env.DB;
  const results = await db.batch([
    db.prepare("DELETE FROM claims WHERE expires_at <= ?1").bind(now),
    db.prepare("DELETE FROM rate_counters WHERE window_start < ?1").bind(now - COUNTER_KEEP),
    db.prepare("DELETE FROM events WHERE at < ?1").bind(now - EVENT_DAYS * DAY),
  ]);
  const counts: Record<string, number> = { claims: 0, rateCounters: 0, events: 0, cleaned: 0, stillLeft: 0 };
  ["claims", "rateCounters", "events"].forEach((k, i) => (counts[k] = results[i].meta.changes ?? 0));

  const leftovers = await db
    .prepare(
      `SELECT * FROM names
       WHERE status = 'broken' OR (status IN ('creating', 'releasing') AND updated_at <= ?1)
       ORDER BY updated_at LIMIT ?2`,
    )
    .bind(now - HOUR, LEFTOVERS_PER_RUN)
    .all<NameRow>();
  const cf = new CloudflareApi(env, fetchFn);
  for (const row of leftovers.results) {
    const result = await teardown(cf, row.tunnel_id, row.dns_record_id);
    if (result.done) {
      await db.prepare("DELETE FROM names WHERE name = ?1").bind(row.name).run();
      await logEvent(env, now, "leftover_cleaned", row.name, { was: row.status });
      counts.cleaned++;
    } else {
      await db.prepare("UPDATE names SET status = 'broken', updated_at = ?2 WHERE name = ?1").bind(row.name, now).run();
      counts.stillLeft++;
    }
  }
  return counts;
}
