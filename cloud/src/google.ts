// Google sign-in (CONTRACT.md section 6.5): the signed flow cookie, the code exchange and the id_token check against
// Google's JWKS (RS256, cached at most 1 hour).

import { b64urlDecodeStrict, b64urlEncode, b64urlEncodeText, utf8 } from "./b64";
import type { Ctx } from "./context";
import type { Env } from "./env";
import { timingSafeEqualText } from "./crypto";
import { normaliseEmail } from "./formats";
import { cookieKey } from "./ratelimit";

export const DEFAULT_GOOGLE_AUTH_URL = "https://accounts.google.com/o/oauth2/v2/auth";
export const DEFAULT_GOOGLE_TOKEN_URL = "https://oauth2.googleapis.com/token";
export const DEFAULT_GOOGLE_JWKS_URL = "https://www.googleapis.com/oauth2/v3/certs";
export const GOOGLE_ISSUERS = new Set(["https://accounts.google.com", "accounts.google.com"]);
export const GOOGLE_FLOW_LIFETIME = 600;
const JWKS_MAX_AGE = 3600;
const JWKS_REFRESH_MIN = 60;

/** Payload of the __Host-pn_g cookie: state, nonce, PKCE verifier, next, expiry, reauth flag. */
export interface GoogleFlow {
  s: string;
  n: string;
  v: string;
  r: string;
  e: number;
  ra: 0 | 1;
}

/** base64url(JSON payload) + "." + base64url(HMAC-SHA256(COOKIE_KEY, "pn-g1\n" + payloadPart)). */
export async function signFlowCookie(env: Env, flow: GoogleFlow): Promise<string> {
  const payloadPart = b64urlEncodeText(JSON.stringify(flow));
  const mac = await crypto.subtle.sign("HMAC", await cookieKey(env), utf8(`pn-g1\n${payloadPart}`) as Uint8Array<ArrayBuffer>);
  return `${payloadPart}.${b64urlEncode(new Uint8Array(mac))}`;
}

/** Checks the cookie's signature (constant time) and shape. Expiry is checked by the caller. */
export async function readFlowCookie(env: Env, value: string | null): Promise<GoogleFlow | null> {
  if (!value || value.length > 2048) return null;
  const dot = value.indexOf(".");
  if (dot < 0) return null;
  const payloadPart = value.slice(0, dot);
  const mac = b64urlDecodeStrict(value.slice(dot + 1));
  const payload = b64urlDecodeStrict(payloadPart);
  if (!mac || mac.length !== 32 || !payload) return null;
  const ok = await crypto.subtle.verify("HMAC", await cookieKey(env), mac as Uint8Array<ArrayBuffer>, utf8(`pn-g1\n${payloadPart}`) as Uint8Array<ArrayBuffer>);
  if (!ok) return null;
  try {
    const f = JSON.parse(new TextDecoder().decode(payload)) as Partial<GoogleFlow>;
    if (typeof f.s !== "string" || typeof f.n !== "string" || typeof f.v !== "string" || typeof f.r !== "string" || typeof f.e !== "number") {
      return null;
    }
    return { s: f.s, n: f.n, v: f.v, r: f.r, e: f.e, ra: f.ra === 1 ? 1 : 0 };
  } catch {
    return null;
  }
}

let jwksCache: { url: string; fetchedAt: number; keys: Map<string, CryptoKey> } | null = null;

/** Tests only: forget the cached Google keys. */
export function resetGoogleKeyCache(): void {
  jwksCache = null;
}

async function fetchJwks(ctx: Ctx, url: string): Promise<Map<string, CryptoKey>> {
  const keys = new Map<string, CryptoKey>();
  const resp = await ctx.deps.fetch(url, { headers: { Accept: "application/json" } });
  if (!resp.ok) throw new Error(`Google keys answered ${resp.status}`);
  const data = (await resp.json()) as { keys?: unknown };
  if (!Array.isArray(data.keys)) throw new Error("Google keys have no keys array");
  for (const k of data.keys as Record<string, unknown>[]) {
    if (!k || k.kty !== "RSA" || typeof k.kid !== "string" || typeof k.n !== "string" || typeof k.e !== "string") continue;
    if (k.use !== undefined && k.use !== "sig") continue;
    if (k.alg !== undefined && k.alg !== "RS256") continue;
    try {
      const key = await crypto.subtle.importKey(
        "jwk",
        { kty: "RSA", n: k.n, e: k.e, alg: "RS256", ext: true },
        { name: "RSASSA-PKCS1-v1_5", hash: "SHA-256" },
        false,
        ["verify"],
      );
      keys.set(k.kid, key);
    } catch {
      // skip keys that do not import
    }
  }
  return keys;
}

