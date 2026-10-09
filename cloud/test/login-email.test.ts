import { describe, expect, it } from "vitest";
import { Harness, lastEmailCode, type Res } from "./helpers";

function askLink(b: ReturnType<Harness["browser"]>, email: string, next?: string): Promise<Res> {
  return b.call("POST", "/v1/login/email", { body: { email, turnstile: "token-ok", ...(next ? { next } : {}) } });
}

describe("login-email", () => {
  it("signs in with the emailed link in the same browser", async () => {
    const h = await Harness.create();
    const b = h.browser();
    const r = await askLink(b, "You@Example.com", "/app?code=ABCD-EFGH");
    expect(r.status).toBe(202);
    expect(r.json).toEqual({ status: "sent" });
    expect(r.setCookies.some((c) => /^__Host-pn_el=[A-Za-z0-9_-]{43};/.test(c))).toBe(true);

    const mails = h.mailer.to("you@example.com");
    expect(mails).toHaveLength(1);
    expect(mails[0].subject).toBe("Your Pairnets sign-in link");
    expect(mails[0].text).toMatch(/https:\/\/sync\.pairnets\.app\/login\/email#code=[A-Za-z0-9_-]{32}\n/);
    expect(mails[0].text).not.toContain("next");

    const page = await b.call("GET", "/login/email");
    expect(page.status).toBe(200);
    expect(page.text).toContain("/assets/email.js");

    const code = lastEmailCode(h, "you@example.com");
    const done = await b.call("POST", "/v1/login/email/confirm", { body: { code } });
    expect(done.status).toBe(200);
    expect(done.json).toEqual({ next: "/app?code=ABCD-EFGH" });
    expect(done.setCookies.some((c) => c.startsWith("__Host-pn_el=; Max-Age=0"))).toBe(true);

    const me = await b.call("GET", "/v1/me");
    expect(me.status).toBe(200);
    expect(me.json.email).toBe("you@example.com");
    expect(me.json.identities).toEqual([{ provider: "email", email: "you@example.com", createdAt: "2026-10-09T00:00:00Z", lastUsedAt: "2026-10-09T00:00:00Z" }]);
    expect(me.json.session).toMatchObject({ kind: "browser", amr: ["email"], authTime: "2026-10-09T00:00:00Z", recentAuth: true });
    const audit = await h.query<{ event: string }>("SELECT event FROM audit ORDER BY id");
    expect(audit.map((a) => a.event)).toEqual(["account_created", "login"]);
  });

  it("a code works once and expires after 15 minutes", async () => {
    const h = await Harness.create();
    const b = h.browser();
    await askLink(b, "you@example.com");
    const code = lastEmailCode(h, "you@example.com");
    expect((await b.call("POST", "/v1/login/email/confirm", { body: { code } })).status).toBe(200);
    const again = await b.call("POST", "/v1/login/email/confirm", { body: { code } });
    expect(again.status).toBe(400);
    expect(again.json.error).toBe("invalid_code");

    const b2 = h.browser("203.0.113.11");
    await askLink(b2, "other@example.com");
    const code2 = lastEmailCode(h, "other@example.com");
    h.advance(901);
    const late = await b2.call("POST", "/v1/login/email/confirm", { body: { code: code2 } });
    expect(late.status).toBe(400);
    expect(late.json.error).toBe("invalid_code");
  });

  it("wrong browser is refused and the code survives", async () => {
    const h = await Harness.create();
    const asked = h.browser();
    await askLink(asked, "you@example.com");
    const code = lastEmailCode(h, "you@example.com");

    const scanner = h.browser("198.51.100.99");
    const r1 = await scanner.call("POST", "/v1/login/email/confirm", { body: { code } });
    expect(r1.status).toBe(400);
    expect(r1.json).toEqual({ error: "wrong_browser", message: "Open the link in the browser where you asked for it." });
    scanner.cookies.set("__Host-pn_el", "A".repeat(43));
    expect((await scanner.call("POST", "/v1/login/email/confirm", { body: { code } })).json.error).toBe("wrong_browser");

    expect((await asked.call("POST", "/v1/login/email/confirm", { body: { code } })).status).toBe(200);
  });

  it("two links asked in a row both work in that browser", async () => {
    const h = await Harness.create();
    const b = h.browser();
    await askLink(b, "you@example.com");
    const first = lastEmailCode(h, "you@example.com");
    await askLink(b, "you@example.com");
    expect((await b.call("POST", "/v1/login/email/confirm", { body: { code: first } })).status).toBe(200);
  });

  it("same answer for limited and unknown addresses", async () => {
    const h = await Harness.create();
    const shape = (r: Res) => ({ status: r.status, body: r.text, cookies: r.setCookies.map((c) => c.replace(/=[^;]*;/, "=X;")) });
    const fresh = shape(await askLink(h.browser("203.0.113.1"), "new@example.com"));
    // 3 an hour per address: the 4th is silently dropped.
    for (let i = 0; i < 3; i++) await askLink(h.browser(`203.0.113.${10 + i}`), "busy@example.com");
    const limited = shape(await askLink(h.browser("203.0.113.20"), "busy@example.com"));
    expect(limited).toEqual(fresh);
    expect(h.mailer.to("busy@example.com")).toHaveLength(3);
    expect(h.mailer.to("new@example.com")).toHaveLength(1);

    // 10 requests a day per address (the dropped 4th above counts too): 6 more go out, the 11th is dropped.
    for (let hour = 1; hour <= 2; hour++) {
      h.advance(3600);
      for (let i = 0; i < 3; i++) expect((await askLink(h.browser(`203.0.113.${30 + hour * 3 + i}`), "busy@example.com")).status).toBe(202);
    }
    expect(h.mailer.to("busy@example.com")).toHaveLength(9);
    h.advance(3600);
    expect(shape(await askLink(h.browser("203.0.113.60"), "busy@example.com"))).toEqual(fresh);
    expect(h.mailer.to("busy@example.com")).toHaveLength(9);
    h.advance(86400);
    await askLink(h.browser("203.0.113.61"), "busy@example.com");
    expect(h.mailer.to("busy@example.com")).toHaveLength(10);
  });

  it("checks Turnstile, the address and the per-IP limit", async () => {
    const h = await Harness.create();
    const b = h.browser();
    h.turnstileAnswer = false;
    const t = await askLink(b, "you@example.com");
    expect(t.status).toBe(400);
    expect(t.json.error).toBe("turnstile_failed");
    const verify = h.net.callsTo("https://challenges.cloudflare.com/turnstile/v0/siteverify");
    expect(verify).toHaveLength(1);
    expect(new URLSearchParams(verify[0].body).get("secret")).toBe("test-turnstile-secret");
    expect(new URLSearchParams(verify[0].body).get("remoteip")).toBe("203.0.113.10");
    h.turnstileAnswer = true;
    expect((await b.call("POST", "/v1/login/email", { body: { email: "nope", turnstile: "x" } })).json.error).toBe("bad_request");
    expect((await b.call("POST", "/v1/login/email", { body: { email: "you@example.com" } })).json.error).toBe("bad_request");
    expect((await b.call("POST", "/v1/login/email", { body: { email: "you@example.com", turnstile: "" } })).json.error).toBe("turnstile_failed");
    // 10 an hour per IP, all of the above counted.
    for (let i = 0; i < 6; i++) await askLink(b, `a${i}@example.com`);
    const over = await askLink(b, "z@example.com");
    expect(over.status).toBe(429);
    expect(over.json.error).toBe("rate_limited");
    expect(Number(over.headers.get("retry-after"))).toBeGreaterThan(0);
  });

  it("breaker: login emails stop at EMAIL_DAILY_LIMIT per UTC day", async () => {
    const h = await Harness.create({ EMAIL_DAILY_LIMIT: "3" });
    for (let i = 0; i < 3; i++) expect((await askLink(h.browser(`203.0.113.${i + 1}`), `u${i}@example.com`)).status).toBe(202);
    const r = await askLink(h.browser("203.0.113.50"), "late@example.com");
    expect(r.status).toBe(503);
    expect(r.json.error).toBe("email_unavailable");
    expect(h.mailer.sent).toHaveLength(3);
    h.advance(86400);
    expect((await askLink(h.browser("203.0.113.51"), "late@example.com")).status).toBe(202);
    expect(h.mailer.sent).toHaveLength(4);
  });

  it("confirm is limited to 30 an hour per IP and checks the code's shape", async () => {
    const h = await Harness.create();
    const b = h.browser();
    expect((await b.call("POST", "/v1/login/email/confirm", { body: { code: 5 } })).json.error).toBe("bad_request");
    expect((await b.call("POST", "/v1/login/email/confirm", { body: { code: "short" } })).json.error).toBe("invalid_code");
    for (let i = 0; i < 28; i++) await b.call("POST", "/v1/login/email/confirm", { body: { code: "A".repeat(32) } });
    const over = await b.call("POST", "/v1/login/email/confirm", { body: { code: "A".repeat(32) } });
    expect(over.status).toBe(429);
  });

  it("an email identity joins an account that already has that verified address", async () => {
    const h = await Harness.create();
    const b = h.browser();
    const id1 = await b.signInByEmail("you@example.com");
    const id2 = await h.browser("203.0.113.12").signInByEmail("you@example.com");
    expect(id2).toBe(id1);
    expect((await h.query("SELECT * FROM accounts")).length).toBe(1);
  });
});
