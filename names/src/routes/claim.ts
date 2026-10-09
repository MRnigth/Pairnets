// Claiming a name takes two calls, both made by the installer (deploy/pairnets-name.sh):
//
//   POST /v1/claim/start  {"name": "alice", "email": "you@example.com"}  -> 202 {"claim_id", "expires_in"}
//        emails a 6-digit code to the address.
//   POST /v1/claim        {"claim_id": "...", "code": "123456"}          -> 201 {"name", "public_url", "tunnel_token", "manage_key"}
//        makes the tunnel, points it at the nest, adds the DNS record, and answers the tunnel token and the manage key.
//        This is the only time either is sent; neither is stored (only a hash of the key).
//
// If any Cloudflare step fails, everything made so far is deleted again, so a name is never half-claimed. When even
// that fails, the row stays as 'broken' (the name is not handed out) and the hourly sweep finishes the cleanup.

import type { Ctx } from "../context";
import { CloudflareError, teardown } from "../cloudflare";
import { keyedHash, keyedMac, randomBase32, randomDigits, randomToken, sha256, timingSafeEqual } from "../crypto";
import { logEvent } from "../events";
import { HttpError, json } from "../http";
import { codeEmail, reserveMail } from "../mail";
import { checkName, normalizeEmail } from "../names";
import { blobBytes, jsonBody } from "../request";
import { DAY, enforce, HOUR } from "../ratelimit";
import { checkRoom, findName, isPaused } from "../store";

/** How long an emailed code works. */
export const CLAIM_TTL = 15 * 60;
/** Wrong codes allowed for one claim. */
export const MAX_TRIES = 5;

const CLAIM_ID_RE = /^pnc_[A-Za-z0-9_-]{24}$/;

function codeMac(ctx: Ctx, claimId: string, code: string): Promise<Uint8Array> {
  return keyedMac(ctx.env, "pn-code", `${claimId}\n${code}`);
}

export async function claimStartRoute(ctx: Ctx): Promise<Response> {
  if (isPaused(ctx)) throw new HttpError(503, "paused");
  const body = jsonBody(ctx);
  const check = checkName(body.name);
  if (!check.ok) throw new HttpError(check.code === "bad_name" ? 400 : 409, check.code);
  const name = check.name;
  const email = normalizeEmail(body.email);
  if (!email) throw new HttpError(400, "bad_email");

  await enforce(ctx, "start-ip", ctx.ip, 5, HOUR);
  await enforce(ctx, "start-email", email, 3, DAY);
  const ipBucket = await keyedHash(ctx.env, "pn-ip", ctx.ip);
  await checkRoom(ctx, ipBucket);
  if (await findName(ctx, name)) throw new HttpError(409, "name_taken");
  // Whether this address already has a name is only told after the code proves it is theirs (no guessing of who
  // has a name).
  if (!(await reserveMail(ctx))) throw new HttpError(429, "mail_limit");

  const claimId = `pnc_${randomToken(18)}`;
  const code = randomDigits(6);
  await ctx.env.DB.prepare(
    "INSERT INTO claims (id, name, email, code_mac, attempts, ip_bucket, created_at, expires_at) VALUES (?1, ?2, ?3, ?4, 0, ?5, ?6, ?7)",
  )
    .bind(claimId, name, email, await codeMac(ctx, claimId, code), ipBucket, ctx.now, ctx.now + CLAIM_TTL)
    .run();
  try {
    await ctx.mailer.send(codeEmail(email, `${name}.${ctx.env.BASE_DOMAIN}`, code));
  } catch (e) {
    console.error("mail: sending failed", e instanceof Error ? e.message : "unknown error");
    await ctx.env.DB.prepare("DELETE FROM claims WHERE id = ?1").bind(claimId).run();
    throw new HttpError(502, "mail_failed");
  }
  await logEvent(ctx.env, ctx.now, "code_sent", name);
  return json(202, { claim_id: claimId, expires_in: CLAIM_TTL });
}

interface ClaimRow {
  id: string;
  name: string;
  email: string;
  code_mac: unknown;
  attempts: number;
  ip_bucket: string;
  expires_at: number;
}

