// Test harness: the real Worker with a fake mailer, a fake Cloudflare API that keeps its own tunnels and DNS records,
// a controllable clock and secrets made up at run time. Nothing here ever reaches the network.

import { createExecutionContext, createScheduledController, waitOnExecutionContext } from "cloudflare:test";
import { env as baseEnv } from "cloudflare:workers";
import { expect } from "vitest";
import { createWorker } from "../src/app";
import { CF_API } from "../src/cloudflare";
import { b64urlEncode, randomBytes } from "../src/crypto";
import type { Env } from "../src/env";
import type { MailMessage, Mailer } from "../src/mail";

export const ORIGIN = "https://names.pairnets.app";
/** 2026-10-09T00:00:00Z. */
export const T0 = 1791504000;
export const ACCOUNT_ID = "test-account";
export const ZONE_ID = "test-zone";
export const API_TOKEN = "test-cf-api-token";
export const IP = "203.0.113.10";

/** A tunnel token's shape. Built here, so the repo never holds token-shaped text (scripts/check-secrets.sh). */
export function fakeTunnelToken(tunnelId: string, version: number): string {
  return "eyJ" + "hIjoi" + b64urlEncode(new TextEncoder().encode(`${tunnelId}/${version}`)) + "Q".repeat(24);
}

export class FakeMailer implements Mailer {
  sent: MailMessage[] = [];
  failing = false;
  async send(msg: MailMessage): Promise<void> {
    if (this.failing) throw new Error("Resend answered 500");
    this.sent.push(msg);
  }
  to(addr: string): MailMessage[] {
    return this.sent.filter((m) => m.to === addr);
  }
}

type NetHandler = (req: Request) => Response | Promise<Response>;

export interface NetCall {
  url: string;
  method: string;
  headers: Headers;
  body: string;
}

/** The outside world. Any request without a handler fails the test. */
export class FakeNet {
  private routes: [string, NetHandler][] = [];
  calls: NetCall[] = [];
  on(prefix: string, handler: NetHandler): void {
    this.routes.unshift([prefix, handler]);
  }
  callsTo(prefix: string): NetCall[] {
    return this.calls.filter((c) => c.url.startsWith(prefix));
  }
  fetch: typeof fetch = async (input, init) => {
    const req = new Request(input as RequestInfo, init as RequestInit);
    const body = req.body ? new TextDecoder().decode(await req.clone().arrayBuffer()) : "";
    this.calls.push({ url: req.url, method: req.method, headers: req.headers, body });
    for (const [prefix, h] of this.routes) if (req.url.startsWith(prefix)) return h(req);
    throw new Error(`unexpected outbound request: ${req.method} ${req.url}`);
  };
}

export function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

interface FakeTunnel {
  name: string;
  configSrc: unknown;
  version: number;
  ingress: unknown;
  connections: number;
}

interface FakeRecord {
  type: unknown;
  name: string;
  content: unknown;
  proxied: unknown;
}

/**
 * Cloudflare's API as far as this service uses it. A step listed in `failing` answers 500 ("tunnel", "token",
 * "ingress", "dns", "rotate", "connections", "delete tunnel", "delete dns"); `network` makes every call throw.
 */
export class FakeCloudflare {
  tunnels = new Map<string, FakeTunnel>();
  records = new Map<string, FakeRecord>();
  failing = new Set<string>();
  network = true;
  private seq = 0;

  private ok(result: unknown): Response {
    return jsonResponse(200, { success: true, errors: [], messages: [], result });
  }

  private error(status: number, code: number): Response {
    return jsonResponse(status, { success: false, errors: [{ code, message: "fake error" }], messages: [], result: null });
  }

  private fails(step: string): Response | null {
    return this.failing.has(step) ? this.error(500, 10000) : null;
  }

  /** Every tunnel gets a running cloudflared (a nest that is up). */
  connectAll(): void {
    for (const t of this.tunnels.values()) t.connections = 2;
  }

  /** Puts a record in the zone that this service did not make (the name is then not free). */
  addForeignRecord(name: string): void {
    this.records.set(this.newRecordId(), { type: "A", name, content: "192.0.2.1", proxied: true });
  }

  private newRecordId(): string {
    return (++this.seq).toString(16).padStart(32, "0");
  }

