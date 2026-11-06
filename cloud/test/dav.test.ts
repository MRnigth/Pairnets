import { describe, expect, it } from "vitest";
// The Pages function is plain JavaScript on the website's side; the Worker's tests drive it into the Worker itself.
// @ts-ignore no declaration file
import { onRequest as pagesFunction } from "../../site/functions/[[path]].js";
import { keyedHash } from "../src/ratelimit";
import { DAV_HELD_ETAG, DAV_MAX_UPLOAD, DAV_REFUSED_HEADER, RELAY_DEVICE_PER_MINUTE, RELAY_LIMIT_PERIOD, relayAllowed } from "../src/relay";
import type { Env } from "../src/env";
import type { SeenRequest } from "./fake-relay";
import { addServer, Harness } from "./helpers";

const SECRET = "test-dav-edge-secret";
const PASSWORD = "abcdefghjkmnpqrstuvw";
const basic = (password: string, user = "mac-mini") => `Basic ${btoa(`${user}:${password}`)}`;

async function setup(overrides: Partial<Env> = {}) {
  const h = await Harness.create({ DAV_EDGE_SECRET: SECRET, ...overrides });
  const b = h.browser();
  await b.signInByEmail("you@example.com");
  const nest = await addServer(h, b);
  nest.deviceKeys.add(PASSWORD);
  const app = h.client("198.51.100.90");
  return { h, nest, app };
}

async function hmacHex(message: string, secret = SECRET): Promise<string> {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = new Uint8Array(await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(message)));
  return [...mac].map((x) => x.toString(16).padStart(2, "0")).join("");
}

/** The headers the Pages function would add for `segment` (name.id), by independent code. */
async function signed(h: Harness, segment: string, ip: string, ts = h.clock.now, secret = SECRET): Promise<Record<string, string>> {
  return {
    "X-Pairnets-Dav-Ip": ip,
    "X-Pairnets-Dav-Ts": String(ts),
    "X-Pairnets-Dav-Sig": await hmacHex(`${ts}|${ip}|${segment}`, secret),
  };
}

const davRequests = (nest: { appRequests(): SeenRequest[] }) => nest.appRequests().filter((r) => r.path.startsWith("/dav"));

