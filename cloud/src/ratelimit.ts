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

/**
 * What a per-address limit counts for a caller's address. An IPv4 address stands for itself. An IPv6 customer is given
 * a whole /64 (2^64 addresses) and can pick any of them at will, so every IPv6 address is counted as its /64 prefix.
 * An IPv4-mapped IPv6 address (::ffff:203.0.113.7) is the IPv4 address. Anything that does not parse is used as it is.
 */
export function limitAddress(ip: string): string {
  let a = ip.trim();
  if (a.startsWith("[") && a.endsWith("]")) a = a.slice(1, -1);
  const zone = a.indexOf("%");
  if (zone >= 0) a = a.slice(0, zone);
  if (!a.includes(":")) return ip;
  // A dotted IPv4 tail is two more 16-bit groups.
  const tail = /^(.*:)(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(a);
  if (tail) {
    const o = tail.slice(2).map(Number);
    if (o.some((x) => x > 255)) return ip;
    a = `${tail[1]}${((o[0] << 8) | o[1]).toString(16)}:${((o[2] << 8) | o[3]).toString(16)}`;
  }
  const halves = a.split("::");
  if (halves.length > 2) return ip;
  const head = halves[0] ? halves[0].split(":") : [];
  const rest = halves.length === 2 && halves[1] ? halves[1].split(":") : [];
  const missing = 8 - head.length - rest.length;
  if (halves.length === 1 ? missing !== 0 : missing < 1) return ip;
  const groups = [...head, ...Array<string>(halves.length === 2 ? missing : 0).fill("0"), ...rest];
  if (groups.length !== 8 || !groups.every((g) => /^[0-9a-fA-F]{1,4}$/.test(g))) return ip;
  const n = groups.map((g) => Number.parseInt(g, 16));
  // ::ffff:a.b.c.d is IPv4 in an IPv6 coat.
  if (n.slice(0, 5).every((x) => x === 0) && n[5] === 0xffff) return `${n[6] >> 8}.${n[6] & 255}.${n[7] >> 8}.${n[7] & 255}`;
  return `${n.slice(0, 4).map((x) => x.toString(16)).join(":")}::/64`;
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

/**
 * Counts one event and tells whether the limit is exceeded. Once a bucket has reached its limit nothing is written
 * any more (the update only applies while the count is below it), so a flood that is already refused costs reads, not
 * database writes. A bucket therefore stops at `limit`; a refused event is answered by the missing row.
 */
export async function hit(ctx: Ctx, event: string, value: string, limit: number, windowSec: number): Promise<LimitResult> {
  const [bucket, windowStart] = await bucketOf(ctx, event, value, windowSec);
  const row = await ctx.env.DB.prepare(
    `INSERT INTO rate_counters (bucket, window_start, count) VALUES (?1, ?2, 1)
     ON CONFLICT (bucket, window_start) DO UPDATE SET count = count + 1 WHERE count < ?3
     RETURNING count`,
  )
    .bind(bucket, windowStart, limit)
    .first<{ count: number }>();
  const count = row ? row.count : limit + 1;
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
