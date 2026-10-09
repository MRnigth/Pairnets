// Small WebCrypto helpers. No runtime dependencies.

import type { Env } from "./env";

const B64URL = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
const B32_LOWER = "0123456789abcdefghjkmnpqrstvwxyz";

export function utf8(s: string): Uint8Array<ArrayBuffer> {
  const b = new TextEncoder().encode(s);
  const copy = new Uint8Array(b.length);
  copy.set(b);
  return copy;
}

export function randomBytes(n: number): Uint8Array<ArrayBuffer> {
  const b = new Uint8Array(n);
  crypto.getRandomValues(b);
  return b;
}

/** base64url without padding. */
export function b64urlEncode(bytes: Uint8Array): string {
  let out = "";
  let i = 0;
  for (; i + 2 < bytes.length; i += 3) {
    const n = (bytes[i] << 16) | (bytes[i + 1] << 8) | bytes[i + 2];
    out += B64URL[(n >> 18) & 63] + B64URL[(n >> 12) & 63] + B64URL[(n >> 6) & 63] + B64URL[n & 63];
  }
  const rest = bytes.length - i;
  if (rest === 1) {
    const n = bytes[i] << 16;
    out += B64URL[(n >> 18) & 63] + B64URL[(n >> 12) & 63];
  } else if (rest === 2) {
    const n = (bytes[i] << 16) | (bytes[i + 1] << 8);
    out += B64URL[(n >> 18) & 63] + B64URL[(n >> 12) & 63] + B64URL[(n >> 6) & 63];
  }
  return out;
}

/** Decodes base64url without padding, or null. */
export function b64urlDecode(s: string): Uint8Array<ArrayBuffer> | null {
  if (!/^[A-Za-z0-9_-]*$/.test(s) || s.length % 4 === 1) return null;
  const bin = atob(s.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((s.length + 3) % 4));
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return b64urlEncode(out) === s ? out : null;
}

/** Standard base64 with padding (what the Cloudflare API takes for a tunnel secret). */
export function b64Encode(bytes: Uint8Array): string {
  let bin = "";
  for (const b of bytes) bin += String.fromCharCode(b);
  return btoa(bin);
}

/** base64url of n random bytes. */
export function randomToken(n: number): string {
  return b64urlEncode(randomBytes(n));
}

/** n random characters of a lowercase 32-character alphabet (one per byte, `& 31`: uniform). */
export function randomBase32(n: number): string {
  const b = randomBytes(n);
  let s = "";
  for (let i = 0; i < n; i++) s += B32_LOWER[b[i] & 31];
  return s;
}

/** n random decimal digits, each uniform (bytes of 250 and up are thrown away). */
export function randomDigits(n: number): string {
  let s = "";
  while (s.length < n) {
    for (const b of randomBytes(n * 2)) {
      if (b < 250 && s.length < n) s += String(b % 10);
    }
  }
  return s;
}

export async function sha256(data: string | Uint8Array): Promise<Uint8Array<ArrayBuffer>> {
  const bytes = typeof data === "string" ? utf8(data) : data;
  return new Uint8Array(await crypto.subtle.digest("SHA-256", bytes as Uint8Array<ArrayBuffer>));
}

let keyCache: { source: string; key: Promise<CryptoKey> } | null = null;

/** HASH_KEY as an HMAC key: base64url of 32 bytes. */
function hashKey(env: Env): Promise<CryptoKey> {
  const source = env.HASH_KEY ?? "";
  if (keyCache && keyCache.source === source) return keyCache.key;
  const bytes = b64urlDecode(source);
  if (!bytes || bytes.length !== 32) return Promise.reject(new Error("HASH_KEY must be base64url of 32 bytes"));
  const key = crypto.subtle.importKey("raw", bytes, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  keyCache = { source, key };
  return key;
}

/** HMAC-SHA256(HASH_KEY, purpose + "\n" + value). */
export async function keyedMac(env: Env, purpose: string, value: string): Promise<Uint8Array<ArrayBuffer>> {
  return new Uint8Array(await crypto.subtle.sign("HMAC", await hashKey(env), utf8(`${purpose}\n${value}`)));
}

/** A short keyed hash for rate-limit buckets and the per-address count: no raw IP or address is stored. */
export async function keyedHash(env: Env, purpose: string, value: string): Promise<string> {
  return b64urlEncode(await keyedMac(env, purpose, value)).slice(0, 22);
}

/** Constant-time comparison of two byte strings (different lengths compare unequal). */
export function timingSafeEqual(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a[i] ^ b[i];
  return diff === 0;
}
