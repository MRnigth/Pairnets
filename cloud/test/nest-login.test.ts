import { describe, expect, it } from "vitest";
import { b64urlDecodeStrict, utf8 } from "../src/b64";
import { FakeGoogle } from "./fake-google";
import { claim, decodeJwsPart, Harness, linkNest } from "./helpers";

const NONCE = "TestNonce000000000000A";

function loginPath(nestId: string, ret = "https://nest.example.com/hosted-return", nonce = NONCE): string {
  return `/nest-login?nest=${nestId}&nonce=${nonce}&return=${encodeURIComponent(ret)}`;
}

async function verifyEs256(publicKey: CryptoKey, jws: string): Promise<boolean> {
  const [h, p, s] = jws.split(".");
  return crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, publicKey, b64urlDecodeStrict(s)! as Uint8Array<ArrayBuffer>, utf8(`${h}.${p}`) as Uint8Array<ArrayBuffer>);
}

async function setup() {
  const h = await Harness.create();
  const b = h.browser();
  const accountId = await b.signInByEmail("you@example.com");
  const nest = await linkNest(h, b);
  return { h, b, accountId, nest };
}

describe("nest-login", () => {
  it("assertion only in the fragment, to the registered address", async () => {
    const { h, b, nest } = await setup();
    h.advance(100);
    const r = await b.call("GET", loginPath(nest.nestId));
    expect(r.status).toBe(302);
    const m = /^https:\/\/nest\.example\.com\/hosted-return#assertion=([A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+)$/.exec(r.location!);
    expect(m).not.toBeNull();
    expect(r.headers.get("cache-control")).toBe("no-store");
    expect(r.headers.get("referrer-policy")).toBe("no-referrer");
    expect(r.text).toBe("");
    // Nothing of the assertion is stored.
    const audit = await h.query<{ event: string; detail: string | null }>("SELECT event, detail FROM audit WHERE event = 'nest_login'");
    expect(audit).toHaveLength(1);
    expect(JSON.stringify(audit)).not.toContain(m![1].split(".")[2]);
  });

  it("aud is the nest id; header, claims and signature are exactly the contract's", async () => {
    const { h, b, accountId, nest } = await setup();
    h.advance(100);
    const r = await b.call("GET", loginPath(nest.nestId));
    const jws = r.location!.split("#assertion=")[1];
    expect(jws.length).toBeLessThanOrEqual(4096);
    const [hp, pp, sp] = jws.split(".");
    expect(new TextDecoder().decode(b64urlDecodeStrict(hp)!)).toBe('{"alg":"ES256","typ":"pn-login+jwt","kid":"test-1"}');
    const claims = decodeJwsPart(pp);
    expect(Object.keys(claims)).toEqual(["iss", "aud", "sub", "iat", "nbf", "exp", "nonce", "jti", "amr", "auth_time", "ver"]);
    expect(claims).toMatchObject({
      iss: "https://id.pairnets.app",
      aud: nest.nestId,
      sub: accountId,
      iat: h.clock.now,
      nbf: h.clock.now,
      exp: h.clock.now + 90,
      nonce: NONCE,
      amr: ["email"],
      auth_time: h.clock.now - 100,
      ver: 1,
    });
    expect(claims.jti).toMatch(/^[A-Za-z0-9_-]{22}$/);
    expect(JSON.stringify(claims)).not.toContain("you@example.com");
    expect(b64urlDecodeStrict(sp)!.length).toBe(64);
    expect(await verifyEs256(h.signing.publicKey, jws)).toBe(true);
    // A second login gets a new jti.
    const again = decodeJwsPart((await b.call("GET", loginPath(nest.nestId))).location!.split("#assertion=")[1].split(".")[1]);
    expect(again.jti).not.toBe(claims.jti);
  });

  it("amr follows how the session signed in", async () => {
    const h = await Harness.create();
    const g = await FakeGoogle.install(h);
    const b = h.browser();
    await g.signIn(b);
    const nest = await linkNest(h, b);
    const r = await b.call("GET", loginPath(nest.nestId));
    expect(decodeJwsPart(r.location!.split("#assertion=")[1].split(".")[1]).amr).toEqual(["google"]);
  });

  it("not signed in: sign in first, then come back to exactly this request", async () => {
    const { h, nest } = await setup();
    const stranger = h.browser("203.0.113.55");
    const path = loginPath(nest.nestId);
    const r = await stranger.call("GET", path);
    expect(r.status).toBe(302);
    expect(r.location).toBe(`/login?next=${encodeURIComponent(path)}`);
    // The login page keeps that next for both ways of signing in.
    const page = await stranger.call("GET", r.location!);
    expect(page.text).toContain(`href="/login/google?next=${encodeURIComponent(path).replace(/&/g, "&amp;")}"`);
    expect(page.text).toContain(`data-next="${path.replace(/&/g, "&amp;")}"`);
    // After an email sign-in the answer points back to the nest login.
    await stranger.call("POST", "/v1/login/email", { body: { email: "you@example.com", turnstile: "ok", next: path } });
    const codeMail = h.mailer.to("you@example.com").at(-1)!;
    const code = /#code=([A-Za-z0-9_-]+)/.exec(codeMail.text)![1];
    const done = await stranger.call("POST", "/v1/login/email/confirm", { body: { code } });
    expect(done.json.next).toBe(path);
    expect((await stranger.call("GET", done.json.next)).location).toMatch(/^https:\/\/nest\.example\.com\/hosted-return#assertion=/);
  });

  it("another account gets the not-linked page", async () => {
    const { h, nest } = await setup();
    const other = h.browser("203.0.113.56");
    await other.signInByEmail("other@example.com");
    const r = await other.call("GET", loginPath(nest.nestId));
    expect(r.status).toBe(404);
    expect(r.location).toBeNull();
    expect(r.text).toContain("This nest is not linked to your Pairnets account.");
    const unknown = await other.call("GET", loginPath("nst_testnest000000000000000001"));
    expect(unknown.status).toBe(404);
    // Same page for both: nothing tells the other account that the nest exists.
    expect(unknown.text).toBe(r.text);
  });

  it("refuses any other return address", async () => {
    const { b, nest } = await setup();
    for (const ret of [
      "https://evil.example/hosted-return",
      "https://nest.example.com/hosted-return/",
      "https://nest.example.com/other",
      "https://nest.example.com:443/hosted-return",
      "http://nest.example.com/hosted-return",
      "https://nest.example.com/hosted-return?x=1",
      "https://NEST.example.com/hosted-return",
      "https://nest.example.com.evil.example/hosted-return",
    ]) {
      const r = await b.call("GET", loginPath(nest.nestId, ret));
      expect(r.status, ret).toBe(400);
      expect(r.location, ret).toBeNull();
      expect(r.text).toContain("This nest&#39;s address changed. Link it again.");
    }
  });

  it("checks the parameters first", async () => {
    const { b, nest } = await setup();
    const bad = [
      `/nest-login?nest=nst_x&nonce=${NONCE}&return=x`,
      `/nest-login?nest=${nest.nestId}&nonce=short&return=x`,
      `/nest-login?nest=${nest.nestId}&nonce=${NONCE}`,
      `/nest-login?nest=${nest.nestId}&nonce=${NONCE}&return=${"x".repeat(301)}`,
      `/nest-login?nest=${nest.nestId}&nonce=${"N".repeat(65)}&return=x`,
    ];
    for (const p of bad) {
      const r = await b.call("GET", p);
      expect(r.status, p).toBe(400);
      expect(r.location).toBeNull();
    }
  });

  it("sign-in switched off for the nest, or the emergency switch: 403 page", async () => {
    const { h, b, nest } = await setup();
    expect((await b.call("PATCH", `/v1/nests/${nest.nestId}`, { body: { hostedLogin: false } })).json.hostedLogin).toBe(false);
    expect((await b.call("GET", loginPath(nest.nestId))).status).toBe(403);
    await b.call("PATCH", `/v1/nests/${nest.nestId}`, { body: { hostedLogin: true } });
    expect((await b.call("GET", loginPath(nest.nestId))).status).toBe(302);
    h.env = { ...h.env, HOSTED_LOGIN_DISABLED: "1" };
    const r = await b.call("GET", loginPath(nest.nestId));
    expect(r.status).toBe(403);
    expect(r.location).toBeNull();
  });

  it("a pending nest gets the 409 page", async () => {
    const h = await Harness.create();
    const b = h.browser();
    await b.signInByEmail("you@example.com");
    const { nest } = await claim(h, b);
    expect((await b.call("GET", loginPath(nest!.nestId))).status).toBe(409);
  });

  it("60 an hour per account", async () => {
    const { h, b, nest } = await setup();
    for (let i = 0; i < 60; i++) expect((await b.call("GET", loginPath(nest.nestId))).status).toBe(302);
    const over = await b.call("GET", loginPath(nest.nestId));
    expect(over.status).toBe(429);
    expect(over.location).toBeNull();
    expect(Number(over.headers.get("retry-after"))).toBeGreaterThan(0);
    h.advance(3600);
    expect((await b.call("GET", loginPath(nest.nestId))).status).toBe(302);
  });

  it("refuses to sign with a broken key (500 page, no redirect)", async () => {
    const { h, b, nest } = await setup();
    h.env = { ...h.env, SIGNING_KEY: JSON.stringify({ kty: "EC", crv: "P-256", x: "AA", y: "AA", d: "AA", kid: "bad kid!" }) };
    const r = await b.call("GET", loginPath(nest.nestId));
    expect(r.status).toBe(500);
    expect(r.location).toBeNull();
  });
});
