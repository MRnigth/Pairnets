// POST /v1/claim: a nest redeems a claim code (CONTRACT.md section 4.2). No cookies, no Origin: called by servers.

import { b64urlEncode } from "../b64";
import { audit } from "../audit";
import type { Ctx } from "../context";
import { newId, sha256 } from "../crypto";
import { checkPublicUrl, cleanLabel, cleanText, maskEmail, normaliseClaimCode } from "../formats";
import { HttpError, json } from "../http";
import { decodeMaster, deriveHeartbeatKey } from "../nestsig";
import { HOUR, hit, limitedError, peek } from "../ratelimit";
import { jsonBody } from "../request";
import { blob } from "../sessions";

const CLAIMS_PER_HOUR = 20;
const FAILURES_PER_HOUR = 10;

export async function claimRoute(ctx: Ctx): Promise<Response> {
  // Per-IP limits first: 20 claims an hour, and no more once 10 have failed this hour.
  const failures = await peek(ctx, "claim-fail", ctx.ip, FAILURES_PER_HOUR, HOUR);
  if (failures.limited) throw limitedError(failures);
  const claims = await hit(ctx, "claim-ip", ctx.ip, CLAIMS_PER_HOUR, HOUR);
  if (claims.limited) throw limitedError(claims);
  try {
    return await redeem(ctx);
  } catch (e) {
    if (e instanceof HttpError && e.status >= 400 && e.status < 500 && e.status !== 429) {
      await hit(ctx, "claim-fail", ctx.ip, FAILURES_PER_HOUR, HOUR);
    }
    throw e;
  }
}

async function redeem(ctx: Ctx): Promise<Response> {
  const b = jsonBody(ctx);
  if (typeof b.code !== "string" || typeof b.publicUrl !== "string" || typeof b.serverVersion !== "string") {
    throw new HttpError(400, "bad_request");
  }
  const serverVersion = cleanText(b.serverVersion, 32);
  const label = cleanLabel(b.label);
  if (serverVersion === "invalid" || label === "invalid") throw new HttpError(400, "bad_request");

  const code = normaliseClaimCode(b.code);
  if (!code) throw new HttpError(400, "invalid_code");
  // The address is checked before the code is used, so a typo in the URL does not burn the code.
  const url = checkPublicUrl(b.publicUrl);
  if (!url) throw new HttpError(400, "bad_url");

  const codeHash = blob(await sha256(code));
  const nestId = newId("nst");
  const db = ctx.env.DB;
  // Atomic redeem: one batch is one transaction. Two parallel claims of one code: exactly one INSERT changes a row.
  const [insert] = await db.batch([
    db
      .prepare(
        `INSERT INTO nests (id, account_id, label, public_url, status, key_version, hosted_login, created_at, last_req_ts)
           SELECT ?1, account_id, COALESCE(?2, label, ?3), ?4, 'pending', 1, 1, ?5, 0
           FROM claim_codes
           WHERE code_hash = ?6 AND used_at IS NULL AND expires_at > ?5
             AND (SELECT COUNT(*) FROM nests n WHERE n.account_id = claim_codes.account_id) < 3`,
      )
      .bind(nestId, label, url.host, url.url, ctx.now, codeHash),
    db
      .prepare(
        `UPDATE claim_codes SET used_at = ?5, nest_id = ?1
           WHERE code_hash = ?6 AND used_at IS NULL AND expires_at > ?5
             AND EXISTS (SELECT 1 FROM nests WHERE id = ?1)`,
      )
      .bind(nestId, null, null, null, ctx.now, codeHash),
  ]);

  if (insert.meta.changes !== 1) {
    const row = await db.prepare("SELECT used_at, expires_at FROM claim_codes WHERE code_hash = ?1").bind(codeHash).first<{
      used_at: number | null;
      expires_at: number;
    }>();
    if (!row) throw new HttpError(400, "invalid_code");
    if (row.used_at !== null) throw new HttpError(409, "used");
    if (row.expires_at <= ctx.now) throw new HttpError(400, "expired");
    throw new HttpError(409, "nest_limit");
  }

  const nest = await db
    .prepare("SELECT n.account_id AS account_id, a.email AS email FROM nests n JOIN accounts a ON a.id = n.account_id WHERE n.id = ?1")
    .bind(nestId)
    .first<{ account_id: string; email: string }>();
  if (!nest) throw new Error("claimed nest vanished");
  await audit(ctx, "nest_claimed", nest.account_id, nestId, { serverVersion });

  const key = await deriveHeartbeatKey(decodeMaster(ctx.env.HB_MASTER), nestId, 1);
  return json(201, {
    nestId,
    accountId: nest.account_id,
    maskedEmail: maskEmail(nest.email),
    heartbeatKey: b64urlEncode(key),
    keyVersion: 1,
  });
}
