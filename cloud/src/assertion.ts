// The login assertion (service -> nest), CONTRACT.md section 2.
// Pure WebCrypto: this module is also imported by the Node-side golden-vector generator, so it must not import
// anything Worker-specific.

import { b64urlEncode, b64urlEncodeText, utf8 } from "./b64";
import { KID_RE } from "./formats";

export const ISSUER = "https://id.pairnets.app";
export const ASSERTION_TYP = "pn-login+jwt";
export const ASSERTION_LIFETIME = 90;
export const MAX_JWS_LENGTH = 4096;

export interface SigningKey {
  kid: string;
  key: CryptoKey;
}

export interface AssertionInput {
  nestId: string;
  accountId: string;
  nonce: string;
  amr: string[];
  authTime: number;
  /** Issue time (Unix seconds). */
  iat: number;
  /** base64url(16 random bytes) unless given (the golden vectors fix it). */
  jti?: string;
}

const P256 = { name: "ECDSA", namedCurve: "P-256" } as const;

/** Imports the private JWK held in the SIGNING_KEY secret; the kid comes from the JWK. */
export async function importSigningJwk(jwk: unknown): Promise<SigningKey> {
  if (!jwk || typeof jwk !== "object") throw new Error("SIGNING_KEY is not a JWK object");
  const j = jwk as Record<string, unknown>;
  if (j.kty !== "EC" || j.crv !== "P-256" || typeof j.d !== "string" || typeof j.x !== "string" || typeof j.y !== "string") {
    throw new Error("SIGNING_KEY must be a private P-256 JWK");
  }
  if (typeof j.kid !== "string" || !KID_RE.test(j.kid)) throw new Error("SIGNING_KEY needs a kid of 1-64 [A-Za-z0-9._-]");
  const key = await crypto.subtle.importKey("jwk", { kty: "EC", crv: "P-256", x: j.x, y: j.y, d: j.d }, P256, false, ["sign"]);
  return { kid: j.kid, key };
}

let cached: { source: string; key: Promise<SigningKey> } | null = null;

/** The SIGNING_KEY secret, imported once per isolate. */
export function signingKeyFromSecret(secret: string): Promise<SigningKey> {
  if (cached && cached.source === secret) return cached.key;
  const key = (async () => importSigningJwk(JSON.parse(secret)))();
  cached = { source: secret, key };
  key.catch(() => {
    if (cached?.key === key) cached = null;
  });
  return key;
}

/** The header exactly as section 2.2 writes it: {"alg":"ES256","typ":"pn-login+jwt","kid":"<kid>"}. */
export function assertionHeader(kid: string): string {
  return JSON.stringify({ alg: "ES256", typ: ASSERTION_TYP, kid });
}

/** The claims in the order of section 2.3. */
export function assertionClaims(input: AssertionInput, jti: string): Record<string, unknown> {
  return {
    iss: ISSUER,
    aud: input.nestId,
    sub: input.accountId,
    iat: input.iat,
    nbf: input.iat,
    exp: input.iat + ASSERTION_LIFETIME,
    nonce: input.nonce,
    jti,
    amr: input.amr,
    auth_time: input.authTime,
    ver: 1,
  };
}

/**
 * Compact JWS over the exact header and payload texts, signed ES256 (raw r || s, 64 bytes, as WebCrypto produces it).
 * The golden-vector generator calls this directly for its deliberately odd headers and payloads.
 */
export async function signCompact(headerText: string, payloadText: string, key: CryptoKey): Promise<string> {
  const signingInput = `${b64urlEncodeText(headerText)}.${b64urlEncodeText(payloadText)}`;
  const sig = new Uint8Array(await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, key, utf8(signingInput) as Uint8Array<ArrayBuffer>));
  if (sig.length !== 64) throw new Error("ES256 signature is not 64 bytes");
  return `${signingInput}.${b64urlEncode(sig)}`;
}

export function newJti(): string {
  const b = new Uint8Array(16);
  crypto.getRandomValues(b);
  return b64urlEncode(b);
}

/** Signs a login assertion (section 2). */
export async function signAssertion(sk: SigningKey, input: AssertionInput): Promise<string> {
  const jti = input.jti ?? newJti();
  const jws = await signCompact(assertionHeader(sk.kid), JSON.stringify(assertionClaims(input, jti)), sk.key);
  if (jws.length > MAX_JWS_LENGTH) throw new Error("assertion longer than 4096 characters");
  return jws;
}
