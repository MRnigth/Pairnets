import { describe, expect, it } from "vitest";
import { Harness, linkNest } from "./helpers";

async function setup() {
  const h = await Harness.create();
  const b = h.browser();
  const accountId = await b.signInByEmail("you@example.com");
  const app = h.client("198.51.100.80");
  return { h, b, accountId, app };
}

describe("app-login", () => {
  it("device-code flow: start, approve, poll once for the token, list nests", async () => {
    const { h, b, app } = await setup();
    const nest = await linkNest(h, b);
    const start = await app.call("POST", "/v1/app/start", { body: { name: "Marcus' laptop", system: "Windows 11", appVersion: "1.0.47" } });
    expect(start.status).toBe(200);
    expect(start.json).toEqual({
      deviceCode: expect.stringMatching(/^pcd_[A-Za-z0-9_-]{43}$/),
      userCode: expect.stringMatching(/^[0-9ABCDEFGHJKMNPQRSTVWXYZ]{4}-[0-9ABCDEFGHJKMNPQRSTVWXYZ]{4}$/),
      verificationUri: "https://id.pairnets.app/app",
      verificationUriComplete: `https://id.pairnets.app/app?code=${start.json.userCode}`,
      expiresIn: 600,
      interval: 3,
    });
    const { deviceCode, userCode } = start.json;
    expect((await app.call("POST", "/v1/app/poll", { body: { deviceCode } })).json).toEqual({ status: "pending" });

    const page = await b.call("GET", `/app?code=${userCode}`);
    expect(page.status).toBe(200);
    expect(page.text).toContain("Marcus&#39; laptop");
    expect(page.text).toContain("Windows 11");
    expect(page.text).toContain(userCode);
    const req = await b.call("GET", `/v1/app/requests/${userCode.toLowerCase()}`);
    expect(req.json).toEqual({
      userCode,
      name: "Marcus' laptop",
      system: "Windows 11",
      appVersion: "1.0.47",
      createdAt: "2026-10-09T00:00:00Z",
      expiresAt: "2026-10-09T00:10:00Z",
      status: "pending",
    });

    expect((await b.call("POST", "/v1/app/approve", { body: { userCode, approve: true } })).json).toEqual({ status: "approved" });
    h.advance(3);
    const done = await app.call("POST", "/v1/app/poll", { body: { deviceCode } });
    expect(done.json).toEqual({ status: "approved", appToken: expect.stringMatching(/^pca_[A-Za-z0-9_-]{43}$/), expiresIn: 3600, email: "you@example.com" });
    h.advance(3);
    const again = await app.call("POST", "/v1/app/poll", { body: { deviceCode } });
    expect(again.status).toBe(400);
    expect(again.json.error).toBe("expired");

    const token = done.json.appToken as string;
    const nests = await app.call("GET", "/v1/nests", { headers: { Authorization: `Bearer ${token}` } });
    expect(nests.status).toBe(200);
    expect(nests.json.nests.map((n: { nestId: string }) => n.nestId)).toEqual([nest.nestId]);
    const me = await app.call("GET", "/v1/me", { headers: { Authorization: `Bearer ${token}` } });
    expect(me.json.session).toMatchObject({ kind: "app", recentAuth: false });
    // The account sees the app in its sessions list, with the computer's name.
    const sessions = (await b.call("GET", "/v1/sessions")).json.sessions;
    expect(sessions.find((s: { kind: string }) => s.kind === "app").deviceName).toBe("Marcus' laptop");

    expect((await app.call("POST", "/v1/app/logout", { headers: { Authorization: `Bearer ${token}` } })).status).toBe(204);
    expect((await app.call("GET", "/v1/nests", { headers: { Authorization: `Bearer ${token}` } })).status).toBe(401);
    expect((await app.call("POST", "/v1/app/logout", { headers: { Authorization: `Bearer ${token}` } })).status).toBe(204);
  });

  it("app token cannot create claim codes (only GET /v1/nests, GET /v1/me, logout)", async () => {
    const { h, b, app } = await setup();
    const start = await app.call("POST", "/v1/app/start", { body: { name: "Laptop" } });
    await b.call("POST", "/v1/app/approve", { body: { userCode: start.json.userCode, approve: true } });
    const token = (await app.call("POST", "/v1/app/poll", { body: { deviceCode: start.json.deviceCode } })).json.appToken;
    const auth = { Authorization: `Bearer ${token}` };
    for (const [method, path] of [
      ["POST", "/v1/nests/claim-codes"],
      ["DELETE", "/v1/nests/claim-codes"],
      ["DELETE", "/v1/me"],
      ["GET", "/v1/sessions"],
      ["POST", "/v1/app/approve"],
      ["PATCH", "/v1/nests/nst_testnest000000000000000001"],
    ]) {
      const r = await app.call(method, path, { headers: auth, origin: "https://id.pairnets.app", body: method === "GET" ? undefined : {} });
      expect(r.status, path).toBe(401);
      expect(r.json.error, path).toBe("unauthorized");
    }
    expect((await app.call("GET", "/account", { headers: auth })).location).toBe("/login?next=/account");
    // An app token expires after an hour.
    h.advance(3600);
    expect((await app.call("GET", "/v1/nests", { headers: auth })).status).toBe(401);
    // A browser cookie does not stand in for a bad Bearer token.
    expect((await b.call("GET", "/v1/nests", { headers: { Authorization: "Bearer pca_nope" } })).status).toBe(401);
  });

  it("deny, slow_down, expiry and already_decided", async () => {
    const { h, b, app } = await setup();
    const s1 = await app.call("POST", "/v1/app/start", { body: { name: "Laptop" } });
    await app.call("POST", "/v1/app/poll", { body: { deviceCode: s1.json.deviceCode } });
    const fast = await app.call("POST", "/v1/app/poll", { body: { deviceCode: s1.json.deviceCode } });
    expect(fast.status).toBe(429);
    expect(fast.json.error).toBe("slow_down");
    expect((await b.call("POST", "/v1/app/approve", { body: { userCode: s1.json.userCode, approve: false } })).json).toEqual({ status: "denied" });
    const again = await b.call("POST", "/v1/app/approve", { body: { userCode: s1.json.userCode, approve: true } });
    expect(again.status).toBe(409);
    expect(again.json.error).toBe("already_decided");
    h.advance(3);
    expect((await app.call("POST", "/v1/app/poll", { body: { deviceCode: s1.json.deviceCode } })).json).toEqual({ status: "denied" });

    // Another account cannot see or answer this account's decided request.
    const other = h.browser("203.0.113.81");
    await other.signInByEmail("other@example.com");
    expect((await other.call("GET", `/v1/app/requests/${s1.json.userCode}`)).status).toBe(404);
    expect((await other.call("POST", "/v1/app/approve", { body: { userCode: s1.json.userCode, approve: true } })).status).toBe(404);

    const s2 = await app.call("POST", "/v1/app/start", { body: { name: "Laptop" } });
    h.advance(601);
    const late = await b.call("POST", "/v1/app/approve", { body: { userCode: s2.json.userCode, approve: true } });
    expect(late.status).toBe(400);
    expect(late.json.error).toBe("expired");
    expect((await app.call("POST", "/v1/app/poll", { body: { deviceCode: s2.json.deviceCode } })).json.error).toBe("expired");
    expect((await app.call("POST", "/v1/app/poll", { body: { deviceCode: "pcd_unknown" } })).json.error).toBe("expired");
    expect((await b.call("GET", `/v1/app/requests/${s2.json.userCode}`)).status).toBe(404);
    expect((await b.call("POST", "/v1/app/approve", { body: { userCode: "ZZZZ-ZZZZ", approve: true } })).status).toBe(404);
    expect((await b.call("POST", "/v1/app/approve", { body: { userCode: "ZZZZ-ZZZZ" } })).json.error).toBe("bad_request");
  });

  it("the approve page needs a session and keeps the code through sign-in", async () => {
    const { h, app } = await setup();
    const s = await app.call("POST", "/v1/app/start", { body: { name: "Laptop" } });
    const stranger = h.browser("203.0.113.82");
    const r = await stranger.call("GET", `/app?code=${s.json.userCode}`);
    expect(r.location).toBe(`/login?next=${encodeURIComponent(`/app?code=${s.json.userCode}`)}`);
    expect((await stranger.call("GET", "/app?code=<script>")).location).toBe("/login?next=%2Fapp");
    await stranger.signInByEmail("stranger@example.com");
    const page = await stranger.call("GET", "/app?code=NOPE-NOPE");
    expect(page.text).toContain("That code is not valid or has expired.");
    expect((await stranger.call("GET", "/app")).text).toContain("Code shown by the app");
    expect((await stranger.call("GET", `/v1/app/requests/${s.json.userCode}`, { headers: {} })).status).toBe(200);
    expect((await h.client().call("GET", `/v1/app/requests/${s.json.userCode}`)).status).toBe(401);
  });

  it("start checks the name and is limited to 10 an hour per IP", async () => {
    const { app } = await setup();
    expect((await app.call("POST", "/v1/app/start", { body: {} })).json.error).toBe("bad_request");
    expect((await app.call("POST", "/v1/app/start", { body: { name: "  " } })).json.error).toBe("bad_request");
    expect((await app.call("POST", "/v1/app/start", { body: { name: "x".repeat(65) } })).json.error).toBe("bad_request");
    expect((await app.call("POST", "/v1/app/start", { body: { name: "ok", appVersion: "x".repeat(33) } })).json.error).toBe("bad_request");
    for (let i = 0; i < 6; i++) expect((await app.call("POST", "/v1/app/start", { body: { name: `PC ${i}` } })).status).toBe(200);
    const over = await app.call("POST", "/v1/app/start", { body: { name: "PC" } });
    expect(over.status).toBe(429);
    expect(over.json.error).toBe("rate_limited");
  });

  it("two parallel polls of an approved login: one token", async () => {
    const { b, app, h } = await setup();
    const s = await app.call("POST", "/v1/app/start", { body: { name: "Laptop" } });
    await b.call("POST", "/v1/app/approve", { body: { userCode: s.json.userCode, approve: true } });
    const polls = await Promise.all([0, 1].map((i) => h.client(`198.51.100.${90 + i}`).call("POST", "/v1/app/poll", { body: { deviceCode: s.json.deviceCode } })));
    const statuses = polls.map((p) => p.status).sort();
    // One gets the token; the other is told to slow down or that it is over.
    expect(polls.filter((p) => p.json.appToken)).toHaveLength(1);
    expect(statuses[0]).toBe(200);
    expect(await h.query("SELECT * FROM sessions WHERE kind = 'app'")).toHaveLength(1);
  });
});
