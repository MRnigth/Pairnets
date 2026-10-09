import { describe, expect, it } from "vitest";
import { b64urlEncode } from "../src/b64";
import { deriveHeartbeatKey } from "../src/nestsig";
import { claim, Harness, linkNest } from "./helpers";

async function signedIn(h: Harness, email = "you@example.com") {
  const b = h.browser();
  const accountId = await b.signInByEmail(email);
  return { b, accountId };
}

describe("claim codes", () => {
  it("need a recent sign-in, and look like the contract", async () => {
    const h = await Harness.create();
    const { b } = await signedIn(h);
    const r = await b.call("POST", "/v1/nests/claim-codes", { body: { label: "Home" } });
    expect(r.status).toBe(201);
    expect(r.json.code).toMatch(/^PN-[0-9ABCDEFGHJKMNPQRSTVWXYZ]{4}-[0-9ABCDEFGHJKMNPQRSTVWXYZ]{4}-[0-9ABCDEFGHJKMNPQRSTVWXYZ]{4}$/);
    expect(r.json.expiresAt).toBe("2026-10-09T00:10:00Z");
    expect(r.json.command).toBe(`sudo -u pairnets /opt/pairnets/pairnets-server hosted link ${r.json.code}`);

    h.advance(901);
    const late = await b.call("POST", "/v1/nests/claim-codes", { body: {} });
    expect(late.status).toBe(401);
    expect(late.json.error).toBe("reauth_required");
    expect((await h.browser().call("POST", "/v1/nests/claim-codes", { body: {} })).json.error).toBe("unauthorized");
  });

  it("at most 5 unused at once and 20 a day; cancelling frees them", async () => {
    const h = await Harness.create();
    const { b } = await signedIn(h);
    for (let i = 0; i < 5; i++) expect((await b.call("POST", "/v1/nests/claim-codes", { body: {} })).status).toBe(201);
    const sixth = await b.call("POST", "/v1/nests/claim-codes", { body: {} });
    expect(sixth.status).toBe(429);
    expect(sixth.json.error).toBe("too_many_codes");
    expect((await b.call("DELETE", "/v1/nests/claim-codes")).status).toBe(204);
    expect(await h.query("SELECT * FROM claim_codes")).toHaveLength(0);
    for (let i = 0; i < 15; i++) {
      expect((await b.call("POST", "/v1/nests/claim-codes", { body: {} })).status).toBe(201);
      if (i % 5 === 4) await b.call("DELETE", "/v1/nests/claim-codes");
    }
    const over = await b.call("POST", "/v1/nests/claim-codes", { body: {} });
    expect(over.status).toBe(429);
    expect(over.json.error).toBe("too_many_codes");
    expect((await b.call("POST", "/v1/nests/claim-codes", { body: { label: 7 } })).status).toBe(400);
  });
});

