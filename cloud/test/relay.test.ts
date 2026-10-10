import { createExecutionContext, waitOnExecutionContext } from "cloudflare:test";
import { env as baseEnv } from "cloudflare:workers";
import { describe, expect, it, vi } from "vitest";
import wranglerToml from "../wrangler.toml?raw";
import type { Env } from "../src/env";
import { RELAY_CSP } from "../src/http";
import { NEST_CACHE_SECONDS, RELAY_LIMIT_PERIOD, RELAY_PER_MINUTE, relayAllowed } from "../src/relay";
import { addServer, Harness, linkNest, ORIGIN } from "./helpers";

async function setup(overrides: Partial<Env> = {}) {
  const h = await Harness.create(overrides);
  const b = h.browser();
  await b.signInByEmail("you@example.com");
  const nest = await addServer(h, b);
  const app = h.client("198.51.100.80");
  return { h, b, nest, app };
}

const OFFLINE = { error: "nest_offline", message: "Your server is not connected right now. Check that it is on." };
const UNKNOWN = { error: "nest_unknown", message: "This server is not linked to Pairnets any more." };

describe("relay", () => {
  it("passes api/* and the hub on with the caller's method, path, query, headers and body, minus ours", async () => {
    const { h, nest, app } = await setup();
    const r = await app.call("POST", `/n/${nest.nestId}/api/echo/files?path=a%20b&x=1`, {
      rawBody: '{"hello":"nest"}',
      headers: {
        Authorization: "Bearer device-key-of-the-app",
        "X-Custom": "kept",
        Cookie: "__Host-pn_id=pcs_should_not_travel",
        "CF-Ray": "abc",
        "CF-IPCountry": "SE",
        "X-Pairnets-Client-IP": "192.0.2.66",
        "X-Pairnets-Route": "nst_somebodyelse00000000000001",
        "X-Pairnets-Sig": "forged",
        "X-Pairnets-Nonce": "forged",
        "X-Pairnets-Ts": "1",
        "X-Pairnets-Nest": "forged",
        Expect: "100-continue",
        TE: "trailers",
      },
    });
    expect(r.status, r.text).toBe(200);
    expect(r.json).toMatchObject({ method: "POST", path: "/api/echo/files", query: "?path=a%20b&x=1", body: '{"hello":"nest"}' });
    const got = r.json.headers as Record<string, string>;
    expect(got.authorization).toBe("Bearer device-key-of-the-app");
    expect(got["x-custom"]).toBe("kept");
    expect(got["content-type"]).toBe("application/json");
    expect(got["x-pairnets-client-ip"]).toBe("198.51.100.80"); // Cloudflare's view of the caller, not what it sent
    // Expect: 100-continue (sent by the apps with every single-request upload) stays on the caller's side: the nest
    // would answer 100 Continue, which a Worker cannot pass on (found in a local end-to-end run).
    for (const gone of ["cookie", "cf-ray", "cf-ipcountry", "cf-connecting-ip", "x-pairnets-route", "x-pairnets-sig", "x-pairnets-nonce", "x-pairnets-ts", "x-pairnets-nest", "expect", "te"]) {
      expect(got[gone], gone).toBeUndefined();
    }
    // The router was asked for exactly this nest.
    expect(h.world.routerRequests.at(-1)!.headers.get("X-Pairnets-Route")).toBe(nest.nestId);

    for (const path of ["hub", "hub/negotiate?negotiateVersion=1", "api/hello"]) {
      const ok = await app.call("POST", `/n/${nest.nestId}/${path}`, { rawBody: "x", contentType: "text/plain" });
      expect(ok.status, path).toBe(200);
    }
    expect(nest.appRequests().map((q) => q.path)).toEqual(["/api/echo/files?path=a%20b&x=1", "/hub", "/hub/negotiate?negotiateVersion=1", "/api/hello"]);
  });

  it("refuses anything but api/* and the hub, and api/relay/* in every spelling, with 404 not_found", async () => {
    const { h, nest, app } = await setup();
    const refused = [
      "",
      "index.html",
      "signin",
      "web/api/state",
      "auth/hosted/start",
      "api",
      "hubs",
      "API/health",
      "api/relay",
      "api/relay/status",
      "api/relay/devices",
      "api/Relay/devices",
      "api/RELAY/status",
      "api/%72elay/devices",
      "api/relay%2Fdevices",
      "api/relay%2fstatus",
      "api//relay/status",
      "api/./relay/status",
      "api/x/../relay/status",
      "api/x/%2e%2e/relay/status",
      "api/relay?x=1",
      "api/%E0%A4%A",
    ];
    for (const path of refused) {
      for (const method of ["GET", "POST", "DELETE"]) {
        const r = await app.call(method, `/n/${nest.nestId}/${path}`, { body: method === "GET" ? undefined : {} });
        expect(r.status, `${method} ${path}`).toBe(404);
        expect(r.json, `${method} ${path}`).toEqual({ error: "not_found", message: "Not found." });
      }
    }
    expect(nest.requests).toHaveLength(0);
    expect(nest.refused).toHaveLength(0);
    // The rule on its own.
    expect(relayAllowed("api/files")).toBe(true);
    expect(relayAllowed("api/relays")).toBe(true);
    expect(relayAllowed("hub")).toBe(true);
    expect(relayAllowed("hub/x")).toBe(true);
    expect(relayAllowed("api/relay")).toBe(false);
    expect(relayAllowed("api/re%6Cay/devices")).toBe(false);
    expect(h.world.routerRequests.filter((q) => new URL(q.url).pathname.toLowerCase().includes("relay"))).toHaveLength(0);
  });

  it("an unknown, removed or own-domain nest is 404 nest_unknown", async () => {
    const { h, b, nest, app } = await setup();
    const own = await linkNest(h, b);
    for (const id of ["garbage", "nst_testnest000000000000000001", own.nestId, "NST_x"]) {
      const r = await app.call("GET", `/n/${id}/api/health`);
      expect(r.status, id).toBe(404);
      expect(r.json, id).toEqual(UNKNOWN);
    }
    expect((await app.call("GET", "/n")).json).toEqual(UNKNOWN);
    expect((await app.call("GET", "/n/")).json).toEqual(UNKNOWN);
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}`)).status).toBe(204);
    expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).json).toEqual(UNKNOWN);
  });

  it("a server that cannot be reached is 503 nest_offline JSON: link error, cloudflared's 502 page, router without its link", async () => {
    const { h, nest, app } = await setup();
    nest.running = false;
    const thrown = await app.call("GET", `/n/${nest.nestId}/api/health`);
    expect(thrown.status).toBe(503);
    expect(thrown.json).toEqual(OFFLINE);
    expect(thrown.headers.get("content-type")).toBe("application/json");

    h.world.linkFailure = "502";
    const page = await app.call("GET", `/n/${nest.nestId}/api/health`);
    expect(page.status).toBe(503);
    expect(page.json).toEqual(OFFLINE);

    nest.running = true;
    h.cf.routerBindings = [];
    const unknownToRouter = await app.call("GET", `/n/${nest.nestId}/api/health`);
    expect(unknownToRouter.status).toBe(503);
    expect(unknownToRouter.json).toEqual(OFFLINE);
    // None of that made the pending server active.
    expect(await h.query("SELECT status FROM nests")).toEqual([{ status: "pending" }]);
  });

  it("the server's own answers pass as they are: its JSON 503, its 404, a redirect not followed", async () => {
    const { nest, app } = await setup();
    const busy = await app.call("PUT", `/n/${nest.nestId}/api/busy`, { body: {} });
    expect(busy.status).toBe(503);
    expect(busy.json).toEqual({ code: "busy", message: "Too many uploads are in progress. Try again later." });
    const missing = await app.call("GET", `/n/${nest.nestId}/api/nothing-here`);
    expect(missing.status).toBe(404);
    expect(missing.json).toEqual({ code: "not-found" });
    const moved = await app.call("GET", `/n/${nest.nestId}/api/redirect`);
    expect(moved.status).toBe(302);
    expect(moved.location).toBe("/api/elsewhere");
    expect(nest.appRequests().map((r) => r.path)).toEqual(["/api/busy", "/api/nothing-here", "/api/redirect"]);
  });

  it("the first answer makes a pending server active, once", async () => {
    const { h, nest, app } = await setup();
    expect(await h.query("SELECT status, confirmed_at FROM nests")).toEqual([{ status: "pending", confirmed_at: null }]);
    const first = h.clock.now;
    expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).text).toBe("ok");
    h.advance(120);
    expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).text).toBe("ok");
    expect(await h.query("SELECT status, confirmed_at FROM nests")).toEqual([{ status: "active", confirmed_at: first }]);
    expect(await h.query("SELECT event FROM audit WHERE event = 'server_active'")).toHaveLength(1);
  });

  it("needs no Origin, reads no cookie, and streams bodies of any type and size (no 16 KiB limit, no JSON rule)", async () => {
    const { b, nest } = await setup();
    // A browser that is signed in: its cookie neither helps nor travels, and a foreign Origin does not matter.
    const big = "x".repeat(2 * 1024 * 1024);
    const r = await b.call("PUT", `/n/${nest.nestId}/api/echo/upload`, { rawBody: big, contentType: "application/octet-stream", origin: "https://evil.example" });
    expect(r.status).toBe(200);
    expect(r.json.length).toBe(big.length);
    expect(r.json.headers.cookie).toBeUndefined();
    expect(r.json.headers["content-type"]).toBe("application/octet-stream");
    const none = await b.call("DELETE", `/n/${nest.nestId}/api/echo/x`, { origin: null });
    expect(none.status).toBe(200);
  });

  it("answers are inert for a browser: no cookies, no Clear-Site-Data, no CORS, a sandbox CSP, no-store", async () => {
    const { h, b, nest, app } = await setup();
    const r = await app.call("GET", `/n/${nest.nestId}/api/page`);
    expect(r.status).toBe(200);
    expect(r.text).toBe("<script>steal()</script>");
    expect(r.setCookies).toEqual([]);
    expect(r.headers.get("clear-site-data")).toBeNull();
    expect(r.headers.get("access-control-allow-origin")).toBeNull();
    expect(r.headers.get("content-security-policy")).toBe(RELAY_CSP);
    expect(RELAY_CSP).toContain("sandbox");
    expect(r.headers.get("x-frame-options")).toBe("DENY");
    expect(r.headers.get("x-content-type-options")).toBe("nosniff");
    expect(r.headers.get("cache-control")).toBe("no-store");
    expect(r.headers.get("x-nest-header")).toBe("kept");
    // The browser's account session is untouched by it.
    const before = b.cookies.get("__Host-pn_id");
    await b.call("GET", `/n/${nest.nestId}/api/page`);
    expect(b.cookies.get("__Host-pn_id")).toBe(before);
    expect((await b.call("GET", "/v1/me")).status).toBe(200);
    // Relay errors carry the same headers.
    const err = await app.call("GET", "/n/garbage/api/x");
    expect(err.headers.get("content-security-policy")).toBe(RELAY_CSP);
    expect(h.world.routerRequests.length).toBeGreaterThan(0);
  });

  it("WebSocket upgrades pass through to the hub", async () => {
    const { h, nest } = await setup();
    const ctx = createExecutionContext();
    const resp = await h.worker.fetch!(
      new Request(`${ORIGIN}/n/${nest.nestId}/hub?id=abc`, { headers: { Upgrade: "websocket", "CF-Connecting-IP": "198.51.100.80" } }) as Request<
        unknown,
        IncomingRequestCfProperties
      >,
      h.env,
      ctx,
    );
    expect(resp.status).toBe(101);
    const ws = resp.webSocket!;
    expect(ws).toBeTruthy();
    ws.accept();
    const reply = new Promise<string>((resolve) => ws.addEventListener("message", (ev) => resolve(String(ev.data))));
    ws.send("ping");
    expect(await reply).toBe("echo:ping");
    ws.close();
    await waitOnExecutionContext(ctx);
    expect(nest.requests.at(-1)!.path).toBe("/hub?id=abc");
  });

  it(`allows more than 600 requests a minute from one address (the binding allows ${RELAY_PER_MINUTE}); no D1 write per request`, async () => {
    const { h, b, nest } = await setup();
    expect(h.env.RELAY_LIMIT, "wrangler.toml's binding is in the test runtime").toBeDefined();
    // Two computers in one home share this address; 700 requests is about 350 small files synced between them.
    const app = h.client("198.51.100.99");
    for (let i = 0; i < 700; i++) expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).status).toBe(200);
    expect(await h.query("SELECT * FROM rate_counters WHERE bucket LIKE 'relay:%'")).toEqual([]);
    // The account pages keep their own 300 a minute, which the relay does not use up.
    expect((await h.client("198.51.100.99").call("GET", "/assets/style.css")).status).toBe(200);
    expect(b).toBeTruthy();
  }, 120_000);

  it("wrangler.toml's RELAY_LIMIT binding has the limit and period the code and docs name", () => {
    const toml = wranglerToml.replace(/\r\n/g, "\n");
    // The lines between `[[ratelimits]]` and the next table.
    const block = toml.split("\n[[ratelimits]]\n")[1]?.split("\n[")[0] ?? "";
    expect(block).toMatch(/^name = "RELAY_LIMIT"$/m);
    expect(block).toMatch(/^namespace_id = "[1-9][0-9]*"$/m);
    expect(block).toMatch(new RegExp(`^simple = \\{ limit = ${RELAY_PER_MINUTE}, period = ${RELAY_LIMIT_PERIOD} \\}$`, "m"));
    expect(RELAY_PER_MINUTE).toBe(6000);
    expect(RELAY_LIMIT_PERIOD).toBe(60);
  });

  it("a refusal from the binding is 429 rate_limited with Retry-After 60, keyed on the address, before any lookup", async () => {
    const limiter = new FakeLimit();
    const seen: string[] = [];
    const { h, nest, app } = await setup({ RELAY_LIMIT: limiter.binding(), DB: countingDb(seen) });
    expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).status).toBe(200);
    limiter.answer = false;
    const lookups = nestLookups(seen);
    const routed = h.world.routerRequests.length;
    const r = await app.call("POST", `/n/${nest.nestId}/api/echo/x`, { body: {} });
    expect(r.status).toBe(429);
    expect(r.json).toEqual({ error: "rate_limited", message: "Too many requests. Wait a little and try again." });
    expect(r.headers.get("retry-after")).toBe("60");
    expect(r.headers.get("content-security-policy")).toBe(RELAY_CSP);
    expect(limiter.keys).toEqual(["198.51.100.80", "198.51.100.80"]);
    expect(nestLookups(seen)).toBe(lookups);
    expect(h.world.routerRequests.length).toBe(routed);
    // Another address has its own count.
    limiter.answer = true;
    expect((await h.client("198.51.100.98").call("GET", `/n/${nest.nestId}/api/health`)).status).toBe(200);
    expect(limiter.keys.at(-1)).toBe("198.51.100.98");
  });

  it("without the binding (a local run) the relay does not limit, warns once and never falls back to D1", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => undefined);
    try {
      const seen: string[] = [];
      const { nest, app } = await setup({ RELAY_LIMIT: undefined, DB: countingDb(seen) });
      const before = seen.length;
      for (let i = 0; i < 3; i++) expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).status).toBe(200);
      expect(warn.mock.calls.filter((c) => String(c[0]).includes("RELAY_LIMIT"))).toHaveLength(1);
      expect(seen.slice(before).filter((q) => q.includes("rate_counters"))).toHaveLength(0);
    } finally {
      warn.mockRestore();
    }
  });

  it("a limiter that fails lets the request through (the limit only stops floods)", async () => {
    const warn = vi.spyOn(console, "warn").mockImplementation(() => undefined);
    try {
      const broken = { limit: () => Promise.reject(new Error("limiter down")) } as unknown as RateLimit;
      const { nest, app } = await setup({ RELAY_LIMIT: broken });
      expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).status).toBe(200);
      expect(warn.mock.calls.some((c) => String(c[0]).includes("rate limiter failed"))).toBe(true);
    } finally {
      warn.mockRestore();
    }
  });

  it(`reads the nest row once per ${NEST_CACHE_SECONDS} s, and a pending server is marked active once`, async () => {
    const seen: string[] = [];
    const { h, nest, app } = await setup({ DB: countingDb(seen) });
    const before = seen.length;
    for (let i = 0; i < 5; i++) expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).text).toBe("ok");
    h.advance(NEST_CACHE_SECONDS - 1);
    expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).text).toBe("ok");
    const relayed = seen.slice(before);
    expect(nestLookups(relayed)).toBe(1);
    expect(relayed.filter((q) => q.startsWith("UPDATE nests SET status = 'active'"))).toHaveLength(1);
    expect(relayed.filter((q) => q.includes("rate_counters"))).toHaveLength(0);
    expect(await h.query("SELECT status FROM nests")).toEqual([{ status: "active" }]);
    // Then it is read again.
    h.advance(1);
    expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).text).toBe("ok");
    expect(nestLookups(seen.slice(before))).toBe(2);
  });

  it(`a removed server: relayed for at most ${NEST_CACHE_SECONDS} s from the cache, and 404 at once when the router dropped it`, async () => {
    const { h, b, nest, app } = await setup();
    expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).text).toBe("ok");
    // Removed by another Worker instance whose router deploy has not gone out yet: still relayed from the cache...
    await h.env.DB.prepare("UPDATE nests SET status = 'broken' WHERE id = ?1").bind(nest.nestId).run();
    expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).text).toBe("ok");
    // ...until the cached row is 30 s old.
    h.advance(NEST_CACHE_SECONDS);
    expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).json).toEqual(UNKNOWN);

    // Removed, and the router no longer has its link: the cached row is read again and the answer is nest_unknown.
    const other = await addServer(h, b, { label: "other", ip: "198.51.100.41" });
    expect((await app.call("GET", `/n/${other.nestId}/api/health`)).text).toBe("ok");
    const deploys = h.cf.routerDeploys.length;
    await h.env.DB.prepare("DELETE FROM nests WHERE id = ?1").bind(other.nestId).run();
    h.cf.routerBindings = h.cf.routerBindings.filter((x) => x.name !== `N_${other.nestId}`);
    expect((await app.call("GET", `/n/${other.nestId}/api/health`)).json).toEqual(UNKNOWN);
    expect(h.cf.routerDeploys.length).toBe(deploys); // no self-heal for a server that is gone
  });

  it("when the router does not know a linked server, self-heal runs with a freshly read row", async () => {
    const seen: string[] = [];
    const { h, nest, app } = await setup({ DB: countingDb(seen) });
    h.advance(121); // past SELF_HEAL_AFTER of the deploy that added it
    expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).text).toBe("ok");
    const lookups = nestLookups(seen);
    const deploys = h.cf.routerDeploys.length;
    h.cf.routerBindings = []; // a code deploy of the router cleared its links
    const r = await app.call("GET", `/n/${nest.nestId}/api/health`);
    expect(r.status).toBe(503);
    expect(r.json).toEqual(OFFLINE);
    expect(nestLookups(seen)).toBe(lookups + 1);
    expect(h.cf.routerDeploys.length).toBe(deploys + 1);
    expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).text).toBe("ok");
  });
});

/** A stand-in for the Workers Rate Limiting binding that answers what the test says and records the keys. */
class FakeLimit {
  answer = true;
  keys: string[] = [];
  binding(): RateLimit {
    return {
      limit: async (options: RateLimitOptions) => {
        this.keys.push(options.key);
        return { success: this.answer };
      },
    };
  }
}

/** The test D1 with the SQL of every prepared statement recorded in `seen`. */
function countingDb(seen: string[]): D1Database {
  return new Proxy(baseEnv.DB, {
    get(target, prop) {
      if (prop === "prepare") {
        return (sql: string) => {
          seen.push(sql);
          return target.prepare(sql);
        };
      }
      const v = Reflect.get(target, prop, target) as unknown;
      return typeof v === "function" ? (v as (...a: unknown[]) => unknown).bind(target) : v;
    },
  });
}

/** How many times the relay read a nest row. */
function nestLookups(seen: string[]): number {
  return seen.filter((q) => q.startsWith("SELECT id, account_id, status, routed_version FROM nests")).length;
}
