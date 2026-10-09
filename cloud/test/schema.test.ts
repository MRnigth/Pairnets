import { env } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import contract from "../CONTRACT.md?raw";
import migration from "../migrations/0001_init.sql?raw";
import migration2 from "../migrations/0002_relay.sql?raw";
import { b64urlEncode } from "../src/b64";
import { FakeGoogle } from "./fake-google";
import { claim, Harness, lastEmailCode, startServer } from "./helpers";
import { TABLES } from "./setup";

const lf = (s: string) => s.replace(/\r\n/g, "\n");

/** Runs a migration file statement by statement, as wrangler would (comments dropped, split at ";"). */
async function runSql(db: D1Database, sql: string): Promise<void> {
  const statements = lf(sql)
    .replace(/--[^\n]*/g, "")
    .split(";")
    .map((x) => x.trim())
    .filter(Boolean);
  for (const st of statements) await db.prepare(st).run();
}

describe("schema", () => {
  it("migration 0001 is exactly the contract's SQL", () => {
    const section = lf(contract).split("## 7. D1 schema")[1];
    const sql = /```sql\n([\s\S]*?)```/.exec(section)![1];
    expect(lf(migration)).toBe(sql);
  });

  it("migration 0002 keeps every nest of 0001, adds the relay columns, and still cascades", async () => {
    const db = env.DB;
    // Back to a database that only had 0001, with rows in it.
    for (const t of TABLES) await db.prepare(`DROP TABLE IF EXISTS ${t}`).run();
    await runSql(db, migration);
    await db.prepare("INSERT INTO accounts (id, email, created_at, updated_at) VALUES ('acc_testacct000000000000000001', 'you@example.com', 1, 2)").run();
    await db
      .prepare(
        `INSERT INTO nests (id, account_id, label, public_url, status, key_version, hosted_login, created_at, confirmed_at, last_req_ts,
           last_seen_at, last_version, last_ready, last_public_host, last_hosted_login)
         VALUES ('nst_testnest000000000000000001', 'acc_testacct000000000000000001', 'Home', 'https://nest.example.com', 'active', 2, 0, 10, 11,
           12, 13, '1.0.47', 1, 'nest.example.com', 1)`,
      )
      .run();
    await db
      .prepare(
        `INSERT INTO device_logins (id, device_code_hash, user_code, name, status, created_at, expires_at)
         VALUES ('dvl_testlogin00000000000000001', x'00', 'ABCDEFGH', 'Laptop', 'pending', 1, 2)`,
      )
      .run();

    await runSql(db, migration2);

    expect((await db.prepare("SELECT * FROM nests").all()).results).toEqual([
      {
        id: "nst_testnest000000000000000001",
        account_id: "acc_testacct000000000000000001",
        label: "Home",
        public_url: "https://nest.example.com",
        status: "active",
        key_version: 2,
        hosted_login: 0,
        created_at: 10,
        confirmed_at: 11,
        last_req_ts: 12,
        last_seen_at: 13,
        last_version: "1.0.47",
        last_ready: 1,
        last_public_host: "nest.example.com",
        last_hosted_login: 1,
        mode: "url",
        tunnel_id: null,
        routed_version: null,
      },
    ]);
    expect((await db.prepare("SELECT nest_id FROM device_logins").first())!.nest_id).toBeNull();
    expect((await db.prepare("SELECT id, version, lease_id FROM router_state").all()).results).toEqual([{ id: 1, version: 0, lease_id: null }]);
    const indexes = (await db.prepare("SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'nests' AND name NOT LIKE 'sqlite_%' ORDER BY name").all<{ name: string }>()).results;
    expect(indexes.map((i) => i.name)).toEqual(["nests_account", "nests_mode_status", "nests_status_created"]);
    // The wider status list, and still nothing else.
    await db.prepare("UPDATE nests SET status = 'broken'").run();
    await expect(db.prepare("UPDATE nests SET status = 'gone'").run()).rejects.toThrow();
    await expect(db.prepare("UPDATE nests SET mode = 'other'").run()).rejects.toThrow();
    await expect(db.prepare("INSERT INTO router_state (id) VALUES (2)").run()).rejects.toThrow();
    // Deleting the account still takes its nests with it.
    await db.prepare("DELETE FROM accounts").run();
    expect((await db.prepare("SELECT COUNT(*) AS n FROM nests").first<{ n: number }>())!.n).toBe(0);
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

    // A relayed server: the installer's device code, the tunnel token and the nest key (RELAY.md 2).
    const { installer, deviceCode, userCode } = await startServer(h);
    await b.call("POST", "/v1/servers/approve", { body: { userCode, approve: true } });
    h.advance(3);
    const delivered = (await installer.call("POST", "/v1/servers/poll", { body: { deviceCode } })).json;
    const server = h.world.install(delivered);
    secrets.push(deviceCode, delivered.tunnelToken, delivered.nestKey);
    expect((await h.client().call("GET", `/n/${server.nestId}/api/health`)).text).toBe("ok");

    // App sign-in: the device code, the app token and the device key the server made.
    const app = h.client();
    const s = await app.call("POST", "/v1/app/start", { body: { name: "Laptop" } });
    await b.call("POST", "/v1/app/approve", { body: { userCode: s.json.userCode, approve: true, nestId: server.nestId } });
    h.advance(3);
    const done = (await app.call("POST", "/v1/app/poll", { body: { deviceCode: s.json.deviceCode } })).json;
    secrets.push(s.json.deviceCode, done.appToken, done.device.key);

    // The server's own secrets.
    secrets.push(h.env.HB_MASTER, h.env.COOKIE_KEY, h.signing.privateJwk.d!, h.env.CF_API_TOKEN);

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
