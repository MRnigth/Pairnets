// What the owner of a name can do later, with the manage key the claim answered (deploy/pairnets-name.sh sends it as
// `Authorization: Bearer pnk_...`):
//
//   POST   /v1/rotate {"name": "alice"} -> 200 {"name", "tunnel_token"}   a new tunnel token; the old one stops working
//   DELETE /v1/name   {"name": "alice"} -> 204 (or 202 while Cloudflare finishes)   give the name back

import type { Ctx } from "../context";
import { CloudflareError, teardown } from "../cloudflare";
import { sha256, timingSafeEqual } from "../crypto";
import { logEvent } from "../events";
import { HttpError, json, noContent } from "../http";
import { NAME_RE } from "../names";
import { blobBytes, jsonBody } from "../request";
import { enforce, hit, HOUR, limitedError, peek } from "../ratelimit";
import { findName, type NameRow } from "../store";

const KEY_RE = /^Bearer (pnk_[A-Za-z0-9_-]{43})$/;
/** Wrong keys allowed per internet address and hour. */
const KEY_FAILURES = 10;

/** The name's row when the request carries its manage key; 401 otherwise (the same answer for a name that does not exist). */
async function authorize(ctx: Ctx): Promise<NameRow> {
  const fails = await peek(ctx, "key-fail", ctx.ip, KEY_FAILURES, HOUR);
  if (fails.limited) throw limitedError(fails);
  const body = jsonBody(ctx);
  const name = typeof body.name === "string" ? body.name : "";
  const key = KEY_RE.exec(ctx.req.headers.get("Authorization") ?? "")?.[1] ?? null;
  const row = NAME_RE.test(name) ? await findName(ctx, name) : null;
  const ok = key !== null && row !== null && timingSafeEqual(await sha256(key), blobBytes(row.manage_key_hash));
  if (!ok || !row) {
    await hit(ctx, "key-fail", ctx.ip, KEY_FAILURES, HOUR);
    throw new HttpError(401, "unauthorized");
  }
  return row;
}

export async function rotateRoute(ctx: Ctx): Promise<Response> {
  const row = await authorize(ctx);
  if (row.status !== "active" || !row.tunnel_id) throw new HttpError(409, "name_busy");
  await enforce(ctx, "rotate", row.name, 5, HOUR);
  let token: string;
  try {
    await ctx.cf.newSecret(row.tunnel_id);
    token = await ctx.cf.tunnelToken(row.tunnel_id);
  } catch (e) {
    const err = e instanceof CloudflareError ? e : new CloudflareError("worker", 0, []);
    await logEvent(ctx.env, ctx.now, "rotate_failed", row.name, { step: err.step, status: err.status, codes: err.codes });
    throw new HttpError(502, "cloudflare_failed", "Cloudflare did not make a new token. Try again in a few minutes.");
  }
  // Whoever still runs the old token is cut off now; the nest reconnects as soon as it has the new one.
  try {
    await ctx.cf.dropConnections(row.tunnel_id);
  } catch (e) {
    await logEvent(ctx.env, ctx.now, "rotate_connections_kept", row.name, { step: e instanceof CloudflareError ? e.step : "worker" });
  }
  await logEvent(ctx.env, ctx.now, "rotated", row.name);
  return json(200, { name: row.name, tunnel_token: token });
}

export async function releaseRoute(ctx: Ctx): Promise<Response> {
  const row = await authorize(ctx);
  await ctx.env.DB.prepare("UPDATE names SET status = 'releasing', updated_at = ?2 WHERE name = ?1").bind(row.name, ctx.now).run();
  const result = await teardown(ctx.cf, row.tunnel_id, row.dns_record_id);
  if (result.done) {
    await ctx.env.DB.prepare("DELETE FROM names WHERE name = ?1").bind(row.name).run();
    await logEvent(ctx.env, ctx.now, "released", row.name);
    return noContent();
  }
  await ctx.env.DB.prepare("UPDATE names SET status = 'broken', updated_at = ?2 WHERE name = ?1").bind(row.name, ctx.now).run();
  await logEvent(ctx.env, ctx.now, "release_pending", row.name, { failed: result.failed });
  return json(202, {
    name: row.name,
    status: "releasing",
    message: "The name is being given back. Cloudflare is slow to finish, so it becomes free within the hour.",
  });
}
