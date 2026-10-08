// The committed golden vectors (test/vectors/assertion-v1.json) still verify, so the file cannot rot.
// The C# HostedGoldenVectorTests reads the same file.

import { describe, expect, it } from "vitest";
import raw from "./vectors/assertion-v1.json?raw";
import { b64urlDecodeStrict, utf8 } from "../src/b64";
import { CASES, VECTOR_FIXED as F, type VectorFile } from "./vectors.cases";
import { verifyAssertion } from "./support/verifier";

const file = JSON.parse(raw) as VectorFile;

function text(seg: string): string {
  return new TextDecoder().decode(b64urlDecodeStrict(seg)!);
}

describe("golden vectors", () => {
  it("have the contract's format and public keys only", () => {
    expect(file.format).toBe("pairnets-login-assertion-vectors");
    expect(file.version).toBe(1);
    expect(file.generator).toBe("cloud/test/vectors.gen.test.ts");
    expect(file.issuer).toBe("https://id.pairnets.app");
    expect(file.nestId).toBe(F.NEST);
    expect(file.sub).toBe(F.ACC);
    expect(file.keys.map((k) => k.kid)).toEqual(["test-1", "test-2"]);
    for (const k of file.keys) {
      expect(Object.keys(k)).toEqual(["kty", "crv", "kid", "x", "y"]);
      expect(k).not.toHaveProperty("d");
      expect(b64urlDecodeStrict(k.x)!.length).toBe(32);
      expect(b64urlDecodeStrict(k.y)!.length).toBe(32);
    }
    expect(raw).not.toMatch(/"d"\s*:/);
    // 2-space indentation, cases in the contract's order with the contract's expectations.
    expect(raw.replace(/\r\n/g, "\n").startsWith('{\n  "format": "pairnets-login-assertion-vectors",\n  "version": 1,\n')).toBe(true);
    expect(file.cases.map((c) => Object.keys(c))).toEqual(file.cases.map(() => ["name", "description", "jws", "now", "nonce", "expect", "reason"]));
    expect(file.cases.map(({ jws: _jws, ...rest }) => rest)).toEqual(CASES);
    expect(file.cases).toHaveLength(34);
  });

  it("valid is exactly H0 and C0", () => {
    const [h, p] = file.cases[0].jws.split(".");
    expect(text(h)).toBe(F.H0);
    expect(text(p)).toBe(F.C0);
  });

  it("every ok case verifies with WebCrypto against its key", async () => {
    const ok = file.cases.filter((c) => c.expect === "ok");
    expect(ok.map((c) => c.name)).toEqual(["valid", "valid-next-key", "skew-late-ok", "skew-early-ok"]);
    for (const c of ok) {
      const [h, p, s] = c.jws.split(".");
      const kid = JSON.parse(text(h)).kid;
      const k = file.keys.find((x) => x.kid === kid)!;
      const key = await crypto.subtle.importKey("jwk", { kty: "EC", crv: "P-256", x: k.x, y: k.y, ext: true }, { name: "ECDSA", namedCurve: "P-256" }, false, ["verify"]);
      const sig = b64urlDecodeStrict(s)!;
      expect(sig.length).toBe(64);
      expect(await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, key, sig as Uint8Array<ArrayBuffer>, utf8(`${h}.${p}`) as Uint8Array<ArrayBuffer>), c.name).toBe(true);
    }
  });

  it("every case gives the contract's outcome and reason in the reference verifier", async () => {
    for (const c of file.cases) {
      const r = await verifyAssertion(c.jws, file.keys, file.nestId, file.sub, c.nonce, c.now, new Map());
      expect({ name: c.name, outcome: r.outcome, reason: r.reason }).toEqual({ name: c.name, outcome: c.expect, reason: c.reason });
      if (c.expect === "ok") {
        expect(r.claims!.sub).toBe(F.ACC);
        expect(r.claims!.amr).toEqual(["google"]);
      }
    }
  });

  it("the reference verifier keeps a jti from being used twice", async () => {
    const cache = new Map<string, number>();
    const valid = file.cases[0];
    expect((await verifyAssertion(valid.jws, file.keys, F.NEST, F.ACC, F.N, F.T, cache)).outcome).toBe("ok");
    expect(await verifyAssertion(valid.jws, file.keys, F.NEST, F.ACC, F.N, F.T + 1, cache)).toEqual({ outcome: "failed", reason: "jti" });
  });
});
