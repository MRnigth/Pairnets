// The router Worker, seen from this service (RELAY.md section 5): which of its answers mean "the link to the nest
// failed", and router deploys. The router's code is deployed once with wrangler (cloud/router); its bindings, one VPC
// link per relayed nest, are set here through the Cloudflare API, the full list every time.
//
// Deploys are serialised with a lease in router_state (one row): a deploy in progress less than 2 minutes old makes the
// next one stand aside (the cron, the next approval or removal, or the relay's self-heal tries again). A deploy that
// finds new or removed servers once its list went out goes round again, so a server approved during a deploy does not
// wait for the hourly cron. Tunnels of removed servers ('broken' rows) are deleted only after a deploy without them
// went out, so the router never points at a deleted tunnel.

import { CloudflareApi, nestIdOfTunnel, type RouterBinding, teardown } from "./cloudflare";
import { randomToken } from "./crypto";
import type { Env } from "./env";

/** The router marks its own error answers with this header (between the two Workers only; value nest_unknown/nest_offline). */
export const ROUTER_ERROR_HEADER = "X-Pairnets-Router";

/** Status codes the link itself (cloudflared, Workers VPC) uses when it cannot reach the nest. */
const LINK_STATUSES = new Set([502, 503, 504, 530]);

/**
 * Whether a router answer means the nest could not be reached: the router's own error answers, and the link's
 * 502/503/504/530 pages, which are not JSON. The nest's own JSON answers (its 503 "busy", say) pass through.
 */
