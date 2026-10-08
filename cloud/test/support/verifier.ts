// A reference implementation of the nest's assertion check (CONTRACT.md section 2.6), steps V1-V21 in order.
// Test code only: it proves that the golden vectors mean what the contract's table says, so the C# verifier and this
// Worker agree on every case. Pure (clock and jti cache passed in), WebCrypto only.

import { b64urlDecodeStrict, utf8 } from "../../src/b64";

export type Outcome = "ok" | "failed" | "clock";
export interface Result {
  outcome: Outcome;
  reason: string | null;
  claims?: Record<string, unknown>;
}
export interface PinnedKey {
  kid: string;
  x: string;
  y: string;
}

export const ISSUER = "https://id.pairnets.app";
const KID_RE = /^[A-Za-z0-9._-]{1,64}$/;

/** JSON integers keep their text form apart from other numbers (section 1.6: "1.0" and "1e3" are not integers). */
export class JsonInt {
  constructor(readonly value: bigint) {}
}

/** Strict JSON: UTF-8 already decoded; refuses duplicate member names. Numbers with a fraction or exponent stay `number`. */
export function parseStrictJson(text: string): unknown {
  let i = 0;
  const fail = (): never => {
    throw new Error(`bad JSON at ${i}`);
  };
  const ws = () => {
    while (i < text.length && " \t\n\r".includes(text[i])) i++;
  };
  const value = (): unknown => {
    ws();
    const c = text[i];
    if (c === "{") {
      i++;
      const obj: Record<string, unknown> = {};
      const seen = new Set<string>();
      ws();
      if (text[i] === "}") {
        i++;
        return obj;
      }
      for (;;) {
        ws();
        if (text[i] !== '"') fail();
        const k = str();
        if (seen.has(k)) fail();
        seen.add(k);
        ws();
        if (text[i++] !== ":") fail();
        obj[k] = value();
        ws();
        const d = text[i++];
        if (d === "}") return obj;
        if (d !== ",") fail();
      }
    }
    if (c === "[") {
      i++;
      const arr: unknown[] = [];
      ws();
      if (text[i] === "]") {
        i++;
        return arr;
      }
      for (;;) {
        arr.push(value());
        ws();
        const d = text[i++];
        if (d === "]") return arr;
        if (d !== ",") fail();
      }
    }
    if (c === '"') return str();
    if (text.startsWith("true", i)) return (i += 4), true;
    if (text.startsWith("false", i)) return (i += 5), false;
    if (text.startsWith("null", i)) return (i += 4), null;
    const m = /^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?/.exec(text.slice(i));
    if (!m || m[0] === "") return fail();
    i += m[0].length;
    if (m[2] === undefined && m[3] === undefined) {
      const big = BigInt(m[0]);
      if (big > 9223372036854775807n || big < -9223372036854775808n) return Number(m[0]);
      return new JsonInt(big);
    }
    return Number(m[0]);
  };
  const str = (): string => {
    i++; // opening quote
    let out = "";
    for (;;) {
      if (i >= text.length) fail();
      const c = text[i++];
      if (c === '"') return out;
      if (c < " ") fail();
      if (c !== "\\") {
        out += c;
        continue;
      }
      const e = text[i++];
      const simple: Record<string, string> = { '"': '"', "\\": "\\", "/": "/", b: "\b", f: "\f", n: "\n", r: "\r", t: "\t" };
      if (e in simple) out += simple[e];
      else if (e === "u" && /^[0-9a-fA-F]{4}$/.test(text.slice(i, i + 4))) {
        out += String.fromCharCode(parseInt(text.slice(i, i + 4), 16));
        i += 4;
      } else fail();
    }
  };
  const v = value();
  ws();
  if (i !== text.length) fail();
  return v;
}

