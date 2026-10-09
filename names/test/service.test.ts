import { describe, expect, it } from "vitest";
import { CSP } from "../src/http";
import { MESSAGES } from "../src/http";
import { codeEmail, RESEND_URL, ResendMailer } from "../src/mail";
import { checkName, normalizeEmail } from "../src/names";
import { FakeNet, Harness, jsonResponse } from "./helpers";

describe("the page and the headers", () => {
  it("GET / says what the service is and where to report abuse", async () => {
    const h = await Harness.create();
    const r = await h.call("GET", "/");
    expect(r.status).toBe(200);
    expect(r.headers.get("Content-Type")).toBe("text/html; charset=utf-8");
    expect(r.text).toContain("alice.pairnets.app");
    expect(r.text).toContain("mailto:support@pairnets.app");
    expect(r.text.replace(/\s+/g, " ")).toContain("they never pass through this service");
    expect(r.headers.get("Content-Security-Policy")).toBe(CSP);
    expect(r.headers.get("X-Frame-Options")).toBe("DENY");
    expect(r.headers.get("Cache-Control")).toBe("no-store");
  });

  it("unknown paths are 404; no response ever allows another site to read it", async () => {
    const h = await Harness.create();
    expect((await h.call("GET", "/admin")).status).toBe(404);
    const api = await h.call("GET", "/v1/nope");
    expect(api.status).toBe(404);
    expect(api.json).toEqual({ error: "not_found", message: "Not found." });
    const preflight = await h.call("OPTIONS", "/v1/claim/start", { headers: { Origin: "https://evil.example", "Access-Control-Request-Method": "POST" } });
    expect(preflight.status).toBe(404);
    for (const r of [api, preflight]) expect([...r.headers.keys()].some((k) => k.startsWith("access-control-"))).toBe(false);
  });

  it("every error message is one plain line the installer can read (no quotes or backslashes)", () => {
    for (const [code, message] of Object.entries(MESSAGES)) expect(message, code).toMatch(/^[^"\\\n\r]+$/);
  });
});

describe("names and addresses", () => {
  it("names: 3 to 32 of a-z, 0-9 and single hyphens inside", () => {
    for (const ok of ["abc", "alice", "my-nest", "a1b", "123", "a".repeat(32), "x-y-z"]) expect(checkName(ok), ok).toEqual({ ok: true, name: ok });
    for (const bad of ["ab", "a".repeat(33), "Alice", "-ab", "ab-", "a--b", "xn--abc", "al.ice", "al ice", "ålice", ""]) {
      expect(checkName(bad), bad).toEqual({ ok: false, code: "bad_name" });
    }
    for (const kept of ["www", "api", "abuse", "pairnets", "get-pairnets", "pairnetsapp"]) expect(checkName(kept), kept).toEqual({ ok: false, code: "name_reserved" });
  });

  it("addresses: trimmed and lowercased, nothing that could change an email's headers", () => {
    expect(normalizeEmail(" Me.Too+tag@Mail.Example.COM ")).toBe("me.too+tag@mail.example.com");
    for (const bad of ["me", "me@localhost", "me@@example.com", ".me@example.com", "me.@example.com", "m..e@example.com", "me@example.c", "me <x@example.com>", "me@example.com\r\nBcc: x@example.com", `${"a".repeat(65)}@example.com`]) {
      expect(normalizeEmail(bad), bad).toBeNull();
    }
  });
});

describe("mail", () => {
  it("Resend: POST with the Bearer key and MAIL_FROM; an error answer throws", async () => {
    const net = new FakeNet();
    net.on(RESEND_URL, () => jsonResponse(200, { id: "x" }));
    const m = new ResendMailer("re_test_key", "Pairnets <noreply@pairnets.app>", net.fetch);
    await m.send(codeEmail("you@example.com", "alice.pairnets.app", "123456"));
    expect(net.calls).toHaveLength(1);
    expect(net.calls[0].headers.get("authorization")).toBe("Bearer re_test_key");
    const sent = JSON.parse(net.calls[0].body);
    expect(sent.from).toBe("Pairnets <noreply@pairnets.app>");
    expect(sent.to).toEqual(["you@example.com"]);
    expect(sent.subject).toBe("Your code for alice.pairnets.app: 123456");
    net.on(RESEND_URL, () => jsonResponse(429, { message: "slow" }));
    await expect(m.send(codeEmail("you@example.com", "alice.pairnets.app", "123456"))).rejects.toThrow("Resend answered 429");
  });
});

describe("hourly sweep", () => {
  it("deletes expired codes, counters older than two days and events older than 90 days", async () => {
    const h = await Harness.create();
    await h.start("alice");
    await h.env.DB.prepare("INSERT INTO events (at, event) VALUES (?1, 'ancient')").bind(h.clock.now - 91 * 86400).run();
    h.advance(15 * 60);
    await h.scheduled();
    expect(await h.query("SELECT * FROM claims")).toHaveLength(0);
    expect(await h.query("SELECT * FROM events WHERE event = 'ancient'")).toHaveLength(0);
    expect((await h.query("SELECT * FROM events")).length).toBeGreaterThan(0);
    expect((await h.query("SELECT * FROM rate_counters")).length).toBeGreaterThan(0);
    h.advance(3 * 86400);
    await h.scheduled();
    expect(await h.query("SELECT * FROM rate_counters")).toHaveLength(0);
  });
});
