import { describe, expect, it } from "vitest";
import { Harness } from "./helpers";

async function setup(email = "you@example.com") {
  const h = await Harness.create();
  const b = h.browser();
  const accountId = await b.signInByEmail(email);
  return { h, b, accountId };
}

describe("username", () => {
  it("is not set at first, and the part of the email before the @ is shown", async () => {
    const { b } = await setup("first.last@example.com");
    expect((await b.call("GET", "/v1/me")).json.username).toBeNull();
    const page = await b.call("GET", "/account");
    expect(page.text).toContain('<input id="username" name="username" maxlength="32" autocomplete="nickname" value="" placeholder="first.last">');
    expect(page.text).toContain("Leave it empty to be shown as <strong>first.last</strong>.");
  });

  it("PATCH /v1/me sets, changes and clears it", async () => {
    const { h, b, accountId } = await setup();
    const set = await b.call("PATCH", "/v1/me", { body: { username: "  Marcus  " } });
    expect(set.status).toBe(200);
    expect(set.json.username).toBe("Marcus");
    expect(set.json.accountId).toBe(accountId);
    expect((await b.call("GET", "/v1/me")).json.username).toBe("Marcus");

    expect((await b.call("PATCH", "/v1/me", { body: { username: "Marcus L" } })).json.username).toBe("Marcus L");
    expect((await b.call("PATCH", "/v1/me", { body: { username: "" } })).json.username).toBeNull();
    await b.call("PATCH", "/v1/me", { body: { username: "Marcus" } });
    expect((await b.call("PATCH", "/v1/me", { body: { username: null } })).json.username).toBeNull();

    const events = await h.query<{ event: string; detail: string | null }>("SELECT event, detail FROM audit WHERE account_id = ?1 AND event = 'username_changed'", accountId);
    expect(events.length).toBe(5);
    expect(events.every((e) => e.detail === null)).toBe(true); // the name itself is not logged
  });

  it("refuses names that are too long, the wrong type or missing", async () => {
    const { b } = await setup();
    await b.call("PATCH", "/v1/me", { body: { username: "Kept" } });
    for (const body of [{ username: "x".repeat(33) }, { username: 5 }, { username: ["a"] }, {}]) {
      const r = await b.call("PATCH", "/v1/me", { body });
      expect(r.status, JSON.stringify(body)).toBe(400);
      expect(r.json.error).toBe("bad_request");
    }
    expect((await b.call("PATCH", "/v1/me", { body: { username: "x".repeat(32) } })).status).toBe(200);
    expect((await b.call("PATCH", "/v1/me", { body: { username: "🐦".repeat(32) } })).status).toBe(200); // characters, not bytes
    expect((await b.call("PATCH", "/v1/me", { body: { username: "a\u0000b\nc" } })).json.username).toBe("abc");
  });

  it("needs a browser session and the account page's Origin", async () => {
    const { h, b } = await setup();
    expect((await h.browser("203.0.113.99").call("PATCH", "/v1/me", { body: { username: "x" } })).status).toBe(401);
    for (const origin of ["https://pairnets.app", "https://www.pairnets.app"]) {
      const r = await b.call("PATCH", "/v1/me", { origin, body: { username: "Evil" } });
      expect(r.status, origin).toBe(403);
    }
    expect((await b.call("GET", "/v1/me")).json.username).toBeNull();
  });

  it("is escaped on the account page", async () => {
    const { b } = await setup();
    await b.call("PATCH", "/v1/me", { body: { username: '<b>"x"</b>' } });
    const page = await b.call("GET", "/account");
    expect(page.text).toContain('value="&lt;b&gt;&quot;x&quot;&lt;/b&gt;"');
    expect(page.text).not.toContain('<b>"x"</b>');
  });

  it("goes when the account is deleted", async () => {
    const { h, b, accountId } = await setup();
    await b.call("PATCH", "/v1/me", { body: { username: "Gone" } });
    h.advance(1);
    expect((await b.call("DELETE", "/v1/me")).status).toBe(204);
    expect(await h.query("SELECT * FROM accounts WHERE id = ?1", accountId)).toEqual([]);
  });
});

describe("GET /v1/signed-in (for pairnets.app)", () => {
  it("says signed out without a session", async () => {
    const h = await Harness.create();
    const r = await h.browser().call("GET", "/v1/signed-in", { origin: "https://pairnets.app" });
    expect(r.status).toBe(200);
    expect(r.json).toEqual({ signedIn: false });
  });

  it("gives the name: the part before the @, then the username", async () => {
    const { b } = await setup("marcus@example.com");
    expect((await b.call("GET", "/v1/signed-in", { origin: "https://pairnets.app" })).json).toEqual({ signedIn: true, name: "marcus" });
    await b.call("PATCH", "/v1/me", { body: { username: "Marcus" } });
    expect((await b.call("GET", "/v1/signed-in", { origin: "https://pairnets.app" })).json).toEqual({ signedIn: true, name: "Marcus" });
    await b.call("POST", "/v1/logout");
    expect((await b.call("GET", "/v1/signed-in", { origin: "https://pairnets.app" })).json).toEqual({ signedIn: false });
  });

  it("an app token does not count", async () => {
    const { b } = await setup();
    const r = await b.call("GET", "/v1/signed-in", { headers: { Cookie: "", Authorization: "Bearer pca_" + "A".repeat(43) } });
    expect(r.json).toEqual({ signedIn: false });
  });

  it("CORS only for pairnets.app and www.pairnets.app, always with Vary: Origin", async () => {
    const { b } = await setup();
    for (const origin of ["https://pairnets.app", "https://www.pairnets.app"]) {
      const r = await b.call("GET", "/v1/signed-in", { origin });
      expect(r.headers.get("access-control-allow-origin"), origin).toBe(origin);
      expect(r.headers.get("access-control-allow-credentials"), origin).toBe("true");
      expect(r.headers.get("vary"), origin).toBe("Origin");
      expect(r.headers.get("cache-control"), origin).toBe("no-store");
    }
    for (const origin of [null, "https://evil.example", "http://pairnets.app", "https://pairnets.app.evil.example", "https://sync.pairnets.app", "null", "https://PAIRNETS.app"]) {
      const r = await b.call("GET", "/v1/signed-in", { origin });
      expect(r.status, String(origin)).toBe(200);
      for (const [k] of r.headers) expect(k.startsWith("access-control-"), `${origin}: ${k}`).toBe(false);
      expect(r.headers.get("vary"), String(origin)).toBe("Origin");
    }
  });

  it("no other route sends CORS to the website", async () => {
    const { b } = await setup();
    for (const path of ["/v1/me", "/v1/nests", "/v1/sessions", "/v1/servers", "/account", "/assets/style.css"]) {
      const r = await b.call("GET", path, { origin: "https://pairnets.app" });
      for (const [k] of r.headers) expect(k.startsWith("access-control-"), `${path}: ${k}`).toBe(false);
    }
  });
});
