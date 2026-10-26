// Sign in with a link (CONTRACT.md 6.9): the app shows a long one-time link, the browser that opens it signs in and
// allows, the page shows a long code, and only that code, pasted into the app, hands over the key. The page's script
// runs against the real page's markup in a small fake document, as in app-page.test.ts.

import { describe, expect, it } from "vitest";
import { B32_UPPER, randomBase32 } from "../src/crypto";
import { linkCodeCheck, safeNext } from "../src/formats";
import { addServer, Harness } from "./helpers";

async function setup() {
  const h = await Harness.create();
  const b = h.browser();
  const accountId = await b.signInByEmail("you@example.com");
  const app = h.client("198.51.100.80");
  return { h, b, accountId, app };
}

async function startLink(app: ReturnType<Harness["client"]>, name = "SORO-SERVER") {
  const start = await app.call("POST", "/v1/app/start", { body: { name, system: "Linux", appVersion: "1.0.61.7", mode: "code" } });
  expect(start.status, start.text).toBe(200);
  const link = start.json.link as string;
  return { deviceCode: start.json.deviceCode as string, link, token: new URL(link).searchParams.get("r")!, path: link.replace("https://sync.pairnets.app", "") };
}

/** A code with a correct check that is still not the one shown. */
async function otherCode(): Promise<string> {
  const random = randomBase32(28, B32_UPPER);
  return (random + (await linkCodeCheck(random))).match(/.{4}/g)!.join("-");
}

