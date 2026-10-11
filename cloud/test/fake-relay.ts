// The world behind the relay, all fake: Cloudflare's API (tunnels, ingress, tokens, the router's settings), the router
// Worker (the real code from cloud/router, with fake Workers VPC links made from the bindings that were last PATCHed),
// and the nests themselves.
//
// A fake nest checks the service's signed admin calls (RELAY.md section 4) with its own code, the way the C# nest will:
// nothing in this file uses the Worker's signing, hashing or base64url helpers.

import { NEST_ORIGIN, route as routerRoute } from "../router/src/index";

export const CF_ACCOUNT = "test-account";
export const CF_TOKEN = "test-cf-api-token";

const enc = new TextEncoder();

export function toB64url(b: Uint8Array): string {
  let s = "";
  for (const x of b) s += String.fromCharCode(x);
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** Strict: the alphabet only, and re-encoding gives the same text (no stray bits). */
export function fromB64url(s: string): Uint8Array | null {
  if (!/^[A-Za-z0-9_-]*$/.test(s) || s.length % 4 === 1) return null;
  let bin: string;
  try {
    bin = atob(s.replace(/-/g, "+").replace(/_/g, "/") + "===".slice((s.length + 3) % 4));
  } catch {
    return null;
  }
  const out = Uint8Array.from(bin, (c) => c.charCodeAt(0));
  return toB64url(out) === s ? out : null;
}

async function sha256Hex(bytes: Uint8Array): Promise<string> {
  const d = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes as Uint8Array<ArrayBuffer>));
  return [...d].map((x) => x.toString(16).padStart(2, "0")).join("");
}

function jsonResp(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });
}

/** A tunnel token's shape. Built here, so the repo never holds token-shaped text (scripts/check-secrets.sh). */
export function fakeTunnelToken(tunnelId: string): string {
  return "eyJ" + "hIjoi" + toB64url(enc.encode(`${CF_ACCOUNT}/${tunnelId}`)) + "Q".repeat(24);
}

interface FakeTunnel {
  /** The exact JSON body of the create call. */
  created: Record<string, unknown>;
  name: string;
  ingress: unknown;
  connections: number;
}

export interface FakeBinding {
  type: string;
  name: string;
  tunnel_id: string;
}

/**
 * Cloudflare's API as far as this service uses it. A step listed in `failing` answers 500 ("tunnel", "ingress",
 * "token", "connections", "delete tunnel", "list tunnels", "router"); `network = false` makes every call throw.
 */
export class FakeCloudflare {
  tunnels = new Map<string, FakeTunnel>();
  /** The router's bindings as the last settings PATCH left them. */
  routerBindings: FakeBinding[] = [];
  /** Every settings PATCH, in order (the bindings each one sent). */
  routerDeploys: FakeBinding[][] = [];
  failing = new Set<string>();
  network = true;
  /** Runs inside a router PATCH, before it answers (tests act "while a deploy is in progress"). */
  duringRouterPatch: (() => Promise<void>) | null = null;

  private ok(result: unknown): Response {
    return jsonResp(200, { success: true, errors: [], messages: [], result });
  }

  private error(status: number, code: number): Response {
    return jsonResp(status, { success: false, errors: [{ code, message: "fake error" }], messages: [], result: null });
  }

  /** The tunnel a token belongs to (what cloudflared would connect). */
  tunnelOfToken(token: string): string | null {
    for (const id of this.tunnels.keys()) if (fakeTunnelToken(id) === token) return id;
    return null;
  }

