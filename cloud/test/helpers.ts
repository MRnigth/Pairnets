// Test harness: the real Worker with a fake mailer, a fake outside world (Google, Turnstile, Resend), a controllable
// clock and keys generated at run time. Nothing here ever reaches the network.

import { createExecutionContext, createScheduledController, waitOnExecutionContext } from "cloudflare:test";
import { env as baseEnv } from "cloudflare:workers";
import { expect } from "vitest";
import { createWorker } from "../src/app";
import { b64urlDecodeStrict, b64urlEncode, b64urlEncodeText, utf8 } from "../src/b64";
import { randomBytes } from "../src/crypto";
import type { Env } from "../src/env";
import type { MailMessage, Mailer } from "../src/mail";
import { signRequest, type NestPurpose } from "../src/nestsig";
import { TURNSTILE_VERIFY_URL } from "../src/turnstile";

export const ORIGIN = "https://id.pairnets.app";
/** 2026-10-09T00:00:00Z, the contract's T. */
export const T0 = 1791504000;

export const GOOGLE_AUTH = "https://google.test/auth";
export const GOOGLE_TOKEN = "https://google.test/token";
export const GOOGLE_JWKS = "https://google.test/jwks";
export const GOOGLE_CLIENT_ID = "test-client.apps.googleusercontent.com";