export function linkFailed(resp: Response): boolean {
  if (resp.headers.has(ROUTER_ERROR_HEADER)) return true;
  if (!LINK_STATUSES.has(resp.status)) return false;
  const ct = (resp.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
  return ct !== "application/json";
}

export const LEASE_SECONDS = 120;
/** A nest the router does not know although it was deployed: redeploy only if the last deploy is older than this. */
export const SELF_HEAL_AFTER = 120;
const MAX_ROUNDS = 3;
const CLEANUP_PER_ROUND = 20;

export interface RouterRunOptions {
  /** Send the bindings (default true). */
  deploy?: boolean;
  /** Also delete tunnels of relay nests that no longer exist at all (the hourly cron). */
  reconcile?: boolean;
  /** Tunnel ids to delete once a deploy without them went out (their rows are already gone: account deletion). */
  alsoDelete?: string[];
}

export interface RouterRun {
  /** Another deploy holds the lease: nothing was done. */
  busy: boolean;
  /** Successful PATCHes. */
  deployed: number;
  version: number | null;
  /** Removed servers whose tunnel and row are now gone. */
  cleaned: number;
  /** Leftover tunnels of nests that no longer exist, now deleted. */
  orphans: number;
  /** The failed step and status, never the API token. */
  error: string | null;
}

interface RelayRow {
  id: string;
  status: "pending" | "active" | "broken";
  tunnel_id: string | null;
}

/** Router deploy, cleanup and reconcile under the lease. Never throws for Cloudflare trouble (see `error`). */
export async function runRouter(env: Env, now: number, fetchFn: typeof fetch, opts: RouterRunOptions = {}): Promise<RouterRun> {
  const db = env.DB;
  const run: RouterRun = { busy: false, deployed: 0, version: null, cleaned: 0, orphans: 0, error: null };
  const lease = randomToken(16);
  const got = await db
    .prepare(
      `INSERT INTO router_state (id, version, lease_id, lease_until) VALUES (1, 0, ?1, ?2)
       ON CONFLICT (id) DO UPDATE SET lease_id = ?1, lease_until = ?2
         WHERE router_state.lease_until IS NULL OR router_state.lease_until <= ?3
       RETURNING version`,
    )
    .bind(lease, now + LEASE_SECONDS, now)
    .first<{ version: number }>();
  if (!got) return { ...run, busy: true };
  let version = got.version;
  run.version = version;
  const cf = new CloudflareApi(env, fetchFn);

  const fail = async (e: unknown) => {
    run.error = e instanceof Error ? e.message : "unknown error";
    await db.prepare("UPDATE router_state SET last_error = ?1 WHERE id = 1 AND lease_id = ?2").bind(run.error, lease).run();
  };

  try {
    if (opts.deploy !== false) {
      const tried = new Set<string>();
      for (let round = 0; round < MAX_ROUNDS; round++) {
        const rows = (
          await db
            .prepare("SELECT id, status, tunnel_id FROM nests WHERE mode = 'relay' AND (status = 'broken' OR tunnel_id IS NOT NULL) ORDER BY id")
            .all<RelayRow>()
        ).results;
        const live = rows.filter((r) => r.status !== "broken" && r.tunnel_id);
        const broken = rows.filter((r) => r.status === "broken" && !tried.has(r.id)).slice(0, CLEANUP_PER_ROUND);
        const bindings: RouterBinding[] = live.map((r) => ({ type: "vpc_network", name: `N_${r.id}`, tunnel_id: r.tunnel_id! }));
        try {
          await cf.setRouterBindings(bindings);
        } catch (e) {
          await fail(e);
          break;
        }
        const next = version + 1;
        const [, state] = await db.batch([
          db
            .prepare(
              `UPDATE nests SET routed_version = ?1 WHERE id IN (SELECT value FROM json_each(?2))
                 AND EXISTS (SELECT 1 FROM router_state WHERE id = 1 AND lease_id = ?3)`,
            )
            .bind(next, JSON.stringify(live.map((r) => r.id)), lease),
          db.prepare("UPDATE router_state SET version = ?1, deployed_at = ?2, last_error = NULL WHERE id = 1 AND lease_id = ?3").bind(next, now, lease),
        ]);
        if (state.meta.changes !== 1) {
          run.error = "router: lease lost";
          break;
        }
        version = next;
        run.version = version;
        run.deployed++;

        // The router no longer has these: their tunnels and rows can go.
        for (const b of broken) {
          tried.add(b.id);
          if ((await teardown(cf, b.tunnel_id, b.id)).done) {
            await db.prepare("DELETE FROM nests WHERE id = ?1 AND status = 'broken'").bind(b.id).run();
            run.cleaned++;
          }
        }
        if (round === 0) {
          for (const id of opts.alsoDelete ?? []) await teardown(cf, id, "");
        }

        // A server approved or removed while this list went out needs another round.
        const more = await db
          .prepare(
            `SELECT (EXISTS (SELECT 1 FROM nests WHERE mode = 'relay' AND status IN ('pending', 'active') AND tunnel_id IS NOT NULL AND routed_version IS NULL)
                  OR EXISTS (SELECT 1 FROM nests WHERE mode = 'relay' AND status = 'broken' AND id NOT IN (SELECT value FROM json_each(?1)))) AS more`,
          )
          .bind(JSON.stringify([...tried]))
          .first<{ more: number }>();
        if (!more?.more) break;
      }
    }

    if (opts.reconcile && !run.error) {
      try {
        for (const t of await cf.listTunnels()) {
          const nestId = nestIdOfTunnel(t.name);
          if (!nestId) continue; // not one of ours
          const row = await db.prepare("SELECT 1 AS x FROM nests WHERE id = ?1").bind(nestId).first();
          if (row) continue;
          if ((await teardown(cf, t.id, nestId)).done) run.orphans++;
        }
      } catch (e) {
        await fail(e);
      }
    }
  } finally {
    await db.prepare("UPDATE router_state SET lease_id = NULL, lease_until = NULL WHERE id = 1 AND lease_id = ?1").bind(lease).run();
  }
  return run;
}

/** For waitUntil: runs the router job and logs the outcome (no secrets in it). */
export function runRouterLogged(env: Env, now: number, fetchFn: typeof fetch, opts: RouterRunOptions = {}): Promise<void> {
  return runRouter(env, now, fetchFn, opts).then(
    (r) => console.log("router", JSON.stringify(r)),
    (e: unknown) => console.error("router: failed", e instanceof Error ? e.message : "unknown error"),
  );
}
