// Nest-signed requests: POST /v1/nest/confirm, /v1/nest/unlink, /v1/heartbeat (CONTRACT.md section 5).
// No cookies, no Origin: the nest proves itself with the HMAC of its heartbeat key.

import { audit } from "../audit";
import type { NestRow } from "./account";
import type { Ctx } from "../context";
import { cleanText, NEST_ID_RE } from "../formats";
import { HttpError } from "../http";
import { sendNotice } from "../mail";
import { decodeMaster, deriveHeartbeatKey, type NestPurpose, signResponse, verifyRequest } from "../nestsig";
import { hit, HOUR, limitedError } from "../ratelimit";
import { jsonBody } from "../request";

export const MAX_SIGNED_BODY = 1024;
export const SIGNED_WINDOW = 300;
export const PENDING_LIFETIME = 3600;
const TS_RE = /^[1-9][0-9]{0,15}$/;

interface Verified {
  nest: NestRow;
  key: Uint8Array;
  reqTs: number;
  body: Record<string, unknown>;
}

/** Steps 1-5 of section 5.2, in order. */
async function verifySigned(ctx: Ctx, purpose: NestPurpose): Promise<Verified> {
  // 1. The nest exists (a pending nest older than 1 hour counts as gone: it is swept by the hourly cron).
  const nestId = ctx.req.headers.get("X-Pairnets-Nest") ?? "";
  const nest = NEST_ID_RE.test(nestId) ? await ctx.env.DB.prepare("SELECT * FROM nests WHERE id = ?1").bind(nestId).first<NestRow>() : null;
  if (!nest || (nest.status === "pending" && nest.created_at <= ctx.now - PENDING_LIFETIME)) throw new HttpError(410, "unlinked");

  // 2. Signature over the header's exact ts text and the exact body bytes.
  const tsText = ctx.req.headers.get("X-Pairnets-Ts") ?? "";
  const key = await deriveHeartbeatKey(decodeMaster(ctx.env.HB_MASTER), nest.id, nest.key_version);
  if (!(await verifyRequest(key, purpose, nest.id, tsText, ctx.body, ctx.req.headers.get("X-Pairnets-Sig")))) {
    throw new HttpError(401, "bad_signature");
  }

  // 3. Body: JSON (at most 1024 bytes) with v == 1 and ts equal to the header.
  if (ctx.body.length > MAX_SIGNED_BODY || !TS_RE.test(tsText)) throw new HttpError(400, "bad_request");
  const body = jsonBody(ctx);
  const reqTs = Number(tsText);
  if (body.v !== 1 || typeof body.ts !== "number" || !Number.isSafeInteger(reqTs) || body.ts !== reqTs) throw new HttpError(400, "bad_request");

  // 4. Within 5 minutes of the service clock.
  if (Math.abs(ctx.now - reqTs) > SIGNED_WINDOW) throw new HttpError(401, "stale");

  // 5. Strictly increasing ts per nest (replay guard).
  const r = await ctx.env.DB.prepare("UPDATE nests SET last_req_ts = ?1 WHERE id = ?2 AND last_req_ts < ?1").bind(reqTs, nest.id).run();
  if (r.meta.changes !== 1) throw new HttpError(401, "stale");
  return { nest, key, reqTs, body };
}

/** A signed 200 answer: {"v":1,"ts":respTs,"reqTs":...,<fields>} with X-Pairnets-Ts / X-Pairnets-Sig. */
async function signedOk(ctx: Ctx, v: Verified, purpose: NestPurpose, fields: Record<string, unknown>): Promise<Response> {
  const respTs = ctx.now;
  const body = JSON.stringify({ v: 1, ts: respTs, reqTs: v.reqTs, ...fields });
  const sig = await signResponse(v.key, purpose, v.nest.id, respTs, body);
  return new Response(body, {
    status: 200,
    headers: { "Content-Type": "application/json", "X-Pairnets-Ts": String(respTs), "X-Pairnets-Sig": sig },
  });
}

