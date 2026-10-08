import { describe, expect, it } from "vitest";
import contract from "../CONTRACT.md?raw";
import migration from "../migrations/0001_init.sql?raw";
import { b64urlEncode } from "../src/b64";
import { FakeGoogle } from "./fake-google";
import { claim, Harness, lastEmailCode } from "./helpers";
import { TABLES } from "./setup";

const lf = (s: string) => s.replace(/\r\n/g, "\n");

describe("schema", () => {
  it("migration 0001 is exactly the contract's SQL", () => {
    const section = lf(contract).split("## 7. D1 schema")[1];
    const sql = /```sql\n([\s\S]*?)```/.exec(section)![1];
    expect(lf(migration)).toBe(sql);
  });

  it("no secret is stored in clear", async () => {
    const h = await Harness.create();
    const secrets: string[] = [];

    // Email sign-in: the emailed code, the binding cookie, the session cookie.
    const b = h.browser();
    await b.call("POST", "/v1/login/email", { body: { email: "you@example.com", turnstile: "ok" } });
    secrets.push(b.cookies.get("__Host-pn_el")!);
    const code = lastEmailCode(h, "you@example.com");
    secrets.push(code);
    await b.call("POST", "/v1/login/email/confirm", { body: { code } });
    secrets.push(b.cookies.get("__Host-pn_id")!);

    // Google sign-in: the flow cookie (state, nonce, verifier) and that session.
    const g = await FakeGoogle.install(h);
    g.claims = { email: "you@example.com" };
    const gb = h.browser("203.0.113.5");
    const start = await gb.call("GET", "/login/google");
    const flowCookie = gb.cookies.get("__Host-pn_g")!;
    secrets.push(flowCookie, ...flowCookie.split("."));
    const auth = new URL(start.location!);
    g.nonce = auth.searchParams.get("nonce")!;
    secrets.push(auth.searchParams.get("state")!, g.nonce);
    await gb.call("GET", `/login/google/callback?state=${auth.searchParams.get("state")}&code=c`);
    secrets.push(g.lastTokenRequest!.get("code_verifier")!, gb.cookies.get("__Host-pn_id")!);

    // Claim: the code in every form, the heartbeat key.
    const { res, nest, code: claimCode } = await claim(h, b);
    secrets.push(claimCode, claimCode.replace(/-/g, "").slice(2), res.json.heartbeatKey);
    await nest!.confirm();
    await nest!.heartbeat();

    // Nest login: the assertion and its parts.
    const login = await b.call("GET", `/nest-login?nest=${nest!.nestId}&nonce=TestNonce000000000000A&return=${encodeURIComponent("https://nest.example.com/hosted-return")}`);
    const jws = login.location!.split("#assertion=")[1];
    secrets.push(jws, ...jws.split("."));

    // App sign-in: the device code and the app token.
    const app = h.client();
    const s = await app.call("POST", "/v1/app/start", { body: { name: "Laptop" } });
    await b.call("POST", "/v1/app/approve", { body: { userCode: s.json.userCode, approve: true } });
    const token = (await app.call("POST", "/v1/app/poll", { body: { deviceCode: s.json.deviceCode } })).json.appToken;
    secrets.push(s.json.deviceCode, token);

    // The server's own secrets.
    secrets.push(h.env.HB_MASTER, h.env.COOKIE_KEY, h.signing.privateJwk.d!);

    expect(secrets.every((x) => typeof x === "string" && x.length >= 12)).toBe(true);
    const dump: string[] = [];
    for (const t of TABLES) {
      const rows = await h.query<Record<string, unknown>>(`SELECT * FROM ${t}`);
      expect(rows.length, t).toBeGreaterThan(0);
      for (const row of rows) {
        for (const v of Object.values(row)) {
          if (Array.isArray(v) || v instanceof ArrayBuffer) dump.push(b64urlEncode(new Uint8Array(v as number[])));
          else dump.push(String(v));
        }
      }
    }
    const all = dump.join("\n");
    for (const secret of secrets) expect(all.includes(secret), `stored in clear: ${secret.slice(0, 8)}...`).toBe(false);
    // Hash columns are 32-byte BLOBs.
    const hashes = await h.query<{ token_hash: number[] }>("SELECT token_hash FROM sessions");
    for (const r of hashes) expect(r.token_hash.length).toBe(32);
    // Rate-limit buckets hold no raw IP or address.
    const buckets = (await h.query<{ bucket: string }>("SELECT bucket FROM rate_counters")).map((r) => r.bucket).join("\n");
    expect(buckets).not.toContain("203.0.113");
    expect(buckets).not.toContain("you@example.com");
    for (const bucket of buckets.split("\n")) expect(bucket).toMatch(/^[a-z-]+:[A-Za-z0-9_-]{22}$/);
  });
});
