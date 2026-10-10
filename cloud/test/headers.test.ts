import { describe, expect, it } from "vitest";
import { assetUrl, fingerprint } from "../src/assets";
import { CSP } from "../src/http";
import { loginEmail, noticeEmail } from "../src/mail";
import { Harness, type Res } from "./helpers";

const EXPECTED: Record<string, string> = {
  "content-security-policy":
    "default-src 'none'; script-src 'self' https://challenges.cloudflare.com; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-src https://challenges.cloudflare.com; form-action 'self'; base-uri 'none'; frame-ancestors 'none'",
  "strict-transport-security": "max-age=31536000",
  "x-content-type-options": "nosniff",
  "x-frame-options": "DENY",
  "referrer-policy": "no-referrer",
  "cross-origin-opener-policy": "same-origin",
  "permissions-policy": "camera=(), microphone=(), geolocation=()",
};

function expectSecurityHeaders(r: Res, cache: "no-store" | "asset" = "no-store") {
  for (const [k, v] of Object.entries(EXPECTED)) expect(r.headers.get(k), k).toBe(v);
  if (cache === "no-store") expect(r.headers.get("cache-control")).toBe("no-store");
  else expect(r.headers.get("cache-control")).not.toBe("no-store");
  for (const [k] of r.headers) expect(k.startsWith("access-control-"), k).toBe(false);
}

describe("headers on every response", () => {
  it("pages, JSON, errors, redirects and assets all carry them", async () => {
    const h = await Harness.create();
    const b = h.browser();
    expect(CSP).toBe(EXPECTED["content-security-policy"]);
    const login = await b.call("GET", "/login");
    expect(login.status).toBe(200);
    expect(login.headers.get("content-type")).toContain("text/html");
    expectSecurityHeaders(login);
    expectSecurityHeaders(await b.call("GET", "/"));
    expectSecurityHeaders(await b.call("GET", "/v1/me"));
    expectSecurityHeaders(await b.call("GET", "/nope"));
    expectSecurityHeaders(await b.call("POST", "/v1/logout", { origin: null }));
    expectSecurityHeaders(await b.call("GET", "/assets/style.css"), "asset");
    expectSecurityHeaders(await b.call("GET", "/assets/nope.js"));
    expectSecurityHeaders(await b.call("GET", "/v1/me", { headers: { Origin: "https://evil.example" } }));
  });

  it("pages have no inline scripts or styles", async () => {
    const h = await Harness.create();
    const page = await h.browser().call("GET", "/login");
    expect(page.text).not.toMatch(/<script(?![^>]*\bsrc=)[^>]*>/);
    expect(page.text).not.toMatch(/<style/);
    expect(page.text).not.toMatch(/\sstyle=/);
    expect(page.text).toContain(`<script src="${assetUrl("login.js")}" defer></script>`);
  });

  it("serves the page scripts and stylesheet", async () => {
    const h = await Harness.create();
    for (const f of ["style.css", "login.js", "email.js", "account.js", "app.js", "add.js"]) {
      const r = await h.browser().call("GET", `/assets/${f}`);
      expect(r.status, f).toBe(200);
      expect(r.text.length).toBeGreaterThan(100);
    }
  });

  it("pages link their stylesheet and scripts with a version, so a deploy shows at once", async () => {
    const h = await Harness.create();
    const visitor = h.browser();
    const b = h.browser();
    await b.signInByEmail("you@example.com");
    const pages: [typeof b, string][] = [
      [visitor, "/login"],
      [visitor, "/login/email"],
      [b, "/account"],
      [b, "/app"],
      [b, "/add"],
    ];
    for (const [who, path] of pages) {
      const page = await who.call("GET", path);
      expect(page.status, path).toBe(200);
      const links = [...page.text.matchAll(/(?:href|src)="(\/assets\/[^"]*)"/g)].map((m) => m[1]);
      expect(links.length, path).toBeGreaterThanOrEqual(2);
      for (const link of links) {
        expect(link, path).toMatch(/^\/assets\/[a-z-]+\.(css|js)\?v=[0-9a-z]+$/);
        const asset = await b.call("GET", link);
        expect(asset.status, link).toBe(200);
        expect(fingerprint(asset.text), link).toBe(link.split("?v=")[1]);
      }
    }
    expect(fingerprint("a")).not.toBe(fingerprint("b"));
  });

  it("every page has a Home button to the website", async () => {
    const h = await Harness.create();
    const b = h.browser();
    const check = async (path: string) => {
      const page = await b.call("GET", path);
      expect(page.headers.get("content-type"), path).toContain("text/html");
      expect(page.text, path).toContain('<a class="button home" href="https://pairnets.app/">← Home</a>');
      expect(page.text, path).toContain('<a class="brand" href="https://pairnets.app/">Pairnets</a>');
    };
    for (const path of ["/login", "/login/email"]) await check(path);
    await b.signInByEmail("you@example.com");
    for (const path of ["/account", "/app", "/add", "/app?code=XXXX-XXXX", "/add?code=XXXX-XXXX"]) await check(path);
  });

  it("/ goes to the account page and /privacy to the website", async () => {
    const h = await Harness.create();
    expect((await h.browser().call("GET", "/")).location).toBe("/account");
    expect((await h.browser().call("GET", "/privacy")).location).toBe("https://pairnets.app/privacy/");
    expect((await h.browser().call("GET", "/account")).location).toBe("/login?next=/account");
  });

  it("the website's legal and help pages answer here too, with or without a trailing slash", async () => {
    const h = await Harness.create();
    for (const page of ["privacy", "terms", "guidelines", "cookies", "security", "faq", "help", "contact", "delete-account", "licenses"]) {
      for (const path of [`/${page}`, `/${page}/`]) {
        const r = await h.browser().call("GET", path);
        expect(r.status, path).toBe(302);
        expect(r.location, path).toBe(`https://pairnets.app/${page}/`);
      }
    }
  });

  it("every page links the legal pages, and the sign-in page says what continuing means", async () => {
    const h = await Harness.create();
    const login = await h.browser().call("GET", "/login");
    for (const link of ["/privacy", "/terms", "/guidelines", "/cookies", "/help"]) {
      expect(login.text).toContain(`<a href="${link}">`);
    }
    expect(login.text).toContain(`By continuing, you agree to the <a href="/terms">Terms</a> and the <a href="/privacy">Privacy Policy</a>.`);
    const missing = await h.browser().call("GET", "/nope");
    expect(missing.text).toContain(`<a href="/privacy">Privacy</a>`);
  });

  it("emails end with a link to the privacy policy", async () => {
    const mail = loginEmail("you@example.com", "https://sync.pairnets.app/login/email#code=abc");
    expect(mail.text).toContain("Privacy: https://pairnets.app/privacy/");
    expect(mail.html).toContain(`href="https://pairnets.app/privacy/"`);
    const notice = noticeEmail("you@example.com", "account_deleted");
    expect(notice.text).toContain("Privacy: https://pairnets.app/privacy/");
  });
});

