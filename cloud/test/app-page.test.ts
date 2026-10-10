// The "Sign in an app?" page (/app): every screen it draws, its own stylesheet, fonts and logo, and what its script does
// after Allow or "This isn't me", errors included. The script runs against the real page's markup in a small fake
// document, with fetch, location and the clock under the test's control.

import { describe, expect, it } from "vitest";
import { ASSETS } from "../src/assets";
import { addServer, Harness } from "./helpers";

async function setup(opts: { system?: string } = {}) {
  const h = await Harness.create();
  const b = h.browser();
  await b.signInByEmail("you@example.com");
  const app = h.client("198.51.100.80");
  const start = await app.call("POST", "/v1/app/start", { body: { name: "PC-1", system: opts.system ?? "Windows", appVersion: "1.0.9000" } });
  return { h, b, app, code: start.json.userCode as string, deviceCode: start.json.deviceCode as string };
}

/** The screens a page shows (not hidden) and has (hidden). */
function screens(html: string): { shown: string[]; all: string[] } {
  const all = [...html.matchAll(/<section class="q-screen" id="screen-([a-z-]+)"[^>]*?(\shidden)?>/g)];
  return { shown: all.filter((m) => !m[2]).map((m) => m[1]), all: all.map((m) => m[1]) };
}

function expectQuietPage(html: string) {
  expect(html).toContain('<link rel="stylesheet" href="/assets/style.css">\n<link rel="stylesheet" href="/assets/app.css">');
  expect(html).toContain('<body class="q-page">');
  // Still no inline scripts or styles (CSP), and the cookie notice like every page.
  expect(html).not.toMatch(/<script(?![^>]*\bsrc=)[^>]*>/);
  expect(html).not.toMatch(/<style/);
  expect(html).not.toMatch(/\sstyle=/);
  expect(html).toContain('<script src="/assets/cookie-notice.js" defer></script>');
}

