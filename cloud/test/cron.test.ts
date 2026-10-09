import { describe, expect, it } from "vitest";
import { addServer, claim, Harness, linkNest, startServer } from "./helpers";

describe("cron sweep", () => {
  it("deletes expired rows, stale pending nests, old counters and 90-day-old audit; keeps the rest", async () => {
    const h = await Harness.create();
    const b = h.browser();
    await b.signInByEmail("you@example.com");
    const active = await linkNest(h, b);
    const { nest: stalePending } = await claim(h, b, "https://stale.example.com");
    await b.call("POST", "/v1/nests/claim-codes", { body: {} });
    await b.call("POST", "/v1/login/email", { body: { email: "you@example.com", turnstile: "ok" } });
    const app = h.client();
    await app.call("POST", "/v1/app/start", { body: { name: "Laptop" } });
    await h.env.DB.prepare("INSERT INTO audit (at, account_id, event) VALUES (?1, NULL, 'ancient')").bind(h.clock.now - 91 * 86400).run();

    const before = async (t: string) => (await h.query(`SELECT * FROM ${t}`)).length;
    expect(await before("login_challenges")).toBeGreaterThan(0);

    h.advance(3600);
    // A fresh pending nest (claimed just now, by a second account) must survive.
    const b2 = h.browser("203.0.113.2");
    await b2.signInByEmail("second@example.com");
    const { nest: freshPending } = await claim(h, b2, "https://fresh.example.com");
    await h.scheduled();

    expect(await h.query("SELECT * FROM login_challenges WHERE expires_at <= ?1", h.clock.now)).toHaveLength(0);
    expect(await h.query("SELECT * FROM login_challenges")).toHaveLength(1); // the second account's, still fresh
    expect(await h.query("SELECT * FROM device_logins")).toHaveLength(0);
    expect((await h.query<{ expires_at: number }>("SELECT expires_at FROM claim_codes")).every((r) => r.expires_at > h.clock.now)).toBe(true);
    const nests = (await h.query<{ id: string; status: string }>("SELECT id, status FROM nests")).map((r) => r.id);
    expect(nests).toContain(active.nestId);
    expect(nests).toContain(freshPending!.nestId);
    expect(nests).not.toContain(stalePending!.nestId);
    expect(await h.query("SELECT * FROM audit WHERE event = 'ancient'")).toHaveLength(0);
    expect((await h.query("SELECT * FROM audit")).length).toBeGreaterThan(0);
    expect((await h.query("SELECT * FROM sessions")).length).toBe(2);

    // 31 idle days later the sessions are gone, and so are the old rate counters.
    h.advance(31 * 86400);
    await h.scheduled();
    expect(await h.query("SELECT * FROM sessions")).toHaveLength(0);
    const counters = await h.query<{ window_start: number }>("SELECT window_start FROM rate_counters");
    expect(counters.every((c) => c.window_start >= h.clock.now - 2 * 86400)).toBe(true);
  });
});

describe("cron sweep and relayed servers", () => {
  async function setup() {
    const h = await Harness.create();
    const b = h.browser();
    await b.signInByEmail("you@example.com");
    return { h, b };
  }

  it("deletes expired server links and gives up a server never reached within an hour (link, tunnel, row)", async () => {
    const { h, b } = await setup();
    const unused = await startServer(h, "unused", "198.51.100.49");
    const reached = await addServer(h, b, { label: "reached" });
    const never = await addServer(h, b, { label: "never", ip: "198.51.100.41" });
    expect((await h.client().call("GET", `/n/${reached.nestId}/api/health`)).text).toBe("ok");

    h.advance(1800);
    await h.scheduled();
    expect(await h.query("SELECT * FROM server_links WHERE user_code = ?1", unused.userCode.replace("-", ""))).toHaveLength(0);
    expect(await h.query("SELECT * FROM nests")).toHaveLength(2);

    h.advance(1800);
    await h.scheduled();
    expect((await h.query<{ id: string; status: string }>("SELECT id, status FROM nests"))).toEqual([{ id: reached.nestId, status: "active" }]);
    expect([...h.cf.tunnels.keys()]).toEqual([reached.tunnelId]);
    expect(h.cf.routerBindings.map((x) => x.name)).toEqual([`N_${reached.nestId}`]);
    expect(never.devices.size).toBe(0);
  });

  it("retries a removed server's tunnel every hour until it is gone", async () => {
    const { h, b } = await setup();
    const nest = await addServer(h, b);
    h.cf.failing.add("delete tunnel");
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}`)).status).toBe(204);
    expect(h.cf.routerBindings).toEqual([]);
    expect(await h.query("SELECT status FROM nests")).toEqual([{ status: "broken" }]);
    h.advance(3600);
    await h.scheduled();
    expect(await h.query("SELECT status FROM nests")).toEqual([{ status: "broken" }]);
    h.cf.failing.clear();
    h.advance(3600);
    await h.scheduled();
    expect(await h.query("SELECT * FROM nests")).toHaveLength(0);
    expect(h.cf.tunnels.size).toBe(0);
  });

  it("deploys the router when a server is missing from it, and not otherwise", async () => {
    const { h, b } = await setup();
    const nest = await addServer(h, b);
    expect(h.cf.routerDeploys).toHaveLength(1);
    await h.scheduled();
    expect(h.cf.routerDeploys).toHaveLength(1);
    await h.env.DB.prepare("UPDATE nests SET routed_version = NULL").run();
    h.cf.routerBindings = [];
    await h.scheduled();
    expect(h.cf.routerDeploys).toHaveLength(2);
    expect(h.cf.routerBindings.map((x) => x.name)).toEqual([`N_${nest.nestId}`]);
  });

  it("deletes tunnels of nests that no longer exist, and leaves every other tunnel alone", async () => {
    const { h, b } = await setup();
    const nest = await addServer(h, b);
    const add = (name: string) => {
      const id = crypto.randomUUID();
      h.cf.tunnels.set(id, { created: { name }, name, ingress: null, connections: 1 });
      return id;
    };
    const orphan = add("pairnets-nst_0000000000000000000000abcd");
    const named = add("pairnets-name-alice");
    const foreign = add("home-office");
    const odd = add("pairnets-nst_NOTANID");
    await h.scheduled();
    expect(h.cf.tunnels.has(orphan)).toBe(false);
    for (const id of [named, foreign, odd, nest.tunnelId]) expect(h.cf.tunnels.has(id)).toBe(true);
  });

  it("finds a tunnel by its name when the request that made it died before storing its id", async () => {
    const { h, b } = await setup();
    const accountId = (await b.call("GET", "/v1/me")).json.accountId as string;
    const nestId = "nst_0000000000000000000000dead";
    await h.env.DB.prepare(
      "INSERT INTO nests (id, account_id, label, public_url, status, hosted_login, created_at, mode) VALUES (?1, ?2, 'x', '', 'pending', 0, ?3, 'relay')",
    )
      .bind(nestId, accountId, h.clock.now)
      .run();
    const tunnelId = crypto.randomUUID();
    h.cf.tunnels.set(tunnelId, { created: { name: `pairnets-${nestId}` }, name: `pairnets-${nestId}`, ingress: null, connections: 0 });
    h.advance(3601);
    await h.scheduled();
    expect(h.cf.tunnels.has(tunnelId)).toBe(false);
    expect(await h.query("SELECT * FROM nests")).toHaveLength(0);
  });
});