describe("WebDAV through the relay", () => {
  it("passes PROPFIND, MKCOL, MOVE and LOCK on with method, path, headers and body, and a base for the links", async () => {
    const { nest, app } = await setup();
    const auth = { Authorization: basic(PASSWORD) };
    const propfind = '<?xml version="1.0"?><propfind xmlns="DAV:"><allprop/></propfind>';
    const r = await app.call("PROPFIND", `/n/${nest.nestId}/dav/Photos/`, { rawBody: propfind, contentType: "application/xml", headers: { ...auth, Depth: "1" } });
    expect(r.status, r.text).toBe(200);
    expect(r.json).toMatchObject({ method: "PROPFIND", path: "/dav/Photos/", body: propfind });
    expect((r.json.headers as Record<string, string>).depth).toBe("1");
    expect((r.json.headers as Record<string, string>)["x-pairnets-dav-base"]).toBe(`https://sync.pairnets.app/n/${nest.nestId}/dav`);

    for (const [method, path, extra] of [
      ["MKCOL", "/dav/New%20folder", {}],
      ["MOVE", "/dav/a.txt", { Destination: `https://sync.pairnets.app/n/${nest.nestId}/dav/b.txt`, Overwrite: "F" }],
      ["LOCK", "/dav/a.txt", { Timeout: "Second-600" }],
      ["UNLOCK", "/dav/a.txt", { "Lock-Token": "<opaquelocktoken:1>" }],
      ["PROPPATCH", "/dav/a.txt", {}],
      ["COPY", "/dav/a.txt", { Destination: "/dav/c.txt" }],
      ["OPTIONS", "/dav/", {}],
      ["PUT", "/dav/a.txt", {}],
      ["DELETE", "/dav/a.txt", {}],
    ] as const) {
      const body = method === "LOCK" || method === "PUT" ? "<lockinfo/>" : undefined;
      const x = await app.call(method, `/n/${nest.nestId}${path}`, { rawBody: body, contentType: body ? "application/xml" : null, headers: { ...auth, ...extra } });
      expect(x.status, `${method}: ${x.text}`).toBe(200);
      expect(x.json, method).toMatchObject({ method, path, ...(body ? { body } : {}) });
      for (const [k, v] of Object.entries(extra)) expect((x.json.headers as Record<string, string>)[k.toLowerCase()], `${method} ${k}`).toBe(v);
    }
  });

  it("serves /dav/<name>.<id>/ as the nest's dav/: the id routes, the name is only for reading", async () => {
    const { nest, app } = await setup();
    const auth = { Authorization: basic(PASSWORD) };
    for (const name of ["soro", "", "my-nest-2", "x".repeat(40)]) {
      const r = await app.call("PROPFIND", `/dav/${name}.${nest.nestId}/Music/a%20b.mp3?x=1`, { headers: { ...auth, Depth: "0" } });
      expect(r.status, name).toBe(200);
      expect(r.json.path, name).toBe("/dav/Music/a%20b.mp3");
      expect(r.json.query).toBe("?x=1");
      expect((r.json.headers as Record<string, string>)["x-pairnets-dav-base"]).toBe(`https://sync.pairnets.app/dav/${name}.${nest.nestId}`);
    }
    const root = await app.call("PROPFIND", `/dav/soro.${nest.nestId}`, { headers: auth });
    expect(root.json.path).toBe("/dav");
    const slash = await app.call("PROPFIND", `/dav/soro.${nest.nestId}/`, { headers: auth });
    expect(slash.json.path).toBe("/dav/");
  });

  it("answers 404 in plain text for a name or id that is not shaped right, and never reaches the nest", async () => {
    const { nest, app } = await setup();
    const auth = { Authorization: basic(PASSWORD) };
    const bad = [
      `/dav/SORO.${nest.nestId}/`, // capitals
      `/dav/${"x".repeat(41)}.${nest.nestId}/`, // name too long
      `/dav/soro.${nest.nestId}x/`, // id too long
      `/dav/soro.nst_short/`,
      `/dav/soro-${nest.nestId}/`, // no dot
      `/dav/so%72o.${nest.nestId}/`, // encoded letters are not a name
      `/dav/so/ro.${nest.nestId}/`,
      "/dav/",
      "/dav",
      `/dav/soro.${nest.nestId.replace("nst_", "xyz_")}/`,
      "/dav/soro.nst_testnest000000000000000009/", // well-formed, but no such nest
    ];
    for (const path of bad) {
      const r = await app.call("PROPFIND", path, { headers: auth });
      expect(r.status, path).toBe(404);
      expect(r.headers.get("content-type"), path).toMatch(/^text\/plain/);
    }
    expect(davRequests(nest)).toEqual([]);
  });

  it("counts the Basic password as the computer's key: its own budget, the same for any spelling, other links unaffected", async () => {
    const { h, nest, app } = await setup();
    const other = "qrstuvwxyz234567abcd";
    nest.deviceKeys.add(other);
    // 5,999 requests already this minute under this password (counted lower-case and without "-").
    h.limits.device.set(await keyedHash(h.env, `${nest.nestId}\n${PASSWORD}`), RELAY_DEVICE_PER_MINUTE - 1);
    const call = (pw: string) => app.call("PROPFIND", `/dav/soro.${nest.nestId}/`, { headers: { Authorization: basic(pw) } });
    expect((await call(PASSWORD)).status).toBe(200); // the 6,000th
    const over = await call(PASSWORD.toUpperCase());
    expect(over.status).toBe(429);
    expect(over.headers.get("content-type")).toMatch(/^text\/plain/);
    expect(Number(over.headers.get("retry-after"))).toBe(RELAY_LIMIT_PERIOD);
    expect((await call(other)).status).toBe(200);
    // With dashes in it (how a person types it): the same budget.
    const dashed = `${PASSWORD.slice(0, 5)}-${PASSWORD.slice(5, 10)}-${PASSWORD.slice(10)}`;
    expect((await call(dashed)).status).toBe(429);
    h.advance(RELAY_LIMIT_PERIOD);
    expect((await call(PASSWORD)).status).toBe(200);
    // The limiter saw the password only as a keyed hash.
    expect(h.limits.device.keys.some((k) => k.includes(PASSWORD) || k.includes(nest.nestId))).toBe(false);
  });

  it("turns a wrong password away with the nest's own 401 and the Worker's challenge, and counts it against the address", async () => {
    const { h, nest, app } = await setup();
    const path = `/dav/soro.${nest.nestId}/`;
    const none = await app.call("PROPFIND", path, {});
    const wrong = await app.call("PROPFIND", path, { headers: { Authorization: basic("zzzzz-zzzzz-zzzzz-zzzzz") } });
    const notLinkShaped = await app.call("PROPFIND", path, { headers: { Authorization: basic("not-the-password") } });
    const asApp = await app.call("PROPFIND", `/n/${nest.nestId}/dav/`, { headers: { Authorization: basic("not-the-password") } });
    for (const r of [none, wrong, notLinkShaped, asApp]) {
      expect(r.status).toBe(401);
      expect(r.headers.get("www-authenticate")).toBe('Basic realm="Pairnets"');
    }
    // The wrong password was a "key" for the budget, and once the nest refused it, the address pays as well.
    expect(h.limits.fail.keys.length).toBeGreaterThanOrEqual(3);
  });

  it("refuses a PUT announcing more than 25 MB with 413 without passing its body on, tells the nest, and lets a small one through", async () => {
    const { nest, app } = await setup();
    expect(DAV_MAX_UPLOAD).toBe(25 * 1024 * 1024);
    const path = `/dav/soro.${nest.nestId}/big.bin`;
    const auth = { Authorization: basic(PASSWORD) };
    // What the client announces counts (a real client always sends Content-Length), not the bytes the test has at hand.
    const big = await app.call("PUT", path, { rawBody: "x", contentType: "application/octet-stream", headers: { ...auth, "Content-Length": String(DAV_MAX_UPLOAD + 1) } });
    expect(big.status).toBe(413);
    expect(big.json.error).toBe("too_large");
    // The nest hears of it without the body, in a request no nest can take for a change: it drops the empty file the client
    // made first (Finder and Explorer do), which it would otherwise store once nothing more came.
    const told = davRequests(nest);
    expect(told.map((r) => [r.method, r.path, r.body])).toEqual([["PUT", "/dav/big.bin", ""]]);
    expect(told[0].headers.get(DAV_REFUSED_HEADER)).toBe("too_large");
    expect(told[0].headers.get("if-match")).toBe(`"${DAV_HELD_ETAG}"`);
    expect(told[0].headers.get("authorization")).toBe(basic(PASSWORD));
    expect(DAV_HELD_ETAG).toBe("new-empty-file"); // the nest's DavEmptyFiles.ETag
    // A caller's own copy of that header never reaches the nest.
    const small = await app.call("PUT", path, { rawBody: "hello", contentType: "application/octet-stream", headers: { ...auth, [DAV_REFUSED_HEADER]: "too_large" } });
    expect(small.status).toBe(200);
    expect(small.json).toMatchObject({ method: "PUT", body: "hello" });
    expect(davRequests(nest).at(-1)!.headers.get(DAV_REFUSED_HEADER)).toBeNull();
    // Reading a bigger file is not a PUT.
    expect((await app.call("GET", path, { headers: auth })).status).toBe(200);
  });

  it("keeps WebDAV inside dav/ and away from the admin calls, in every spelling", async () => {
    const { nest, app } = await setup();
    const auth = { Authorization: basic(PASSWORD) };
    const paths = [
      `/n/${nest.nestId}/dav/..%2Fapi%2Frelay%2Fstatus`,
      `/n/${nest.nestId}/dav/..%2fapi%2fhello`,
      `/n/${nest.nestId}/dav/%2e%2e%2fapi%2frelay%2fdevices`,
      `/dav/soro.${nest.nestId}/..%2Fapi%2Frelay%2Fstatus`,
      `/dav/soro.${nest.nestId}/..%2F..%2Fhub`,
      `/dav/soro.${nest.nestId}/%zz`,
    ];
    for (const path of paths) {
      const r = await app.call("GET", path, { headers: auth });
      expect(r.status, path).toBe(404);
    }
    expect(nest.appRequests()).toEqual([]);
    expect([relayAllowed("dav/a/b"), relayAllowed("dav"), relayAllowed("dav/"), relayAllowed("api/info"), relayAllowed("davx/a")]).toEqual([true, true, true, true, false]);
    expect([relayAllowed("dav/../api/info"), relayAllowed("dav/..%2Fapi%2Frelay%2Fx"), relayAllowed("api/relay/status"), relayAllowed("dav/%zz")]).toEqual([false, false, false, false]);
  });
});

