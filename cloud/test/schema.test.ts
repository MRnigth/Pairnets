import { env } from "cloudflare:workers";
import { describe, expect, it } from "vitest";
import contract from "../CONTRACT.md?raw";
import migration from "../migrations/0001_init.sql?raw";
import migration2 from "../migrations/0002_relay.sql?raw";
import migration3 from "../migrations/0003_username.sql?raw";
import migration4 from "../migrations/0004_request_place.sql?raw";
import migration5 from "../migrations/0005_inactive.sql?raw";
import migration6 from "../migrations/0006_friends.sql?raw";
import migration7 from "../migrations/0007_links.sql?raw";
import migration8 from "../migrations/0008_crash_reports.sql?raw";
import migration9 from "../migrations/0009_bug_reports.sql?raw";
import migration10 from "../migrations/0010_terms.sql?raw";
import migration11 from "../migrations/0011_link_sign_in.sql?raw";
import { b64urlEncode } from "../src/b64";
import { FakeGoogle } from "./fake-google";
import { addServer, appSignIn, claim, Harness, lastEmailCode, startServer } from "./helpers";
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
    // The database is shared by the tests after this one: bring it back to the full schema.
    await runSql(db, migration3);
    await runSql(db, migration4);
    await runSql(db, migration5);
    await runSql(db, migration6);
    await runSql(db, migration7);
    for (const m of [migration8, migration9, migration10, migration11]) await runSql(db, m);
  });

  it("migration 0004 adds the nullable place columns to device_logins and keeps its rows", async () => {
    const db = env.DB;
    for (const t of TABLES) await db.prepare(`DROP TABLE IF EXISTS ${t}`).run();
    await runSql(db, migration);
    await runSql(db, migration2);
    await runSql(db, migration3);
    await db
      .prepare(
        `INSERT INTO device_logins (id, device_code_hash, user_code, name, status, created_at, expires_at)
         VALUES ('dvl_testlogin00000000000000001', x'00', 'ABCDEFGH', 'Laptop', 'pending', 1, 2)`,
      )
      .run();
    await runSql(db, migration4);
    expect(await db.prepare("SELECT name, request_country, request_city FROM device_logins").first()).toEqual({
      name: "Laptop",
      request_country: null,
      request_city: null,
    });
    for (const m of [migration5, migration6, migration7, migration8, migration9, migration10, migration11]) await runSql(db, m);
  });

  it("migration 0005 adds the nullable inactive_warned_at to accounts and keeps its rows", async () => {
    const db = env.DB;
    for (const t of TABLES) await db.prepare(`DROP TABLE IF EXISTS ${t}`).run();
    for (const m of [migration, migration2, migration3, migration4]) await runSql(db, m);
    await db.prepare("INSERT INTO accounts (id, email, created_at, updated_at) VALUES ('acc_testacct000000000000000001', 'you@example.com', 1, 2)").run();
    await runSql(db, migration5);
    expect(await db.prepare("SELECT email, inactive_warned_at FROM accounts").first()).toEqual({ email: "you@example.com", inactive_warned_at: null });
    for (const m of [migration6, migration7, migration8, migration9, migration10, migration11]) await runSql(db, m);
  });

  it("migration 0006 adds the friends' tables and keeps every row of 0005", async () => {
    const db = env.DB;
    for (const t of TABLES) await db.prepare(`DROP TABLE IF EXISTS ${t}`).run();
    for (const m of [migration, migration2, migration3, migration4, migration5]) await runSql(db, m);
    await db.prepare("INSERT INTO accounts (id, email, created_at, updated_at) VALUES ('acc_testacct000000000000000001', 'you@example.com', 1, 2)").run();
    await db
      .prepare(
        `INSERT INTO nests (id, account_id, label, public_url, status, created_at, mode) VALUES
           ('nst_testnest000000000000000001', 'acc_testacct000000000000000001', 'Home nest', '', 'active', 10, 'relay')`,
      )
      .run();
    await runSql(db, migration6);
    expect((await db.prepare("SELECT id, label FROM nests").all()).results).toEqual([{ id: "nst_testnest000000000000000001", label: "Home nest" }]);
    expect((await db.prepare("SELECT COUNT(*) AS n FROM nest_members").first<{ n: number }>())!.n).toBe(0);
    expect((await db.prepare("SELECT COUNT(*) AS n FROM nest_invites").first<{ n: number }>())!.n).toBe(0);

    // A member: synced defaults to 1, removed_at to NULL; access is "view" or "edit" only.
    await db
      .prepare("INSERT INTO nest_members (nest_id, account_id, access, folders, added_at) VALUES ('nst_testnest000000000000000001', 'acc_testacct000000000000000002', 'view', '[\"Photos\"]', 5)")
      .run();
    expect(await db.prepare("SELECT access, folders, synced, removed_at FROM nest_members").first()).toEqual({ access: "view", folders: '["Photos"]', synced: 1, removed_at: null });
    await expect(
      db.prepare("INSERT INTO nest_members (nest_id, account_id, access, added_at) VALUES ('nst_testnest000000000000000001', 'acc_testacct000000000000000003', 'owner', 5)").run(),
    ).rejects.toThrow();
    await expect(db.prepare("UPDATE nest_members SET synced = 2").run()).rejects.toThrow();
    // One row per person and nest.
    await expect(
      db.prepare("INSERT INTO nest_members (nest_id, account_id, access, added_at) VALUES ('nst_testnest000000000000000001', 'acc_testacct000000000000000002', 'edit', 6)").run(),
    ).rejects.toThrow();

    // An invite: the token is unique, and a decision is "in" or "declined".
    const invite = (id: string, token: string) =>
      db
        .prepare(
          `INSERT INTO nest_invites (id, nest_id, token, access, created_by, created_at, expires_at)
             VALUES (?1, 'nst_testnest000000000000000001', ?2, 'view', 'acc_testacct000000000000000001', 1, 2)`,
        )
        .bind(id, token)
        .run();
    await invite("inv_testinvite0000000000000001", "pni_" + "a".repeat(32));
    await expect(invite("inv_testinvite0000000000000002", "pni_" + "a".repeat(32))).rejects.toThrow();
    await expect(db.prepare("UPDATE nest_invites SET decision = 'maybe'").run()).rejects.toThrow();
    await db.prepare("UPDATE nest_invites SET decision = 'declined', decided_at = 3").run();
    const indexes = (await db.prepare("SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name IN ('nest_members', 'nest_invites') AND name NOT LIKE 'sqlite_%' ORDER BY name").all<{ name: string }>()).results;
    expect(indexes.map((i) => i.name)).toEqual(["nest_invites_expires", "nest_invites_nest", "nest_members_account", "nest_members_synced"]);
    for (const m of [migration7, migration8, migration9, migration10, migration11]) await runSql(db, m); // the full schema again, for the tests after this one
  });

  it("migration 0008 adds crash_reports, with no column for who sent a report, and keeps the accounts", async () => {
    const db = env.DB;
    for (const t of TABLES) await db.prepare(`DROP TABLE IF EXISTS ${t}`).run();
    for (const m of [migration, migration2, migration3, migration4, migration5, migration6]) await runSql(db, m);
    await runSql(db, migration7);
    await db.prepare("INSERT INTO accounts (id, email, created_at, updated_at) VALUES ('acc_testacct000000000000000001', 'you@example.com', 1, 2)").run();
    await runSql(db, migration8);
    const columns = (await db.prepare("PRAGMA table_info(crash_reports)").all<{ name: string }>()).results.map((c) => c.name);
    expect(columns.filter((c) => /ip|account|device|nest|email/.test(c))).toEqual([]);
    expect(await db.prepare("SELECT COUNT(*) AS n FROM accounts").first()).toEqual({ n: 1 });
    await runSql(db, migration9); // the full schema again, for the tests after this one
    await runSql(db, migration10);
    await runSql(db, migration11);
  });

  it("migration 0009 adds bug_reports, with no column for who sent a report, and keeps the crash reports", async () => {
    const db = env.DB;
    for (const t of TABLES) await db.prepare(`DROP TABLE IF EXISTS ${t}`).run();
    for (const m of [migration, migration2, migration3, migration4, migration5, migration6, migration7, migration8]) await runSql(db, m);
    await db
      .prepare(
        `INSERT INTO crash_reports (crash_id, app, version, system, place, error, message, details, closed, count, reports, notes, first_at, last_at)
           VALUES ('3f9a1c0e72b4', 'Windows app', '1.0.61', 'Windows', 'Task', 'System.Exception', 'x', 'y', 0, 1, 1, '[]', 1, 1)`,
      )
      .run();
    await runSql(db, migration9);
    const columns = (await db.prepare("PRAGMA table_info(bug_reports)").all<{ name: string }>()).results.map((c) => c.name);
    expect(columns.sort()).toEqual(["app", "created_at", "id", "note", "report", "system", "version"]);
    expect(columns.filter((c) => /ip|account|device|nest|email/.test(c))).toEqual([]);
    expect(await db.prepare("SELECT COUNT(*) AS n FROM crash_reports").first()).toEqual({ n: 1 });
    await runSql(db, migration10); // the full schema again, for the tests after this one
  });

  it("migration 0010 adds the nullable terms columns to accounts and keeps its rows", async () => {
    const db = env.DB;
    for (const t of TABLES) await db.prepare(`DROP TABLE IF EXISTS ${t}`).run();
    for (const m of [migration, migration2, migration3, migration4, migration5, migration6, migration7, migration8, migration9]) await runSql(db, m);
    await db.prepare("INSERT INTO accounts (id, email, created_at, updated_at) VALUES ('acc_testacct000000000000000001', 'you@example.com', 1, 2)").run();
    await runSql(db, migration10);
    expect(await db.prepare("SELECT email, terms_version, terms_accepted_at FROM accounts").first()).toEqual({ email: "you@example.com", terms_version: null, terms_accepted_at: null });
    await runSql(db, migration11); // the full schema again, for the tests after this one
  });

  it("migration 0011 adds the link sign-in columns: old sign-ins are 'poll', one row per link, and no code in clear", async () => {
    const db = env.DB;
    for (const t of TABLES) await db.prepare(`DROP TABLE IF EXISTS ${t}`).run();
    for (const m of [migration, migration2, migration3, migration4, migration5, migration6, migration7, migration8, migration9, migration10]) await runSql(db, m);
    await db
      .prepare(
        `INSERT INTO device_logins (id, device_code_hash, user_code, name, status, created_at, expires_at)
           VALUES ('dvl_testlogin000000000000000001', x'01', 'ABCD2345', 'Laptop', 'pending', 1, 2)`,
      )
      .run();
    await runSql(db, migration11);
    expect(await db.prepare("SELECT mode, link_hash, code_hash, code_expires_at, tries FROM device_logins").first()).toEqual({
      mode: "poll",
      link_hash: null,
      code_hash: null,
      code_expires_at: null,
      tries: 0,
    });
    await expect(db.prepare("UPDATE device_logins SET mode = 'other'").run()).rejects.toThrow();
    const add = (id: string, userCode: string, link: string | null) =>
      db
        .prepare(`INSERT INTO device_logins (id, device_code_hash, user_code, name, status, created_at, expires_at, mode, link_hash) VALUES (?1, ?2, ?3, 'PC', 'pending', 1, 2, 'code', ?4)`)
        .bind(id, new Uint8Array([id.charCodeAt(id.length - 1), 7]), userCode, link === null ? null : new Uint8Array([9, 9, link.length]))
        .run();
    await add("dvl_testlogin000000000000000002", "ABCD2346", "aa");
    await expect(add("dvl_testlogin000000000000000003", "ABCD2347", "bb")).rejects.toThrow(); // the same link twice
    await add("dvl_testlogin000000000000000004", "ABCD2348", null); // no link: many rows may have none
    await add("dvl_testlogin000000000000000005", "ABCD2349", null);
    const columns = (await db.prepare("PRAGMA table_info(device_logins)").all<{ name: string }>()).results.map((c) => c.name);
    expect(columns.filter((c) => /^(link|code)$/.test(c))).toEqual([]); // only hashes
  });

  it("migration 0007 adds an invite's kind (old invites are for a person) and the links table, and keeps every row of 0006", async () => {
    const db = env.DB;
    for (const t of TABLES) await db.prepare(`DROP TABLE IF EXISTS ${t}`).run();
    for (const m of [migration, migration2, migration3, migration4, migration5, migration6]) await runSql(db, m);
    await db
      .prepare(
        `INSERT INTO nest_invites (id, nest_id, token, access, created_by, created_at, expires_at)
           VALUES ('inv_testinvite0000000000000001', 'nst_testnest000000000000000001', 'pni_${"a".repeat(32)}', 'view', 'acc_testacct000000000000000001', 1, 2)`,
      )
      .run();
    await runSql(db, migration7);
    expect(await db.prepare("SELECT id, kind, joining_nest FROM nest_invites").first()).toEqual({ id: "inv_testinvite0000000000000001", kind: "person", joining_nest: null });
    await expect(db.prepare("UPDATE nest_invites SET kind = 'robot'").run()).rejects.toThrow();
    await db.prepare("UPDATE nest_invites SET kind = 'nest', joining_nest = 'nst_testnest000000000000000002'").run();

    const add = (id: string, to: string, status = "pending") =>
      db
        .prepare("INSERT INTO nest_links (id, from_nest, to_nest, account_id, device_id, status, created_at) VALUES (?1, 'nst_testnest000000000000000001', ?2, 'acc_testacct000000000000000002', NULL, ?3, 1)")
        .bind(id, to, status)
        .run();
    await add("lnk_testlink00000000000000001", "nst_testnest000000000000000002");
    await expect(add("lnk_testlink00000000000000002", "nst_testnest000000000000000002")).rejects.toThrow(); // one link per pair
    await expect(add("lnk_testlink00000000000000003", "nst_testnest000000000000000003", "broken")).rejects.toThrow();
    const indexes = (await db.prepare("SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'nest_links' AND name NOT LIKE 'sqlite_%' ORDER BY name").all<{ name: string }>()).results;
    expect(indexes.map((i) => i.name)).toEqual(["nest_links_account", "nest_links_to"]);
    await db.prepare("INSERT INTO link_ends (link_id, to_nest, created_at) VALUES ('lnk_testlink00000000000000001', 'nst_testnest000000000000000002', 1)").run();
    await expect(db.prepare("INSERT INTO link_ends (link_id, to_nest, created_at) VALUES ('lnk_testlink00000000000000001', 'nst_testnest000000000000000003', 2)").run()).rejects.toThrow();
    for (const m of [migration8, migration9, migration10, migration11]) await runSql(db, m); // the full schema again, for the tests after this one
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
    await b.signInByEmail("you@example.com"); // allowing a computer needs a recent sign-in
    await b.call("POST", "/v1/app/approve", { body: { userCode: s.json.userCode, approve: true, nestId: server.nestId } });
    h.advance(3);
    const done = (await app.call("POST", "/v1/app/poll", { body: { deviceCode: s.json.deviceCode } })).json;
    secrets.push(s.json.deviceCode, done.appToken, done.device.key);

    // A friend: invited, asks to join, let in, and their computer signs in (RELAY.md 10). The invite's token is kept
    // as it is on purpose (using it only makes a request), so it is not on the list; the friend's device key is.
    const friend = h.browser("203.0.113.30");
    await friend.signInByEmail("anna.berg@example.com");
    const inv = await b.call("POST", `/v1/servers/${server.nestId}/invites`, { body: { access: "view", folders: ["Photos"], email: "anna.berg@example.com" } });
    expect(inv.status, inv.text).toBe(201);
    expect((await friend.call("POST", "/v1/invite/request", { body: { t: new URL(inv.json.link).searchParams.get("t") } })).status).toBe(200);
    expect((await b.call("POST", `/v1/servers/${server.nestId}/invites/${inv.json.id}/approve`)).status).toBe(200);
    // One more invite, still open, so its table has a row after the cron-free run.
    expect((await b.call("POST", `/v1/servers/${server.nestId}/invites`, { body: {} })).status).toBe(201);
    const friendApp = await appSignIn(h, friend, server.nestId, "Anna-Laptop", "198.51.100.90");
    secrets.push(friendApp.deviceCode, friendApp.poll.json.appToken, friendApp.poll.json.device.key);
    // A linked nest (RELAY.md 11): the key handed from the friend's nest to the joining one is never kept here.
    const ida = h.browser("203.0.113.31");
    await ida.signInByEmail("ida@example.com");
    const joining = await addServer(h, ida, { label: "Ida's Home", ip: "198.51.100.61" });
    const nestInv = await b.call("POST", `/v1/servers/${server.nestId}/invites`, { body: { kind: "nest", access: "view", folders: ["School"] } });
    expect(nestInv.status, nestInv.text).toBe(201);
    const asked = await ida.call("POST", "/v1/invite/request", { body: { t: new URL(nestInv.json.link).searchParams.get("t"), nestId: joining.nestId } });
    expect(asked.status, asked.text).toBe(200);
    expect((await b.call("POST", `/v1/servers/${server.nestId}/invites/${nestInv.json.id}/approve`)).json.link.status).toBe("active");
    secrets.push([...joining.links.values()][0].key!);
    // A link end still to be told holds only ids, but every table must have a row to look at.
    await h.env.DB.prepare("INSERT INTO link_ends (link_id, to_nest, created_at) VALUES ('lnk_testlink00000000000000009', ?1, 1)").bind(joining.nestId).run();
    // A crash report (it holds no secret, but every table must have a row to look at).
    await app.call("POST", "/v1/crash", {
      body: {
        cards: [{ id: "3f9a1c0e72b4", app: "Windows app", version: "1.0.60", system: "Windows", place: "Task", error: "System.Exception",
          message: "x", details: "at Pairnets.X()", count: 1, closed: false }],
      },
    });

    // A bug report (cleaned on the computer; it holds no secret either).
    await app.call("POST", "/v1/bugreport", {
      body: { app: "Windows app", version: "1.0.61", system: "Windows", report: "Pairnets bug report\nServer: <url>", note: null },
    });

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
