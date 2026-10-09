// The names table, and the limits on how many names there may be.

import type { Ctx } from "./context";
import { intVar } from "./env";
import { HttpError } from "./http";
import { DAY } from "./ratelimit";

export interface NameRow {
  name: string;
  status: "creating" | "active" | "releasing" | "broken";
  tunnel_id: string | null;
  dns_record_id: string | null;
  manage_key_hash: unknown;
  email: string;
  ip_bucket: string;
  created_at: number;
  updated_at: number;
}

/** Names one internet address may have at once. */
export const NAMES_PER_ADDRESS = 3;

export function findName(ctx: Ctx, name: string): Promise<NameRow | null> {
  return ctx.env.DB.prepare("SELECT * FROM names WHERE name = ?1").bind(name).first<NameRow>();
}

export function isPaused(ctx: Ctx): boolean {
  return ctx.env.NAMES_PAUSED === "1";
}

/**
 * Throws when there is no room for one more name: the zone is full (MAX_NAMES, under Cloudflare's DNS record limit),
 * today's new names are used up (DAILY_NAMES), or this internet address already has its names.
 */
export async function checkRoom(ctx: Ctx, ipBucket: string): Promise<void> {
  const row = await ctx.env.DB.prepare(
    `SELECT COUNT(*) AS total,
            SUM(CASE WHEN created_at > ?1 THEN 1 ELSE 0 END) AS today,
            SUM(CASE WHEN ip_bucket = ?2 THEN 1 ELSE 0 END) AS here
     FROM names`,
  )
    .bind(ctx.now - DAY, ipBucket)
    .first<{ total: number; today: number | null; here: number | null }>();
  const total = row?.total ?? 0;
  if (total >= intVar(ctx.env.MAX_NAMES, 180)) throw new HttpError(503, "full");
  if ((row?.today ?? 0) >= intVar(ctx.env.DAILY_NAMES, 40)) throw new HttpError(429, "daily_limit");
  if ((row?.here ?? 0) >= NAMES_PER_ADDRESS) throw new HttpError(429, "address_limit");
}