function decodeJsonObject(bytes: Uint8Array): Record<string, unknown> | null {
  try {
    const text = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(bytes);
    const v = parseStrictJson(text);
    return v && typeof v === "object" && !Array.isArray(v) && !(v instanceof JsonInt) ? (v as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

function constantTimeEquals(a: string, b: string): boolean {
  const x = utf8(a);
  const y = utf8(b);
  if (x.length !== y.length) return false;
  let d = 0;
  for (let i = 0; i < x.length; i++) d |= x[i] ^ y[i];
  return d === 0;
}

async function importPinned(k: PinnedKey): Promise<CryptoKey> {
  return crypto.subtle.importKey("jwk", { kty: "EC", crv: "P-256", x: k.x, y: k.y, ext: true }, { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
}

const failed = (reason: string): Result => ({ outcome: "failed", reason });

export async function verifyAssertion(
  jws: string,
  keys: PinnedKey[],
  nestId: string,
  sub: string,
  expectedNonce: string,
  now: number,
  jtiCache: Map<string, number>,
): Promise<Result> {
  // V1
  if (jws.length > 4096 || jws.split(".").length !== 3) return failed("shape");
  const [hSeg, pSeg, sSeg] = jws.split(".");
  // V2
  const hBytes = hSeg ? b64urlDecodeStrict(hSeg) : null;
  if (!hBytes || hBytes.length > 512) return failed("b64");
  // V3a-d
  const header = decodeJsonObject(hBytes);
  if (!header) return failed("header");
  if (header.alg !== "ES256") return failed("alg");
  if (header.typ !== "pn-login+jwt") return failed("typ");
  const names = Object.keys(header).sort();
  if (names.join(",") !== "alg,kid,typ" || typeof header.kid !== "string" || !KID_RE.test(header.kid)) return failed("header");
  // V4
  const pinned = keys.find((k) => k.kid === header.kid);
  if (!pinned) return failed("kid");
  // V5
  const sig = b64urlDecodeStrict(sSeg);
  if (!sig || sig.length !== 64) return failed("sig-format");
  // V6
  const pBytes = pSeg ? b64urlDecodeStrict(pSeg) : null;
  if (!pBytes || pBytes.length > 2048) return failed("b64");
  // V7
  const ok = await crypto.subtle.verify(
    { name: "ECDSA", hash: "SHA-256" },
    await importPinned(pinned),
    sig as Uint8Array<ArrayBuffer>,
    utf8(`${hSeg}.${pSeg}`) as Uint8Array<ArrayBuffer>,
  );
  if (!ok) return failed("sig");
  // V8
  const c = decodeJsonObject(pBytes);
  if (!c) return failed("claims");
  for (const k of ["iss", "aud", "sub", "nonce", "jti"]) if (typeof c[k] !== "string") return failed("claims");
  for (const k of ["iat", "nbf", "exp", "auth_time", "ver"]) if (!(c[k] instanceof JsonInt)) return failed("claims");
  if (!Array.isArray(c.amr)) return failed("claims");
  const n = (k: string) => Number((c[k] as JsonInt).value);
  const iat = n("iat");
  const nbf = n("nbf");
  const exp = n("exp");
  const authTime = n("auth_time");
  // V9-V12
  if (n("ver") !== 1) return failed("ver");
  if (c.iss !== ISSUER) return failed("iss");
  if (c.aud !== nestId) return failed("aud");
  if (!constantTimeEquals(c.sub as string, sub)) return failed("sub");
  // V13
  if (Math.abs(now - iat) > 300) return { outcome: "clock", reason: "clock" };
  // V14-V18
  if (!(iat <= now + 60)) return failed("iat");
  if (!(exp > iat && exp - iat <= 300)) return failed("lifetime");
  if (!(nbf <= now + 60 && nbf <= exp)) return failed("nbf");
  if (!(now < exp + 60)) return failed("exp");
  if (!(authTime <= iat)) return failed("auth_time");
  // V19
  if (!constantTimeEquals(c.nonce as string, expectedNonce)) return failed("nonce");
  // V20
  const amr = c.amr as unknown[];
  if (amr.length < 1 || amr.length > 4 || !amr.every((a) => typeof a === "string" && /^[a-z0-9_-]{1,32}$/.test(a))) return failed("amr");
  // V21
  const jti = c.jti as string;
  for (const [k, until] of jtiCache) if (until < now) jtiCache.delete(k);
  if (!/^[A-Za-z0-9_-]{16,64}$/.test(jti) || jtiCache.has(jti)) return failed("jti");
  jtiCache.set(jti, exp + 60);
  const plain: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(c)) plain[k] = v instanceof JsonInt ? Number(v.value) : v;
  return { outcome: "ok", reason: null, claims: plain };
}