  handle = async (req: Request): Promise<Response> => {
    if (!this.network) throw new TypeError("network down");
    if (req.headers.get("Authorization") !== `Bearer ${API_TOKEN}`) return this.error(403, 10000);
    const path = new URL(req.url).pathname.replace("/client/v4", "");
    const body = req.method === "GET" || req.method === "DELETE" ? null : ((await req.json()) as Record<string, unknown>);
    const acc = `/accounts/${ACCOUNT_ID}/cfd_tunnel`;
    const zone = `/zones/${ZONE_ID}/dns_records`;
    let m: RegExpExecArray | null;

    if (req.method === "POST" && path === acc) {
      const failed = this.fails("tunnel");
      if (failed) return failed;
      const id = crypto.randomUUID();
      this.tunnels.set(id, { name: String(body!.name), configSrc: body!.config_src, version: 1, ingress: null, connections: 0 });
      return this.ok({ id, name: body!.name, config_src: body!.config_src });
    }
    if ((m = new RegExp(`^${acc}/([^/]+)/token$`).exec(path)) && req.method === "GET") {
      const failed = this.fails("token");
      if (failed) return failed;
      const t = this.tunnels.get(m[1]);
      return t ? this.ok(fakeTunnelToken(m[1], t.version)) : this.error(404, 1003);
    }
    if ((m = new RegExp(`^${acc}/([^/]+)/configurations$`).exec(path)) && req.method === "PUT") {
      const failed = this.fails("ingress");
      if (failed) return failed;
      const t = this.tunnels.get(m[1]);
      if (!t) return this.error(404, 1003);
      t.ingress = (body!.config as { ingress: unknown }).ingress;
      return this.ok({ tunnel_id: m[1], version: 1 });
    }
    if ((m = new RegExp(`^${acc}/([^/]+)/connections$`).exec(path)) && req.method === "DELETE") {
      const failed = this.fails("connections");
      if (failed) return failed;
      const t = this.tunnels.get(m[1]);
      if (!t) return this.error(404, 1003);
      t.connections = 0;
      return this.ok(null);
    }
    if ((m = new RegExp(`^${acc}/([^/]+)$`).exec(path)) && req.method === "PATCH") {
      const failed = this.fails("rotate");
      if (failed) return failed;
      const t = this.tunnels.get(m[1]);
      if (!t) return this.error(404, 1003);
      t.version++;
      return this.ok({ id: m[1], name: t.name });
    }
    if ((m = new RegExp(`^${acc}/([^/]+)$`).exec(path)) && req.method === "DELETE") {
      const failed = this.fails("delete tunnel");
      if (failed) return failed;
      if (!this.tunnels.has(m[1])) return this.error(404, 1003);
      if (this.tunnels.get(m[1])!.connections > 0) return this.error(400, 1022); // active connections: refused
      this.tunnels.delete(m[1]);
      return this.ok({ id: m[1] });
    }
    if (req.method === "POST" && path === zone) {
      const failed = this.fails("dns");
      if (failed) return failed;
      const name = String(body!.name);
      if ([...this.records.values()].some((r) => r.name === name)) return this.error(400, 81053);
      const id = this.newRecordId();
      this.records.set(id, { type: body!.type, name, content: body!.content, proxied: body!.proxied });
      return this.ok({ id, name });
    }
    if ((m = new RegExp(`^${zone}/([^/]+)$`).exec(path)) && req.method === "DELETE") {
      const failed = this.fails("delete dns");
      if (failed) return failed;
      if (!this.records.delete(m[1])) return this.error(404, 81044);
      return this.ok({ id: m[1] });
    }
    return this.error(404, 7003);
  };
}

export interface Res {
  status: number;
  headers: Headers;
  text: string;
  json: any;
}

async function toRes(resp: Response): Promise<Res> {
  const text = await resp.text();
  let json: unknown = null;
  try {
    json = text ? JSON.parse(text) : null;
  } catch {
    json = null;
  }
  return { status: resp.status, headers: resp.headers, text, json };
}

export interface CallOptions {
  body?: unknown;
  rawBody?: string;
  ip?: string;
  headers?: Record<string, string>;
  contentType?: string | null;
}

export interface Claimed {
  name: string;
  publicUrl: string;
  token: string;
  key: string;
}

export class Harness {
  readonly mailer = new FakeMailer();
  readonly net = new FakeNet();
  readonly cf = new FakeCloudflare();
  readonly clock = { now: T0 };
  readonly worker = createWorker({ mailer: this.mailer, fetch: this.net.fetch, now: () => this.clock.now });