describe("no sign-in box in this service's name for a nest", () => {
  const PHISH = 'Basic realm="Pairnets sign-in: type your Google password"';

  it("never passes a nest's own challenge, and gives WebDAV clients only the fixed one", async () => {
    const { nest, app } = await setup();
    const segment = `sign-in.${nest.nestId}`;
    for (const challenge of [PHISH, null]) {
      nest.davChallenge = challenge;
      for (const path of [`/dav/${segment}/`, `/n/${nest.nestId}/dav/`]) {
        const r = await app.call("PROPFIND", path, { headers: { Authorization: basic("zzzzzzzzzzzzzzzzzzzz") } });
        expect(r.status, `${path} ${challenge}`).toBe(401);
        expect(r.headers.get("www-authenticate"), `${path} ${challenge}`).toBe('Basic realm="Pairnets"');
      }
    }
    // Outside dav/ no challenge at all, whatever the nest says.
    const api = await app.call("GET", `/n/${nest.nestId}/api/challenge`, {});
    expect(api.status).toBe(401);
    expect(api.headers.get("www-authenticate")).toBeNull();
  });

  it("gives a browser no challenge at all, so it never draws a password box", async () => {
    const { h, nest, app } = await setup();
    nest.davChallenge = PHISH;
    const segment = `sign-in.${nest.nestId}`;
    for (const mode of ["navigate", "no-cors", "cors", "same-origin"]) {
      for (const path of [`/dav/${segment}/`, `/dav/${segment}/photo.jpg`, `/n/${nest.nestId}/dav/`]) {
        const r = await app.call("GET", path, { headers: { "Sec-Fetch-Mode": mode, "Sec-Fetch-Dest": mode === "navigate" ? "document" : "image" } });
        expect(r.status, `${mode} ${path}`).toBe(401);
        expect(r.headers.get("www-authenticate"), `${mode} ${path}`).toBeNull();
      }
    }
    // A browser's request through the website's function, too (the function passes Sec-Fetch-Mode on).
    const SYNC = {
      fetch: async (req: Request) => {
        const r = await h.fetch(req);
        return new Response(r.text, { status: r.status, headers: r.headers });
      },
    };
    const headers = { "Sec-Fetch-Mode": "navigate", "CF-Connecting-IP": "203.0.113.55" };
    const viaSite = await pagesFunction({ request: new Request(`https://pairnets.app/${segment}/`, { headers }), env: { DAV_EDGE_SECRET: SECRET, SYNC }, next: () => new Response("static") });
    expect(viaSite.status).toBe(401);
    expect(viaSite.headers.get("www-authenticate")).toBeNull();
    // The same request from a WebDAV client gets the fixed challenge.
    const client = await pagesFunction({ request: new Request(`https://pairnets.app/${segment}/`, { method: "PROPFIND", headers: { "CF-Connecting-IP": "203.0.113.55" } }), env: { DAV_EDGE_SECRET: SECRET, SYNC }, next: () => new Response("static") });
    expect(client.status).toBe(401);
    expect(client.headers.get("www-authenticate")).toBe('Basic realm="Pairnets"');
  });

  it("passes Authorization on only as the nest reads it: a link-shaped password on dav/, Bearer elsewhere", async () => {
    const { nest, app } = await setup();
    const seen = () => nest.appRequests().at(-1)!.headers.get("authorization");
    // dav/: the link's password, however it is typed, goes on.
    for (const pw of [PASSWORD, PASSWORD.toUpperCase(), "abcde-fghjk-mnpqr-stuvw", "abcde fghjk mnpqr stuvw"]) {
      await app.call("PROPFIND", `/n/${nest.nestId}/dav/`, { headers: { Authorization: basic(pw) } });
      expect(seen(), pw).toBe(basic(pw));
    }
    // dav/: anything else a person types into a sign-in box, or another kind of credential, is dropped.
    for (const auth of [basic("my google password"), basic("hunter2"), basic(PASSWORD + "x"), basic("abcdefghjkmnpqrstuv1"), "Bearer pc_key", "Digest username=x"]) {
      const r = await app.call("PROPFIND", `/dav/soro.${nest.nestId}/`, { headers: { Authorization: auth } });
      expect(r.status, auth).toBe(401);
      expect(seen(), auth).toBeNull();
    }
    // Elsewhere: Bearer goes on (the hub's negotiate uses it), anything else never does.
    const bearer = await app.call("GET", `/n/${nest.nestId}/api/echo`, { headers: { Authorization: "Bearer pc_key" } });
    expect((bearer.json.headers as Record<string, string>).authorization).toBe("Bearer pc_key");
    for (const auth of [basic(PASSWORD), basic("my google password"), "pc_key", "Digest username=x"]) {
      const r = await app.call("GET", `/n/${nest.nestId}/api/echo`, { headers: { Authorization: auth } });
      expect(r.status, auth).toBe(200);
      expect((r.json.headers as Record<string, string>).authorization, auth).toBeUndefined();
    }
  });
});