  handle = async (req: Request): Promise<Response> => {
    if (!this.network) throw new TypeError("network down");
    if (req.headers.get("Authorization") !== `Bearer ${CF_TOKEN}`) return this.error(403, 10000);
    const url = new URL(req.url);
    const path = url.pathname.replace("/client/v4", "");
    const acc = `/accounts/${CF_ACCOUNT}/cfd_tunnel`;
    const fails = (step: string) => (this.failing.has(step) ? this.error(500, 10000) : null);
    let m: RegExpExecArray | null;

    if (req.method === "POST" && path === acc) {
      const body = (await req.json()) as Record<string, unknown>;
      const failed = fails("tunnel");
      if (failed) return failed;
      const id = crypto.randomUUID();
      this.tunnels.set(id, { created: body, name: String(body.name), ingress: null, connections: 0 });
      return this.ok({ id, name: body.name });
    }
    if (req.method === "GET" && path === acc) {
      const failed = fails("list tunnels");
      if (failed) return failed;
      const name = url.searchParams.get("name");
      const page = Number(url.searchParams.get("page") ?? "1");
      const per = Number(url.searchParams.get("per_page") ?? "20");
      const all = [...this.tunnels].filter(([, t]) => name === null || t.name === name).map(([id, t]) => ({ id, name: t.name }));
      return this.ok(all.slice((page - 1) * per, page * per));
    }
    if ((m = new RegExp(`^${acc}/([^/]+)/configurations$`).exec(path)) && req.method === "PUT") {
      const body = (await req.json()) as { config?: { ingress?: unknown } };
      const failed = fails("ingress");
      if (failed) return failed;
      const t = this.tunnels.get(m[1]);
      if (!t) return this.error(404, 1003);
      t.ingress = body.config?.ingress;
      return this.ok({ tunnel_id: m[1], version: 1 });
    }
    if ((m = new RegExp(`^${acc}/([^/]+)/token$`).exec(path)) && req.method === "GET") {
      const failed = fails("token");
      if (failed) return failed;
      return this.tunnels.has(m[1]) ? this.ok(fakeTunnelToken(m[1])) : this.error(404, 1003);
    }
    if ((m = new RegExp(`^${acc}/([^/]+)/connections$`).exec(path)) && req.method === "DELETE") {
      const failed = fails("connections");
      if (failed) return failed;
      const t = this.tunnels.get(m[1]);
      if (!t) return this.error(404, 1003);
      t.connections = 0;
      return this.ok(null);
    }
    if ((m = new RegExp(`^${acc}/([^/]+)$`).exec(path)) && req.method === "DELETE") {
      const failed = fails("delete tunnel");
      if (failed) return failed;
      const t = this.tunnels.get(m[1]);
      if (!t) return this.error(404, 1003);
      if (t.connections > 0) return this.error(400, 1022); // active connections: refused
      this.tunnels.delete(m[1]);
      return this.ok({ id: m[1] });
    }
    if (path === `/accounts/${CF_ACCOUNT}/workers/scripts/pairnets-router/settings` && req.method === "PATCH") {
      if (!(req.headers.get("Content-Type") ?? "").startsWith("multipart/form-data")) return this.error(400, 10001);
      const form = await req.formData();
      const part = form.get("settings") as unknown;
      const text = typeof part === "string" ? part : part && typeof (part as Blob).text === "function" ? await (part as Blob).text() : null;
      if (text === null || [...form.keys()].length !== 1) return this.error(400, 10001);
      const settings = JSON.parse(text) as { bindings?: FakeBinding[] };
      if (this.duringRouterPatch) {
        const f = this.duringRouterPatch;
        this.duringRouterPatch = null;
        await f();
      }
      const failed = fails("router");
      if (failed) return failed;
      if (!Array.isArray(settings.bindings)) return this.error(400, 10001);
      // A binding to a tunnel that does not exist is refused, like a bad id would be.
      if (settings.bindings.some((b) => b.type === "vpc_network" && !this.tunnels.has(b.tunnel_id))) return this.error(400, 10021);
      this.routerBindings = settings.bindings;
      this.routerDeploys.push(settings.bindings);
      return this.ok({ bindings: settings.bindings });
    }
    return this.error(404, 7003);
  };
}

export interface FakeDevice {
  id: string;
  name: string;
  system: string | null;
  createdAt: string;
  lastSeen: string | null;
  key: string;
  approvedBy: string;
}

export interface SeenRequest {
  method: string;
  url: string;
  path: string;
  headers: Headers;
  body: string;
}

/** What POST /v1/servers/poll answered (the installer writes it into the nest's settings). */
export interface InstallAnswer {
  nestId: string;
  nestKey: string;
  tunnelToken: string;
  label: string;
  relayUrl: string;
}

/** A nest in relay mode behind its tunnel (RELAY.md section 3), with the admin endpoints of section 4. */
export class FakeNestServer {
  devices = new Map<string, FakeDevice>();
  requests: SeenRequest[] = [];
  /** Admin calls that were refused, with the step that refused them. */
  refused: { path: string; step: string }[] = [];
  private nonces = new Map<string, number>();
  private seq = 0;
  /** Keys of the computers this nest lets in on /api/secret (what its own API would check). */
  deviceKeys = new Set<string>();
  /** cloudflared is running (the link works). */
  running = true;
  serverVersion = "1.0.48";
  freeBytes = 123456789;
  dataBytes = 456;

  constructor(
    readonly nestId: string,
    readonly key: Uint8Array,
    readonly tunnelId: string,
    readonly clock: { now: number },
  ) {}

