import { describe, expect, it } from "vitest";
import { Harness } from "./helpers";

async function claimWith(h: Harness, name = "alice") {
  const started = await h.start(name);
  expect(started.status).toBe(202);
  return h.finish(started.json.claim_id, h.lastCode());
}

describe("a claim that fails half-way", () => {
  for (const step of ["tunnel", "token", "ingress", "dns"]) {
    it(`at "${step}" leaves nothing behind, and the name can be claimed again`, async () => {
      const h = await Harness.create();
      h.cf.failing.add(step);
      const r = await claimWith(h);
      expect(r.status).toBe(502);
      expect(r.json.error).toBe("cloudflare_failed");
      expect(r.json.message).toBe("Cloudflare did not finish setting up the name, so nothing was kept. Try again in a few minutes.");
      expect(h.cf.tunnels.size).toBe(0);
      expect(h.cf.records.size).toBe(0);
      expect(await h.query("SELECT * FROM names")).toHaveLength(0);
      const events = await h.query<{ event: string; detail: string }>("SELECT event, detail FROM events WHERE event = 'claim_failed'");
      expect(events).toHaveLength(1);
      expect(JSON.parse(events[0].detail)).toMatchObject({ step, status: 500, codes: [10000], undone: true });

      h.cf.failing.clear();
      expect((await claimWith(h)).status).toBe(201);
      expect(h.cf.tunnels.size).toBe(1);
    });
  }

  it("with Cloudflare unreachable answers the same, and keeps nothing", async () => {
    const h = await Harness.create();
    h.cf.network = false;
    const r = await claimWith(h);
    expect(r.status).toBe(502);
    expect(r.json.error).toBe("cloudflare_failed");
    expect(await h.query("SELECT * FROM names")).toHaveLength(0);
  });

  it("whose undo fails too keeps the name blocked until the hourly sweep has deleted the leftovers", async () => {
    const h = await Harness.create();
    h.cf.failing.add("dns");
    h.cf.failing.add("delete tunnel");
    const r = await claimWith(h);
    expect(r.status).toBe(502);
    expect(h.cf.tunnels.size).toBe(1); // could not be deleted
    const rows = await h.query<{ status: string; tunnel_id: string }>("SELECT status, tunnel_id FROM names");
    expect(rows).toEqual([{ status: "broken", tunnel_id: [...h.cf.tunnels.keys()][0] }]);
    expect((await h.call("GET", "/v1/available?name=alice")).json.available).toBe(false);

    // Still failing: the sweep keeps the row and tries again later.
    await h.scheduled();
    expect(await h.query("SELECT * FROM names")).toHaveLength(1);

    h.cf.failing.clear();
    h.advance(3600);
    await h.scheduled();
    expect(h.cf.tunnels.size).toBe(0);
    expect(await h.query("SELECT * FROM names")).toHaveLength(0);
    expect((await h.query<{ event: string }>("SELECT event FROM events")).map((e) => e.event)).toContain("leftover_cleaned");
    expect((await h.call("GET", "/v1/available?name=alice")).json.available).toBe(true);
  });

  it("whose Worker died half-way is cleaned up by the sweep after an hour, not before", async () => {
    const h = await Harness.create();
    await claimWith(h, "alice");
    const tunnelId = [...h.cf.tunnels.keys()][0];
    const recordId = [...h.cf.records.keys()][0];
    // As if the request had stopped right after the DNS record was made.
    await h.env.DB.prepare("UPDATE names SET status = 'creating', manage_key_hash = NULL WHERE name = 'alice'").run();
    h.advance(1800);
    await h.scheduled();
    expect(await h.query("SELECT * FROM names")).toHaveLength(1);
    expect(h.cf.tunnels.has(tunnelId)).toBe(true);

    h.advance(1800);
    await h.scheduled();
    expect(await h.query("SELECT * FROM names")).toHaveLength(0);
    expect(h.cf.tunnels.has(tunnelId)).toBe(false);
    expect(h.cf.records.has(recordId)).toBe(false);
  });

  it("leaves active names alone", async () => {
    const h = await Harness.create();
    await claimWith(h, "alice");
    h.advance(7 * 86400);
    await h.scheduled();
    expect(await h.query("SELECT status FROM names")).toEqual([{ status: "active" }]);
    expect(h.cf.tunnels.size).toBe(1);
  });
});
