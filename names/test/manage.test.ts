import { describe, expect, it } from "vitest";
import { Harness } from "./helpers";

describe("rotate", () => {
  it("gives a new token with the manage key, and cuts off whoever runs the old one", async () => {
    const h = await Harness.create();
    const c = await h.claim("alice");
    h.cf.connectAll();
    const r = await h.manage("POST", "/v1/rotate", "alice", c.key);
    expect(r.status).toBe(200);
    expect(Object.keys(r.json)).toEqual(["name", "tunnel_token"]);
    expect(r.json.tunnel_token).toMatch(/^[A-Za-z0-9+/=_-]{40,}$/);
    expect(r.json.tunnel_token).not.toBe(c.token);
    const tunnel = [...h.cf.tunnels.values()][0];
    expect(tunnel.version).toBe(2);
    expect(tunnel.connections).toBe(0);
    expect(await h.everythingStored()).not.toContain(r.json.tunnel_token);
    expect((await h.query<{ event: string }>("SELECT event FROM events")).map((e) => e.event)).toContain("rotated");
  });

  it("refuses a missing, wrong or other name's key, and an unknown name, with the same answer", async () => {
    const h = await Harness.create();
    const alice = await h.claim("alice", "a@example.com");
    const bob = await h.claim("bob", "b@example.com");
    const tries: [string, string | null][] = [
      ["alice", null],
      ["alice", `pnk_${"A".repeat(43)}`],
      ["alice", bob.key],
      ["carol", alice.key],
      ["Not a name", alice.key],
    ];
    for (const [name, key] of tries) {
      const r = await h.manage("POST", "/v1/rotate", name, key);
      expect(r.status, `${name} ${key}`).toBe(401);
      expect(r.json).toEqual({ error: "unauthorized", message: "That key does not fit this name." });
    }
    // The key alone, or in another header form, is not enough either.
    expect((await h.call("POST", "/v1/rotate", { body: { name: "alice" }, headers: { Authorization: alice.key } })).status).toBe(401);
    expect([...h.cf.tunnels.values()].every((t) => t.version === 1)).toBe(true);
  });

  it("is limited to 5 an hour per name", async () => {
    const h = await Harness.create();
    const c = await h.claim("alice");
    for (let i = 0; i < 5; i++) expect((await h.manage("POST", "/v1/rotate", "alice", c.key)).status).toBe(200);
    const sixth = await h.manage("POST", "/v1/rotate", "alice", c.key);
    expect(sixth.status).toBe(429);
    expect(Number(sixth.headers.get("Retry-After"))).toBeGreaterThan(0);
  });

  it("says so when Cloudflare does not make the token", async () => {
    const h = await Harness.create();
    const c = await h.claim("alice");
    h.cf.failing.add("rotate");
    const r = await h.manage("POST", "/v1/rotate", "alice", c.key);
    expect(r.status).toBe(502);
    expect(r.json).toEqual({ error: "cloudflare_failed", message: "Cloudflare did not make a new token. Try again in a few minutes." });
  });
});

describe("give back", () => {
  it("deletes the DNS record and the tunnel, frees the name and the address, and the key stops working", async () => {
    const h = await Harness.create();
    const c = await h.claim("alice");
    h.cf.connectAll();
    const r = await h.manage("DELETE", "/v1/name", "alice", c.key);
    expect(r.status).toBe(204);
    expect(h.cf.tunnels.size).toBe(0);
    expect(h.cf.records.size).toBe(0);
    expect(await h.query("SELECT * FROM names")).toHaveLength(0);
    expect((await h.manage("POST", "/v1/rotate", "alice", c.key)).status).toBe(401);
    expect((await h.manage("DELETE", "/v1/name", "alice", c.key)).status).toBe(401);
    // Anyone may take it now, the same address included.
    expect((await h.claim("alice")).publicUrl).toBe("https://alice.pairnets.app");
  });

  it("needs the key", async () => {
    const h = await Harness.create();
    await h.claim("alice");
    expect((await h.manage("DELETE", "/v1/name", "alice", null)).status).toBe(401);
    expect(h.cf.tunnels.size).toBe(1);
  });

  it("that Cloudflare does not finish answers 202, keeps the name out of reach, and the sweep finishes it", async () => {
    const h = await Harness.create();
    const c = await h.claim("alice");
    h.cf.failing.add("delete tunnel");
    const r = await h.manage("DELETE", "/v1/name", "alice", c.key);
    expect(r.status).toBe(202);
    expect(r.json.status).toBe("releasing");
    expect(h.cf.records.size).toBe(0); // the name already stopped working
    expect((await h.call("GET", "/v1/available?name=alice")).json.available).toBe(false);
    // Not active any more: no new tokens for it.
    expect((await h.manage("POST", "/v1/rotate", "alice", c.key)).json.error).toBe("name_busy");

    h.cf.failing.clear();
    await h.scheduled();
    expect(h.cf.tunnels.size).toBe(0);
    expect(await h.query("SELECT * FROM names")).toHaveLength(0);
  });
});
