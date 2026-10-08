// Fixed-window rate limits in D1 (CONTRACT.md section 6.10). The bucket is the event plus the first 22 characters of
// base64url(HMAC-SHA256(COOKIE_KEY, "pn-rl\n" + value)), so no raw IP or address is stored.

import { b64urlDecodeStrict, b64urlEncode, utf8 } from "./b64";
import type { Ctx } from "./context";
import type { Env } from "./env";
import { HttpError, type ErrorCode } from "./http";

let keyCache: { source: string; key: Promise<CryptoKey> } | null = null;

export function cookieKey(env: Env): Promise<CryptoKey> {
  const source = env.COOKIE_KEY ?? "";
  if (keyCache && keyCache.source === source) return keyCache.key;
  const bytes = b64urlDecodeStrict(source);
  if (!bytes || bytes.length !== 32) return Promise.reject(new Error("COOKIE_KEY must be base64url of 32 bytes"));
  const key = crypto.subtle.importKey("raw", bytes as Uint8Array<ArrayBuffer>, { name: "HMAC", hash: "SHA-256" }, false, ["sign", "verify"]);
  keyCache = { source, key };
  return key;
}

export async function keyedHash(env: Env, value: string): Promise<string> {
  const mac = await crypto.subtle.sign("HMAC", await cookieKey(env), utf8(`pn-rl\n${value}`) as Uint8Array<ArrayBuffer>);
  return b64urlEncode(new Uint8Array(mac)).slice(0, 22);
}

export interface LimitResult {
  limited: boolean;
  count: number;
  retryAfter: number;
}

async function bucketOf(ctx: Ctx, event: string, value: string, windowSec: number): Promise<[string, number]> {
  const bucket = `${event}:${await keyedHash(ctx.env, value)}`;
  const windowStart = Math.floor(ctx.now / windowSec) * windowSec;
  return [bucket, windowStart];
}

/** Counts one event and tells whether the limit is now exceeded. */
export async function hit(ctx: Ctx, event: string, value: string, limit: number, windowSec: number): Promise<LimitResult> {
  const [bucket, windowStart] = await bucketOf(ctx, event, value, windowSec);
  const row = await ctx.env.DB.prepare(
    `INSERT INTO rate_counters (bucket, window_start, count) VALUES (?1, ?2, 1)
     ON CONFLICT (bucket, window_start) DO UPDATE SET count = count + 1
     RETURNING count`,
  )
    .bind(bucket, windowStart)
    .first<{ count: number }>();
  const count = row?.count ?? 1;
  return { limited: count > limit, count, retryAfter: Math.max(1, windowStart + windowSec - ctx.now) };
}

/** Reads a counter without counting (for "N failures per hour" style limits). */
export async function peek(ctx: Ctx, event: string, value: string, limit: number, windowSec: number): Promise<LimitResult> {
  const [bucket, windowStart] = await bucketOf(ctx, event, value, windowSec);
  const row = await ctx.env.DB.prepare("SELECT count FROM rate_counters WHERE bucket = ?1 AND window_start = ?2")
    .bind(bucket, windowStart)
    .first<{ count: number }>();
  const count = row?.count ?? 0;
  return { limited: count >= limit, count, retryAfter: Math.max(1, windowStart + windowSec - ctx.now) };
}

export function limitedError(r: LimitResult, code: ErrorCode = "rate_limited"): HttpError {
  return new HttpError(429, code, undefined, { "Retry-After": String(r.retryAfter) });
}

/** Counts and throws 429 when over the limit. */
export async function enforce(ctx: Ctx, event: string, value: string, limit: number, windowSec: number, code: ErrorCode = "rate_limited"): Promise<void> {
  const r = await hit(ctx, event, value, limit, windowSec);
  if (r.limited) throw limitedError(r, code);
}

export const MINUTE = 60;
export const HOUR = 3600;
export const DAY = 86400;
