import { describe, expect, it } from "vitest";
import { b64urlEncode } from "../src/b64";
import { randomBytes } from "../src/crypto";
import { FakeGoogle } from "./fake-google";
import { addServer, appSignIn, Harness, linkNest } from "./helpers";

async function setup() {
  const h = await Harness.create();
  const b = h.browser();
  const accountId = await b.signInByEmail("you@example.com");
  const app = h.client("198.51.100.80");
  return { h, b, accountId, app };
}

describe("app-login", () => {
  it("device-code flow: start, choose the server, allow, and the first poll brings a device key once", async () => {
    const { h, b, app } = await setup();
    const nest = await addServer(h, b, { label: "soro" });
    const start = await app.call("POST", "/v1/app/start", { body: { name: "Alex's laptop", system: "Windows 11", appVersion: "1.0.47" } });
    expect(start.status).toBe(200);
    expect(start.json).toEqual({
      deviceCode: expect.stringMatching(/^pcd_[A-Za-z0-9_-]{43}$/),
      userCode: expect.stringMatching(/^[0-9ABCDEFGHJKMNPQRSTVWXYZ]{4}-[0-9ABCDEFGHJKMNPQRSTVWXYZ]{4}$/),
      verificationUri: "https://sync.pairnets.app/app",
      verificationUriComplete: `https://sync.pairnets.app/app?code=${start.json.userCode}`,
      expiresIn: 600,
      interval: 3,
    });
    const { deviceCode, userCode } = start.json;
    expect((await app.call("POST", "/v1/app/poll", { body: { deviceCode } })).json).toEqual({ status: "pending" });

    const page = await b.call("GET", `/app?code=${userCode}`);
    expect(page.status).toBe(200);
    expect(page.text).toContain("Alex&#39;s laptop");
    expect(page.text).toContain("Windows 11");
    expect(page.text).toContain(userCode);
    // One nest: nothing to choose, the page names it and app.js sends it.
    expect(page.text).toContain(`data-nest="${nest.nestId}"`);
    expect(page.text).toContain("It will sync with <b>soro</b>");
    expect(page.text).toContain('id="approve"');
    const req = await b.call("GET", `/v1/app/requests/${userCode.toLowerCase()}`);
    expect(req.json).toEqual({
      userCode,
      name: "Alex's laptop",
      system: "Windows 11",
      appVersion: "1.0.47",
      createdAt: "2026-10-09T00:00:03Z",
      expiresAt: "2026-10-09T00:10:03Z",
      status: "pending",
    });

    expect((await b.call("POST", "/v1/app/approve", { body: { userCode, approve: true, nestId: nest.nestId } })).json).toEqual({ status: "approved" });
    h.advance(3);
    const done = await app.call("POST", "/v1/app/poll", { body: { deviceCode } });
    expect(done.status, done.text).toBe(200);
    expect(done.json).toEqual({
      status: "approved",
      appToken: expect.stringMatching(/^pca_[A-Za-z0-9_-]{43}$/),
      expiresIn: 3600,
      email: "you@example.com",
      nest: { id: nest.nestId, label: "soro", serverUrl: `https://sync.pairnets.app/n/${nest.nestId}/`, sharedBy: null, access: "owner", folders: null },
      device: { id: expect.any(String), name: "Alex's laptop", key: expect.stringMatching(/^pn_/) },
    });
    // The nest made exactly that device, approved by the masked account address; the service kept no key.
    expect([...nest.devices.values()]).toEqual([
      expect.objectContaining({
        id: done.json.device.id,
        name: "Alex's laptop",
        system: "Windows 11",
        key: done.json.device.key,
        approvedBy: "approved by y***@example.com on sync.pairnets.app",
      }),
    ]);
    h.advance(3);
    const again = await app.call("POST", "/v1/app/poll", { body: { deviceCode } });
    expect(again.status).toBe(400);
    expect(again.json.error).toBe("expired");
    expect(nest.devices.size).toBe(1);

    const token = done.json.appToken as string;
    const me = await app.call("GET", "/v1/me", { headers: { Authorization: `Bearer ${token}` } });
    expect(me.json.session).toMatchObject({ kind: "app", recentAuth: false });
    // Relayed servers are not in the version 1 list (that one is for nests on their own domain).
    expect((await app.call("GET", "/v1/nests", { headers: { Authorization: `Bearer ${token}` } })).json).toEqual({ nests: [] });
    // The account sees the app in its sessions list, with the computer's name.
    const sessions = (await b.call("GET", "/v1/sessions")).json.sessions;
    expect(sessions.find((s: { kind: string }) => s.kind === "app").deviceName).toBe("Alex's laptop");

    expect((await app.call("POST", "/v1/app/logout", { headers: { Authorization: `Bearer ${token}` } })).status).toBe(204);
    expect((await app.call("GET", "/v1/me", { headers: { Authorization: `Bearer ${token}` } })).status).toBe(401);
    expect((await app.call("POST", "/v1/app/logout", { headers: { Authorization: `Bearer ${token}` } })).status).toBe(204);
  });

  it("app token cannot create claim codes (only GET /v1/nests, GET /v1/me, logout)", async () => {
    const { h, b } = await setup();
    const nest = await addServer(h, b);
    const { poll } = await appSignIn(h, b, nest.nestId);
    const auth = { Authorization: `Bearer ${poll.json.appToken}` };
    const app = h.client();
    for (const [method, path] of [
      ["POST", "/v1/nests/claim-codes"],
      ["DELETE", "/v1/nests/claim-codes"],
      ["DELETE", "/v1/me"],
      ["GET", "/v1/sessions"],
      ["POST", "/v1/app/approve"],
      ["PATCH", "/v1/nests/nst_testnest000000000000000001"],
      ["GET", "/v1/servers"],
      ["POST", "/v1/servers/approve"],
      ["DELETE", `/v1/servers/${nest.nestId}`],
      ["DELETE", `/v1/servers/${nest.nestId}/devices/dev1`],
    ]) {
      const r = await app.call(method, path, { headers: auth, origin: "https://sync.pairnets.app", body: method === "GET" ? undefined : {} });
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
    const nest = await addServer(h, b);
    const s1 = await app.call("POST", "/v1/app/start", { body: { name: "Laptop" } });
    await app.call("POST", "/v1/app/poll", { body: { deviceCode: s1.json.deviceCode } });
    const fast = await app.call("POST", "/v1/app/poll", { body: { deviceCode: s1.json.deviceCode } });
    expect(fast.status).toBe(429);
    expect(fast.json.error).toBe("slow_down");
    expect((await b.call("POST", "/v1/app/approve", { body: { userCode: s1.json.userCode, approve: false } })).json).toEqual({ status: "denied" });
    const again = await b.call("POST", "/v1/app/approve", { body: { userCode: s1.json.userCode, approve: true, nestId: nest.nestId } });
    expect(again.status).toBe(409);
    expect(again.json.error).toBe("already_decided");
    h.advance(3);
    expect((await app.call("POST", "/v1/app/poll", { body: { deviceCode: s1.json.deviceCode } })).json).toEqual({ status: "denied" });
    expect(nest.devices.size).toBe(0);

    // Another account cannot see or answer this account's decided request.
    const other = h.browser("203.0.113.81");
    await other.signInByEmail("other@example.com");
    expect((await other.call("GET", `/v1/app/requests/${s1.json.userCode}`)).status).toBe(404);
    expect((await other.call("POST", "/v1/app/approve", { body: { userCode: s1.json.userCode, approve: false } })).status).toBe(404);

    const s2 = await app.call("POST", "/v1/app/start", { body: { name: "Laptop" } });
    h.advance(601);
    const late = await b.call("POST", "/v1/app/approve", { body: { userCode: s2.json.userCode, approve: true, nestId: nest.nestId } });
    expect(late.status).toBe(400);
    expect(late.json.error).toBe("expired");
    expect((await app.call("POST", "/v1/app/poll", { body: { deviceCode: s2.json.deviceCode } })).json.error).toBe("expired");
    expect((await app.call("POST", "/v1/app/poll", { body: { deviceCode: "pcd_unknown" } })).json.error).toBe("expired");
    expect((await b.call("GET", `/v1/app/requests/${s2.json.userCode}`)).status).toBe(404);
    expect((await b.call("POST", "/v1/app/approve", { body: { userCode: "ZZZZ-ZZZZ", approve: true, nestId: nest.nestId } })).status).toBe(404);
    expect((await b.call("POST", "/v1/app/approve", { body: { userCode: "ZZZZ-ZZZZ" } })).json.error).toBe("bad_request");
  });

  it("allowing needs one of the account's own relayed servers", async () => {
    const { h, b, app } = await setup();
    const mine = await addServer(h, b);
    const ownDomain = await linkNest(h, b);
    const other = h.browser("203.0.113.84");
    await other.signInByEmail("other@example.com");
    const theirs = await addServer(h, other, { ip: "198.51.100.41" });
    const s = await app.call("POST", "/v1/app/start", { body: { name: "Laptop" } });
    const allow = (nestId: unknown) => b.call("POST", "/v1/app/approve", { body: { userCode: s.json.userCode, approve: true, nestId } });

    // Without a nest only while the account has none; this one has one, so it must be chosen.
    expect((await allow(undefined)).json.error).toBe("bad_request");
    expect((await allow(null)).json.error).toBe("bad_request");
    expect((await allow("garbage")).json.error).toBe("bad_request");
    expect((await allow(42)).json.error).toBe("bad_request");
    expect((await allow(theirs.nestId)).status).toBe(404);
    expect((await allow(ownDomain.nestId)).status).toBe(404);
    expect((await allow("nst_testnest000000000000000001")).status).toBe(404);
    // Still pending after all that: the right server works.
    expect((await allow(mine.nestId)).json).toEqual({ status: "approved" });
    expect((await h.query<{ nest_id: string }>("SELECT nest_id FROM device_logins"))[0].nest_id).toBe(mine.nestId);
  });

  it("an unreachable server answers 503 nest_offline and keeps the login approved; a later poll gets the key", async () => {
    const { h, b, app } = await setup();
    const nest = await addServer(h, b);
    const s = await app.call("POST", "/v1/app/start", { body: { name: "Laptop", system: "macOS 15" } });
    await b.call("POST", "/v1/app/approve", { body: { userCode: s.json.userCode, approve: true, nestId: nest.nestId } });
    nest.running = false;
    h.advance(3);
    const offline = await app.call("POST", "/v1/app/poll", { body: { deviceCode: s.json.deviceCode } });
    expect(offline.status).toBe(503);
    expect(offline.json).toEqual({ error: "nest_offline", message: "Your server is not connected right now. Check that it is on." });
    expect((await h.query<{ status: string }>("SELECT status FROM device_logins"))[0].status).toBe("approved");
    // The poll interval still holds.
    expect((await app.call("POST", "/v1/app/poll", { body: { deviceCode: s.json.deviceCode } })).json.error).toBe("slow_down");

    // A link that answers with cloudflared's 502 page counts the same.
    h.world.linkFailure = "502";
    h.advance(3);
    expect((await app.call("POST", "/v1/app/poll", { body: { deviceCode: s.json.deviceCode } })).json.error).toBe("nest_offline");

    nest.running = true;
    h.advance(3);
    const ok = await app.call("POST", "/v1/app/poll", { body: { deviceCode: s.json.deviceCode } });
    expect(ok.status).toBe(200);
    expect(ok.json.device.name).toBe("Laptop");
    expect(nest.devices.size).toBe(1);
    expect([...nest.devices.values()][0].system).toBe("macOS 15");
  });

  it("a server that refuses the signature gives no key (503), and nothing is delivered", async () => {
    const { h, b } = await setup();
    const nest = await addServer(h, b);
    // As if the service's master key had changed since the server was added: its signatures no longer verify.
    h.env.HB_MASTER = b64urlEncode(randomBytes(32));
    const { poll } = await appSignIn(h, b, nest.nestId);
    expect(poll.status).toBe(503);
    expect(poll.json.error).toBe("nest_offline");
    expect(nest.refused).toEqual([{ path: "/api/relay/devices", step: "sig" }]);
    expect(nest.devices.size).toBe(0);
    expect(await h.query("SELECT * FROM sessions WHERE kind = 'app'")).toHaveLength(0);
  });

  it("a server removed after the approval: the poll says expired", async () => {
    const { h, b, app } = await setup();
    const nest = await addServer(h, b);
    const s = await app.call("POST", "/v1/app/start", { body: { name: "Laptop" } });
    await b.call("POST", "/v1/app/approve", { body: { userCode: s.json.userCode, approve: true, nestId: nest.nestId } });
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}`)).status).toBe(204);
    h.advance(3);
    const r = await app.call("POST", "/v1/app/poll", { body: { deviceCode: s.json.deviceCode } });
    expect(r.status).toBe(400);
    expect(r.json.error).toBe("expired");
    expect(nest.devices.size).toBe(0);
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
    expect(page.text).toContain("That code didn't work");
    expect((await stranger.call("GET", "/app")).text).toContain("Code shown by Pairnets");
    expect((await stranger.call("GET", `/v1/app/requests/${s.json.userCode}`, { headers: {} })).status).toBe(200);
    expect((await h.client().call("GET", `/v1/app/requests/${s.json.userCode}`)).status).toBe(401);
    // With no nest yet, the page says so, says how to set one up, and still offers Allow (the app then waits).
    const none = await stranger.call("GET", `/app?code=${s.json.userCode}`);
    expect(none.text).toContain("You don't have a nest yet.");
    expect(none.text).toContain("curl -fsSL https://pairnets.app/get.sh | sudo bash");
    expect(none.text).toContain('id="approve"');
    expect(none.text).toContain('id="deny"');
  });

  it("an account with no nest yet: the app is told and waits, then joins the nest the account adds", async () => {
    const { h, b, app } = await setup();
    const s = await app.call("POST", "/v1/app/start", { body: { name: "Laptop", system: "Windows 11" } });
    expect((await b.call("POST", "/v1/app/approve", { body: { userCode: s.json.userCode, approve: true } })).json).toEqual({ status: "approved" });
    // The login now has half an hour to wait for a nest.
    const row = (await h.query<{ expires_at: number; nest_id: string | null }>("SELECT expires_at, nest_id FROM device_logins"))[0];
    expect(row.nest_id).toBeNull();
    expect(row.expires_at).toBe(h.clock.now + 1800);
    const poll = () => app.call("POST", "/v1/app/poll", { body: { deviceCode: s.json.deviceCode } });
    expect((await poll()).json).toEqual({ status: "no_nest", email: "you@example.com" });
    h.advance(600);
    expect((await poll()).json.status).toBe("no_nest");
    // The account adds a nest (the installer's link); the next poll joins it and hands out the key, once.
    h.advance(3);
    const nest = await addServer(h, b);
    h.advance(3);
    const done = await poll();
    expect(done.status, done.text).toBe(200);
    expect(done.json.status).toBe("approved");
    expect(done.json.nest.id).toBe(nest.nestId);
    expect(done.json.device.name).toBe("Laptop");
    expect((await h.query<{ nest_id: string }>("SELECT nest_id FROM device_logins"))[0].nest_id).toBe(nest.nestId);
    h.advance(3);
    expect((await poll()).json.error).toBe("expired");
  });

  it("the page lists every server of the account and picks the first", async () => {
    const { h, b, app } = await setup();
    const one = await addServer(h, b, { label: "Basement" });
    const two = await addServer(h, b, { label: "Office <NAS>", ip: "198.51.100.42" });
    const s = await app.call("POST", "/v1/app/start", { body: { name: "Laptop" } });
    const page = await b.call("GET", `/app?code=${s.json.userCode}`);
    expect(page.text).toContain(`<input type="radio" name="nest" value="${one.nestId}" data-label="Basement" checked> Basement`);
    expect(page.text).toContain(`<input type="radio" name="nest" value="${two.nestId}" data-label="Office &lt;NAS&gt;"> Office &lt;NAS&gt;`);
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

  it("two parallel polls of an approved login: one token, one device", async () => {
    const { b, app, h } = await setup();
    const nest = await addServer(h, b);
    const s = await app.call("POST", "/v1/app/start", { body: { name: "Laptop" } });
    await b.call("POST", "/v1/app/approve", { body: { userCode: s.json.userCode, approve: true, nestId: nest.nestId } });
    const polls = await Promise.all([0, 1].map((i) => h.client(`198.51.100.${90 + i}`).call("POST", "/v1/app/poll", { body: { deviceCode: s.json.deviceCode } })));
    const statuses = polls.map((p) => p.status).sort();
    // One gets the token; the other is told to slow down or that it is over.
    expect(polls.filter((p) => p.json.appToken)).toHaveLength(1);
    expect(statuses[0]).toBe(200);
    expect(await h.query("SELECT * FROM sessions WHERE kind = 'app'")).toHaveLength(1);
    expect(nest.devices.size).toBe(1);
  });
});

describe("allowing a computer is a security step", () => {
  async function pending(cf?: { country?: string; city?: string }, staleSession = false) {
    const s = await setup();
    const nest = await addServer(s.h, s.b, { label: "soro" });
    if (staleSession) s.h.advance(901); // the person signed in long ago; the request below is new
    const start = await s.app.call("POST", "/v1/app/start", {
      body: { name: "Alex‮'s laptop", system: "Windows 11", appVersion: "1.0.47" },
      cf,
    });
    expect(start.status).toBe(200);
    return { ...s, nest, start, userCode: start.json.userCode as string, deviceCode: start.json.deviceCode as string };
  }

  it("a sign-in older than 15 minutes cannot allow; a fresh one can, and the computer gets in", async () => {
    const { h, b, nest, userCode, deviceCode, app } = await pending(undefined, true);
    const old = await b.call("POST", "/v1/app/approve", { body: { userCode, approve: true, nestId: nest.nestId } });
    expect(old.status).toBe(401);
    expect(old.json.error).toBe("reauth_required");
    const seen = await b.call("GET", `/v1/app/requests/${userCode}`);
    expect(seen.json.status, seen.text).toBe("pending");
    // Saying no never needs the fresh sign-in.
    const start2 = await app.call("POST", "/v1/app/start", { body: { name: "Other" } });
    expect((await b.call("POST", "/v1/app/approve", { body: { userCode: start2.json.userCode, approve: false } })).json).toEqual({ status: "denied" });

    // The page sends the person to sign in again, and back to this very request.
    const page = await b.call("GET", `/app?code=${userCode}`);
    expect(page.status).toBe(302);
    expect(page.location).toBe(`/login?reauth=1&next=${encodeURIComponent(`/app?code=${userCode}`)}`);
    const login = await b.call("GET", page.location!);
    expect(login.status).toBe(200);
    expect(login.text).toContain("sign in again");

    // A fresh sign-in (the new session) allows in one go.
    await b.signInByEmail("you@example.com", `/app?code=${userCode}`);
    expect((await b.call("GET", `/app?code=${userCode}`)).status).toBe(200);
    const ok = await b.call("POST", "/v1/app/approve", { body: { userCode, approve: true, nestId: nest.nestId } });
    expect(ok.json).toEqual({ status: "approved" });
    h.advance(3);
    expect((await app.call("POST", "/v1/app/poll", { body: { deviceCode } })).json.status).toBe("approved");
  });

  it("signed in with Google long ago: signing in again with Google allows, though Google sends its old login time", async () => {
    // The live bug of 2026-10-16: Google answered the re-check with the person's earlier login time, and the site said
    // "Google sign-in did not work".
    const h = await Harness.create();
    const g = await FakeGoogle.install(h);
    const b = h.browser();
    const googleLogin = h.clock.now;
    await g.signIn(b);
    const nest = await addServer(h, b, { label: "soro" });
    h.advance(16 * 60);
    const app = h.client("198.51.100.80");
    const start = await app.call("POST", "/v1/app/start", { body: { name: "PC-2", system: "Linux", appVersion: "1.0.56" } });
    const { userCode, deviceCode } = start.json as { userCode: string; deviceCode: string };

    const next = `/app?code=${userCode}`;
    const page = await b.call("GET", next);
    expect(page.location).toBe(`/login?reauth=1&next=${encodeURIComponent(next)}`);
    const login = await b.call("GET", page.location!);
    expect(login.text).toContain(`href="/login/google?next=${encodeURIComponent(next)}&amp;reauth=1"`);

    g.claims = { auth_time: googleLogin };
    const { auth, callback } = await g.signIn(b, `?next=${encodeURIComponent(next)}&reauth=1`);
    expect(auth.searchParams.get("max_age")).toBe("0");
    expect(callback.location).toBe(next);
    expect((await b.call("GET", next)).status).toBe(200);
    const ok = await b.call("POST", "/v1/app/approve", { body: { userCode, approve: true, nestId: nest.nestId } });
    expect(ok.json).toEqual({ status: "approved" });
    h.advance(3);
    expect((await app.call("POST", "/v1/app/poll", { body: { deviceCode } })).json.status).toBe("approved");
    expect(h.mailer.to("you@gmail.com").filter((m) => m.subject.includes("A computer was allowed"))).toHaveLength(1);
  });

  it("the page script sends a reauth_required answer to the sign-in page with reauth=1", async () => {
    const { b } = await pending();
    const js = await b.call("GET", "/assets/app.js");
    expect(js.text).toContain('(err === "reauth_required" ? "reauth=1&" : "")');
  });

  it("the Allow page says where the request came from, and the computer name has no hidden characters", async () => {
    const { b, userCode } = await pending({ country: "DK", city: "Copenhagen" });
    const page = await b.call("GET", `/app?code=${userCode}`);
    expect(page.status).toBe(200);
    expect(page.text).toContain("This request came from <b>Copenhagen, Denmark</b>.");
    expect(page.text).toContain("If that isn't where your computer is, don't allow it.");
    expect(page.text).toContain("Allow Alex&#39;s laptop to sign in?");
    expect(page.text).not.toContain("‮");
  });

  it("when Cloudflare does not know the place the page says so; only country and city are stored", async () => {
    const { h, b, userCode } = await pending({ country: "XX" });
    const page = await b.call("GET", `/app?code=${userCode}`);
    expect(page.text).toContain("We couldn't tell where this request came from.");
    const none = await pending({ country: "SE", city: "Malmö" });
    const row = await h.query<{ request_country: string | null; request_city: string | null }>(
      "SELECT request_country, request_city FROM device_logins WHERE user_code = ?1",
      none.userCode.replace("-", ""),
    );
    expect(row).toEqual([{ request_country: "SE", request_city: "Malmö" }]);
  });

  it("allowing emails a security notice with the name, the nest, the time and the place", async () => {
    const { h, b, nest, userCode } = await pending({ country: "DK", city: "Copenhagen" });
    expect(h.mailer.to("you@example.com").filter((m) => m.subject.includes("allowed"))).toHaveLength(0);
    h.advance(5);
    await b.call("POST", "/v1/app/approve", { body: { userCode, approve: true, nestId: nest.nestId } });
    const notice = h.mailer.to("you@example.com").filter((m) => m.subject.includes("A computer was allowed"));
    expect(notice).toHaveLength(1);
    expect(notice[0].subject).toBe("A computer was allowed to use your account: Alex's laptop");
    const plain = (html: string) => html.replace(/&quot;/g, '"').replace(/&#39;/g, "'");
    for (const part of [notice[0].text, plain(notice[0].html)]) {
      expect(part).toContain("\"Alex's laptop\"");
      expect(part).toContain('the nest "soro"');
      expect(part).toContain("2026-10-09 00:00 UTC");
      expect(part).toContain("Copenhagen, Denmark");
      expect(part).toContain("https://sync.pairnets.app/account");
      expect(part).toContain("If this wasn");
    }
  });

  it("a refusal sends no notice", async () => {
    const { h, b, userCode } = await pending();
    await b.call("POST", "/v1/app/approve", { body: { userCode, approve: false } });
    expect(h.mailer.to("you@example.com").filter((m) => m.subject.includes("A computer was allowed"))).toHaveLength(0);
  });
});