describe("the Sign in an app page", () => {
  it("asking, one nest: the code is the question, the nest is named, the details are folded away", async () => {
    const { h, b, code } = await setup();
    const nest = await addServer(h, b, { label: "pairnets-server" });
    h.advance(90);
    const page = await b.call("GET", `/app?code=${code}`);
    expect(page.status).toBe(200);
    expectQuietPage(page.text);
    expect(page.text).toContain("<title>Allow PC-1 to sign in? · Pairnets</title>");
    expect(page.text).toContain('<script src="/assets/app.js" defer></script>');
    expect(screens(page.text)).toEqual({ shown: ["ask"], all: ["ask", "allowed", "refused", "expired"] });
    expect(page.text).toContain("Allow PC-1 to sign in?");
    expect(page.text).toContain("Does Pairnets on PC-1 show this code?");
    expect(page.text).toContain(`<p class="q-code">${code}</p>`);
    expect(page.text).toContain("It will sync with <b>pairnets-server</b>");
    expect(page.text).toContain(`data-nest="${nest.nestId}"`);
    expect(page.text).toContain('data-system="Windows"');
    expect(page.text).not.toContain('name="nest"');
    expect(page.text).toContain('<button type="button" class="q-btn q-primary" id="approve">Yes, allow PC-1</button>');
    expect(page.text).toContain('<button type="button" class="q-btn q-ghost" id="deny">No, this isn\x27t me</button>');
    expect(page.text).toContain("<details class=\"q-more\"><summary>Details</summary><p>Windows · Pairnets 1.0.9000 · asked 1 min ago · the code works for 9 more minutes</p></details>");
    expect(page.text).toContain('<p class="q-error" id="app-error" role="alert" hidden></p>');
    // The screens app.js switches to are already there, waiting.
    expect(page.text).toContain('<b id="allowed-nest">pairnets-server</b>');
    expect(page.text).toContain('href="pairnets://signed-in"');
    expect(page.text).toContain("This code has run out");
  });

  it("asking, several nests: a choice, the first one picked", async () => {
    const { h, b, code } = await setup();
    const one = await addServer(h, b, { label: "pairnets-server" });
    const two = await addServer(h, b, { label: "office-nest", ip: "198.51.100.42" });
    const page = await b.call("GET", `/app?code=${code}`);
    expect(page.text).toContain("<legend>Sync PC-1 with</legend>");
    expect(page.text).toContain(`<input type="radio" name="nest" value="${one.nestId}" data-label="pairnets-server" checked> pairnets-server`);
    expect(page.text).toContain(`<input type="radio" name="nest" value="${two.nestId}" data-label="office-nest"> office-nest`);
    expect(page.text).not.toContain("data-nest=");
  });

  it("asking, no nest yet: says so, and the allowed screen is the one that waits for the nest", async () => {
    const { b, code } = await setup();
    const page = await b.call("GET", `/app?code=${code}`);
    expect(page.text).toContain("You don't have a nest yet.");
    expect(screens(page.text).all).toEqual(["ask", "allowed-nonest", "refused", "expired"]);
    expect(page.text).toContain("PC-1 is waiting for your nest");
    expect(page.text).toContain("<code>curl -fsSL https://pairnets.app/get.sh | sudo bash</code>");
    expect(page.text).not.toContain("data-nest=");
  });

  it("answered: a reload draws the answer's screen and says which answer was given, and when", async () => {
    const { h, b, app, code, deviceCode } = await setup();
    const nest = await addServer(h, b, { label: "pairnets-server" });
    await b.call("POST", "/v1/app/approve", { body: { userCode: code, approve: true, nestId: nest.nestId } });
    h.advance(150);
    let page = await b.call("GET", `/app?code=${code}`);
    expectQuietPage(page.text);
    expect(screens(page.text)).toEqual({ shown: ["allowed"], all: ["allowed"] });
    expect(page.text).toContain("<title>PC-1 is signed in · Pairnets</title>");
    expect(page.text).toContain("It syncs with <b id=\"allowed-nest\">pairnets-server</b> from now on.");
    expect(page.text).toContain('<p class="q-earlier">You allowed this 2 min ago.</p>');
    expect(page.text).not.toContain('id="approve"');
    expect(page.text).not.toContain("/assets/app.js");
    // Still so once the app has collected its key.
    h.advance(3);
    expect((await app.call("POST", "/v1/app/poll", { body: { deviceCode } })).json.status).toBe("approved");
    page = await b.call("GET", `/app?code=${code}`);
    expect(screens(page.text).shown).toEqual(["allowed"]);
    expect(page.text).toContain("You allowed this 2 min ago.");

    const again = await app.call("POST", "/v1/app/start", { body: { name: "PC-1", system: "Windows" } });
    await b.call("POST", "/v1/app/approve", { body: { userCode: again.json.userCode, approve: false } });
    const refused = await b.call("GET", `/app?code=${again.json.userCode}`);
    expect(screens(refused.text)).toEqual({ shown: ["refused"], all: ["refused"] });
    expect(refused.text).toContain("PC-1 was not let in");
    expect(refused.text).toContain('<p class="q-earlier">You said no to this just now.</p>');
  });

  it("answered while the account had no nest: the waiting screen", async () => {
    const { b, code } = await setup();
    await b.call("POST", "/v1/app/approve", { body: { userCode: code, approve: true } });
    const page = await b.call("GET", `/app?code=${code}`);
    expect(screens(page.text)).toEqual({ shown: ["allowed-nonest"], all: ["allowed-nonest"] });
    expect(page.text).toContain("You allowed this just now.");
  });

  it("no code, or one that does not work: a box to type the code", async () => {
    const { h, b, code } = await setup();
    const blank = await b.call("GET", "/app");
    expectQuietPage(blank.text);
    expect(blank.text).toContain("<h1 id=\"screen-code-title\">Sign in an app</h1>");
    expect(blank.text).toContain('<form class="q-field" method="get" action="/app">');
    expect(blank.text).toContain('<input id="code" name="code" required maxlength="16"');
    expect(blank.text).not.toContain("/assets/app.js");
    expect((await b.call("GET", "/app?code=ZZZZ-ZZZZ")).text).toContain("That code didn't work");
    h.advance(601);
    expect((await b.call("GET", `/app?code=${code}`)).text).toContain("That code didn't work");
  });

  it("serves its stylesheet, the ring logo and Instrument Sans from the Worker itself", async () => {
    const h = await Harness.create();
    const v = h.browser();
    const css = await v.call("GET", "/assets/app.css");
    expect(css.status).toBe(200);
    expect(css.headers.get("content-type")).toBe("text/css; charset=utf-8");
    for (const w of ["Regular", "Medium", "SemiBold"]) {
      expect(css.text).toContain(`src: url(/assets/fonts/InstrumentSans-${w}.woff2) format("woff2")`);
      const font = await h.browser().call("GET", `/assets/fonts/InstrumentSans-${w}.woff2`);
      expect(font.status, w).toBe(200);
      expect(font.headers.get("content-type")).toBe("font/woff2");
      expect(font.headers.get("cache-control")).toBe("public, max-age=3600");
      const bytes = ASSETS[`fonts/InstrumentSans-${w}.woff2`].body as Uint8Array;
      expect(String.fromCharCode(...bytes.slice(0, 4))).toBe("wOF2");
      expect(bytes.length).toBeGreaterThan(10_000);
      expect(bytes.length).toBeLessThan(40_000);
    }
    // Light and dark from the system, and the cookie notice kept in the page's flow (never over the buttons).
    expect(css.text).toContain("@media (prefers-color-scheme: dark)");
    expect(css.text).toContain(".q-page .cookie-notice { position: static;");
    const logo = await v.call("GET", "/assets/logo.svg");
    expect(logo.headers.get("content-type")).toBe("image/svg+xml");
    expect(logo.text).toContain('<path d="M75.2,92.6 A36,36 0 1 1 92.6,75.2"');
    // Fonts may come from this site only.
    expect((await v.call("GET", "/login")).headers.get("content-security-policy")).toContain("font-src 'self';");
  });
});

