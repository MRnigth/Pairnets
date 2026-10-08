import { describe, expect, it } from "vitest";
import { claim, Harness, linkNest } from "./helpers";

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