  async handle(req: Request): Promise<Response> {
    const url = new URL(req.url);
    const bytes = req.body ? new Uint8Array(await req.arrayBuffer()) : new Uint8Array(0);
    const body = new TextDecoder().decode(bytes);
    this.requests.push({ method: req.method, url: req.url, path: url.pathname + url.search, headers: req.headers, body });
    if (url.origin !== NEST_ORIGIN) return jsonResp(421, { code: "wrong-origin" });
    const p = url.pathname;
    if (p === "/hub" && (req.headers.get("Upgrade") ?? "").toLowerCase() === "websocket") {
      // The SignalR hub over a WebSocket: this one echoes every message back.
      const pair = new WebSocketPair();
      const [client, server] = Object.values(pair);
      server.accept();
      server.addEventListener("message", (ev) => server.send(`echo:${String(ev.data)}`));
      return new Response(null, { status: 101, webSocket: client });
    }
    if (p.toLowerCase().startsWith("/api/relay")) return this.admin(req, url, bytes);
    if (p === "/api/health") return new Response("ok", { headers: { "Content-Type": "text/plain" } });
    if (p === "/api/hello") return jsonResp(200, { product: "Pairnets", publicUrl: null, signIn: false, relay: { nestId: this.nestId } });
    if (p === "/api/busy") return jsonResp(503, { code: "busy", message: "Too many uploads are in progress. Try again later." });
    if (p === "/api/page") {
      return new Response("<script>steal()</script>", {
        headers: {
          "Content-Type": "text/html",
          "Set-Cookie": "__Host-pn_id=pcs_planted; Path=/; Secure; HttpOnly",
          "Clear-Site-Data": '"cookies"',
          "Access-Control-Allow-Origin": "*",
          "X-Nest-Header": "kept",
        },
      });
    }
    if (p === "/api/redirect") return new Response(null, { status: 302, headers: { Location: "/api/elsewhere" } });
    if (p === "/api/secret") {
      // Like the nest's own API: only a key it gave out gets in (X-Sync-Token, or Bearer on the hub).
      const key = req.headers.get("x-sync-token") ?? (req.headers.get("authorization") ?? "").replace(/^bearer\s+/i, "");
      return this.deviceKeys.has(key) ? jsonResp(200, { ok: true }) : jsonResp(401, { code: "unauthorized" });
    }
    if (p.startsWith("/api/echo") || p === "/hub" || p.startsWith("/hub/")) {
      return jsonResp(200, { method: req.method, path: url.pathname, query: url.search, headers: Object.fromEntries(req.headers), length: bytes.length, body: body.slice(0, 200) });
    }
    return jsonResp(404, { code: "not-found" });
  }

  /** RELAY.md 4: the checks in their order; any failure is 401 bad_signature with no other detail. */
  private async check(req: Request, url: URL, body: Uint8Array): Promise<string | null> {
    // 1. Relay mode is on and the nest header is this nest.
    if (req.headers.get("X-Pairnets-Nest") !== this.nestId) return "nest";
    // 2. A 43-character strict base64url signature that verifies.
    const sig = req.headers.get("X-Pairnets-Sig") ?? "";
    const mac = sig.length === 43 ? fromB64url(sig) : null;
    if (!mac || mac.length !== 32) return "sig-format";
    const ts = req.headers.get("X-Pairnets-Ts") ?? "";
    const nonce = req.headers.get("X-Pairnets-Nonce") ?? "";
    const message = `ra1\n${this.nestId}\n${ts}\n${nonce}\n${req.method.toUpperCase()} ${url.pathname}${url.search}\n${await sha256Hex(body)}`;
    const key = await crypto.subtle.importKey("raw", this.key as Uint8Array<ArrayBuffer>, { name: "HMAC", hash: "SHA-256" }, false, ["verify"]);
    if (!(await crypto.subtle.verify("HMAC", key, mac as Uint8Array<ArrayBuffer>, enc.encode(message)))) return "sig";
    // 3. Within 120 s of the nest's clock.
    if (!/^[0-9]{1,16}$/.test(ts) || Math.abs(this.clock.now - Number(ts)) > 120) return "clock";
    // 4. A nonce not seen in the last 10 minutes (at most 10,000 kept, oldest dropped first).
    for (const [n, at] of this.nonces) if (at < this.clock.now - 600) this.nonces.delete(n);
    if (this.nonces.has(nonce)) return "nonce";
    this.nonces.set(nonce, this.clock.now);
    if (this.nonces.size > 10000) this.nonces.delete(this.nonces.keys().next().value!);
    return null;
  }