describe("the address of the caller behind the website's function", () => {
  it("trusts a fresh, correctly signed address, and writes links under pairnets.app", async () => {
    const { h, nest, app } = await setup();
    const segment = `soro.${nest.nestId}`;
    const r = await app.call("PROPFIND", `/dav/${segment}/`, { headers: { Authorization: basic(PASSWORD), ...(await signed(h, segment, "203.0.113.77")) } });
    expect(r.status, r.text).toBe(200);
    const got = r.json.headers as Record<string, string>;
    expect(got["x-pairnets-client-ip"]).toBe("203.0.113.77");
    expect(got["x-pairnets-dav-base"]).toBe(`https://pairnets.app/${segment}`);
    for (const header of ["x-pairnets-dav-ip", "x-pairnets-dav-ts", "x-pairnets-dav-sig"]) expect(got[header], header).toBeUndefined();
    // The per-address limit counts that person, not the function's address.
    expect(h.limits.address.keys.at(-1)).toBe("203.0.113.77");
  });

  it("ignores a forged, stale, unrelated or unsigned address and a base the caller made up", async () => {
    const { h, nest, app } = await setup();
    const segment = `soro.${nest.nestId}`;
    const auth = { Authorization: basic(PASSWORD) };
    const cases: Record<string, Record<string, string>> = {
      "wrong secret": await signed(h, segment, "203.0.113.77", h.clock.now, "not-the-secret"),
      "another nest": await signed(h, `soro.nst_testnest000000000000000009`, "203.0.113.77"),
      "another address": { ...(await signed(h, segment, "203.0.113.78")), "X-Pairnets-Dav-Ip": "203.0.113.77" },
      stale: await signed(h, segment, "203.0.113.77", h.clock.now - 61),
      future: await signed(h, segment, "203.0.113.77", h.clock.now + 61),
      "no signature": { "X-Pairnets-Dav-Ip": "203.0.113.77", "X-Pairnets-Dav-Ts": String(h.clock.now) },
      "bad signature": { ...(await signed(h, segment, "203.0.113.77")), "X-Pairnets-Dav-Sig": "z".repeat(64) },
    };
    for (const [label, extra] of Object.entries(cases)) {
      const r = await app.call("PROPFIND", `/dav/${segment}/`, { headers: { ...auth, ...extra, "X-Pairnets-Dav-Base": "https://evil.example/x" } });
      expect(r.status, label).toBe(200);
      const got = r.json.headers as Record<string, string>;
      expect(got["x-pairnets-client-ip"], label).toBe("198.51.100.90");
      expect(got["x-pairnets-dav-base"], label).toBe(`https://sync.pairnets.app/dav/${segment}`);
    }
    // A base sent along with a good signature is the Worker's, too.
    const good = await app.call("PROPFIND", `/dav/${segment}/`, { headers: { ...auth, ...(await signed(h, segment, "203.0.113.77")), "X-Pairnets-Dav-Base": "https://evil.example/x" } });
    expect((good.json.headers as Record<string, string>)["x-pairnets-dav-base"]).toBe(`https://pairnets.app/${segment}`);
    // Through /n/<id>/dav/ the signature means nothing, and a base is never taken from the caller.
    const direct = await app.call("PROPFIND", `/n/${nest.nestId}/dav/`, { headers: { ...auth, ...(await signed(h, segment, "203.0.113.77")), "X-Pairnets-Dav-Base": "https://evil.example/x" } });
    expect((direct.json.headers as Record<string, string>)["x-pairnets-client-ip"]).toBe("198.51.100.90");
    expect((direct.json.headers as Record<string, string>)["x-pairnets-dav-base"]).toBe(`https://sync.pairnets.app/n/${nest.nestId}/dav`);
  });

  it("ignores signed headers while the Worker has no secret", async () => {
    const { h, nest, app } = await setup({ DAV_EDGE_SECRET: undefined });
    const segment = `soro.${nest.nestId}`;
    const r = await app.call("PROPFIND", `/dav/${segment}/`, { headers: { Authorization: basic(PASSWORD), ...(await signed(h, segment, "203.0.113.77")) } });
    expect((r.json.headers as Record<string, string>)["x-pairnets-client-ip"]).toBe("198.51.100.90");
  });
});