async function accountEmail(ctx: Ctx, accountId: string): Promise<string | null> {
  const a = await ctx.env.DB.prepare("SELECT email FROM accounts WHERE id = ?1").bind(accountId).first<{ email: string }>();
  return a?.email ?? null;
}

async function confirmUnlinkLimit(ctx: Ctx, nestId: string): Promise<void> {
  const r = await hit(ctx, "nest-cu", nestId, 10, HOUR);
  if (r.limited) throw limitedError(r);
}

// POST /v1/nest/confirm
export async function nestConfirmRoute(ctx: Ctx): Promise<Response> {
  const v = await verifySigned(ctx, "nc1");
  const serverVersion = cleanText(v.body.serverVersion, 32);
  if (serverVersion === "invalid") throw new HttpError(400, "bad_request");
  await confirmUnlinkLimit(ctx, v.nest.id);
  if (v.nest.status === "pending") {
    const r = await ctx.env.DB.prepare(
      "UPDATE nests SET status = 'active', confirmed_at = ?1, last_version = COALESCE(?2, last_version) WHERE id = ?3 AND status = 'pending'",
    )
      .bind(ctx.now, serverVersion, v.nest.id)
      .run();
    if (r.meta.changes === 1) {
      await audit(ctx, "nest_confirmed", v.nest.account_id, v.nest.id, { serverVersion });
      const email = await accountEmail(ctx, v.nest.account_id);
      if (email) await sendNotice(ctx, v.nest.account_id, email, "nest_linked", v.nest.public_url);
    }
  }
  return signedOk(ctx, v, "nc1", { status: "active" });
}

// POST /v1/nest/unlink
export async function nestUnlinkRoute(ctx: Ctx): Promise<Response> {
  const v = await verifySigned(ctx, "nu1");
  const reason = v.body.reason;
  if (reason !== "owner" && reason !== "declined") throw new HttpError(400, "bad_request");
  await confirmUnlinkLimit(ctx, v.nest.id);
  await ctx.env.DB.prepare("DELETE FROM nests WHERE id = ?1").bind(v.nest.id).run();
  await audit(ctx, "nest_unlinked", v.nest.account_id, v.nest.id, { reason });
  if (v.nest.status === "active") {
    const email = await accountEmail(ctx, v.nest.account_id);
    if (email) await sendNotice(ctx, v.nest.account_id, email, "nest_unlinked", v.nest.public_url);
  }
  return signedOk(ctx, v, "nu1", { status: "removed" });
}

// POST /v1/heartbeat
export async function heartbeatRoute(ctx: Ctx): Promise<Response> {
  const v = await verifySigned(ctx, "hb1");
  if (v.nest.status !== "active") throw new HttpError(409, "not_confirmed");
  const b = v.body;
  const serverVersion = cleanText(b.serverVersion, 32);
  const publicHost = b.publicHost === null ? null : cleanText(b.publicHost, 253);
  if (
    typeof b.serverVersion !== "string" ||
    serverVersion === "invalid" ||
    typeof b.ready !== "boolean" ||
    (b.publicHost !== null && typeof b.publicHost !== "string") ||
    publicHost === "invalid" ||
    typeof b.hostedLogin !== "boolean"
  ) {
    throw new HttpError(400, "bad_request");
  }
  const r = await hit(ctx, "hb", v.nest.id, 1, 60);
  if (r.limited) throw limitedError(r);
  await ctx.env.DB.prepare(
    `UPDATE nests SET last_seen_at = ?1, last_version = ?2, last_ready = ?3, last_public_host = ?4, last_hosted_login = ?5
     WHERE id = ?6`,
  )
    .bind(ctx.now, serverVersion, b.ready ? 1 : 0, publicHost, b.hostedLogin ? 1 : 0, v.nest.id)
    .run();
  return signedOk(ctx, v, "hb1", { disableHostedLogin: ctx.env.HOSTED_LOGIN_DISABLED === "1" });
}
