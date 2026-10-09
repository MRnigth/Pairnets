import { describe, expect, it } from "vitest";
import { Harness } from "./helpers";

describe("limits", () => {
  it("5 codes an hour per internet address", async () => {
    const h = await Harness.create();
    for (let i = 0; i < 5; i++) expect((await h.start(`name${i}`, `p${i}@example.com`)).status).toBe(202);
    const sixth = await h.start("name5", "p5@example.com");
    expect(sixth.status).toBe(429);
    expect(sixth.json.error).toBe("rate_limited");
    expect(Number(sixth.headers.get("Retry-After"))).toBeGreaterThan(0);
    expect((await h.start("name5", "p5@example.com", "198.51.100.9")).status).toBe(202);
    h.advance(3600);
    expect((await h.start("name6", "p6@example.com")).status).toBe(202);
  });

  it("3 codes a day per email address", async () => {
    const h = await Harness.create();
    for (let i = 0; i < 3; i++) expect((await h.start("alice", "you@example.com", `198.51.100.${i + 1}`)).status).toBe(202);
    expect((await h.start("alice", "you@example.com", "198.51.100.50")).status).toBe(429);
  });

  it("3 names per internet address", async () => {
    const h = await Harness.create();
    for (const n of ["one", "two", "three"]) await h.claim(n, `${n}@example.com`);
    const r = await h.start("four", "four@example.com");
    expect(r.status).toBe(429);
    expect(r.json.error).toBe("address_limit");
    expect((await h.start("four", "four@example.com", "198.51.100.9")).status).toBe(202);
  });

  it("stops before the zone is full (MAX_NAMES)", async () => {
    const h = await Harness.create({ MAX_NAMES: "2" });
    await h.claim("one", "one@example.com", "198.51.100.1");
    // A code asked for while there was room is checked again when it is typed.
    const late = await h.start("three", "three@example.com", "198.51.100.3");
    await h.claim("two", "two@example.com", "198.51.100.2");
    const r = await h.start("four", "four@example.com", "198.51.100.4");
    expect(r.status).toBe(503);
    expect(r.json.error).toBe("full");
    expect(r.json.message).toContain("own domain");
    const typed = await h.finish(late.json.claim_id, h.lastCode("three@example.com"), "198.51.100.3");
    expect(typed.status).toBe(503);
    expect(h.cf.tunnels.size).toBe(2);
  });

  it("DAILY_NAMES new names a day, all users together", async () => {
    const h = await Harness.create({ DAILY_NAMES: "1" });
    await h.claim("one", "one@example.com", "198.51.100.1");
    const r = await h.start("two", "two@example.com", "198.51.100.2");
    expect(r.status).toBe(429);
    expect(r.json.error).toBe("daily_limit");
    h.advance(86400);
    expect((await h.start("two", "two@example.com", "198.51.100.2")).status).toBe(202);
  });

  it("MAIL_DAILY_LIMIT code emails a day, never more than 95", async () => {
    const h = await Harness.create({ MAIL_DAILY_LIMIT: "1" });
    expect((await h.start("one", "one@example.com", "198.51.100.1")).status).toBe(202);
    const r = await h.start("two", "two@example.com", "198.51.100.2");
    expect(r.status).toBe(429);
    expect(r.json.error).toBe("mail_limit");
    expect(h.mailer.sent).toHaveLength(1);
  });

  it("10 wrong keys an hour per internet address, then even the right key waits", async () => {
    const h = await Harness.create();
    const c = await h.claim("alice");
    for (let i = 0; i < 10; i++) expect((await h.manage("POST", "/v1/rotate", "alice", `pnk_${"B".repeat(43)}`)).status).toBe(401);
    expect((await h.manage("POST", "/v1/rotate", "alice", c.key)).status).toBe(429);
    expect((await h.manage("POST", "/v1/rotate", "alice", c.key, "198.51.100.9")).status).toBe(200);
  });

  it("NAMES_PAUSED stops new names; the owners of names can still rotate and give back", async () => {
    const h = await Harness.create();
    const c = await h.claim("alice");
    const pending = await h.start("bob", "b@example.com");
    h.env = { ...h.env, NAMES_PAUSED: "1" };
    const r = await h.start("carol", "c@example.com");
    expect(r.status).toBe(503);
    expect(r.json.error).toBe("paused");
    expect((await h.finish(pending.json.claim_id, h.lastCode("b@example.com"))).json.error).toBe("paused");
    expect((await h.manage("POST", "/v1/rotate", "alice", c.key)).status).toBe(200);
    expect((await h.manage("DELETE", "/v1/name", "alice", c.key)).status).toBe(204);
  });

  it("120 requests a minute per internet address, whatever they are", async () => {
    const h = await Harness.create();
    for (let i = 0; i < 120; i++) expect((await h.call("GET", "/")).status).toBe(200);
    expect((await h.call("GET", "/")).status).toBe(429);
    expect((await h.call("GET", "/v1/available?name=alice")).status).toBe(429);
    expect((await h.call("GET", "/", { ip: "198.51.100.9" })).status).toBe(200);
    h.advance(60);
    expect((await h.call("GET", "/")).status).toBe(200);
  });
});