// ------------------------------------------------------------------ app.js in a fake document

type Attrs = Record<string, string>;

class FakeEl {
  hidden: boolean;
  disabled = false;
  checked: boolean;
  value: string;
  textContent = "";
  focused = false;
  classes = new Set<string>();
  classList = { add: (c: string) => this.classes.add(c), remove: (c: string) => this.classes.delete(c) };
  private listeners: (() => void)[] = [];
  constructor(public attrs: Attrs) {
    this.hidden = "hidden" in attrs;
    this.checked = "checked" in attrs;
    this.value = attrs.value ?? "";
  }
  getAttribute(n: string): string | null {
    return n in this.attrs ? this.attrs[n] : null;
  }
  addEventListener(_type: string, f: () => void) {
    this.listeners.push(f);
  }
  click() {
    if (!this.disabled) for (const f of this.listeners) f();
  }
  focus() {
    this.focused = true;
  }
}

const unescape = (s: string) => s.replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&amp;/g, "&");

/** Every element with an id, and the nest radios, from the page's real markup. */
function fakeDocument(html: string) {
  const byId = new Map<string, FakeEl>();
  const radios: FakeEl[] = [];
  for (const m of html.matchAll(/<([a-z0-9]+)\s([^>]*)>/g)) {
    const attrs: Attrs = {};
    for (const a of m[2].matchAll(/([a-z-]+)(?:="([^"]*)")?/g)) attrs[a[1]] = unescape(a[2] ?? "");
    const el = new FakeEl(attrs);
    if (attrs.id) byId.set(attrs.id, el);
    if (m[1] === "input" && attrs.name === "nest") radios.push(el);
  }
  return {
    byId,
    radios,
    doc: { getElementById: (id: string) => byId.get(id) ?? null, getElementsByName: (n: string) => (n === "nest" ? radios : []) },
  };
}

type Answer = { status: number; body: unknown } | "offline";

const WINDOWS_CHROME = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36";
const IPHONE = "Mozilla/5.0 (iPhone; CPU iPhone OS 19_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/19.0 Mobile/15E148 Safari/604.1";
const MAC_SAFARI = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/19.0 Safari/605.1.15";

/** Loads the page and its real script; `answers` are what POST /v1/app/approve replies, in order. */
async function runPage(path: string, b: { call: (m: string, p: string) => Promise<{ text: string }> }, answers: Answer[], ua = WINDOWS_CHROME) {
  const html = (await b.call("GET", path)).text;
  const script = (await b.call("GET", "/assets/app.js")).text;
  const page = fakeDocument(html);
  const sent: Record<string, unknown>[] = [];
  const timers: { fn: () => void; ms: number }[] = [];
  const location = { href: `https://sync.pairnets.app${path}`, reloaded: false, reload() { this.reloaded = true; } };
  const fetch = async (url: string, init: { method: string; body?: string }) => {
    expect(url).toBe("/v1/app/approve");
    expect(init.method).toBe("POST");
    sent.push(JSON.parse(init.body ?? "{}"));
    const a = answers.shift();
    if (!a) throw new Error("no answer queued");
    if (a === "offline") throw new TypeError("Failed to fetch");
    return new Response(a.status === 204 ? null : JSON.stringify(a.body), { status: a.status });
  };
  const setTimeout = (fn: () => void, ms: number) => timers.push({ fn, ms });
  new Function("document", "location", "navigator", "fetch", "setTimeout", script)(page.doc, location, { userAgent: ua, maxTouchPoints: 0 }, fetch, setTimeout);
  const el = (id: string) => {
    const e = page.byId.get(id);
    if (!e) throw new Error(`no #${id} on the page`);
    return e;
  };
  const shown = () => [...page.byId.entries()].filter(([id, e]) => id.startsWith("screen-") && !id.endsWith("-title") && !e.hidden).map(([id]) => id.slice(7));
  const settle = async () => {
    for (let i = 0; i < 5; i++) await new Promise((r) => globalThis.setTimeout(r, 0));
  };
  return { el, shown, settle, sent, timers, location, radios: page.radios };
}

describe("the Sign in an app page's script", () => {
  it("Allow: the signed-in screen, and Pairnets is brought to the front once (same kind of computer)", async () => {
    const { h, b, code } = await setup();
    const nest = await addServer(h, b, { label: "pairnets-server" });
    const p = await runPage(`/app?code=${code}`, b, [{ status: 200, body: { status: "approved" } }]);
    expect(p.shown()).toEqual(["ask"]);
    p.el("approve").click();
    expect(p.el("approve").disabled).toBe(true);
    expect(p.el("deny").disabled).toBe(true);
    await p.settle();
    expect(p.sent).toEqual([{ userCode: code, approve: true, nestId: nest.nestId }]);
    expect(p.shown()).toEqual(["allowed"]);
    expect(p.el("screen-allowed-title").focused).toBe(true);
    expect(p.timers).toHaveLength(1);
    p.timers[0].fn();
    expect(p.location.href).toBe("pairnets://signed-in");
  });

  it("only tries pairnets:// by itself on the same kind of computer, never on a phone", async () => {
    const { h, b } = await setup();
    await addServer(h, b);
    let n = 0;
    for (const [system, ua, tries] of [
      ["Windows", IPHONE, false],
      ["Windows", MAC_SAFARI, false],
      ["macOS", MAC_SAFARI, true],
      ["Linux", WINDOWS_CHROME, false],
      ["other", WINDOWS_CHROME, false],
    ] as const) {
      const start = await h.client(`198.51.100.${100 + n++}`).call("POST", "/v1/app/start", { body: { name: "PC-1", system } });
      const code = start.json.userCode as string;
      const p = await runPage(`/app?code=${code}`, b, [{ status: 200, body: { status: "approved" } }], ua);
      p.el("approve").click();
      await p.settle();
      expect(p.shown(), `${system} / ${ua}`).toEqual(["allowed"]);
      expect(p.timers.length > 0, `${system} / ${ua}`).toBe(tries);
    }
  });

  it("an error says what happened in plain words and both buttons work again: a retry goes through", async () => {
    const { h, b, code } = await setup();
    await addServer(h, b);
    const p = await runPage(`/app?code=${code}`, b, [
      "offline",
      { status: 429, body: { error: "rate_limited", message: "Too many requests. Wait a little and try again." } },
      { status: 500, body: { error: "server_error", message: "Something went wrong on our side." } },
      { status: 200, body: { status: "approved" } },
    ]);
    p.el("approve").click();
    await p.settle();
    expect(p.el("app-error").hidden).toBe(false);
    expect(p.el("app-error").textContent).toBe("Could not reach Pairnets. Check the connection and try again.");
    expect(p.el("approve").disabled).toBe(false);
    expect(p.el("deny").disabled).toBe(false);
    expect(p.shown()).toEqual(["ask"]);

    p.el("approve").click();
    await p.settle();
    expect(p.el("app-error").textContent).toBe("That was a lot of tries in a short time. Wait a minute, then try again.");
    expect(p.el("approve").disabled).toBe(false);

    p.el("approve").click();
    await p.settle();
    expect(p.el("app-error").textContent).toBe("Something went wrong on our side. Try again in a moment.");

    p.el("approve").click();
    expect(p.el("app-error").hidden).toBe(true);
    await p.settle();
    expect(p.sent).toHaveLength(4);
    expect(p.shown()).toEqual(["allowed"]);
  });

  it("a click while the answer is on its way does nothing", async () => {
    const { h, b, code } = await setup();
    await addServer(h, b);
    const p = await runPage(`/app?code=${code}`, b, [{ status: 200, body: { status: "denied" } }]);
    p.el("deny").click();
    p.el("deny").disabled = false; // even if the button were pressed again somehow
    p.el("deny").click();
    p.el("approve").click();
    await p.settle();
    expect(p.sent).toEqual([{ userCode: code, approve: false }]);
    expect(p.shown()).toEqual(["refused"]);
    expect(p.timers).toHaveLength(0);
  });

  it("ran out: the run-out screen; answered elsewhere: reload (the page says which answer); signed out: sign in and come back", async () => {
    const { h, b, code } = await setup();
    await addServer(h, b);
    let p = await runPage(`/app?code=${code}`, b, [{ status: 400, body: { error: "expired", message: "That code has expired." } }]);
    p.el("approve").click();
    await p.settle();
    expect(p.shown()).toEqual(["expired"]);
    expect(p.el("screen-expired-title").focused).toBe(true);

    p = await runPage(`/app?code=${code}`, b, [{ status: 409, body: { error: "already_decided", message: "This sign-in was already answered." } }]);
    p.el("deny").click();
    await p.settle();
    expect(p.location.reloaded).toBe(true);

    p = await runPage(`/app?code=${code}`, b, [{ status: 401, body: { error: "unauthorized", message: "Sign in first." } }]);
    p.el("approve").click();
    await p.settle();
    expect(p.location.href).toBe(`/login?next=${encodeURIComponent(`/app?code=${code}`)}`);
  });

  it("several nests: sends the one picked and names it on the signed-in screen", async () => {
    const { h, b, code } = await setup();
    await addServer(h, b, { label: "pairnets-server" });
    const two = await addServer(h, b, { label: "office <nest>", ip: "198.51.100.42" });
    const p = await runPage(`/app?code=${code}`, b, [{ status: 200, body: { status: "approved" } }]);
    p.radios[0].checked = false;
    p.radios[1].checked = true;
    p.el("approve").click();
    await p.settle();
    expect(p.sent).toEqual([{ userCode: code, approve: true, nestId: two.nestId }]);
    expect(p.el("allowed-nest").textContent).toBe("office <nest>");
  });

  it("no nest yet: allows without a nest, shows the waiting screen and leaves the app where it is", async () => {
    const { b, code } = await setup();
    const p = await runPage(`/app?code=${code}`, b, [{ status: 200, body: { status: "approved" } }]);
    p.el("approve").click();
    await p.settle();
    expect(p.sent).toEqual([{ userCode: code, approve: true }]);
    expect(p.shown()).toEqual(["allowed-nonest"]);
    expect(p.timers).toHaveLength(0);
  });
});