async function googleKey(ctx: Ctx, kid: string): Promise<CryptoKey | null> {
  const url = ctx.env.GOOGLE_JWKS_URL || DEFAULT_GOOGLE_JWKS_URL;
  if (!jwksCache || jwksCache.url !== url || ctx.now - jwksCache.fetchedAt >= JWKS_MAX_AGE || ctx.now < jwksCache.fetchedAt) {
    jwksCache = { url, fetchedAt: ctx.now, keys: await fetchJwks(ctx, url) };
  }
  let key = jwksCache.keys.get(kid);
  if (!key && ctx.now - jwksCache.fetchedAt >= JWKS_REFRESH_MIN) {
    // Google rotated its keys: fetch again, at most once a minute.
    jwksCache = { url, fetchedAt: ctx.now, keys: await fetchJwks(ctx, url) };
    key = jwksCache.keys.get(kid);
  }
  return key ?? null;
}

function decodeJsonPart(part: string): Record<string, unknown> | null {
  const bytes = b64urlDecodeStrict(part);
  if (!bytes) return null;
  try {
    const v: unknown = JSON.parse(new TextDecoder("utf-8", { fatal: true, ignoreBOM: false }).decode(bytes));
    return v && typeof v === "object" && !Array.isArray(v) ? (v as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

export interface GoogleIdentity {
  sub: string;
  email: string;
}

/**
 * Verifies an id_token: RS256 signature against Google's JWKS, iss, aud == GOOGLE_CLIENT_ID, exp > now - 60, nonce,
 * email_verified == true, sub present, and auth_time >= flow start for a re-auth. Returns null on any failure.
 */
export async function verifyGoogleIdToken(ctx: Ctx, idToken: string, flow: GoogleFlow): Promise<GoogleIdentity | null> {
  if (typeof idToken !== "string" || idToken.length > 8192) return null;
  const parts = idToken.split(".");
  if (parts.length !== 3) return null;
  const header = decodeJsonPart(parts[0]);
  if (!header || header.alg !== "RS256" || typeof header.kid !== "string") return null;
  const sig = b64urlDecodeStrict(parts[2]);
  if (!sig) return null;
  let key: CryptoKey | null;
  try {
    key = await googleKey(ctx, header.kid);
  } catch {
    return null;
  }
  if (!key) return null;
  const ok = await crypto.subtle.verify(
    "RSASSA-PKCS1-v1_5",
    key,
    sig as Uint8Array<ArrayBuffer>,
    utf8(`${parts[0]}.${parts[1]}`) as Uint8Array<ArrayBuffer>,
  );
  if (!ok) return null;
  const c = decodeJsonPart(parts[1]);
  if (!c) return null;
  if (typeof c.iss !== "string" || !GOOGLE_ISSUERS.has(c.iss)) return null;
  if (typeof c.aud !== "string" || c.aud !== ctx.env.GOOGLE_CLIENT_ID) return null;
  if (typeof c.exp !== "number" || !(c.exp > ctx.now - 60)) return null;
  if (typeof c.nonce !== "string" || !timingSafeEqualText(c.nonce, flow.n)) return null;
  if (c.email_verified !== true) return null;
  if (typeof c.sub !== "string" || c.sub.length === 0 || c.sub.length > 255) return null;
  const email = normaliseEmail(c.email);
  if (!email) return null;
  if (flow.ra === 1) {
    const flowStart = flow.e - GOOGLE_FLOW_LIFETIME;
    if (typeof c.auth_time !== "number" || c.auth_time < flowStart) return null;
  }
  return { sub: c.sub, email };
}