describe("site/functions/[[path]].js on pairnets.app", () => {
  const next = async () => new Response("static page", { status: 200, headers: { "X-From": "static" } });

  function context(url: string, init: RequestInit = {}, env: Record<string, unknown> = {}) {
    return { request: new Request(url, init), env, next };
  }

  it("leaves every other path of the site to the static files, touching nothing", async () => {
    const boom = { fetch: () => Promise.reject(new Error("must not be called")) };
    const env = { SYNC: boom, DAV_EDGE_SECRET: SECRET };
    const paths = [
      "/", "/index.html", "/privacy", "/privacy/", "/terms/", "/faq/", "/help/", "/add/", "/cookies/", "/licenses/", "/security/", "/contact/",
      "/delete-account/", "/guidelines/", "/download/windows", "/get.sh", "/favicon.ico", "/assets/site.css", "/404.html",
      "/soro.nst_short", "/Soro.nst_abcdefghjkmnpqrstvwxyz2345", "/x/soro.nst_abcdefghjkmnpqrstvwxyz2345", "/nst_abcdefghjkmnpqrstvwxyz2345",
      "/soro.nst_abcdefghjkmnpqrstvwxyz2345x", "/dav/", "/n/nst_abcdefghjkmnpqrstvwxyz2345/api/info",
    ];
    for (const path of paths) {
      for (const method of ["GET", "POST"]) {
        const res = await pagesFunction(context(`https://pairnets.app${path}`, { method, body: method === "POST" ? "x" : undefined }, env));
        expect(res.headers.get("X-From"), `${method} ${path}`).toBe("static");
        expect(await res.text()).toBe("static page");
      }
    }
  });

  it("says 503 when the website has no secret yet, and passes a WebDAV request on, signed, when it has", async () => {
    const h = await Harness.create({ DAV_EDGE_SECRET: SECRET });
    const b = h.browser();
    await b.signInByEmail("you@example.com");
    const nest = await addServer(h, b);
    nest.deviceKeys.add(PASSWORD);
    const segment = `soro.${nest.nestId}`;

    const off = await pagesFunction(context(`https://pairnets.app/${segment}/`, { method: "PROPFIND" }, {}));
    expect(off.status).toBe(503);

    const seen: Request[] = [];
    const SYNC = {
      fetch: async (req: Request) => {
        seen.push(req);
        const r = await h.fetch(req);
        return new Response(r.text, { status: r.status, headers: r.headers });
      },
    };
    const body = '<propfind xmlns="DAV:"><prop/></propfind>';
    const realNow = Date.now;
    Date.now = () => h.clock.now * 1000; // the Worker's test clock
    const res = await pagesFunction(
      context(
        `https://pairnets.app/${segment}/Docs/a%20b.txt?x=1`,
        {
          method: "PROPFIND",
          body,
          headers: {
            Authorization: basic(PASSWORD),
            Depth: "1",
            "Content-Type": "application/xml",
            "CF-Connecting-IP": "203.0.113.55",
            "X-Pairnets-Dav-Ip": "6.6.6.6", // a caller's own copies are replaced
            "X-Pairnets-Dav-Base": "https://evil.example/x",
          },
        },
        { SYNC, DAV_EDGE_SECRET: SECRET },
      ),
    );
    Date.now = realNow;
    expect(res.status, await res.clone().text()).toBe(200);
    const echoed = JSON.parse(await res.text());
    expect(echoed).toMatchObject({ method: "PROPFIND", path: "/dav/Docs/a%20b.txt", query: "?x=1", body });
    expect(echoed.headers["x-pairnets-client-ip"]).toBe("203.0.113.55");
    expect(echoed.headers["x-pairnets-dav-base"]).toBe(`https://pairnets.app/${segment}`);
    expect(echoed.headers.depth).toBe("1");
    expect(seen).toHaveLength(1);
    expect(new URL(seen[0].url).pathname).toBe(`/dav/${segment}/Docs/a%20b.txt`);
  });

  it("passes a request made without the service binding to the Worker's public address", async () => {
    const seen: string[] = [];
    const original = globalThis.fetch;
    globalThis.fetch = (async (req: Request) => {
      seen.push(`${req.method} ${req.url}`);
      return new Response("ok");
    }) as typeof fetch;
    try {
      const res = await pagesFunction(context("https://pairnets.app/soro.nst_abcdefghjkmnpqrstvwxyz2345", { method: "MKCOL" }, { DAV_EDGE_SECRET: SECRET }));
      expect(await res.text()).toBe("ok");
    } finally {
      globalThis.fetch = original;
    }
    expect(seen).toEqual(["MKCOL https://sync.pairnets.app/dav/soro.nst_abcdefghjkmnpqrstvwxyz2345"]);
  });
});