describe("sign in with a link", () => {
  it("start, open the link, Allow: the long code; pasted into the app it brings the key once, and the poll never does", async () => {
    const { h, b, app } = await setup();
    const nest = await addServer(h, b, { label: "Home nest" });
    const start = await app.call("POST", "/v1/app/start", { body: { name: "SORO-SERVER", system: "Linux", appVersion: "1.0.61.7", mode: "code" } });
    expect(start.json).toEqual({
      deviceCode: expect.stringMatching(/^pcd_[A-Za-z0-9_-]{43}$/),
      link: expect.stringMatching(/^https:\/\/sync\.pairnets\.app\/app\/link\?r=pnr_[A-Za-z0-9_-]{43}$/),
      expiresIn: 900,
    });
    const { deviceCode, token, path } = { deviceCode: start.json.deviceCode, token: new URL(start.json.link).searchParams.get("r")!, path: new URL(start.json.link).pathname + new URL(start.json.link).search };

    // The page: the computer, where it asked from, the warning, and Allow that shows the code. No short code to compare.
    const page = await b.call("GET", path);
    expect(page.status).toBe(200);
    expect(page.text).toContain("You opened a sign-in link from Pairnets on a computer called <b>SORO-SERVER</b>");
    expect(page.text).toContain("If a stranger sent you this link, press No.");
    expect(page.text).toContain(`data-link="${token}"`);
    expect(page.text).not.toContain("data-code=");
    expect(page.text).not.toContain("q-codebox");
    expect(page.text).toContain(">Allow and show the code</button>");
    expect(page.text).toContain('id="screen-longcode"');
    expect(page.text).toContain("the link works for 15 more minutes");

    // Polling never hands it over, not even after Allow.
    expect((await app.call("POST", "/v1/app/poll", { body: { deviceCode } })).json).toEqual({ status: "pending" });
    const ok = await b.call("POST", "/v1/app/approve", { body: { link: token, approve: true, nestId: nest.nestId } });
    expect(ok.status, ok.text).toBe(200);
    expect(ok.json).toEqual({ status: "approved", code: expect.stringMatching(/^([0-9ABCDEFGHJKMNPQRSTVWXYZ]{4}-){7}[0-9ABCDEFGHJKMNPQRSTVWXYZ]{4}$/), codeExpiresIn: 300 });
    const code = ok.json.code as string;
    h.advance(3);
    expect((await app.call("POST", "/v1/app/poll", { body: { deviceCode } })).json).toEqual({ status: "pending" });
    expect(nest.devices.size).toBe(0);

    // Only hashes are kept.
    const row = (await h.query<Record<string, unknown>>("SELECT * FROM device_logins"))[0];
    for (const v of Object.values(row)) {
      if (typeof v !== "string") continue;
      expect(v).not.toContain(token);
      expect(v).not.toContain(code.replace(/-/g, ""));
    }

    // Pasted with spaces, in small letters, and with a look-alike: still the code.
    h.advance(3);
    const pasted = ` ${code.toLowerCase().replace(/-/g, " ").replace(/0/g, "o")} `;
    const done = await app.call("POST", "/v1/app/redeem", { body: { deviceCode, code: pasted } });
    expect(done.status, done.text).toBe(200);
    expect(done.json).toEqual({
      status: "approved",
      appToken: expect.stringMatching(/^pca_[A-Za-z0-9_-]{43}$/),
      expiresIn: 3600,
      email: "you@example.com",
      nest: { id: nest.nestId, label: "Home nest", serverUrl: `https://sync.pairnets.app/n/${nest.nestId}/`, sharedBy: null, access: "owner", folders: null },
      device: { id: expect.any(String), name: "SORO-SERVER", key: expect.stringMatching(/^pn_/) },
    });
    expect(nest.devices.size).toBe(1);

    // Once: the same code again is the end of it, and the link no longer finds a sign-in to answer.
    h.advance(3);
    const again = await app.call("POST", "/v1/app/redeem", { body: { deviceCode, code } });
    expect(again.status).toBe(400);
    expect(again.json.error).toBe("expired");
    // The page (reloaded) says it is signed in, and the page's check sees the same.
    const after = await b.call("GET", path);
    expect(after.text).toContain("SORO-SERVER is signed in");
    const req = await b.call("GET", `/v1/app/requests/${after.text.match(/data-request="([^"]+)"/)?.[1] ?? "none"}`);
    expect(req.json.status).toBe("delivered");
    const audits = await h.query<{ event: string }>("SELECT event FROM audit WHERE event LIKE 'app_%' ORDER BY id");
    expect(audits.map((a) => a.event)).toEqual(["app_approved", "app_signed_in"]);
  });

  it("the page's check: the sign-in's status by the request code, for this account only, while the code waits", async () => {
    const { h, b, app } = await setup();
    const nest = await addServer(h, b);
    const { path, token, deviceCode } = await startLink(app);
    const request = (await b.call("GET", path)).text.match(/data-request="([A-Z0-9-]+)"/)![1];
    // Before Allow the request code finds nothing (it is never shown, and can't be used to answer either).
    expect((await b.call("GET", `/v1/app/requests/${request}`)).status).toBe(404);
    expect((await b.call("POST", "/v1/app/approve", { body: { userCode: request, approve: true, nestId: nest.nestId } })).status).toBe(404);
    expect((await b.call("GET", `/app?code=${request}`)).text).toContain("That code didn't work");

    const { json } = await b.call("POST", "/v1/app/approve", { body: { link: token, approve: true, nestId: nest.nestId } });
    expect((await b.call("GET", `/v1/app/requests/${request}`)).json.status).toBe("approved");
    const other = h.browser("203.0.113.9");
    await other.signInByEmail("someone@example.com");
    expect((await other.call("GET", `/v1/app/requests/${request}`)).status).toBe(404);
    expect((await other.call("GET", path)).text).toContain("This sign-in link doesn't work");
    h.advance(3);
    await app.call("POST", "/v1/app/redeem", { body: { deviceCode, code: json.code } });
    expect((await b.call("GET", `/v1/app/requests/${request}`)).json.status).toBe("delivered");
  });

  it("a reload after Allow never shows the code again", async () => {
    const { h, b, app } = await setup();
    const nest = await addServer(h, b);
    const { path, token } = await startLink(app);
    const { json } = await b.call("POST", "/v1/app/approve", { body: { link: token, approve: true, nestId: nest.nestId } });
    h.advance(60);
    const page = await b.call("GET", path);
    expect(page.text).toContain("Paste the code into Pairnets on SORO-SERVER");
    expect(page.text).toContain("The code was shown once, when you pressed Allow (1 min ago)");
    expect(page.text).not.toContain(json.code);
    expect(page.text).not.toContain(json.code.replace(/-/g, ""));
  });

  it("wrong codes: a typo is not a try, a wrong code is, and the fifth stops the sign-in for good", async () => {
    const { h, b, app } = await setup();
    const nest = await addServer(h, b);
    const { token, deviceCode } = await startLink(app);
    const { json } = await b.call("POST", "/v1/app/approve", { body: { link: token, approve: true, nestId: nest.nestId } });
    const right = json.code as string;
    const redeem = (code: string) => app.call("POST", "/v1/app/redeem", { body: { deviceCode, code } });

    // Typos: cut short, a letter that can't be in a code, one character changed (the check catches it).
    const flipped = right.slice(0, 5) + (right[5] === "A" ? "B" : "A") + right.slice(6);
    for (const typo of [right.slice(0, 20), right.replace(/.$/, "U"), flipped, ""]) {
      const r = await redeem(typo);
      expect(r.status, typo).toBe(400);
      expect(r.json.error, typo).toBe("code_typo");
    }
    expect((await h.query<{ tries: number }>("SELECT tries FROM device_logins"))[0].tries).toBe(0);

    for (let left = 4; left >= 1; left--) {
      const r = await redeem(await otherCode());
      expect(r.status).toBe(400);
      expect(r.json).toEqual({ error: "invalid_code", message: `That code doesn't match. ${left === 1 ? "1 try" : `${left} tries`} left.`, triesLeft: left });
    }
    const fifth = await redeem(await otherCode());
    expect(fifth.status).toBe(400);
    expect(fifth.json.error).toBe("stopped");
    // Stopped: now even the right code does nothing.
    h.advance(3);
    const late = await redeem(right);
    expect(late.json.error).toBe("stopped");
    expect(nest.devices.size).toBe(0);
    expect((await h.query<{ status: string }>("SELECT status FROM device_logins"))[0].status).toBe("denied");
    expect((await h.query<{ event: string }>("SELECT event FROM audit WHERE event = 'app_link_stopped'")).length).toBe(1);
  });

  it("times: the link must be allowed within 15 minutes, the code pasted within 5 minutes of Allow", async () => {
    const { h, b, app } = await setup();
    const nest = await addServer(h, b);

    // The link runs out.
    const first = await startLink(app);
    h.advance(901);
    await b.signInByEmail("you@example.com"); // still a recent sign-in
    expect((await b.call("GET", first.path)).text).toContain("This sign-in link doesn't work");
    const late = await b.call("POST", "/v1/app/approve", { body: { link: first.token, approve: true, nestId: nest.nestId } });
    expect(late.status).toBe(400);
    expect(late.json.error).toBe("expired");

    // Allowed at minute 14: the code still gets its full 5 minutes, then runs out.
    const second = await startLink(app);
    h.advance(14 * 60);
    await b.signInByEmail("you@example.com");
    const { json } = await b.call("POST", "/v1/app/approve", { body: { link: second.token, approve: true, nestId: nest.nestId } });
    h.advance(299);
    const before = await app.call("POST", "/v1/app/redeem", { body: { deviceCode: second.deviceCode, code: await otherCode() } });
    expect(before.json.error).toBe("invalid_code");
    h.advance(1);
    const after = await app.call("POST", "/v1/app/redeem", { body: { deviceCode: second.deviceCode, code: json.code } });
    expect(after.status).toBe(400);
    expect(after.json.error).toBe("expired");
  });

  it("the code expires on its own clock even while the link is still valid", async () => {
    const { h, b, app } = await setup();
    const nest = await addServer(h, b);
    const { token, deviceCode } = await startLink(app);
    const { json } = await b.call("POST", "/v1/app/approve", { body: { link: token, approve: true, nestId: nest.nestId } });
    await h.query("UPDATE device_logins SET code_expires_at = ?1", h.clock.now - 1);
    const r = await app.call("POST", "/v1/app/redeem", { body: { deviceCode, code: json.code } });
    expect(r.status).toBe(400);
    expect(r.json.error).toBe("code_expired");
  });

  it("before Allow the app is told to finish in the browser; No stops it", async () => {
    const { h, b, app } = await setup();
    await addServer(h, b);
    const { token, deviceCode } = await startLink(app);
    const early = await app.call("POST", "/v1/app/redeem", { body: { deviceCode, code: await otherCode() } });
    expect(early.status).toBe(409);
    expect(early.json.error).toBe("not_allowed_yet");
    expect((await b.call("POST", "/v1/app/approve", { body: { link: token, approve: false } })).json).toEqual({ status: "denied" });
    const r = await app.call("POST", "/v1/app/redeem", { body: { deviceCode, code: await otherCode() } });
    expect(r.status).toBe(400);
    expect(r.json.error).toBe("stopped");
  });

  it("an ordinary sign-in can't be finished with a code, and the redeem call checks its input", async () => {
    const { h, b, app } = await setup();
    const nest = await addServer(h, b);
    const s = await app.call("POST", "/v1/app/start", { body: { name: "Laptop" } });
    await b.call("POST", "/v1/app/approve", { body: { userCode: s.json.userCode, approve: true, nestId: nest.nestId } });
    expect((await app.call("POST", "/v1/app/redeem", { body: { deviceCode: s.json.deviceCode, code: await otherCode() } })).json.error).toBe("expired");
    expect((await app.call("POST", "/v1/app/redeem", { body: { deviceCode: "pcd_x", code: "y" } })).json.error).toBe("expired");
    expect((await app.call("POST", "/v1/app/redeem", { body: { code: "y" } })).json.error).toBe("bad_request");
    expect((await app.call("POST", "/v1/app/start", { body: { name: "Laptop", mode: "other" } })).json.error).toBe("bad_request");
    expect((await b.call("POST", "/v1/app/approve", { body: { link: 5, approve: true } })).json.error).toBe("bad_request");
    expect((await b.call("POST", "/v1/app/approve", { body: { link: "pnr_short", approve: true } })).status).toBe(404);
  });

  it("no nest yet: the page says to set one up and can't allow; the API refuses too", async () => {
    const { b, app } = await setup();
    const { path, token } = await startLink(app);
    const page = await b.call("GET", path);
    expect(page.text).toContain("Set one up in your account first, then start again in Pairnets on SORO-SERVER.");
    expect(page.text).toContain('id="approve" disabled');
    const r = await b.call("POST", "/v1/app/approve", { body: { link: token, approve: true } });
    expect(r.status).toBe(400);
    expect(r.json.error).toBe("bad_request");
  });

  it("signed out: sign in and come straight back to the link; an old sign-in is asked for again", async () => {
    const { h, b, app } = await setup();
    await addServer(h, b);
    const { path } = await startLink(app);
    const stranger = h.browser("203.0.113.7");
    const out = await stranger.call("GET", path);
    expect(out.status).toBe(302);
    expect(out.location).toBe(`/login?next=${encodeURIComponent(path)}`);
    expect(safeNext(path)).toBe(path);
    expect(safeNext("/app/link?r=pnr_short")).toBe("/account");
    expect((await stranger.call("GET", "/app/link?r=nonsense")).location).toBe(`/login?next=${encodeURIComponent("/app")}`);

    h.advance(16 * 60); // the browser's sign-in is now older than 15 minutes
    const fresh = await startLink(app, "PC-2");
    const re = await b.call("GET", fresh.path);
    expect(re.status).toBe(302);
    expect(re.location).toBe(`/login?reauth=1&next=${encodeURIComponent(fresh.path)}`);
  });

  it("a nest that is off: 503, and the same code works once it is back", async () => {
    const { h, b, app } = await setup();
    const nest = await addServer(h, b);
    const { token, deviceCode } = await startLink(app);
    const { json } = await b.call("POST", "/v1/app/approve", { body: { link: token, approve: true, nestId: nest.nestId } });
    nest.running = false;
    const off = await app.call("POST", "/v1/app/redeem", { body: { deviceCode, code: json.code } });
    expect(off.status).toBe(503);
    expect(off.json.error).toBe("nest_offline");
    nest.running = true;
    const on = await app.call("POST", "/v1/app/redeem", { body: { deviceCode, code: json.code } });
    expect(on.status, on.text).toBe(200);
    expect(nest.devices.size).toBe(1);
  });
});