describe("claim", () => {
  it("redeems a code: a pending nest and the nest's heartbeat key", async () => {
    const h = await Harness.create();
    const { b, accountId } = await signedIn(h, "yes@gmail.com");
    const { res } = await claim(h, b, "https://nest.example.com", { label: "  Home nest  " });
    expect(res.status).toBe(201);
    expect(Object.keys(res.json)).toEqual(["nestId", "accountId", "maskedEmail", "heartbeatKey", "keyVersion"]);
    expect(res.json.nestId).toMatch(/^nst_[0-9a-hjkmnp-tv-z]{26}$/);
    expect(res.json.accountId).toBe(accountId);
    expect(res.json.maskedEmail).toBe("y***@gmail.com");
    expect(res.json.keyVersion).toBe(1);
    const expectedKey = await deriveHeartbeatKey(h.hbMaster, res.json.nestId, 1);
    expect(res.json.heartbeatKey).toBe(b64urlEncode(expectedKey));
    const [row] = await h.query<Record<string, unknown>>("SELECT * FROM nests");
    expect(row).toMatchObject({ id: res.json.nestId, account_id: accountId, label: "Home nest", public_url: "https://nest.example.com", status: "pending", key_version: 1, hosted_login: 1, last_req_ts: 0 });
    const listed = await b.call("GET", "/v1/nests");
    expect(listed.json.nests[0]).toMatchObject({ nestId: res.json.nestId, status: "pending", online: false, lastSeenAt: null, serverVersion: null, confirmedAt: null, hostedLogin: true });
  });

  it("label: the request's, else the code's, else the address's host", async () => {
    const h = await Harness.create();
    const { b } = await signedIn(h);
    const code = (await b.call("POST", "/v1/nests/claim-codes", { body: { label: "From code" } })).json.code;
    await h.client().call("POST", "/v1/claim", { body: { code, publicUrl: "https://a.example.com", serverVersion: "1" } });
    await claim(h, b, "https://b.example.com:8443");
    const code3 = (await b.call("POST", "/v1/nests/claim-codes", { body: { label: "From code" } })).json.code;
    await h.client().call("POST", "/v1/claim", { body: { code: code3, publicUrl: "https://c.example.com", serverVersion: "1", label: "From request" } });
    const labels = (await h.query<{ label: string }>("SELECT label FROM nests ORDER BY public_url")).map((r) => r.label);
    expect(labels).toEqual(["From code", "b.example.com:8443", "From request"]);
  });

  it("codes expire / work once / need recent sign-in", async () => {
    const h = await Harness.create();
    const { b } = await signedIn(h);
    const code = (await b.call("POST", "/v1/nests/claim-codes", { body: {} })).json.code as string;
    const typed = code.toLowerCase().replace(/-/g, " ");
    const first = await h.client().call("POST", "/v1/claim", { body: { code: typed, publicUrl: "https://a.example.com", serverVersion: "1" } });
    expect(first.status).toBe(201);
    const again = await h.client().call("POST", "/v1/claim", { body: { code, publicUrl: "https://b.example.com", serverVersion: "1" } });
    expect(again.status).toBe(409);
    expect(again.json.error).toBe("used");

    const code2 = (await b.call("POST", "/v1/nests/claim-codes", { body: {} })).json.code;
    h.advance(600);
    const late = await h.client().call("POST", "/v1/claim", { body: { code: code2, publicUrl: "https://b.example.com", serverVersion: "1" } });
    expect(late.status).toBe(400);
    expect(late.json.error).toBe("expired");

    const unknown = await h.client().call("POST", "/v1/claim", { body: { code: "PN-0000-0000-0000", publicUrl: "https://b.example.com", serverVersion: "1" } });
    expect(unknown.json.error).toBe("invalid_code");
    const malformed = await h.client().call("POST", "/v1/claim", { body: { code: "PN-0000-0000-000U", publicUrl: "https://b.example.com", serverVersion: "1" } });
    expect(malformed.json.error).toBe("invalid_code");
  });

  it("checks the address before using the code, so a typo does not burn it", async () => {
    const h = await Harness.create();
    const { b } = await signedIn(h);
    const code = (await b.call("POST", "/v1/nests/claim-codes", { body: {} })).json.code;
    for (const bad of ["https://nest.example.com/", "http://nest.example.com", "https://localhost", "https://192.0.2.1", "https://id.pairnets.app", "https://sync.pairnets.app"]) {
      const r = await h.client().call("POST", "/v1/claim", { body: { code, publicUrl: bad, serverVersion: "1" } });
      expect(r.status, bad).toBe(400);
      expect(r.json.error, bad).toBe("bad_url");
    }
    expect((await h.client("198.51.100.21").call("POST", "/v1/claim", { body: { code, publicUrl: "https://nest.example.com", serverVersion: "1" } })).status).toBe(201);
  });

  it("refuses wrong shapes", async () => {
    const h = await Harness.create();
    const c = h.client();
    for (const body of [
      {},
      { code: 1, publicUrl: "https://a.example.com", serverVersion: "1" },
      { code: "PN-0000-0000-0000", publicUrl: "https://a.example.com" },
      { code: "PN-0000-0000-0000", publicUrl: "https://a.example.com", serverVersion: "x".repeat(33) },
      { code: "PN-0000-0000-0000", publicUrl: "https://a.example.com", serverVersion: "1", label: "x".repeat(65) },
    ]) {
      expect((await c.call("POST", "/v1/claim", { body })).json.error).toBe("bad_request");
    }
  });

  it("3 nests per account, pending ones included", async () => {
    const h = await Harness.create();
    const { b } = await signedIn(h);
    const codes: string[] = [];
    for (let i = 0; i < 4; i++) codes.push((await b.call("POST", "/v1/nests/claim-codes", { body: {} })).json.code);
    for (let i = 0; i < 3; i++) {
      expect((await h.client().call("POST", "/v1/claim", { body: { code: codes[i], publicUrl: `https://n${i}.example.com`, serverVersion: "1" } })).status).toBe(201);
    }
    const fourth = await h.client().call("POST", "/v1/claim", { body: { code: codes[3], publicUrl: "https://n3.example.com", serverVersion: "1" } });
    expect(fourth.status).toBe(409);
    expect(fourth.json.error).toBe("nest_limit");
    const more = await b.call("POST", "/v1/nests/claim-codes", { body: {} });
    expect(more.status).toBe(409);
    expect(more.json.error).toBe("nest_limit");
    // The code was not used up by the refused claim.
    expect((await h.query<{ used_at: number | null }>("SELECT used_at FROM claim_codes WHERE nest_id IS NULL"))[0].used_at).toBeNull();
  });

  it("parallel redeem: exactly one wins", async () => {
    const h = await Harness.create();
    const { b } = await signedIn(h);
    for (let round = 0; round < 5; round++) {
      const code = (await b.call("POST", "/v1/nests/claim-codes", { body: {} })).json.code;
      const results = await Promise.all(
        [0, 1, 2].map((i) => h.client(`198.51.100.${40 + round * 3 + i}`).call("POST", "/v1/claim", { body: { code, publicUrl: `https://r${round}-${i}.example.com`, serverVersion: "1" } })),
      );
      expect(results.map((r) => r.status).sort()).toEqual([201, 409, 409]);
      expect(results.filter((r) => r.status === 409).every((r) => r.json.error === "used")).toBe(true);
      // Only the winner's nest exists, and the code names it.
      const winner = results.find((r) => r.status === 201)!.json.nestId;
      expect((await h.query<{ nest_id: string }>("SELECT nest_id FROM claim_codes WHERE nest_id IS NOT NULL")).map((r) => r.nest_id)).toContain(winner);
      await h.query("DELETE FROM nests");
    }
  });

  it("rate limited after failures", async () => {
    const h = await Harness.create();
    const c = h.client("198.51.100.66");
    for (let i = 0; i < 10; i++) {
      expect((await c.call("POST", "/v1/claim", { body: { code: "PN-0000-0000-0000", publicUrl: "https://a.example.com", serverVersion: "1" } })).json.error).toBe("invalid_code");
    }
    const blocked = await c.call("POST", "/v1/claim", { body: { code: "PN-0000-0000-0000", publicUrl: "https://a.example.com", serverVersion: "1" } });
    expect(blocked.status).toBe(429);
    expect(blocked.json.error).toBe("rate_limited");
    expect(Number(blocked.headers.get("retry-after"))).toBeGreaterThan(0);
    h.advance(3600);
    expect((await c.call("POST", "/v1/claim", { body: {} })).status).toBe(400);

  });

  it("allows 20 claims an hour per IP", async () => {
    const h = await Harness.create();
    const c = h.client("198.51.100.67");
    // 12 successful claims (4 accounts x 3 nests) and 8 failures from one IP: 20 in all.
    let n = 0;
    for (let a = 0; a < 4; a++) {
      const b = h.browser(`203.0.113.${100 + a}`);
      await b.signInByEmail(`owner${a}@example.com`);
      for (let i = 0; i < 3; i++) {
        const code = (await b.call("POST", "/v1/nests/claim-codes", { body: {} })).json.code;
        expect((await c.call("POST", "/v1/claim", { body: { code, publicUrl: `https://n${n++}.example.com`, serverVersion: "1" } })).status).toBe(201);
      }
    }
    for (let i = 0; i < 8; i++) expect((await c.call("POST", "/v1/claim", { body: {} })).status).toBe(400);
    const over = await c.call("POST", "/v1/claim", { body: {} });
    expect(over.status).toBe(429);
    expect(over.json.error).toBe("rate_limited");
  });

  it("pending nest cannot be signed in to, and disappears after an hour", async () => {
    const h = await Harness.create();
    const { b } = await signedIn(h);
    const { res } = await claim(h, b);
    const nestId = res.json.nestId;
    const login = await b.call("GET", `/nest-login?nest=${nestId}&nonce=TestNonce000000000000A&return=${encodeURIComponent("https://nest.example.com/hosted-return")}`);
    expect(login.status).toBe(409);
    expect(login.text).toContain("This nest has not finished linking.");
    expect(login.location).toBeNull();
    h.advance(3600);
    await h.scheduled();
    expect(await h.query("SELECT * FROM nests")).toHaveLength(0);
  });

  it("notice email on confirm", async () => {
    const h = await Harness.create();
    const { b } = await signedIn(h);
    await linkNest(h, b, "https://nest.example.com");
    const notice = h.mailer.to("you@example.com").find((m) => m.subject === "A nest was linked to your account: https://nest.example.com");
    expect(notice).toBeDefined();
    expect(notice!.text).toContain("https://nest.example.com");
    const events = (await h.query<{ event: string }>("SELECT event FROM audit ORDER BY id")).map((r) => r.event);
    expect(events).toEqual(expect.arrayContaining(["claim_code_created", "nest_claimed", "nest_confirmed"]));
  });
});
