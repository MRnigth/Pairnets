// Golden-vector generator (CONTRACT.md section 8): `npm run vectors` writes test/vectors/assertion-v1.json.
// Runs in Node (vitest.vectors.config.ts), uses the Worker's own signing code, and keeps private keys in memory only.
// Re-running it changes every key and signature (ECDSA is randomised); only run it on purpose.

import { writeFileSync } from "node:fs";
import { expect, it } from "vitest";
import { assertionClaims, assertionHeader, signAssertion, signCompact, type SigningKey } from "../src/assertion";
import { B64URL_ALPHABET, b64urlDecodeStrict, b64urlEncode, b64urlEncodeText, utf8 } from "../src/b64";
import { CASES, VECTOR_FIXED as F, type VectorCase, type VectorFile } from "./vectors.cases";
import { verifyAssertion } from "./support/verifier";

interface KeyPair {
  sk: SigningKey;
  pub: { kty: "EC"; crv: "P-256"; kid: string; x: string; y: string };
  privateKey: CryptoKey;
}

async function makeKey(kid: string): Promise<KeyPair> {
  const kp = (await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"])) as CryptoKeyPair;
  const jwk = (await crypto.subtle.exportKey("jwk", kp.publicKey)) as JsonWebKey;
  return { sk: { kid, key: kp.privateKey }, pub: { kty: "EC", crv: "P-256", kid, x: jwk.x!, y: jwk.y! }, privateKey: kp.privateKey };
}

/** DER SEQUENCE { INTEGER r, INTEGER s } of a raw r || s signature. */
function derOf(raw: Uint8Array): Uint8Array {
  const int = (b: Uint8Array) => {
    let i = 0;
    while (i < b.length - 1 && b[i] === 0) i++;
    let v = b.slice(i);
    if (v[0] & 0x80) v = Uint8Array.from([0, ...v]);
    return [0x02, v.length, ...v];
  };
  const body = [...int(raw.slice(0, 32)), ...int(raw.slice(32))];
  return Uint8Array.from([0x30, body.length, ...body]);
}

const C0 = () =>
  assertionClaims({ nestId: F.NEST, accountId: F.ACC, nonce: F.N, amr: ["google"], authTime: F.T - 600, iat: F.T, jti: F.J }, F.J);

it("writes test/vectors/assertion-v1.json", async () => {
  const k1 = await makeKey("test-1");
  const k2 = await makeKey("test-2");
  const H0 = assertionHeader("test-1");
  expect(H0).toBe(F.H0);
  expect(JSON.stringify(C0())).toBe(F.C0);

  // The reference assertion, re-signed until its signature has a "-" or "_" (case 13 needs one).
  let valid = "";
  do {
    valid = await signAssertion(k1.sk, { nestId: F.NEST, accountId: F.ACC, nonce: F.N, amr: ["google"], authTime: F.T - 600, iat: F.T, jti: F.J });
  } while (!/[-_]/.test(valid.split(".")[2]));
  const [vh, vp, vs] = valid.split(".");

  const claims = (change: Record<string, unknown>) => JSON.stringify({ ...C0(), ...change });
  const header = (change: Record<string, unknown>) => JSON.stringify({ ...JSON.parse(F.H0), ...change });
  const sign1 = (h: string, p: string) => signCompact(h, p, k1.privateKey);

  const hs256Input = `${b64urlEncodeText(header({ alg: "HS256" }))}.${b64urlEncodeText(F.C0)}`;
  const point = Uint8Array.from([4, ...b64urlDecodeStrict(k1.pub.x)!, ...b64urlDecodeStrict(k1.pub.y)!]);
  const hmacKey = await crypto.subtle.importKey("raw", point, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const hs256 = `${hs256Input}.${b64urlEncode(new Uint8Array(await crypto.subtle.sign("HMAC", hmacKey, utf8(hs256Input) as Uint8Array<ArrayBuffer>)))}`;

  const last = vs[vs.length - 1];
  const nonCanonical = vs.slice(0, -1) + B64URL_ALPHABET[B64URL_ALPHABET.indexOf(last) | 1];

  const jws: Record<string, string> = {
    valid,
    "valid-next-key": await signAssertion(k2.sk, { nestId: F.NEST, accountId: F.ACC, nonce: F.N, amr: ["google"], authTime: F.T - 600, iat: F.T, jti: F.J }),
    "skew-late-ok": valid,
    "skew-early-ok": valid,
    "wrong-kid": await sign1(header({ kid: "test-9" }), F.C0),
    "alg-none": `${b64urlEncodeText(header({ alg: "none" }))}.${b64urlEncodeText(F.C0)}.`,
    "alg-hs256": hs256,
    "typ-jwt": await sign1(header({ typ: "JWT" }), F.C0),
    "crit-header": await sign1('{"alg":"ES256","typ":"pn-login+jwt","kid":"test-1","crit":["exp"]}', F.C0),
    "jwk-header": await signCompact(JSON.stringify({ ...JSON.parse(F.H0), jwk: k2.pub }), F.C0, k2.privateKey),
    "sig-der": `${vh}.${vp}.${b64urlEncode(derOf(b64urlDecodeStrict(vs)!))}`,
    "sig-noncanonical": `${vh}.${vp}.${nonCanonical}`,
    "sig-std-alphabet": `${vh}.${vp}.${vs.replace(/-/g, "+").replace(/_/g, "/")}`,
    "payload-padding": `${vh}.${vp}=.${vs}`,
    "payload-whitespace": `${vh}.${vp.slice(0, 10)} ${vp.slice(10)}.${vs}`,
    "tampered-payload": `${vh}.${b64urlEncodeText(claims({ sub: F.ACC2 }))}.${vs}`,
    "wrong-key-same-kid": await signCompact(F.H0, F.C0, k2.privateKey),
    "duplicate-claim": await sign1(F.H0, `${F.C0.slice(0, -1)},"sub":"${F.ACC2}"}`),
    "aud-array": await sign1(F.H0, claims({ aud: [F.NEST] })),
    "iat-string": await sign1(F.H0, claims({ iat: String(F.T) })),
    "ver-2": await sign1(F.H0, claims({ ver: 2 })),
    "iss-trailing-slash": await sign1(F.H0, claims({ iss: "https://id.pairnets.app/" })),
    "bad-aud": await sign1(F.H0, claims({ aud: F.NEST2 })),
    "bad-sub": await sign1(F.H0, claims({ sub: F.ACC2 })),
    "clock-behind": valid,
    "clock-ahead": valid,
    "iat-future": valid,
    "lifetime-too-long": await sign1(F.H0, claims({ exp: F.T + 301 })),
    "nbf-future": await sign1(F.H0, claims({ nbf: F.T + 61 })),
    expired: valid,
    "auth-time-future": await sign1(F.H0, claims({ auth_time: F.T + 1 })),
    "nonce-mismatch": valid,
    "amr-empty": await sign1(F.H0, claims({ amr: [] })),
    "jti-short": await sign1(F.H0, claims({ jti: "short" })),
  };

  const cases: VectorCase[] = CASES.map((c) => ({
    name: c.name,
    description: c.description,
    jws: jws[c.name],
    now: c.now,
    nonce: c.nonce,
    expect: c.expect,
    reason: c.reason,
  }));
  expect(cases.every((c) => typeof c.jws === "string")).toBe(true);

  const file: VectorFile = {
    format: "pairnets-login-assertion-vectors",
    version: 1,
    generator: "cloud/test/vectors.gen.test.ts",
    issuer: "https://id.pairnets.app",
    nestId: F.NEST,
    sub: F.ACC,
    keys: [k1.pub, k2.pub],
    cases,
  };

  // Public keys only.
  for (const k of file.keys) {
    expect(Object.keys(k)).toEqual(["kty", "crv", "kid", "x", "y"]);
    expect(k).not.toHaveProperty("d");
  }
  expect(JSON.stringify(file)).not.toContain('"d"');

  // Every case means what the contract's table says.
  for (const c of cases) {
    const r = await verifyAssertion(c.jws, file.keys, file.nestId, file.sub, c.nonce, c.now, new Map());
    expect({ name: c.name, outcome: r.outcome, reason: r.reason }).toEqual({ name: c.name, outcome: c.expect, reason: c.reason });
  }

  writeFileSync(new URL("./vectors/assertion-v1.json", import.meta.url), `${JSON.stringify(file, null, 2)}\n`, "utf8");
});
