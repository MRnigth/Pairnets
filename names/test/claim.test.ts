import { describe, expect, it } from "vitest";
import { b64urlEncode, sha256 } from "../src/crypto";
import { API_TOKEN, Harness, IP } from "./helpers";

describe("claim", () => {
  it("emails a code, then makes the tunnel, its ingress and the DNS record, and answers the token and key once", async () => {
    const h = await Harness.create();
    const started = await h.start("alice", "  You@Example.com ");
    expect(started.status).toBe(202);
    expect(Object.keys(started.json)).toEqual(["claim_id", "expires_in"]);
    expect(started.json.claim_id).toMatch(/^pnc_[A-Za-z0-9_-]{24}$/);
    expect(started.json.expires_in).toBe(900);

    // The address is lowercased and trimmed; the code is in the subject and on its own line.
    const mail = h.mailer.to("you@example.com");
    expect(mail).toHaveLength(1);
    const code = h.lastCode();
    expect(mail[0].subject).toBe(`Your code for alice.pairnets.app: ${code}`);
    expect(mail[0].text).toContain("support@pairnets.app");
    expect(mail[0].html).toContain(code);
    // Nothing is made in Cloudflare before the code is typed.
    expect(h.cf.tunnels.size).toBe(0);

    const done = await h.finish(started.json.claim_id, ` ${code.slice(0, 3)} ${code.slice(3)} `);
    expect(done.status).toBe(201);
    expect(Object.keys(done.json)).toEqual(["name", "public_url", "tunnel_token", "manage_key"]);
    expect(done.json.name).toBe("alice");
    expect(done.json.public_url).toBe("https://alice.pairnets.app");
    expect(done.json.manage_key).toMatch(/^pnk_[A-Za-z0-9_-]{43}$/);

    // One tunnel, managed from Cloudflare, sending the name to the nest on port 5075 and nothing else.
    expect(h.cf.tunnels.size).toBe(1);
    const [tunnelId, tunnel] = [...h.cf.tunnels][0];
    expect(tunnel.name).toMatch(/^pairnets-alice-[0-9a-z]{6}$/);
    expect(tunnel.configSrc).toBe("cloudflare");
    expect(tunnel.ingress).toEqual([{ hostname: "alice.pairnets.app", service: "http://localhost:5075" }, { service: "http_status:404" }]);
    expect(done.json.tunnel_token).toMatch(/^[A-Za-z0-9+/=_-]{40,}$/);
    // One proxied CNAME to that tunnel.
    expect([...h.cf.records.values()]).toEqual([{ type: "CNAME", name: "alice.pairnets.app", content: `${tunnelId}.cfargotunnel.com`, proxied: true }]);
    // Every Cloudflare call carried the API token; nothing else ever saw it.
    const calls = h.net.callsTo("https://api.cloudflare.com/");
    expect(calls.length).toBe(4);
    expect(calls.every((c) => c.headers.get("authorization") === `Bearer ${API_TOKEN}`)).toBe(true);
    expect(done.text).not.toContain(API_TOKEN);

    // Stored: an active row with a hash of the key. Never the token, the key, the code or the claim id.
    const rows = await h.query<{ status: string; tunnel_id: string; dns_record_id: string; manage_key_hash: number[]; email: string }>("SELECT * FROM names");
    expect(rows).toHaveLength(1);
    expect(rows[0].status).toBe("active");
    expect(rows[0].tunnel_id).toBe(tunnelId);
    expect(rows[0].dns_record_id).toMatch(/^[0-9a-f]{32}$/);
    expect(rows[0].email).toBe("you@example.com");
    expect(b64urlEncode(Uint8Array.from(rows[0].manage_key_hash))).toBe(b64urlEncode(await sha256(done.json.manage_key)));
    const stored = await h.everythingStored();
    for (const secret of [done.json.tunnel_token, done.json.manage_key, code, started.json.claim_id, IP]) expect(stored).not.toContain(secret);
    expect(await h.query("SELECT * FROM claims")).toHaveLength(0);
    expect((await h.query<{ event: string }>("SELECT event FROM events")).map((e) => e.event)).toEqual(["code_sent", "claimed"]);
  });

  it("refuses bad names, kept names and bad addresses before sending anything", async () => {
    const h = await Harness.create();
    for (const name of ["ab", "Alice", "-alice", "alice-", "al_ice", "a--b", "x".repeat(33), 7, null]) {
      const r = await h.start(name as string);
      expect(r.status, String(name)).toBe(400);
      expect(r.json.error).toBe("bad_name");
    }
    for (const name of ["www", "names", "nest", "mail", "support", "pairnets-help", "mypairnets"]) {
      const r = await h.start(name);
      expect(r.status, name).toBe(409);
      expect(r.json.error).toBe("name_reserved");
    }
    for (const email of ["", "you", "you@", "@example.com", "you@example", "a b@example.com", 'you"@example.com', "you@example.com, them@example.com", null]) {
      const r = await h.start("alice", email as string);
      expect(r.status, String(email)).toBe(400);
      expect(r.json.error).toBe("bad_email");
    }
    expect(h.mailer.sent).toHaveLength(0);
    expect(await h.query("SELECT * FROM claims")).toHaveLength(0);
  });

  it("takes only JSON", async () => {
    const h = await Harness.create();
    expect((await h.call("POST", "/v1/claim/start", { rawBody: "name=alice&email=you%40example.com", contentType: "application/x-www-form-urlencoded" })).status).toBe(415);
    expect((await h.call("POST", "/v1/claim/start", { rawBody: "{not json" })).json.error).toBe("bad_request");
    expect((await h.call("POST", "/v1/claim/start", { rawBody: "[1,2]" })).json.error).toBe("bad_request");
    expect((await h.call("POST", "/v1/claim/start", { rawBody: "" })).json.error).toBe("bad_request");
    expect((await h.call("POST", "/v1/claim/start", { body: { name: "alice", email: "you@example.com" }, contentType: null })).status).toBe(415);
    expect((await h.call("POST", "/v1/claim/start", { rawBody: "x".repeat(17 * 1024) })).status).toBe(413);
    expect((await h.call("POST", "/v1/claim", { body: { claim_id: "nope", code: "123456" } })).json.error).toBe("bad_request");
  });

  it("a wrong code says how many tries are left; the fifth wrong one ends the claim", async () => {
    const h = await Harness.create();
    const started = await h.start("alice");
    const right = h.lastCode();
    const wrong = right === "000000" ? "111111" : "000000";
    for (let left = 4; left >= 1; left--) {
      const r = await h.finish(started.json.claim_id, wrong);
      expect(r.status).toBe(400);
      expect(r.json.error).toBe("wrong_code");
      expect(r.json.message).toBe(`That code is not right (${left} ${left === 1 ? "try" : "tries"} left).`);
    }
    const fifth = await h.finish(started.json.claim_id, "abc");
    expect(fifth.status).toBe(429);
    expect(fifth.json.error).toBe("too_many_tries");
    // The claim is gone: even the right code no longer works.
    const late = await h.finish(started.json.claim_id, right);
    expect(late.status).toBe(400);
    expect(late.json.error).toBe("expired");
    expect(h.cf.tunnels.size).toBe(0);
  });

  it("a code works once, within 15 minutes", async () => {
    const h = await Harness.create();
    const first = await h.start("alice");
    const code = h.lastCode();
    h.advance(15 * 60);
    const late = await h.finish(first.json.claim_id, code);
    expect(late.status).toBe(400);
    expect(late.json.error).toBe("expired");
    expect(late.json.message).toBe("The code has expired. Run the installer again to get a new one.");

    const second = await h.start("alice");
    const code2 = h.lastCode();
    expect((await h.finish(second.json.claim_id, code2)).status).toBe(201);
    expect((await h.finish(second.json.claim_id, code2)).json.error).toBe("expired");
    expect(h.cf.tunnels.size).toBe(1);
  });

  it("a name has one owner: a taken name is refused, and of two claims at once the first to finish wins", async () => {
    const h = await Harness.create();
    const a = await h.start("alice", "a@example.com");
    const codeA = h.lastCode("a@example.com");
    const b = await h.start("alice", "b@example.com", "198.51.100.7");
    const codeB = h.lastCode("b@example.com");
    expect((await h.finish(a.json.claim_id, codeA)).status).toBe(201);
    const lost = await h.finish(b.json.claim_id, codeB, "198.51.100.7");
    expect(lost.status).toBe(409);
    expect(lost.json.error).toBe("name_taken");
    expect(lost.json.message).toBe("That name is taken. Pick another one.");

    const again = await h.start("alice", "c@example.com", "198.51.100.8");
    expect(again.status).toBe(409);
    expect(again.json.error).toBe("name_taken");
    expect(h.cf.tunnels.size).toBe(1);
    expect(h.cf.records.size).toBe(1);
  });

  it("an email address has one name, and is only told so after its code", async () => {
    const h = await Harness.create();
    await h.claim("alice");
    const started = await h.start("bob");
    expect(started.status).toBe(202);
    const r = await h.finish(started.json.claim_id, h.lastCode());
    expect(r.status).toBe(409);
    expect(r.json.error).toBe("email_has_name");
    expect(r.json.message).toContain("pairnets-name.sh release");
    expect(h.cf.tunnels.size).toBe(1);
  });

  it("a name already in the zone's DNS is not free: the tunnel made for it is deleted again", async () => {
    const h = await Harness.create();
    h.cf.addForeignRecord("blog2.pairnets.app");
    const started = await h.start("blog2");
    const r = await h.finish(started.json.claim_id, h.lastCode());
    expect(r.status).toBe(409);
    expect(r.json.error).toBe("name_taken");
    expect(h.cf.tunnels.size).toBe(0);
    expect(h.cf.records.size).toBe(1); // only the foreign record, untouched
    expect(await h.query("SELECT * FROM names")).toHaveLength(0);
  });

  it("an email that cannot be sent says so and keeps nothing", async () => {
    const h = await Harness.create();
    h.mailer.failing = true;
    const r = await h.start("alice");
    expect(r.status).toBe(502);
    expect(r.json.error).toBe("mail_failed");
    expect(await h.query("SELECT * FROM claims")).toHaveLength(0);
  });

  it("GET /v1/available tells free, taken, kept and invalid names apart", async () => {
    const h = await Harness.create();
    await h.claim("alice");
    const ask = async (name: string) => (await h.call("GET", `/v1/available?name=${encodeURIComponent(name)}`)).json;
    expect(await ask("bob")).toEqual({ name: "bob", available: true });
    expect(await ask("alice")).toEqual({ name: "alice", available: false, reason: "taken" });
    expect(await ask("www")).toEqual({ name: "www", available: false, reason: "reserved" });
    expect(await ask("No!")).toEqual({ name: "No!", available: false, reason: "invalid" });
    expect((await h.call("GET", "/v1/available")).json).toEqual({ name: "", available: false, reason: "invalid" });
  });
});