  private async admin(req: Request, url: URL, bytes: Uint8Array): Promise<Response> {
    const refused = await this.check(req, url, bytes);
    if (refused) {
      this.refused.push({ path: url.pathname, step: refused });
      return jsonResp(401, { error: "bad_signature" });
    }
    if (bytes.length > 4096) return jsonResp(413, { error: "too_large" });
    const p = url.pathname;
    if (req.method === "GET" && p === "/api/relay/status") {
      return jsonResp(200, { serverVersion: this.serverVersion, devices: this.devices.size, freeBytes: this.freeBytes, dataBytes: this.dataBytes });
    }
    if (req.method === "GET" && p === "/api/relay/devices") {
      return jsonResp(200, {
        devices: [...this.devices.values()].map((d) => ({ id: d.id, name: d.name, system: d.system, createdAt: d.createdAt, lastSeen: d.lastSeen })),
      });
    }
    if (req.method === "POST" && p === "/api/relay/devices") {
      const b = JSON.parse(new TextDecoder().decode(bytes)) as { name: string; system: string | null; approvedBy: string };
      let name = b.name;
      for (let i = 2; [...this.devices.values()].some((d) => d.name === name); i++) name = `${b.name} (${i})`;
      const id = `dev${++this.seq}`;
      const key = "pn_" + toB64url(crypto.getRandomValues(new Uint8Array(32)));
      this.devices.set(id, {
        id,
        name,
        system: b.system,
        createdAt: new Date(this.clock.now * 1000).toISOString(),
        lastSeen: null,
        key,
        approvedBy: `approved by ${b.approvedBy} on sync.pairnets.app`,
      });
      return jsonResp(200, { id, name, key });
    }
    const m = /^\/api\/relay\/devices\/([^/]+)$/.exec(p);
    if (req.method === "DELETE" && m) {
      return this.devices.delete(decodeURIComponent(m[1])) ? jsonResp(200, { removed: true }) : jsonResp(404, { error: "not_found" });
    }
    return jsonResp(404, { error: "not_found" });
  }

  /** Requests that reached the nest outside /api/relay/ (what apps sent through the relay). */
  appRequests(): SeenRequest[] {
    return this.requests.filter((r) => !r.path.startsWith("/api/relay/"));
  }
}

/** The router as pairnets-sync's ROUTER binding reaches it, and the nests behind their tunnels. */
export class RelayWorld {
  nests: FakeNestServer[] = [];
  /** How a link fails when its tunnel has no running nest: an exception, or a 502 page like cloudflared's. */
  linkFailure: "throw" | "502" = "throw";
  /** Requests the router received (headers as pairnets-sync sent them). */
  routerRequests: { url: string; method: string; headers: Headers }[] = [];

  constructor(
    readonly cf: FakeCloudflare,
    readonly clock: { now: number },
  ) {}

  /** The ROUTER service binding. */
  binding(): Fetcher {
    return { fetch: (input: RequestInfo | URL, init?: RequestInit) => this.routerFetch(new Request(input, init)) } as unknown as Fetcher;
  }

  private routerFetch(req: Request): Promise<Response> {
    this.routerRequests.push({ url: req.url, method: req.method, headers: new Headers(req.headers) });
    const env: Record<string, unknown> = {};
    for (const b of this.cf.routerBindings) {
      if (b.type === "vpc_network") env[b.name] = { fetch: (i: RequestInfo | URL, init?: RequestInit) => this.linkFetch(b.tunnel_id, new Request(i, init)) };
    }
    return routerRoute(req, env);
  }

  private async linkFetch(tunnelId: string, req: Request): Promise<Response> {
    const nest = this.cf.tunnels.has(tunnelId) ? this.nests.find((n) => n.tunnelId === tunnelId && n.running) : undefined;
    if (!nest) {
      if (this.linkFailure === "502") return new Response("<html><body>502 Bad Gateway</body></html>", { status: 502, headers: { "Content-Type": "text/html" } });
      throw new Error("connection refused");
    }
    return nest.handle(req);
  }

  /** The installer's last steps: write the settings it was given, start the nest and cloudflared. */
  install(answer: InstallAnswer): FakeNestServer {
    const tunnelId = this.cf.tunnelOfToken(answer.tunnelToken);
    if (!tunnelId) throw new Error("the installer got a token for no tunnel");
    const key = fromB64url(answer.nestKey);
    if (!key || key.length !== 32) throw new Error("the nest key is not 32 bytes of strict base64url");
    const nest = new FakeNestServer(answer.nestId, key, tunnelId, this.clock);
    this.nests.push(nest);
    this.cf.tunnels.get(tunnelId)!.connections = 2;
    return nest;
  }

  nest(nestId: string): FakeNestServer {
    const n = this.nests.find((x) => x.nestId === nestId);
    if (!n) throw new Error(`no fake nest ${nestId}`);
    return n;
  }
}