export async function claimRoute(ctx: Ctx): Promise<Response> {
  if (isPaused(ctx)) throw new HttpError(503, "paused");
  const body = jsonBody(ctx);
  const claimId = typeof body.claim_id === "string" ? body.claim_id : "";
  const code = typeof body.code === "string" ? body.code.replace(/\s+/g, "") : "";
  if (!CLAIM_ID_RE.test(claimId)) throw new HttpError(400, "bad_request");
  await enforce(ctx, "claim-ip", ctx.ip, 20, HOUR);

  const db = ctx.env.DB;
  const dropClaim = () => db.prepare("DELETE FROM claims WHERE id = ?1").bind(claimId).run();
  const claim = await db.prepare("SELECT * FROM claims WHERE id = ?1").bind(claimId).first<ClaimRow>();
  if (!claim || claim.expires_at <= ctx.now) {
    if (claim) await dropClaim();
    throw new HttpError(400, "expired");
  }
  const right = /^[0-9]{6}$/.test(code) && timingSafeEqual(await codeMac(ctx, claimId, code), blobBytes(claim.code_mac));
  if (!right) {
    const row = await db.prepare("UPDATE claims SET attempts = attempts + 1 WHERE id = ?1 RETURNING attempts").bind(claimId).first<{ attempts: number }>();
    const left = MAX_TRIES - (row?.attempts ?? MAX_TRIES);
    if (left <= 0) {
      await dropClaim();
      throw new HttpError(429, "too_many_tries");
    }
    throw new HttpError(400, "wrong_code", `That code is not right (${left} ${left === 1 ? "try" : "tries"} left).`);
  }
  // The code is used up whatever happens next.
  await dropClaim();

  const { name, email } = claim;
  const check = checkName(name);
  if (!check.ok) throw new HttpError(409, check.code);
  if (await db.prepare("SELECT 1 FROM names WHERE email = ?1").bind(email).first()) throw new HttpError(409, "email_has_name");
  if (await findName(ctx, name)) throw new HttpError(409, "name_taken");
  await checkRoom(ctx, claim.ip_bucket);
  try {
    await db
      .prepare("INSERT INTO names (name, status, email, ip_bucket, created_at, updated_at) VALUES (?1, 'creating', ?2, ?3, ?4, ?4)")
      .bind(name, email, claim.ip_bucket, ctx.now)
      .run();
  } catch {
    // Someone else's claim for the same name (or address) got there a moment earlier.
    throw new HttpError(409, "name_taken");
  }

  const host = `${name}.${ctx.env.BASE_DOMAIN}`;
  let tunnelId: string | null = null;
  let dnsRecordId: string | null = null;
  let token: string;
  try {
    tunnelId = await ctx.cf.createTunnel(`pairnets-${name}-${randomBase32(6)}`);
    // Each id is saved as soon as it exists, so a Worker that dies half-way leaves the sweep enough to clean up.
    await db.prepare("UPDATE names SET tunnel_id = ?2 WHERE name = ?1").bind(name, tunnelId).run();
    token = await ctx.cf.tunnelToken(tunnelId);
    await ctx.cf.setIngress(tunnelId, host);
    dnsRecordId = await ctx.cf.createDns(host, tunnelId);
    await db.prepare("UPDATE names SET dns_record_id = ?2 WHERE name = ?1").bind(name, dnsRecordId).run();
  } catch (e) {
    const err = e instanceof CloudflareError ? e : new CloudflareError("worker", 0, []);
    const undo = await teardown(ctx.cf, tunnelId, dnsRecordId);
    if (undo.done) {
      await db.prepare("DELETE FROM names WHERE name = ?1").bind(name).run();
    } else {
      await db
        .prepare("UPDATE names SET status = 'broken', tunnel_id = ?2, dns_record_id = ?3, updated_at = ?4 WHERE name = ?1")
        .bind(name, tunnelId, dnsRecordId, ctx.now)
        .run();
    }
    await logEvent(ctx.env, ctx.now, "claim_failed", name, { step: err.step, status: err.status, codes: err.codes, undone: undo.done, undoFailed: undo.failed });
    if (err.isConflict) throw new HttpError(409, "name_taken");
    throw new HttpError(502, "cloudflare_failed");
  }

  const manageKey = `pnk_${randomToken(32)}`;
  await db
    .prepare("UPDATE names SET status = 'active', manage_key_hash = ?2, updated_at = ?3 WHERE name = ?1")
    .bind(name, await sha256(manageKey), ctx.now)
    .run();
  await logEvent(ctx.env, ctx.now, "claimed", name);
  return json(201, { name, public_url: `https://${host}`, tunnel_token: token, manage_key: manageKey });
}
