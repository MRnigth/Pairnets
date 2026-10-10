import { describe, expect, it } from "vitest";
import { TABLES } from "./setup";
import { FakeGoogle } from "./fake-google";
import { Harness, linkNest } from "./helpers";

async function setup(email = "you@example.com") {
  const h = await Harness.create();
  const b = h.browser();
  const accountId = await b.signInByEmail(email);
  return { h, b, accountId };
}

describe("account", () => {
  it("GET /v1/me has the contract's shape", async () => {
    const { b, accountId } = await setup();
    const me = await b.call("GET", "/v1/me");
    expect(Object.keys(me.json)).toEqual(["accountId", "email", "username", "createdAt", "identities", "session"]);
    expect(me.json.accountId).toBe(accountId);
    expect(Object.keys(me.json.session)).toEqual(["id", "kind", "authTime", "amr", "recentAuth"]);
    expect(me.json.session.id).toMatch(/^ses_[0-9a-hjkmnp-tv-z]{26}$/);
    expect((await b.h.browser("203.0.113.3").call("GET", "/v1/me")).json.error).toBe("unauthorized");
  });

  it("reauth required for creating codes, removing a nest and deleting the account", async () => {
    const { h, b } = await setup();
    const nest = await linkNest(h, b);
    h.advance(901);
    expect((await b.call("GET", "/v1/me")).json.session.recentAuth).toBe(false);
    for (const [method, path] of [
      ["POST", "/v1/nests/claim-codes"],
      ["DELETE", `/v1/nests/${nest.nestId}`],
      ["DELETE", "/v1/me"],
    ]) {
      const r = await b.call(method, path, { body: method === "POST" ? {} : undefined });
      expect(r.status, path).toBe(401);
      expect(r.json.error, path).toBe("reauth_required");
    }
    // Not needing a recent sign-in: list, rename, switch, cancel codes.
    expect((await b.call("GET", "/v1/nests")).status).toBe(200);
    expect((await b.call("PATCH", `/v1/nests/${nest.nestId}`, { body: { label: "Renamed" } })).json.label).toBe("Renamed");
    expect((await b.call("DELETE", "/v1/nests/claim-codes")).status).toBe(204);
    // The account page sends people through /login?reauth=1.
    const page = await b.call("GET", "/login?reauth=1&next=/account");
    expect(page.status).toBe(200);
    expect(page.text).toContain("sign in again");
    expect(page.text).toContain('href="/login/google?next=%2Faccount&amp;reauth=1"');
  });

  it("removes a nest with a notice; another account's nest is 404", async () => {
    const { h, b, accountId } = await setup();
    const nest = await linkNest(h, b);
    const other = h.browser("203.0.113.70");
    await other.signInByEmail("other@example.com");
    expect((await other.call("DELETE", `/v1/nests/${nest.nestId}`)).status).toBe(404);
    expect((await other.call("PATCH", `/v1/nests/${nest.nestId}`, { body: { label: "x" } })).json.error).toBe("not_found");
    expect((await b.call("DELETE", "/v1/nests/nst_testnest000000000000000001")).status).toBe(404);
    expect((await b.call("DELETE", "/v1/nests/garbage")).status).toBe(404);

    expect((await b.call("DELETE", `/v1/nests/${nest.nestId}`)).status).toBe(204);
    expect((await b.call("GET", "/v1/nests")).json.nests).toEqual([]);
    expect(h.mailer.to("you@example.com").some((m) => m.subject === "A nest was removed from your account: https://nest.example.com")).toBe(true);
    expect(await h.query("SELECT event FROM audit WHERE event = 'nest_removed' AND account_id = ?1", accountId)).toHaveLength(1);
    // The nest's next signed request learns it is gone.
    expect((await nest.heartbeat()).status).toBe(410);
  });

  it("PATCH checks types and keeps what is not sent", async () => {
    const { h, b } = await setup();
    const nest = await linkNest(h, b);
    expect((await b.call("PATCH", `/v1/nests/${nest.nestId}`, { body: { hostedLogin: "no" } })).json.error).toBe("bad_request");
    expect((await b.call("PATCH", `/v1/nests/${nest.nestId}`, { body: { label: "x".repeat(65) } })).json.error).toBe("bad_request");
    const r = await b.call("PATCH", `/v1/nests/${nest.nestId}`, { body: { hostedLogin: false } });
    expect(r.status).toBe(200);
    expect(r.json).toMatchObject({ label: "nest.example.com", hostedLogin: false, status: "active" });
    expect(Object.keys(r.json)).toEqual([
      "nestId",
      "label",
      "publicUrl",
      "status",
      "online",
      "lastSeenAt",
      "serverVersion",
      "ready",
      "publicHost",
      "hostedLogin",
      "createdAt",
      "confirmedAt",
    ]);
  });

  it("lists and revokes sessions; sign-out clears the cookie", async () => {
    const { h, b } = await setup();
    const g = await FakeGoogle.install(h);
    g.claims = { email: "you@example.com" };
    const second = h.browser("203.0.113.71");
    second.userAgent = "SecondBrowser/2.0";
    await g.signIn(second);
    const list = await b.call("GET", "/v1/sessions");
    expect(list.json.sessions).toHaveLength(2);
    const mine = list.json.sessions.find((s: { current: boolean }) => s.current);
    const theirs = list.json.sessions.find((s: { current: boolean }) => !s.current);
    expect(Object.keys(mine)).toEqual(["id", "kind", "createdAt", "lastSeenAt", "userAgent", "deviceName", "current"]);
    expect(theirs.userAgent).toBe("SecondBrowser/2.0");

    expect((await b.call("DELETE", `/v1/sessions/${theirs.id}`)).status).toBe(204);
    expect((await second.call("GET", "/v1/me")).status).toBe(401);
    expect((await b.call("DELETE", `/v1/sessions/${theirs.id}`)).status).toBe(404);
    expect((await b.call("DELETE", "/v1/sessions/garbage")).status).toBe(404);

    const out = await b.call("POST", "/v1/logout");
    expect(out.status).toBe(204);
    expect(out.setCookies).toContain("__Host-pn_id=; Max-Age=0; Path=/; Secure; HttpOnly; SameSite=Lax");
    expect(await h.query("SELECT * FROM sessions")).toHaveLength(0);
  });

  it("revoking another account's session is 404", async () => {
    const { h, b } = await setup();
    const other = h.browser("203.0.113.72");
    await other.signInByEmail("other@example.com");
    const otherId = (await other.call("GET", "/v1/me")).json.session.id;
    expect((await b.call("DELETE", `/v1/sessions/${otherId}`)).status).toBe(404);
    expect((await other.call("GET", "/v1/me")).status).toBe(200);
  });

  it("sessions: refreshed at most hourly, 30 days idle, never past 90 days", async () => {
    const { h, b } = await setup();
    h.advance(1800);
    expect((await b.call("GET", "/v1/me")).setCookies).toEqual([]);
    h.advance(1800);
    const refreshed = await b.call("GET", "/v1/me");
    expect(refreshed.setCookies).toHaveLength(1);
    expect(refreshed.setCookies[0]).toMatch(/^__Host-pn_id=pcs_[A-Za-z0-9_-]{43}; Max-Age=2592000;/);
    // Idle for 30 days: gone.
    h.advance(30 * 86400);
    expect((await b.call("GET", "/v1/me")).status).toBe(401);

    const { h: h2, b: b2 } = await setup();
    for (let day = 0; day < 89; day++) {
      h2.advance(86400);
      expect((await b2.call("GET", "/v1/me")).status, `day ${day + 1}`).toBe(200);
    }
    // The cookie never outlives 90 days from the start.
    h2.advance(86400 - 60);
    const last = await b2.call("GET", "/v1/me");
    expect(last.status).toBe(200);
    expect(last.setCookies[0]).toMatch(/Max-Age=60;/);
    h2.advance(60);
    expect((await b2.call("GET", "/v1/me")).status).toBe(401);
  }, 60_000);

  it("the account page lists nests and sessions, escaped", async () => {
    const { h, b } = await setup();
    const nest = await linkNest(h, b);
    await b.call("PATCH", `/v1/nests/${nest.nestId}`, { body: { label: "<b>Home</b>" } });
    const page = await b.call("GET", "/account");
    expect(page.status).toBe(200);
    expect(page.text).toContain("you@example.com");
    expect(page.text).toContain("&lt;b&gt;Home&lt;/b&gt;");
    expect(page.text).not.toContain("<b>Home</b>");
    expect(page.text).toContain(`data-nest="${nest.nestId}"`);
    expect(page.text).toContain(">unknown<");
    expect(page.text).toContain("/assets/account.js");
  });

  it("DELETE /v1/me cascades: nothing about the account is left", async () => {
    const { h, b, accountId } = await setup();
    const g = await FakeGoogle.install(h);
    g.claims = { email: "you@example.com" };
    await g.signIn(h.browser("203.0.113.73"));
    const nest = await linkNest(h, b);
    await nest.heartbeat();
    await b.call("POST", "/v1/nests/claim-codes", { body: {} });
    const app = h.client();
    const start = await app.call("POST", "/v1/app/start", { body: { name: "Laptop" } });
    await b.call("POST", "/v1/app/approve", { body: { userCode: start.json.userCode, approve: true } });
    await app.call("POST", "/v1/app/poll", { body: { deviceCode: start.json.deviceCode } });
    // A second account that must survive.
    const other = h.browser("203.0.113.74");
    const otherId = await other.signInByEmail("other@example.com");
    await linkNest(h, other, "https://other.example.com");

    const del = await b.call("DELETE", "/v1/me");
    expect(del.status).toBe(204);
    expect(del.setCookies).toContain("__Host-pn_id=; Max-Age=0; Path=/; Secure; HttpOnly; SameSite=Lax");
    expect(h.mailer.to("you@example.com").some((m) => m.subject === "Your Pairnets account was deleted")).toBe(true);

    for (const table of TABLES.filter((t) => t !== "rate_counters")) {
      const rows = await h.query<Record<string, unknown>>(`SELECT * FROM ${table}`);
      const dump = JSON.stringify(rows);
      expect(dump, table).not.toContain(accountId);
      expect(dump, table).not.toContain("you@example.com");
      expect(dump, table).not.toContain(nest.nestId);
    }
    expect((await h.query("SELECT id FROM accounts")).map((r) => (r as { id: string }).id)).toEqual([otherId]);
    expect((await other.call("GET", "/v1/nests")).json.nests).toHaveLength(1);
    expect((await b.call("GET", "/v1/me")).status).toBe(401);
    expect((await nest.heartbeat()).status).toBe(410);
  });
});
