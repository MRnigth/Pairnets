import { describe, expect, it } from "vitest";
import { b64urlDecodeStrict, utf8 } from "../src/b64";
import { decodeMaster, deriveHeartbeatKey } from "../src/nestsig";
import { signAdmin } from "../src/nestadmin";
import { FakeNestServer, fromB64url } from "./fake-relay";

// RELAY.md section 4 test vectors. The key is CONTRACT.md 5.6's derived key (HB_MASTER = bytes 00..1f, nest
// nst_testnest000000000000000001, key version 1). The expected signatures were computed separately with Node's
// crypto module; these values are public test data, not secrets.
const MASTER = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
const NEST = "nst_testnest000000000000000001";
const KEY = "n4Tr_MkavyYVcV4JYFqyLo4V_3SdAf5UYSdjq0Gzw9g";
const POST_BODY = '{"name":"Laptop","system":"Windows 11","approvedBy":"m***@gmail.com"}';

const VECTORS = [
  { ts: "1791504000", nonce: "TestNonce000000000000A", method: "GET", path: "/api/relay/status", body: "", sig: "dGVhzZGCRgzy9J5LNwbHrbfC9t0gpTh87O3sp5SprzY" },
  { ts: "1791504001", nonce: "TestNonce000000000000B", method: "POST", path: "/api/relay/devices", body: POST_BODY, sig: "CS_In_osTtvilvMxr0yOzlxNjkN8UCQGfs9hGNbb8-4" },
];

function signedRequest(v: (typeof VECTORS)[number], sig = v.sig): Request {
  return new Request(`http://127.0.0.1:5075${v.path}`, {
    method: v.method,
    headers: { "X-Pairnets-Nest": NEST, "X-Pairnets-Ts": v.ts, "X-Pairnets-Nonce": v.nonce, "X-Pairnets-Sig": sig, "Content-Type": "application/json" },
    body: v.method === "GET" ? undefined : v.body,
  });
}

describe("signed admin calls (ra1)", () => {
  it("the nest key is CONTRACT.md 5.1's key", async () => {
    const key = await deriveHeartbeatKey(decodeMaster(MASTER), NEST, 1);
    expect(key).toEqual(b64urlDecodeStrict(KEY));
  });

  it("the Worker signs the vectors exactly", async () => {
    const key = b64urlDecodeStrict(KEY)!;
    for (const v of VECTORS) expect(await signAdmin(key, NEST, v.ts, v.nonce, v.method, v.path, utf8(v.body))).toBe(v.sig);
    // The method is uppercased before signing.
    const v = VECTORS[0];
    expect(await signAdmin(key, NEST, v.ts, v.nonce, "get", v.path, utf8(""))).toBe(v.sig);
  });

  it("the Worker also signs the nest's own examples exactly (made separately with openssl)", async () => {
    // tests/Pairnets.Tests/Unit/RelaySignatureTests.cs: key = bytes 00..1f, nonces = bytes 00..0f and 10..1f.
    const key = Uint8Array.from({ length: 32 }, (_, i) => i);
    expect(await signAdmin(key, NEST, "1791504000", "AAECAwQFBgcICQoLDA0ODw", "POST", "/api/relay/devices", utf8(POST_BODY))).toBe(
      "k40tvOTQMypQF3ThgisEKHCdjLJCkP495Wbrh1ZQP7M",
    );
    expect(await signAdmin(key, NEST, "1791504001", "EBESExQVFhcYGRobHB0eHw", "GET", "/api/relay/status", utf8(""))).toBe(
      "GxyX-jrLv6v9uZpV5k0n_JnZSiH6I8He2oKOnVdRvQo",
    );
  });

  it("a nest that checks them the documented way accepts each once, inside 120 s, and nothing else", async () => {
    const clock = { now: 1791504000 };
    const nest = new FakeNestServer(NEST, fromB64url(KEY)!, "tunnel", clock);
    expect((await nest.handle(signedRequest(VECTORS[0]))).status).toBe(200);
    expect((await nest.handle(signedRequest(VECTORS[1]))).status).toBe(200);
    // The same nonce again: refused.
    const replay = await nest.handle(signedRequest(VECTORS[0]));
    expect(replay.status).toBe(401);
    expect(await replay.json()).toEqual({ error: "bad_signature" });
    // A signature for another path, a changed body, padding, too old.
    const wrongPath = { ...VECTORS[0], nonce: "TestNonce000000000000C", path: "/api/relay/devices" };
    expect((await nest.handle(signedRequest(wrongPath, VECTORS[0].sig))).status).toBe(401);
    const changedBody = { ...VECTORS[1], nonce: "TestNonce000000000000D", body: POST_BODY.replace("Laptop", "Laptoq") };
    expect((await nest.handle(signedRequest(changedBody, VECTORS[1].sig))).status).toBe(401);
    expect((await nest.handle(signedRequest({ ...VECTORS[0], nonce: "TestNonce000000000000E" }, `${VECTORS[0].sig}=`))).status).toBe(401);
    clock.now += 121;
    const late = { ...VECTORS[0], nonce: "TestNonce000000000000F" };
    const key = b64urlDecodeStrict(KEY)!;
    expect((await nest.handle(signedRequest(late, await signAdmin(key, NEST, late.ts, late.nonce, "GET", late.path, utf8(""))))).status).toBe(401);
    expect(nest.refused.map((r) => r.step)).toEqual(["nonce", "sig", "sig", "sig-format", "clock"]);
  });
});