// ------------------------------------------------------------------ app.js on a sign-in link, in a fake document

class FakeEl {
  hidden: boolean;
  disabled: boolean;
  checked: boolean;
  value: string;
  textContent = "";
  focused = false;
  classList = { add: () => {}, remove: () => {} };
  private listeners: (() => void)[] = [];
  constructor(public attrs: Record<string, string>) {
    this.hidden = "hidden" in attrs;
    this.disabled = "disabled" in attrs;
    this.checked = "checked" in attrs;
    this.value = attrs.value ?? "";
  }
  getAttribute(n: string): string | null {
    return n in this.attrs ? this.attrs[n] : null;
  }
  addEventListener(_t: string, f: () => void) {
    this.listeners.push(f);
  }
  click() {
    if (!this.disabled) for (const f of this.listeners) f();
  }
  focus() {
    this.focused = true;
  }
}

async function runLinkPage(b: { call: (m: string, p: string) => Promise<{ text: string }> }, path: string, answers: { status: number; body: unknown }[]) {
  const html = (await b.call("GET", path)).text;
  const script = (await b.call("GET", "/assets/app.js")).text;
  const byId = new Map<string, FakeEl>();
  for (const m of html.matchAll(/<([a-z0-9]+)\s([^>]*)>/g)) {
    const attrs: Record<string, string> = {};
    for (const a of m[2].matchAll(/([a-z-]+)(?:="([^"]*)")?/g)) attrs[a[1]] = a[2] ?? "";
    if (attrs.id) byId.set(attrs.id, new FakeEl(attrs));
  }
  const calls: { url: string; method: string; body: unknown }[] = [];
  const timers: (() => void)[] = [];
  const copied: string[] = [];
  const fetch = async (url: string, init: { method: string; body?: string }) => {
    calls.push({ url, method: init.method, body: init.body ? JSON.parse(init.body) : null });
    const a = answers.shift();
    if (!a) throw new Error(`no answer queued for ${url}`);
    return new Response(JSON.stringify(a.body), { status: a.status });
  };
  const navigator = { userAgent: "Mozilla/5.0 (X11; Linux x86_64)", maxTouchPoints: 0, clipboard: { writeText: async (t: string) => void copied.push(t) } };
  const location = { href: `https://sync.pairnets.app${path}`, reload() {} };
  const doc = { getElementById: (id: string) => byId.get(id) ?? null, getElementsByName: () => [] };
  new Function("document", "location", "navigator", "fetch", "setTimeout", script)(doc, location, navigator, fetch, (fn: () => void) => timers.push(fn));
  const settle = async () => {
    for (let i = 0; i < 6; i++) await new Promise((r) => globalThis.setTimeout(r, 0));
  };
  const shown = () => [...byId.entries()].filter(([id, e]) => id.startsWith("screen-") && !id.endsWith("-title") && !e.hidden).map(([id]) => id.slice(7));
  return { el: (id: string) => byId.get(id)!, calls, timers, copied, settle, shown, location };
}

describe("the sign-in link page's script", () => {
  it("Allow shows the long code in its groups, Copy copies it, and the page moves on once the app used it", async () => {
    const { h, b, app } = await setup();
    const nest = await addServer(h, b, { label: "Home nest" });
    const { path, token } = await startLink(app);
    const code = "K7QM-3XWD-R9FA-T2HN-8VCJ-P4GE-M2SB-Q6XY";
    const p = await runLinkPage(b, path, [
      { status: 200, body: { status: "approved", code, codeExpiresIn: 300 } },
      { status: 200, body: { status: "approved" } },
      { status: 200, body: { status: "delivered" } },
    ]);
    expect(p.shown()).toEqual(["ask"]);
    p.el("approve").click();
    await p.settle();
    expect(p.calls[0]).toEqual({ url: "/v1/app/approve", method: "POST", body: { link: token, approve: true, nestId: nest.nestId } });
    expect(p.shown()).toEqual(["longcode"]);
    expect([1, 2, 3, 4, 5, 6, 7, 8].map((i) => p.el(`long-code-${i}`).textContent).join("-")).toBe(code);
    expect(p.location.href).not.toContain("pairnets://"); // never tries to open the app: it may be on another computer
    p.el("copy-code").click();
    await p.settle();
    expect(p.copied).toEqual([code]);
    expect(p.el("copy-code").textContent).toBe("Copied");

    const request = p.el("app-request").getAttribute("data-request");
    p.timers.shift()!();
    await p.settle();
    expect(p.calls[1]).toEqual({ url: `/v1/app/requests/${request}`, method: "GET", body: null });
    expect(p.shown()).toEqual(["longcode"]);
    p.timers.shift()!();
    await p.settle();
    expect(p.shown()).toEqual(["allowed"]);
    expect(p.timers).toHaveLength(0);
  });

  it("an old sign-in goes to sign in again and comes back to the link; stopped or run out shows that", async () => {
    const { h, b, app } = await setup();
    await addServer(h, b);
    const { path } = await startLink(app);
    const p = await runLinkPage(b, path, [{ status: 401, body: { error: "reauth_required" } }]);
    p.el("approve").click();
    await p.settle();
    expect(p.location.href).toBe(`/login?reauth=1&next=${encodeURIComponent(path)}`);

    const q = await runLinkPage(b, path, [
      { status: 200, body: { status: "approved", code: "K7QM-3XWD-R9FA-T2HN-8VCJ-P4GE-M2SB-Q6XY" } },
      { status: 200, body: { status: "denied" } },
    ]);
    q.el("approve").click();
    await q.settle();
    q.timers.shift()!();
    await q.settle();
    expect(q.shown()).toEqual(["refused"]);

    const r = await runLinkPage(b, path, [
      { status: 200, body: { status: "approved", code: "K7QM-3XWD-R9FA-T2HN-8VCJ-P4GE-M2SB-Q6XY" } },
      { status: 404, body: { error: "not_found" } },
    ]);
    r.el("approve").click();
    await r.settle();
    r.timers.shift()!();
    await r.settle();
    expect(r.shown()).toEqual(["expired"]);
  });
});
