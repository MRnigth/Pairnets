import { describe, expect, it } from "vitest";
import { b64urlEncode } from "../src/b64";
import { sha256 } from "../src/crypto";
import { FakeGoogle } from "./fake-google";
import { GOOGLE_CLIENT_ID, GOOGLE_JWKS, Harness } from "./helpers";

describe("login-google", () => {
  it("signs in: PKCE, state, nonce, then a session and the next page", async () => {
    const h = await Harness.create();
    const g = await FakeGoogle.install(h);
    const b = h.browser();
    const { start, callback, auth } = await g.signIn(b, "?next=%2Fapp%3Fcode%3DABCD-EFGH");

    expect(start.status).toBe(302);
    const p = auth.searchParams;
    expect(p.get("client_id")).toBe(GOOGLE_CLIENT_ID);
    expect(p.get("redirect_uri")).toBe("https://id.pairnets.app/login/google/callback");
    expect(p.get("response_type")).toBe("code");
    expect(p.get("scope")).toBe("openid email");
    expect(p.get("state")).toMatch(/^[A-Za-z0-9_-]{22}$/);
    expect(p.get("nonce")).toMatch(/^[A-Za-z0-9_-]{22}$/);
    expect(p.get("code_challenge_method")).toBe("S256");
    expect(p.get("prompt")).toBe("select_account");
    expect(p.has("max_age")).toBe(false);

    const t = g.lastTokenRequest!;
    expect(t.get("grant_type")).toBe("authorization_code");
    expect(t.get("code")).toBe("test-auth-code");
    expect(t.get("client_id")).toBe(GOOGLE_CLIENT_ID);
    expect(t.get("client_secret")).toBe("test-google-secret");
    expect(t.get("redirect_uri")).toBe("https://id.pairnets.app/login/google/callback");
    expect(b64urlEncode(await sha256(t.get("code_verifier")!))).toBe(p.get("code_challenge"));

    expect(callback.status).toBe(302);
    expect(callback.location).toBe("/app?code=ABCD-EFGH");
    expect(callback.setCookies.some((c) => c.startsWith("__Host-pn_g=; Max-Age=0"))).toBe(true);
    const me = await b.call("GET", "/v1/me");
    expect(me.json.email).toBe("you@gmail.com");
    expect(me.json.session.amr).toEqual(["google"]);
    expect(me.json.identities.map((i: { provider: string }) => i.provider)).toEqual(["google"]);
  });

  it("each claim wrong -> refused", async () => {
    const cases: [string, (g: FakeGoogle) => void][] = [
      ["signature by another key", (g) => (g.forgeSignature = true)],
      ["unknown kid", (g) => (g.header = { kid: "other-key" })],
      ["alg HS256", (g) => (g.header = { alg: "HS256" })],
      ["alg none", (g) => (g.header = { alg: "none" })],
      ["wrong issuer", (g) => (g.claims = { iss: "https://evil.example" })],
      ["wrong audience", (g) => (g.claims = { aud: "other-client" })],
      ["audience as array", (g) => (g.claims = { aud: [GOOGLE_CLIENT_ID] })],
      ["expired", (g) => (g.claims = { exp: 1791504000 - 61 })],
      ["wrong nonce", (g) => (g.claims = { nonce: "someone-elses-nonce" })],
      ["email not verified", (g) => (g.claims = { email_verified: false })],
      ["email_verified as string", (g) => (g.claims = { email_verified: "true" })],
      ["no sub", (g) => (g.claims = { sub: undefined })],
      ["no email", (g) => (g.claims = { email: undefined })],
      ["token endpoint error", (g) => (g.tokenStatus = 400)],
    ];
    for (const [name, change] of cases) {
      const h = await Harness.create();
      const g = await FakeGoogle.install(h);
      change(g);
      const b = h.browser();
      const { callback } = await g.signIn(b);
      expect(callback.location, name).toBe("/login?error=google_failed");
      expect(b.cookies.has("__Host-pn_id"), name).toBe(false);
      expect((await h.query("SELECT * FROM accounts")).length, name).toBe(0);
    }
  });

  it("accepts the short issuer form and an exp just inside the 60 s skew", async () => {
    const h = await Harness.create();
    const g = await FakeGoogle.install(h);
    g.claims = { iss: "accounts.google.com", exp: 1791504000 - 59 };
    expect((await g.signIn(h.browser())).callback.location).toBe("/account");
  });

  it("refuses a wrong state, a missing or expired flow cookie, and reports a denial", async () => {
    const h = await Harness.create();
    const g = await FakeGoogle.install(h);
    const b = h.browser();
    const start = await b.call("GET", "/login/google");
    const state = new URL(start.location!).searchParams.get("state");
    expect((await b.call("GET", "/login/google/callback?state=wrong&code=x")).location).toBe("/login?error=google_failed");
    // The cookie was cleared by that failure: the flow is over.
    expect((await b.call("GET", `/login/google/callback?state=${state}&code=x`)).location).toBe("/login?error=google_expired");

    const b2 = h.browser();
    const s2 = new URL((await b2.call("GET", "/login/google")).location!).searchParams.get("state");
    expect((await b2.call("GET", `/login/google/callback?state=${s2}&error=access_denied`)).location).toBe("/login?error=google_denied");

    const b3 = h.browser();
    const s3 = new URL((await b3.call("GET", "/login/google")).location!).searchParams.get("state");
    h.advance(601);
    expect((await b3.call("GET", `/login/google/callback?state=${s3}&code=x`)).location).toBe("/login?error=google_expired");

    // A flow cookie from another browser (or a forged one) does not help.
    const b4 = h.browser();
    b4.cookies.set("__Host-pn_g", "eyJzIjoieCJ9.AAAA");
    expect((await b4.call("GET", "/login/google/callback?state=x&code=x")).location).toBe("/login?error=google_expired");
    expect(g.lastTokenRequest).toBeNull();
  });

  it("re-auth asks Google for a fresh sign-in and checks auth_time", async () => {
    const h = await Harness.create();
    const g = await FakeGoogle.install(h);
    const b = h.browser();
    g.claims = { auth_time: 1791504000 - 1 };
    const first = await g.signIn(b, "?reauth=1");
    expect(first.auth.searchParams.get("max_age")).toBe("0");
    expect(first.callback.location).toBe("/login?error=google_failed");
    g.claims = { auth_time: undefined };
    expect((await g.signIn(b, "?reauth=1")).callback.location).toBe("/login?error=google_failed");
    g.claims = {};
    expect((await g.signIn(b, "?reauth=1")).callback.location).toBe("/account");
  });

  it("a re-auth replaces the old session", async () => {
    const h = await Harness.create();
    const g = await FakeGoogle.install(h);
    const b = h.browser();
    await g.signIn(b);
    const old = b.cookies.get("__Host-pn_id")!;
    h.advance(1000);
    expect((await b.call("GET", "/v1/me")).json.session.recentAuth).toBe(false);
    await g.signIn(b, "?reauth=1");
    expect(b.cookies.get("__Host-pn_id")).not.toBe(old);
    expect((await b.call("GET", "/v1/me")).json.session.recentAuth).toBe(true);
    expect(await h.query("SELECT id FROM sessions")).toHaveLength(1);
  });

  it("Google is added to an account that already has the verified email, with a notice", async () => {
    const h = await Harness.create();
    const g = await FakeGoogle.install(h);
    const emailUser = h.browser();
    const accountId = await emailUser.signInByEmail("you@gmail.com");
    const b = h.browser("203.0.113.40");
    await g.signIn(b);
    const me = await b.call("GET", "/v1/me");
    expect(me.json.accountId).toBe(accountId);
    expect(me.json.identities.map((i: { provider: string }) => i.provider).sort()).toEqual(["email", "google"]);
    const notice = h.mailer.to("you@gmail.com").find((m) => m.subject === "Google sign-in was added to your Pairnets account");
    expect(notice).toBeDefined();
    const audit = await h.query<{ event: string }>("SELECT event FROM audit WHERE event = 'identity_linked'");
    expect(audit).toHaveLength(1);
    // Next time the identity itself is found: no second notice.
    await g.signIn(h.browser("203.0.113.41"));
    expect(h.mailer.to("you@gmail.com").filter((m) => m.subject.startsWith("Google sign-in was added"))).toHaveLength(1);
  });

  it("caches Google's keys for at most an hour", async () => {
    const h = await Harness.create();
    const g = await FakeGoogle.install(h);
    await g.signIn(h.browser());
    await g.signIn(h.browser());
    expect(g.jwksFetches).toBe(1);
    h.advance(3600);
    await g.signIn(h.browser());
    expect(g.jwksFetches).toBe(2);
    expect(h.net.callsTo(GOOGLE_JWKS).every((c) => c.method === "GET")).toBe(true);
  });

  it("limits starts to 30 per 10 minutes per IP (a page)", async () => {
    const h = await Harness.create();
    const b = h.browser("203.0.113.90");
    for (let i = 0; i < 30; i++) expect((await b.call("GET", "/login/google")).status).toBe(302);
    const over = await b.call("GET", "/login/google");
    expect(over.status).toBe(429);
    expect(over.headers.get("content-type")).toContain("text/html");
    expect(Number(over.headers.get("retry-after"))).toBeGreaterThan(0);
  });
});