  private constructor(public env: Env) {
    this.net.on(CF_API, this.cf.handle);
  }

  static async create(overrides: Partial<Env> = {}): Promise<Harness> {
    const env: Env = {
      ...(baseEnv as unknown as Env),
      DB: baseEnv.DB,
      CF_API_TOKEN: API_TOKEN,
      CF_ACCOUNT_ID: ACCOUNT_ID,
      CF_ZONE_ID: ZONE_ID,
      HASH_KEY: b64urlEncode(randomBytes(32)),
      RESEND_API_KEY: "test-resend-key",
      BASE_DOMAIN: "pairnets.app",
      MAIL_FROM: "Pairnets <noreply@pairnets.app>",
      MAX_NAMES: "180",
      DAILY_NAMES: "40",
      MAIL_DAILY_LIMIT: "40",
      NAMES_PAUSED: "0",
      ...overrides,
    };
    return new Harness(env);
  }

  async fetch(req: Request): Promise<Res> {
    const ctx = createExecutionContext();
    const resp = await this.worker.fetch!(req as Request<unknown, IncomingRequestCfProperties>, this.env, ctx);
    const res = await toRes(resp);
    await waitOnExecutionContext(ctx);
    return res;
  }

  /** A request as the installer's curl makes it. */
  call(method: string, path: string, opts: CallOptions = {}): Promise<Res> {
    const headers = new Headers(opts.headers ?? {});
    headers.set("CF-Connecting-IP", opts.ip ?? IP);
    let body: string | undefined;
    if (opts.rawBody !== undefined) body = opts.rawBody;
    else if (opts.body !== undefined) body = JSON.stringify(opts.body);
    if (body !== undefined && opts.contentType !== null) headers.set("Content-Type", opts.contentType ?? "application/json");
    return this.fetch(new Request(`${ORIGIN}${path}`, { method, headers, body }));
  }

  async scheduled(): Promise<void> {
    const ctx = createExecutionContext();
    await this.worker.scheduled!(createScheduledController({ cron: "23 * * * *", scheduledTime: this.clock.now * 1000 }), this.env, ctx);
    await waitOnExecutionContext(ctx);
  }

  advance(seconds: number): void {
    this.clock.now += seconds;
  }

  async query<T = Record<string, unknown>>(sql: string, ...params: unknown[]): Promise<T[]> {
    return (await this.env.DB.prepare(sql).bind(...params).all<T>()).results;
  }

  start(name: string, email = "you@example.com", ip = IP): Promise<Res> {
    return this.call("POST", "/v1/claim/start", { body: { name, email }, ip });
  }

  /** The code in the newest email to this address. */
  lastCode(email = "you@example.com"): string {
    const mails = this.mailer.to(email);
    expect(mails.length).toBeGreaterThan(0);
    const m = /\n {4}([0-9]{6})\n/.exec(mails[mails.length - 1].text);
    expect(m).not.toBeNull();
    return m![1];
  }

  finish(claimId: string, code: string, ip = IP): Promise<Res> {
    return this.call("POST", "/v1/claim", { body: { claim_id: claimId, code }, ip });
  }

  /** Start plus the emailed code: the whole claim, which must work. */
  async claim(name = "alice", email = "you@example.com", ip = IP): Promise<Claimed> {
    const started = await this.start(name, email, ip);
    expect(started.status, started.text).toBe(202);
    const done = await this.finish(started.json.claim_id, this.lastCode(email), ip);
    expect(done.status, done.text).toBe(201);
    return { name: done.json.name, publicUrl: done.json.public_url, token: done.json.tunnel_token, key: done.json.manage_key };
  }

  manage(method: "POST" | "DELETE", path: string, name: string, key: string | null, ip = IP): Promise<Res> {
    return this.call(method, path, { body: { name }, ip, headers: key === null ? {} : { Authorization: `Bearer ${key}` } });
  }

  /** Every value in every table, as one string (to check that no secret was stored). */
  async everythingStored(): Promise<string> {
    const parts: string[] = [];
    for (const t of ["names", "claims", "rate_counters", "events"]) parts.push(JSON.stringify(await this.query(`SELECT * FROM ${t}`)));
    return parts.join("\n");
  }
}
