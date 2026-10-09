// Small WebCrypto helpers. No runtime dependencies.

import { b64urlEncode, utf8 } from "./b64";

export const B32_LOWER = "0123456789abcdefghjkmnpqrstvwxyz";
export const B32_UPPER = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

export function randomBytes(n: number): Uint8Array<ArrayBuffer> {
  const b = new Uint8Array(n);
  crypto.getRandomValues(b);
  return b;
}

/** base64url of n random bytes. */
export function randomToken(n: number): string {
  return b64urlEncode(randomBytes(n));
}

/** n random characters from a 32-character alphabet, one per random byte (`alphabet[byte & 31]`, uniform). */
export function randomBase32(n: number, alphabet: string): string {
  const b = randomBytes(n);
  let s = "";
  for (let i = 0; i < n; i++) s += alphabet[b[i] & 31];
  return s;
}

export function newId(prefix: "acc" | "nst" | "ses" | "chl" | "dvl" | "svl"): string {
  return `${prefix}_${randomBase32(26, B32_LOWER)}`;
}

function bytesOf(data: string | Uint8Array): Uint8Array<ArrayBuffer> {
  const b = typeof data === "string" ? utf8(data) : data;
  // Copy into a plain ArrayBuffer-backed view so WebCrypto's BufferSource typing is happy everywhere.
  const copy = new Uint8Array(b.length);
  copy.set(b);
  return copy;
}

export async function sha256(data: string | Uint8Array): Promise<Uint8Array<ArrayBuffer>> {
  return new Uint8Array(await crypto.subtle.digest("SHA-256", bytesOf(data)));
}

export function hex(bytes: Uint8Array): string {
  let s = "";
  for (let i = 0; i < bytes.length; i++) s += bytes[i].toString(16).padStart(2, "0");
  return s;
}

export async function sha256Hex(data: string | Uint8Array): Promise<string> {
  return hex(await sha256(data));
}

export async function hmacKey(keyBytes: Uint8Array): Promise<CryptoKey> {
  return crypto.subtle.importKey("raw", bytesOf(keyBytes), { name: "HMAC", hash: "SHA-256" }, false, ["sign", "verify"]);
}

export async function hmacSha256(keyBytes: Uint8Array, message: string | Uint8Array): Promise<Uint8Array<ArrayBuffer>> {
  const key = await hmacKey(keyBytes);
  return new Uint8Array(await crypto.subtle.sign("HMAC", key, bytesOf(message)));
}

/** Constant-time HMAC check (crypto.subtle.verify). */
export async function hmacVerify(keyBytes: Uint8Array, message: string | Uint8Array, mac: Uint8Array): Promise<boolean> {
  const key = await hmacKey(keyBytes);
  return crypto.subtle.verify("HMAC", key, bytesOf(mac), bytesOf(message));
}

/** Constant-time comparison of two byte strings (different lengths compare unequal). */
export function timingSafeEqual(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a[i] ^ b[i];
  return diff === 0;
}

export function timingSafeEqualText(a: string, b: string): boolean {
  return timingSafeEqual(utf8(a), utf8(b));
}
