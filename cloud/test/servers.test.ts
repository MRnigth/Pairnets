import { describe, expect, it } from "vitest";
import { assetUrl } from "../src/assets";
import { b64urlEncode } from "../src/b64";
import { fakeTunnelToken, fromB64url, toB64url } from "./fake-relay";
import { addServer, FakeNest, Harness, linkNest, startServer } from "./helpers";

async function setup(overrides = {}) {
  const h = await Harness.create(overrides);
  const b = h.browser();
  const accountId = await b.signInByEmail("you@example.com");
  return { h, b, accountId };
}

/** The nest key, derived here with WebCrypto directly (CONTRACT.md 5.1), not with the Worker's helpers. */
async function expectedNestKey(h: Harness, nestId: string): Promise<string> {
  const k = await crypto.subtle.importKey("raw", h.hbMaster as Uint8Array<ArrayBuffer>, { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  return toB64url(new Uint8Array(await crypto.subtle.sign("HMAC", k, new TextEncoder().encode(`pn-hb-key\n${nestId}\n1`))));
}

describe("adding a server", () => {
  it("start, approve and poll once: the installer gets the tunnel token and the nest key", async () => {
    const { h, b, accountId } = await setup();
    const installer = h.client("198.51.100.40");
    const start = await installer.call("POST", "/v1/servers/start", { body: { hostname: "so\u0007ro", serverVersion: "1.0.48" } });
    expect(start.status).toBe(200);
    expect(start.json).toEqual({
      deviceCode: expect.stringMatching(/^psd_[A-Za-z0-9_-]{43}$/),
      userCode: expect.stringMatching(/^[0-9ABCDEFGHJKMNPQRSTVWXYZ]{4}-[0-9ABCDEFGHJKMNPQRSTVWXYZ]{4}$/),
      verificationUri: "https://sync.pairnets.app/add",
      verificationUriComplete: `https://sync.pairnets.app/add?code=${start.json.userCode}`,
      expiresIn: 900,
      interval: 3,
    });
    const { deviceCode, userCode } = start.json;
    expect((await installer.call("POST", "/v1/servers/poll", { body: { deviceCode } })).json).toEqual({ status: "pending" });

    // What the page shows.
    expect((await b.call("GET", `/v1/servers/requests/${userCode.toLowerCase()}`)).json).toEqual({
      userCode,
      hostname: "soro",
      serverVersion: "1.0.48",
      createdAt: "2026-10-09T00:00:00Z",
      expiresAt: "2026-10-09T00:15:00Z",
      status: "pending",
    });
    const page = await b.call("GET", `/add?code=${userCode}`);
    expect(page.status).toBe(200);
    expect(page.text).toContain("<dd>soro</dd>");
    expect(page.text).toContain("<dd>1.0.48</dd>");
    expect(page.text).toContain(`<strong>${userCode}</strong>`);
    expect(page.text).toContain('<input id="label" maxlength="64" autocomplete="off" value="soro">');
    expect(page.text).toContain(">Add this nest</button>");
    expect(page.text).toContain(">Not mine</button>");
    expect(page.text).toContain(`<script src="${assetUrl("add.js")}" defer></script>`);

    const approved = await b.call("POST", "/v1/servers/approve", { body: { userCode, approve: true, label: "Basement" } });
    expect(approved.status, approved.text).toBe(200);
    expect(approved.json).toEqual({ status: "approved", nestId: expect.stringMatching(/^nst_[0-9a-hjkmnp-tv-z]{26}$/) });
    const nestId = approved.json.nestId as string;

    // At Cloudflare: one tunnel named after the nest, with no hostname routed, and the router knows it.
    expect(h.cf.tunnels.size).toBe(1);
    const [tunnelId, tunnel] = [...h.cf.tunnels][0];
    expect(tunnel.created).toEqual({ name: `pairnets-${nestId}`, config_src: "cloudflare" });
    expect(tunnel.ingress).toEqual([{ service: "http_status:404" }]);
    expect(h.cf.routerBindings).toEqual([{ type: "vpc_network", name: `N_${nestId}`, tunnel_id: tunnelId }]);
    const rows = await h.query("SELECT account_id, label, public_url, status, mode, tunnel_id, hosted_login, routed_version FROM nests");
    expect(rows).toEqual([
      { account_id: accountId, label: "Basement", public_url: "", status: "pending", mode: "relay", tunnel_id: tunnelId, hosted_login: 0, routed_version: 1 },
    ]);
    expect(h.mailer.to("you@example.com").some((m) => m.subject === "A nest was added to your account: Basement")).toBe(true);

    h.advance(3);
    const done = await installer.call("POST", "/v1/servers/poll", { body: { deviceCode } });
    expect(done.status, done.text).toBe(200);
    expect(done.json).toEqual({
      status: "approved",
      nestId,
      label: "Basement",
      relayUrl: `https://sync.pairnets.app/n/${nestId}/`,
      tunnelToken: fakeTunnelToken(tunnelId),
      nestKey: await expectedNestKey(h, nestId),
      keyVersion: 1,
      serviceUrl: "https://sync.pairnets.app",
    });
    expect(fromB64url(done.json.nestKey)!.length).toBe(32);
    expect(Object.keys(done.json)).toEqual(["status", "nestId", "label", "relayUrl", "tunnelToken", "nestKey", "keyVersion", "serviceUrl"]);

    // Once.
    h.advance(3);
    const again = await installer.call("POST", "/v1/servers/poll", { body: { deviceCode } });
    expect(again.status).toBe(400);
    expect(again.json.error).toBe("expired");
    expect(await h.query("SELECT status FROM server_links")).toEqual([{ status: "delivered" }]);
    const events = (await h.query<{ event: string }>("SELECT event FROM audit WHERE nest_id = ?1 ORDER BY id", nestId)).map((e) => e.event);
    expect(events).toEqual(["server_added", "server_delivered"]);

    // The installer starts the nest; its health answers through the relay and the server becomes active.
    h.world.install(done.json);
    expect((await h.client().call("GET", `/n/${nestId}/api/health`)).text).toBe("ok");
    expect(await h.query("SELECT status, confirmed_at FROM nests")).toEqual([{ status: "active", confirmed_at: h.clock.now }]);
  });

  it("names the server after the given label, else the hostname, else My server", async () => {
    const { h, b } = await setup();
    await addServer(h, b, { hostname: "nas", label: "  " });
    const { userCode } = await (async () => {
      const r = await h.client("198.51.100.41").call("POST", "/v1/servers/start", { body: {} });
      return r.json;
    })();
    expect((await b.call("POST", "/v1/servers/approve", { body: { userCode, approve: true } })).status).toBe(200);
    expect((await h.query<{ label: string }>("SELECT label FROM nests ORDER BY created_at, label")).map((r) => r.label).sort()).toEqual(["My server", "nas"]);
    const long = await startServer(h, "x", "198.51.100.42");
    expect((await b.call("POST", "/v1/servers/approve", { body: { userCode: long.userCode, approve: true, label: "x".repeat(65) } })).json.error).toBe("bad_request");
  });

  it("Not mine: the installer hears denied and nothing is made at Cloudflare", async () => {
    const { h, b } = await setup();
    const { installer, deviceCode, userCode } = await startServer(h);
    expect((await b.call("POST", "/v1/servers/approve", { body: { userCode, approve: false } })).json).toEqual({ status: "denied" });
    h.advance(3);
    expect((await installer.call("POST", "/v1/servers/poll", { body: { deviceCode } })).json).toEqual({ status: "denied" });
    expect(h.cf.tunnels.size).toBe(0);
    expect(await h.query("SELECT * FROM nests")).toHaveLength(0);
    const again = await b.call("POST", "/v1/servers/approve", { body: { userCode, approve: true } });
    expect(again.status).toBe(409);
    expect(again.json.error).toBe("already_decided");
  });

  it("decided requests stay with their account; unknown, expired and malformed codes are refused", async () => {
    const { h, b } = await setup();
    const one = await startServer(h);
    await b.call("POST", "/v1/servers/approve", { body: { userCode: one.userCode, approve: true } });
    const other = h.browser("203.0.113.71");
    await other.signInByEmail("other@example.com");
    expect((await other.call("GET", `/v1/servers/requests/${one.userCode}`)).status).toBe(404);
    expect((await other.call("POST", "/v1/servers/approve", { body: { userCode: one.userCode, approve: true } })).status).toBe(404);
    expect((await b.call("GET", `/v1/servers/requests/${one.userCode}`)).json.status).toBe("approved");
    const mine = await b.call("POST", "/v1/servers/approve", { body: { userCode: one.userCode, approve: false } });
    expect(mine.status).toBe(409);
    expect(mine.json.error).toBe("already_decided");

    const late = await startServer(h, "late", "198.51.100.43");
    h.advance(901);
    const r = await b.call("POST", "/v1/servers/approve", { body: { userCode: late.userCode, approve: true } });
    expect(r.status).toBe(400);
    expect(r.json.error).toBe("expired");
    expect((await b.call("GET", `/v1/servers/requests/${late.userCode}`)).status).toBe(404);
    expect((await late.installer.call("POST", "/v1/servers/poll", { body: { deviceCode: late.deviceCode } })).json.error).toBe("expired");

    expect((await b.call("POST", "/v1/servers/approve", { body: { userCode: "ZZZZ-ZZZZ", approve: true } })).status).toBe(404);
    expect((await b.call("POST", "/v1/servers/approve", { body: { userCode: "nope", approve: true } })).status).toBe(404);
    expect((await b.call("POST", "/v1/servers/approve", { body: { userCode: "ZZZZ-ZZZZ" } })).json.error).toBe("bad_request");
    expect((await b.call("POST", "/v1/servers/approve", { body: { userCode: 7, approve: true } })).json.error).toBe("bad_request");
    expect((await h.client().call("POST", "/v1/servers/approve", { body: { userCode: late.userCode, approve: true }, origin: "https://sync.pairnets.app" })).status).toBe(401);
  });

  it("the add page needs a session, keeps the code through sign-in, and answers 404 for a bad code", async () => {
    const { h } = await setup();
    const { userCode } = await startServer(h);
    const stranger = h.browser("203.0.113.82");
    expect((await stranger.call("GET", `/add?code=${userCode}`)).location).toBe(`/login?next=${encodeURIComponent(`/add?code=${userCode}`)}`);
    expect((await stranger.call("GET", "/add?code=<script>")).location).toBe("/login?next=%2Fadd");
    // The login page keeps that next (the allow-list knows /add?code=).
    const login = await stranger.call("GET", `/login?next=${encodeURIComponent(`/add?code=${userCode}`)}`);
    expect(login.text).toContain(`data-next="/add?code=${userCode}"`);
    await stranger.signInByEmail("stranger@example.com", `/add?code=${userCode}`);
    const bad = await stranger.call("GET", "/add?code=NOPE-NOPE");
    expect(bad.status).toBe(404);
    expect(bad.text).toContain("That code is not valid or has expired.");
    expect((await stranger.call("GET", "/add")).text).toContain("Code shown by the installer");
    expect((await stranger.call("GET", `/add?code=${userCode}`)).status).toBe(200);
    expect((await h.client().call("GET", `/v1/servers/requests/${userCode}`)).status).toBe(401);
  });

  it("an account has at most 3 servers, own-domain nests included (409 nest_limit, nothing made)", async () => {
    const { h, b } = await setup();
    await linkNest(h, b);
    await addServer(h, b, { ip: "198.51.100.44" });
    await addServer(h, b, { ip: "198.51.100.45" });
    const fourth = await startServer(h, "fourth", "198.51.100.46");
    const r = await b.call("POST", "/v1/servers/approve", { body: { userCode: fourth.userCode, approve: true } });
    expect(r.status).toBe(409);
    expect(r.json).toEqual({ error: "nest_limit", message: "This account already has 3 servers. Remove one first." });
    expect(h.cf.tunnels.size).toBe(2);
    expect((await b.call("GET", `/v1/servers/requests/${fourth.userCode}`)).json.status).toBe("pending");
  });

  it("the service holds at most MAX_NESTS relayed servers (503 full)", async () => {
    const { h, b } = await setup({ MAX_NESTS: "1" });
    await addServer(h, b);
    const other = h.browser("203.0.113.72");
    await other.signInByEmail("other@example.com");
    const { userCode } = await startServer(h, "two", "198.51.100.47");
    const r = await other.call("POST", "/v1/servers/approve", { body: { userCode, approve: true } });
    expect(r.status).toBe(503);
    expect(r.json).toEqual({ error: "full", message: "Pairnets cannot take more servers right now. Try again later." });
    expect(h.cf.tunnels.size).toBe(1);
  });

  for (const step of ["tunnel", "ingress"]) {
    it(`a Cloudflare failure at "${step}" undoes everything; the same code can be approved again`, async () => {
      const { h, b, accountId } = await setup();
      const { installer, deviceCode, userCode } = await startServer(h);
      h.cf.failing.add(step);
      const r = await b.call("POST", "/v1/servers/approve", { body: { userCode, approve: true } });
      expect(r.status).toBe(502);
      expect(r.json).toEqual({
        error: "cloudflare_failed",
        message: "Cloudflare did not finish setting up the server, so nothing was kept. Try again in a few minutes.",
      });
      expect(h.cf.tunnels.size).toBe(0);
      expect(await h.query("SELECT * FROM nests")).toHaveLength(0);
      expect(h.cf.routerDeploys).toHaveLength(0);
      const failed = await h.query<{ account_id: string; detail: string }>("SELECT account_id, detail FROM audit WHERE event = 'server_add_failed'");
      expect(failed).toHaveLength(1);
      expect(failed[0].account_id).toBe(accountId);
      expect(JSON.parse(failed[0].detail)).toEqual({ step, status: 500, codes: [10000], undone: true });
      h.advance(3);
      expect((await installer.call("POST", "/v1/servers/poll", { body: { deviceCode } })).json).toEqual({ status: "pending" });

      h.cf.failing.clear();
      const again = await b.call("POST", "/v1/servers/approve", { body: { userCode, approve: true } });
      expect(again.status).toBe(200);
      expect(h.cf.tunnels.size).toBe(1);
    });
  }

  it("with Cloudflare unreachable the answer is the same and nothing is kept", async () => {
    const { h, b } = await setup();
    const { userCode } = await startServer(h);
    h.cf.network = false;
    const r = await b.call("POST", "/v1/servers/approve", { body: { userCode, approve: true } });
    expect(r.status).toBe(502);
    expect(r.json.error).toBe("cloudflare_failed");
    expect(await h.query("SELECT * FROM nests")).toHaveLength(0);
    // Errors never carry the API token.
    expect(JSON.stringify(await h.query("SELECT * FROM audit"))).not.toContain(h.env.CF_API_TOKEN);
  });

  it("an undo that fails too leaves the row 'broken' with its tunnel, out of the relay, until the sweep deletes both", async () => {
    const { h, b } = await setup();
    const { userCode } = await startServer(h);
    h.cf.failing.add("ingress");
    h.cf.failing.add("delete tunnel");
    expect((await b.call("POST", "/v1/servers/approve", { body: { userCode, approve: true } })).status).toBe(502);
    const tunnelId = [...h.cf.tunnels.keys()][0];
    const rows = await h.query<{ id: string; status: string; tunnel_id: string }>("SELECT id, status, tunnel_id FROM nests");
    expect(rows).toEqual([{ id: expect.any(String), status: "broken", tunnel_id: tunnelId }]);
    expect((await h.client().call("GET", `/n/${rows[0].id}/api/health`)).json.error).toBe("nest_unknown");
    expect(JSON.parse((await h.query<{ detail: string }>("SELECT detail FROM audit WHERE event = 'server_add_failed'"))[0].detail).undone).toBe(false);

    // Still failing: the sweep keeps the row and tries again later.
    await h.scheduled();
    expect(await h.query("SELECT * FROM nests")).toHaveLength(1);
    h.cf.failing.clear();
    h.advance(3600);
    await h.scheduled();
    expect(h.cf.tunnels.size).toBe(0);
    expect(await h.query("SELECT * FROM nests")).toHaveLength(0);
  });

  it("a failed token read keeps the server approved; the next poll gets it", async () => {
    const { h, b } = await setup();
    const { installer, deviceCode, userCode } = await startServer(h);
    await b.call("POST", "/v1/servers/approve", { body: { userCode, approve: true } });
    h.cf.failing.add("token");
    h.advance(3);
    const r = await installer.call("POST", "/v1/servers/poll", { body: { deviceCode } });
    expect(r.status).toBe(502);
    expect(r.json).toEqual({ error: "cloudflare_failed", message: "Cloudflare did not answer. The installer tries again by itself." });
    expect(await h.query("SELECT status FROM server_links")).toEqual([{ status: "approved" }]);
    h.cf.failing.clear();
    h.advance(3);
    const ok = await installer.call("POST", "/v1/servers/poll", { body: { deviceCode } });
    expect(ok.status).toBe(200);
    expect(ok.json.tunnelToken).toBe(fakeTunnelToken([...h.cf.tunnels.keys()][0]));
  });

  it("polls: slow_down within the interval, expired for unknown codes and after 15 minutes", async () => {
    const { h } = await setup();
    const { installer, deviceCode } = await startServer(h);
    expect((await installer.call("POST", "/v1/servers/poll", { body: { deviceCode } })).status).toBe(200);
    const fast = await installer.call("POST", "/v1/servers/poll", { body: { deviceCode } });
    expect(fast.status).toBe(429);
    expect(fast.json.error).toBe("slow_down");
    expect(fast.headers.get("retry-after")).toBe("3");
    h.advance(2);
    expect((await installer.call("POST", "/v1/servers/poll", { body: { deviceCode } })).status).toBe(429);
    h.advance(1);
    expect((await installer.call("POST", "/v1/servers/poll", { body: { deviceCode } })).json).toEqual({ status: "pending" });
    for (const bad of ["psd_unknown", `psd_${b64urlEncode(new Uint8Array(32))}`, "pcd_x"]) {
      expect((await installer.call("POST", "/v1/servers/poll", { body: { deviceCode: bad } })).json.error, bad).toBe("expired");
    }
    expect((await installer.call("POST", "/v1/servers/poll", { body: { deviceCode: 5 } })).json.error).toBe("bad_request");
    expect((await installer.call("POST", "/v1/servers/poll", { body: {} })).json.error).toBe("bad_request");
    h.advance(900);
    expect((await installer.call("POST", "/v1/servers/poll", { body: { deviceCode } })).json.error).toBe("expired");
  });

  it("start: optional fields of the right type, at most 64 characters; 10 an hour per IP", async () => {
    const { h } = await setup();
    const c = h.client("198.51.100.48");
    expect((await c.call("POST", "/v1/servers/start", { body: { hostname: 5 } })).json.error).toBe("bad_request");
    expect((await c.call("POST", "/v1/servers/start", { body: { hostname: "x".repeat(65) } })).json.error).toBe("bad_request");
    expect((await c.call("POST", "/v1/servers/start", { body: { serverVersion: ["1"] } })).json.error).toBe("bad_request");
    expect((await c.call("POST", "/v1/servers/start", { rawBody: "[1]" })).json.error).toBe("bad_request");
    expect((await c.call("POST", "/v1/servers/start")).status).toBe(200);
    // Every start counts, refused ones included: 4 refused + 6 made = 10.
    for (let i = 0; i < 5; i++) expect((await c.call("POST", "/v1/servers/start", { body: { hostname: `pc${i}` } })).status).toBe(200);
    const over = await c.call("POST", "/v1/servers/start", { body: {} });
    expect(over.status).toBe(429);
    expect(over.json.error).toBe("rate_limited");
    expect(Number(over.headers.get("retry-after"))).toBeGreaterThan(0);
  });

  it("two approvals of one code at once: one server", async () => {
    const { h, b } = await setup();
    const { userCode } = await startServer(h);
    const both = await Promise.all([0, 1].map(() => b.call("POST", "/v1/servers/approve", { body: { userCode, approve: true } })));
    expect(both.map((r) => r.status).sort()).toEqual([200, 409]);
    expect(h.cf.tunnels.size).toBe(1);
    expect(await h.query("SELECT * FROM nests")).toHaveLength(1);
  });

  it("20 approvals an hour per account", async () => {
    const { h, b } = await setup();
    let ip = 0;
    const attempt = async () => {
      const s = await startServer(h, "pc", `198.51.100.${100 + Math.floor(ip++ / 10)}`);
      return b.call("POST", "/v1/servers/approve", { body: { userCode: s.userCode, approve: true } });
    };
    for (let i = 0; i < 3; i++) expect((await attempt()).status).toBe(200);
    for (let i = 3; i < 20; i++) expect((await attempt()).json.error).toBe("nest_limit");
    const over = await attempt();
    expect(over.status).toBe(429);
    expect(over.json.error).toBe("rate_limited");
  });

  it("relayed servers stay out of the version 1 paths (signed requests, nest login, /v1/nests)", async () => {
    const { h, b } = await setup();
    const nest = await addServer(h, b);
    // The nest key would verify (same derivation), but these requests belong to nests on their own domain.
    const v1 = new FakeNest(h, nest.nestId, nest.key);
    expect((await v1.confirm()).status).toBe(410);
    expect((await v1.heartbeat()).status).toBe(410);
    expect((await v1.unlink()).status).toBe(410);
    expect(await h.query("SELECT status FROM nests")).toEqual([{ status: "pending" }]);
    const login = await b.call("GET", `/nest-login?nest=${nest.nestId}&nonce=TestNonce000000000000A&return=${encodeURIComponent("/hosted-return")}`);
    expect(login.status).toBe(404);
    expect(login.location).toBeNull();
    expect((await b.call("GET", "/v1/nests")).json).toEqual({ nests: [] });
    expect((await b.call("PATCH", `/v1/nests/${nest.nestId}`, { body: { label: "x" } })).status).toBe(404);
    expect((await b.call("DELETE", `/v1/nests/${nest.nestId}`)).status).toBe(404);
  });
});