describe("cookie attributes", () => {
  it("every cookie is Secure, HttpOnly, Path=/, SameSite=Lax and has no Domain", async () => {
    const h = await Harness.create();
    const b = h.browser();
    const start = await b.call("POST", "/v1/login/email", { body: { email: "you@example.com", turnstile: "ok" } });
    const google = await b.call("GET", "/login/google");
    const code = (await import("./helpers")).lastEmailCode(h, "you@example.com");
    const done = await b.call("POST", "/v1/login/email/confirm", { body: { code } });
    const all = [...start.setCookies, ...google.setCookies, ...done.setCookies];
    const names = new Set(all.map((c) => c.split("=")[0]));
    expect(names).toEqual(new Set(["__Host-pn_el", "__Host-pn_g", "__Host-pn_id"]));
    for (const c of all) {
      expect(c).toMatch(/; Secure/);
      expect(c).toMatch(/; HttpOnly/);
      expect(c).toMatch(/; Path=\//);
      expect(c).toMatch(/; SameSite=Lax/);
      expect(c.toLowerCase()).not.toContain("domain=");
    }
    const session = done.setCookies.find((c) => c.startsWith("__Host-pn_id="))!;
    expect(session).toMatch(/^__Host-pn_id=pcs_[A-Za-z0-9_-]{43}; Max-Age=2592000;/);
    expect(start.setCookies.find((c) => c.startsWith("__Host-pn_el="))).toMatch(/Max-Age=900;/);
    expect(google.setCookies.find((c) => c.startsWith("__Host-pn_g="))).toMatch(/Max-Age=600;/);
  });
});

describe("JSON API basics", () => {
  it("uses the uniform error body", async () => {
    const h = await Harness.create();
    const r = await h.browser().call("GET", "/v1/nope");
    expect(r.status).toBe(404);
    expect(r.json).toEqual({ error: "not_found", message: expect.any(String) });
    expect(r.headers.get("content-type")).toBe("application/json");
  });

  it("refuses bodies that are not JSON (415) or too large (413)", async () => {
    const h = await Harness.create();
    const b = h.browser();
    const wrongType = await b.call("POST", "/v1/login/email", { rawBody: "email=x", contentType: "application/x-www-form-urlencoded" });
    expect(wrongType.status).toBe(415);
    expect(wrongType.json.error).toBe("bad_request");
    const big = await b.call("POST", "/v1/login/email", { rawBody: JSON.stringify({ email: "x".repeat(17000) }) });
    expect(big.status).toBe(413);
    expect(big.json.error).toBe("bad_request");
    const notJson = await b.call("POST", "/v1/login/email", { rawBody: "{nope" });
    expect(notJson.status).toBe(400);
    expect(notJson.json.error).toBe("bad_request");
  });

  it("limits any request to 300 a minute per IP", async () => {
    const h = await Harness.create();
    const b = h.browser("203.0.113.77");
    for (let i = 0; i < 300; i++) expect((await b.call("GET", "/assets/style.css")).status).toBe(200);
    const over = await b.call("GET", "/v1/me");
    expect(over.status).toBe(429);
    expect(over.json.error).toBe("rate_limited");
    expect(Number(over.headers.get("retry-after"))).toBeGreaterThan(0);
    expect((await h.browser("203.0.113.78").call("GET", "/assets/style.css")).status).toBe(200);
    h.advance(60);
    expect((await b.call("GET", "/assets/style.css")).status).toBe(200);
  }, 60_000);

  it("answers 500 server_error without details when the configuration is broken", async () => {
    const h = await Harness.create({ COOKIE_KEY: "not-a-key" });
    const r = await h.browser().call("GET", "/v1/me");
    expect(r.status).toBe(500);
    expect(r.json).toEqual({ error: "server_error", message: "Something went wrong on our side." });
  });
});