export class FakeMailer implements Mailer {
  sent: MailMessage[] = [];
  async send(msg: MailMessage): Promise<void> {
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

export interface TestKeys {
  privateJwk: JsonWebKey & { kid: string };
  publicKey: CryptoKey;
  kid: string;
  x: string;
  y: string;
}

export async function makeSigningKey(kid = "test-1"): Promise<TestKeys> {
  const kp = (await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"])) as CryptoKeyPair;
  const jwk = (await crypto.subtle.exportKey("jwk", kp.privateKey)) as JsonWebKey;
  return {
    privateJwk: { kty: "EC", crv: "P-256", x: jwk.x, y: jwk.y, d: jwk.d, kid },
    publicKey: kp.publicKey,
    kid,
    x: jwk.x!,
    y: jwk.y!,
  };
}

export interface Res {
  status: number;
  headers: Headers;
  text: string;
  json: any;
  setCookies: string[];
  location: string | null;
}

async function toRes(resp: Response): Promise<Res> {
  const text = await resp.text();
  let json: unknown = null;
  try {
    json = text ? JSON.parse(text) : null;
  } catch {
    json = null;
  }
  return { status: resp.status, headers: resp.headers, text, json, setCookies: resp.headers.getSetCookie(), location: resp.headers.get("Location") };
}

export interface CallOptions {
  body?: unknown;
  rawBody?: string | Uint8Array;
  /** Origin header; defaults to ORIGIN for POST/PATCH/PUT/DELETE in a browser; null = none. */
  origin?: string | null;
  headers?: Record<string, string>;
  contentType?: string | null;
}

const MUTATING = new Set(["POST", "PATCH", "PUT", "DELETE"]);

export class Harness {
  readonly mailer = new FakeMailer();
  readonly net = new FakeNet();
  readonly clock = { now: T0 };
  readonly worker = createWorker({ mailer: this.mailer, fetch: this.net.fetch, now: () => this.clock.now });
  turnstileAnswer = true;

  private constructor(
    public env: Env,
    public readonly signing: TestKeys,
    public readonly hbMaster: Uint8Array,
  ) {
    this.net.on(TURNSTILE_VERIFY_URL, () => jsonResponse(200, { success: this.turnstileAnswer }));
  }

  static async create(overrides: Partial<Env> = {}): Promise<Harness> {
    const signing = await makeSigningKey("test-1");
    const hbMaster = randomBytes(32);
    const env: Env = {
      ...(baseEnv as unknown as Env),
      DB: baseEnv.DB,
      PUBLIC_ORIGIN: ORIGIN,
      SIGNING_KEY: JSON.stringify(signing.privateJwk),
      HB_MASTER: b64urlEncode(hbMaster),
      COOKIE_KEY: b64urlEncode(randomBytes(32)),
      GOOGLE_CLIENT_SECRET: "test-google-secret",
      TURNSTILE_SECRET: "test-turnstile-secret",
      RESEND_API_KEY: "test-resend-key",
      GOOGLE_CLIENT_ID,
      TURNSTILE_SITE_KEY: "test-site-key",
      MAIL_FROM: "Pairnets <noreply@pairnets.app>",
      EMAIL_DAILY_LIMIT: "80",
      HOSTED_LOGIN_DISABLED: "0",
      GOOGLE_AUTH_URL: GOOGLE_AUTH,
      GOOGLE_TOKEN_URL: GOOGLE_TOKEN,
      GOOGLE_JWKS_URL: GOOGLE_JWKS,
      ...overrides,
    };
    return new Harness(env, signing, hbMaster);
  }

  async fetch(req: Request): Promise<Res> {
    const ctx = createExecutionContext();
    const resp = await this.worker.fetch!(req as Request<unknown, IncomingRequestCfProperties>, this.env, ctx);
    const res = await toRes(resp);
    await waitOnExecutionContext(ctx);
    return res;
  }

  async scheduled(): Promise<void> {
    const ctx = createExecutionContext();
    await this.worker.scheduled!(createScheduledController({ cron: "17 * * * *", scheduledTime: this.clock.now * 1000 }), this.env, ctx);
    await waitOnExecutionContext(ctx);
  }

  /** A browser with its own cookie jar. */
  browser(ip = "203.0.113.10"): Browser {
    return new Browser(this, ip, true);
  }

  /** A server or app: no cookies, no Origin unless given. */
  client(ip = "198.51.100.20"): Browser {
    return new Browser(this, ip, false);
  }

  advance(seconds: number): void {
    this.clock.now += seconds;
  }

  async query<T = Record<string, unknown>>(sql: string, ...params: unknown[]): Promise<T[]> {
    return (await this.env.DB.prepare(sql).bind(...params).all<T>()).results;
  }
}

export class Browser {
  readonly cookies = new Map<string, string>();
  userAgent = "TestBrowser/1.0";

  constructor(
    readonly h: Harness,
    readonly ip: string,
    readonly isBrowser: boolean,
  ) {}

  cookieHeader(): string {
    return [...this.cookies].map(([k, v]) => `${k}=${v}`).join("; ");
  }

  async call(method: string, path: string, opts: CallOptions = {}): Promise<Res> {
    const headers = new Headers(opts.headers ?? {});
    headers.set("CF-Connecting-IP", this.ip);
    if (!headers.has("User-Agent")) headers.set("User-Agent", this.userAgent);
    const origin = opts.origin === undefined ? (this.isBrowser && MUTATING.has(method) ? ORIGIN : null) : opts.origin;
    if (origin !== null) headers.set("Origin", origin);
    if (this.isBrowser && this.cookies.size && !headers.has("Cookie")) headers.set("Cookie", this.cookieHeader());
    let body: BodyInit | undefined;
    if (opts.rawBody !== undefined) body = opts.rawBody;
    else if (opts.body !== undefined) body = JSON.stringify(opts.body);
    if (body !== undefined && opts.contentType !== null) headers.set("Content-Type", opts.contentType ?? "application/json");
    const res = await this.h.fetch(new Request(`${ORIGIN}${path}`, { method, headers, body, redirect: "manual" }));
    for (const c of res.setCookies) {
      const [pair, ...attrs] = c.split(";").map((s) => s.trim());
      const eq = pair.indexOf("=");
      const name = pair.slice(0, eq);
      const value = pair.slice(eq + 1);
      const maxAge = attrs.find((a) => a.toLowerCase().startsWith("max-age="));
      if (maxAge && Number(maxAge.split("=")[1]) <= 0) this.cookies.delete(name);
      else if (this.isBrowser) this.cookies.set(name, value);
    }
    return res;
  }

  /** Signs in by email link: asks for the link, reads it from the fake mailbox, confirms. Returns the account id. */
  async signInByEmail(email: string, next = "/account"): Promise<string> {
    const start = await this.call("POST", "/v1/login/email", { body: { email, turnstile: "token-ok", next } });
    expect(start.status).toBe(202);
    const code = lastEmailCode(this.h, email);
    const done = await this.call("POST", "/v1/login/email/confirm", { body: { code } });
    expect(done.status).toBe(200);
    const me = await this.call("GET", "/v1/me");
    expect(me.status).toBe(200);
    return me.json.accountId as string;
  }
}

export function lastEmailCode(h: Harness, email: string): string {
  const mails = h.mailer.to(email);
  expect(mails.length).toBeGreaterThan(0);
  const m = /\/login\/email#code=([A-Za-z0-9_-]+)/.exec(mails[mails.length - 1].text);
  expect(m).not.toBeNull();
  return m![1];
}

/** The nest side of the signed requests (section 5.2), as the C# nest will do it. */
export class FakeNest {
  lastTs = 0;
  constructor(
    readonly h: Harness,
    readonly nestId: string,
    readonly key: Uint8Array,
    readonly ip = "198.51.100.30",
  ) {}

  nextTs(): number {
    this.lastTs = Math.max(this.h.clock.now, this.lastTs + 1);
    return this.lastTs;
  }

  async send(
    path: string,
    purpose: NestPurpose,
    fields: Record<string, unknown>,
    opts: { ts?: number; tsHeader?: string; body?: string; sig?: string; key?: Uint8Array; nestHeader?: string } = {},
  ): Promise<Res> {
    const ts = opts.ts ?? this.nextTs();
    const body = opts.body ?? JSON.stringify({ v: 1, ts, ...fields });
    const tsHeader = opts.tsHeader ?? String(ts);
    const sig = opts.sig ?? (await signRequest(opts.key ?? this.key, purpose, this.nestId, tsHeader, utf8(body)));
    return this.h.fetch(
      new Request(`${ORIGIN}${path}`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "User-Agent": "pairnets-server/1.0.47",
          "CF-Connecting-IP": this.ip,
          "X-Pairnets-Nest": opts.nestHeader ?? this.nestId,
          "X-Pairnets-Ts": tsHeader,
          "X-Pairnets-Sig": sig,
        },
        body,
      }),
    );
  }

  confirm(serverVersion = "1.0.47"): Promise<Res> {
    return this.send("/v1/nest/confirm", "nc1", { serverVersion });
  }

  unlink(reason: "owner" | "declined" = "owner"): Promise<Res> {
    return this.send("/v1/nest/unlink", "nu1", { reason });
  }

  heartbeat(fields: Partial<{ serverVersion: string; ready: boolean; publicHost: string | null; hostedLogin: boolean }> = {}): Promise<Res> {
    return this.send("/v1/heartbeat", "hb1", {
      serverVersion: "1.0.47",
      ready: true,
      publicHost: "nest.example.com",
      hostedLogin: true,
      ...fields,
    });
  }
}

/** Creates a claim code in the browser (signed in, recent) and redeems it as a nest would. */
export async function claim(
  h: Harness,
  browser: Browser,
  publicUrl = "https://nest.example.com",
  extra: Record<string, unknown> = {},
): Promise<{ res: Res; nest: FakeNest | null; code: string }> {
  const created = await browser.call("POST", "/v1/nests/claim-codes", { body: {} });
  expect(created.status).toBe(201);
  const code = created.json.code as string;
  const res = await h.client().call("POST", "/v1/claim", { body: { code, publicUrl, serverVersion: "1.0.47", ...extra } });
  const nest = res.status === 201 ? new FakeNest(h, res.json.nestId, b64urlDecodeStrict(res.json.heartbeatKey)!) : null;
  return { res, nest, code };
}

/** Claim plus the signed confirm: an active, linked nest. */
export async function linkNest(h: Harness, browser: Browser, publicUrl = "https://nest.example.com"): Promise<FakeNest> {
  const { res, nest } = await claim(h, browser, publicUrl);
  expect(res.status).toBe(201);
  const confirmed = await nest!.confirm();
  expect(confirmed.status).toBe(200);
  return nest!;
}

export function decodeJwsPart(part: string): any {
  return JSON.parse(new TextDecoder().decode(b64urlDecodeStrict(part)!));
}

export { b64urlEncodeText };
