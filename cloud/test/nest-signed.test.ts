import { describe, expect, it } from "vitest";
import { b64urlDecodeStrict, b64urlEncode, utf8 } from "../src/b64";
import { deriveHeartbeatKey, signRequest, signResponse } from "../src/nestsig";
import { claim, FakeNest, Harness, linkNest, type Res } from "./helpers";

const VECTOR_MASTER = Uint8Array.from({ length: 32 }, (_, i) => i);
const VECTOR_NEST = "nst_testnest000000000000000001";

describe("HMAC test vectors (section 5.6)", () => {
  it("matches all four", async () => {
    expect(b64urlEncode(VECTOR_MASTER)).toBe("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8");
    const key = await deriveHeartbeatKey(VECTOR_MASTER, VECTOR_NEST, 1);
    expect(b64urlEncode(key)).toBe("n4Tr_MkavyYVcV4JYFqyLo4V_3SdAf5UYSdjq0Gzw9g");

    const hb = '{"v":1,"ts":1791504000,"serverVersion":"1.0.47","ready":true,"publicHost":"nest.example.com","hostedLogin":true}';
    expect(utf8(hb).length).toBe(112);
    expect(await signRequest(key, "hb1", VECTOR_NEST, "1791504000", utf8(hb))).toBe("bHpoOXDoG0p3j0swx0Agcxo8zl7KhnwU1Qug6JP93ZE");

    const resp = '{"v":1,"ts":1791504001,"reqTs":1791504000,"disableHostedLogin":false}';
    expect(await signResponse(key, "hb1", VECTOR_NEST, 1791504001, resp)).toBe("rGRf5tEBz034Ylc8b96O1mRgG4JxmqqSG7fwPdELpNk");

    const nc = '{"v":1,"ts":1791504005,"serverVersion":"1.0.47"}';
    expect(await signRequest(key, "nc1", VECTOR_NEST, "1791504005", utf8(nc))).toBe("Zm0rOOd9GSK6RchlkdDwrk7NYUzTCjOgiajMTRMHNEk");
  });
});

/** The nest's check of a signed 200 (section 5.2). */
async function expectSignedAnswer(nest: FakeNest, r: Res, purpose: "hb1" | "nc1" | "nu1", reqTs: number) {
  expect(r.status).toBe(200);
  const respTs = Number(r.headers.get("x-pairnets-ts"));
  expect(r.json.v).toBe(1);
  expect(r.json.ts).toBe(respTs);
  expect(r.json.reqTs).toBe(reqTs);
  expect(Math.abs(nest.h.clock.now - respTs)).toBeLessThanOrEqual(300);
  expect(r.headers.get("x-pairnets-sig")).toBe(await signResponse(nest.key, purpose, nest.nestId, respTs, r.text));
}

async function setup() {
  const h = await Harness.create();
  const b = h.browser();
  const accountId = await b.signInByEmail("you@example.com");
  return { h, b, accountId };
}

