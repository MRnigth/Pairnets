import { describe, expect, it } from "vitest";
import { Harness } from "./helpers";

const TAG = '<script src="/assets/cookie-notice.js" defer></script>';

describe("cookie notice", () => {
  it("every HTML page loads it once", async () => {
    const h = await Harness.create();
    const visitor = h.browser();
    const b = h.browser();
    await b.signInByEmail("you@example.com");
    const pages = [
      ["/login", await visitor.call("GET", "/login")],
      ["/login?reauth=1", await visitor.call("GET", "/login?reauth=1")],
      ["/login/email", await visitor.call("GET", "/login/email")],
      ["/nope (404 page)", await visitor.call("GET", "/nope")],
      ["/nest-login (error page)", await b.call("GET", "/nest-login")],
      ["/account", await b.call("GET", "/account")],
      ["/app", await b.call("GET", "/app")],
      ["/app?code=", await b.call("GET", "/app?code=ABCD-EFGH")],
      ["/add", await b.call("GET", "/add")],
      ["/add?code= (unknown)", await b.call("GET", "/add?code=ABCD-EFGH")],
    ] as const;
    for (const [name, page] of pages) {
      expect(page.headers.get("content-type"), name).toContain("text/html");
      expect(page.text.split(TAG).length - 1, name).toBe(1);
    }
  });

  it("is served as JavaScript and never touches cookies", async () => {
    const h = await Harness.create();
    const r = await h.browser().call("GET", "/assets/cookie-notice.js");
    expect(r.status).toBe(200);
    expect(r.headers.get("content-type")).toMatch(/^text\/javascript\b/);
    expect(r.text).not.toContain("document.cookie");
    expect(r.text).not.toContain("innerHTML");
    expect(r.text).toContain('"pn-cookie-notice"');
    expect(r.text).toContain('role", "region"');
    expect(r.text).toContain('"Cookie notice"');
    expect(r.text).toContain("https://pairnets.app/cookies/");
    expect(r.text).toContain("Pairnets only uses cookies that are needed to sign you in. No tracking, no ads.");
  });

  it("is styled by the stylesheet", async () => {
    const h = await Harness.create();
    const css = (await h.browser().call("GET", "/assets/style.css")).text;
    expect(css).toContain(".cookie-notice {");
    expect(css).toContain("prefers-reduced-motion");
  });
});