describe("nest-signed", () => {
  it("confirm: pending -> active, signed answer, once", async () => {
    const { h, b } = await setup();
    const { nest } = await claim(h, b);
    const r = await nest!.confirm("1.0.48");
    await expectSignedAnswer(nest!, r, "nc1", nest!.lastTs);
    expect(Object.keys(r.json)).toEqual(["v", "ts", "reqTs", "status"]);
    expect(r.json.status).toBe("active");
    const [row] = await h.query<Record<string, unknown>>("SELECT status, confirmed_at, last_version, last_req_ts FROM nests");
    expect(row).toEqual({ status: "active", confirmed_at: h.clock.now, last_version: "1.0.48", last_req_ts: nest!.lastTs });
    const again = await nest!.confirm();
    await expectSignedAnswer(nest!, again, "nc1", nest!.lastTs);
    expect(h.mailer.sent.filter((m) => m.subject.startsWith("A nest was linked"))).toHaveLength(1);
  });

  it("unlink: deletes the nest; a notice only when it was active", async () => {
    const { h, b } = await setup();
    const { nest: pending } = await claim(h, b, "https://declined.example.com");
    const declined = await pending!.unlink("declined");
    await expectSignedAnswer(pending!, declined, "nu1", pending!.lastTs);
    expect(declined.json.status).toBe("removed");
    expect(h.mailer.sent.filter((m) => m.subject.includes("unlinked"))).toHaveLength(0);

    const active = await linkNest(h, b, "https://active.example.com");
    const r = await active.unlink("owner");
    await expectSignedAnswer(active, r, "nu1", active.lastTs);
    expect(await h.query("SELECT * FROM nests")).toHaveLength(0);
    expect(h.mailer.to("you@example.com").some((m) => m.subject === "A nest was unlinked from your account: https://active.example.com")).toBe(true);
    // Gone now: 410 from then on.
    expect((await active.heartbeat()).status).toBe(410);
    expect((await active.unlink("owner")).json.error).toBe("unlinked");
    const bad = await (await linkNest(h, b, "https://third.example.com")).send("/v1/nest/unlink", "nu1", { reason: "bored" });
    expect(bad.json.error).toBe("bad_request");
  });

  it("heartbeat: records the nest's state and answers signed", async () => {
    const { h, b } = await setup();
    const nest = await linkNest(h, b);
    h.advance(30);
    const r = await nest.heartbeat({ serverVersion: "1.0.50", ready: false, publicHost: "other.example.com", hostedLogin: false });
    await expectSignedAnswer(nest, r, "hb1", nest.lastTs);
    expect(r.text).toBe(`{"v":1,"ts":${h.clock.now},"reqTs":${nest.lastTs},"disableHostedLogin":false}`);
    const listed = (await b.call("GET", "/v1/nests")).json.nests[0];
    expect(listed).toMatchObject({ online: true, serverVersion: "1.0.50", ready: false, publicHost: "other.example.com", lastSeenAt: "2026-10-09T00:00:30Z" });
    // publicHost never changes the registered address; the account page warns instead.
    expect(listed.publicUrl).toBe("https://nest.example.com");
    expect((await b.call("GET", "/account")).text).toContain("This nest now reports the address other.example.com");
    h.advance(25 * 60 + 1);
    expect((await b.call("GET", "/v1/nests")).json.nests[0].online).toBe(false);
    expect((await b.call("GET", "/account")).text).toContain(">offline<");
  });

  it("heartbeat: emergency switch answers disableHostedLogin true", async () => {
    const { h, b } = await setup();
    const nest = await linkNest(h, b);
    h.env = { ...h.env, HOSTED_LOGIN_DISABLED: "1" };
    const r = await nest.heartbeat();
    await expectSignedAnswer(nest, r, "hb1", nest.lastTs);
    expect(r.json.disableHostedLogin).toBe(true);
  });

  it("heartbeat for a pending nest is not_confirmed; at most one a minute", async () => {
    const { h, b } = await setup();
    const { nest } = await claim(h, b);
    const pending = await nest!.heartbeat();
    expect(pending.status).toBe(409);
    expect(pending.json.error).toBe("not_confirmed");
    await nest!.confirm();
    h.advance(60);
    expect((await nest!.heartbeat()).status).toBe(200);
    h.advance(1);
    const fast = await nest!.heartbeat();
    expect(fast.status).toBe(429);
    expect(fast.json.error).toBe("rate_limited");
    expect(Number(fast.headers.get("retry-after"))).toBeGreaterThan(0);
    expect(fast.headers.get("x-pairnets-sig")).toBeNull();
    h.advance(60);
    expect((await nest!.heartbeat()).status).toBe(200);
  });

  it("confirm and unlink: at most 10 an hour per nest", async () => {
    const { h, b } = await setup();
    const nest = await linkNest(h, b);
    for (let i = 0; i < 9; i++) expect((await nest.confirm()).status).toBe(200);
    const over = await nest.confirm();
    expect(over.status).toBe(429);
    expect(over.json.error).toBe("rate_limited");
  });

  it("replay refused", async () => {
    const { h, b } = await setup();
    const nest = await linkNest(h, b);
    h.advance(120);
    const ts = h.clock.now;
    expect((await nest.heartbeat()).status).toBe(200);
    const body = JSON.stringify({ v: 1, ts, serverVersion: "1.0.47", ready: true, publicHost: "nest.example.com", hostedLogin: true });
    h.advance(61);
    const replay = await nest.send("/v1/heartbeat", "hb1", {}, { ts, body });
    expect(replay.status).toBe(401);
    expect(replay.json.error).toBe("stale");
    // An older (but fresh enough) ts is refused too.
    const older = await nest.send("/v1/heartbeat", "hb1", { serverVersion: "1", ready: true, publicHost: null, hostedLogin: true }, { ts: ts - 1 });
    expect(older.json.error).toBe("stale");
    // A ts more than 5 minutes away from the service clock.
    const tooNew = await nest.send("/v1/heartbeat", "hb1", { serverVersion: "1", ready: true, publicHost: null, hostedLogin: true }, { ts: h.clock.now + 301 });
    expect(tooNew.json.error).toBe("stale");
    // A stale answer leaves room for the nest's one retry with a new ts.
    nest.lastTs = ts;
    expect((await nest.heartbeat()).status).toBe(200);
  });

  it("wrong key refused", async () => {
    const { h, b } = await setup();
    const nest = await linkNest(h, b);
    const other = new Uint8Array(32).fill(7);
    const r = await nest.send("/v1/heartbeat", "hb1", { serverVersion: "1", ready: true, publicHost: null, hostedLogin: true }, { key: other });
    expect(r.status).toBe(401);
    expect(r.json).toEqual({ error: "bad_signature", message: expect.any(String) });
    // Another nest's valid key does not work for this nest either.
    const second = await linkNest(h, b, "https://second.example.com");
    const cross = await nest.send("/v1/heartbeat", "hb1", { serverVersion: "1", ready: true, publicHost: null, hostedLogin: true }, { key: second.key });
    expect(cross.json.error).toBe("bad_signature");
    // Signature for another purpose (a confirm signature sent as a heartbeat).
    const ts = nest.nextTs();
    const body = JSON.stringify({ v: 1, ts, serverVersion: "1", ready: true, publicHost: null, hostedLogin: true });
    const sig = await signRequest(nest.key, "nc1", nest.nestId, String(ts), utf8(body));
    expect((await nest.send("/v1/heartbeat", "hb1", {}, { ts, body, sig })).json.error).toBe("bad_signature");
    // Malformed signatures: padded, standard alphabet, wrong length.
    const good = await signRequest(nest.key, "hb1", nest.nestId, String(ts), utf8(body));
    for (const s of [`${good}=`, good.replace(/-/g, "+").replace(/_/g, "/") + (/[-_]/.test(good) ? "" : "+"), good.slice(0, 42), ""]) {
      expect((await nest.send("/v1/heartbeat", "hb1", {}, { ts, body, sig: s })).json.error, s).toBe("bad_signature");
    }
    // A signature over different body bytes.
    expect((await nest.send("/v1/heartbeat", "hb1", {}, { ts, body: `${body} `, sig: good })).json.error).toBe("bad_signature");
  });

  it("checks the order: unknown nest 410, then signature, then body", async () => {
    const { h, b } = await setup();
    const nest = await linkNest(h, b);
    const fields = { serverVersion: "1", ready: true, publicHost: null, hostedLogin: true };
    expect((await nest.send("/v1/heartbeat", "hb1", fields, { nestHeader: "nst_testnest000000000000000001" })).status).toBe(410);
    expect((await nest.send("/v1/heartbeat", "hb1", fields, { nestHeader: "NST_x" })).status).toBe(410);

    // Body ts differs from the header (validly signed): 400.
    const ts = nest.nextTs();
    const mismatch = await nest.send("/v1/heartbeat", "hb1", {}, { ts, body: JSON.stringify({ v: 1, ts: ts + 1, ...fields }) });
    expect(mismatch.status).toBe(400);
    expect(mismatch.json.error).toBe("bad_request");
    // v must be 1.
    const ts2 = nest.nextTs();
    expect((await nest.send("/v1/heartbeat", "hb1", {}, { ts: ts2, body: JSON.stringify({ v: 2, ts: ts2, ...fields }) })).json.error).toBe("bad_request");
    // Header ts with a leading zero or a sign.
    const ts3 = nest.nextTs();
    expect((await nest.send("/v1/heartbeat", "hb1", {}, { tsHeader: `0${ts3}`, body: JSON.stringify({ v: 1, ts: ts3, ...fields }) })).json.error).toBe("bad_request");
    // Body over 1024 bytes.
    const ts4 = nest.nextTs();
    expect((await nest.send("/v1/heartbeat", "hb1", { ...fields, serverVersion: "x".repeat(1100) }, { ts: ts4 })).json.error).toBe("bad_request");
    // Wrong field types.
    expect((await nest.send("/v1/heartbeat", "hb1", { ...fields, ready: "yes" })).json.error).toBe("bad_request");
    expect((await nest.send("/v1/heartbeat", "hb1", { serverVersion: "1", ready: true, hostedLogin: true })).json.error).toBe("bad_request");
  });

  it("a pending nest older than an hour is gone for signed requests", async () => {
    const { h, b } = await setup();
    const { nest } = await claim(h, b);
    h.advance(3600);
    const r = await nest!.confirm();
    expect(r.status).toBe(410);
    expect(r.json.error).toBe("unlinked");
  });

  it("the heartbeat key is never stored", async () => {
    const { h, b } = await setup();
    const nest = await linkNest(h, b);
    const keyText = b64urlEncode(nest.key);
    const rows = await h.query<Record<string, unknown>>("SELECT * FROM nests");
    expect(JSON.stringify(rows)).not.toContain(keyText);
    expect(b64urlDecodeStrict(keyText)).toEqual(nest.key);
  });
});
