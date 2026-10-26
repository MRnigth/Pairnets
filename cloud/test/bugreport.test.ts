import { describe, expect, it } from "vitest";
import { ALL_PER_DAY, MAILS_PER_DAY, MAX_REPORT, PER_ADDRESS_PER_HOUR, REPORT_KEEP_DAYS } from "../src/routes/bugreport";
import { DAY } from "../src/ratelimit";
import { Harness } from "./helpers";

const ADMIN = "boss@example.com";

const REPORT = [
  "Pairnets bug report",
  "=================",
  "App:       Windows app 1.0.61",
  "## Settings (the token is never included)",
  "Server:              <url>",
  "Folder:              <path>",
  "## Log (last 300 lines of pairnets-20261022.log)",
  "2026-10-22 10:34:10.343 [WRN] Pairnets.Engine: Pass blocked (FolderEmpty)",
].join("\n");

function report(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    app: "Windows app",
    version: "1.0.61",
    system: "Microsoft Windows 10.0.26100 (X64), .NET 8.0.10",
    report: REPORT,
    note: "The folder went empty after I moved it",
    ...over,
  };
}

describe("POST /v1/bugreport", () => {
  it("keeps the report without anything about who sent it, and emails the admins", async () => {
    const h = await Harness.create({ ADMIN_EMAILS: ADMIN });
    const res = await h.client().call("POST", "/v1/bugreport", { body: report() });

    expect(res.status).toBe(204);
    const rows = await h.query("SELECT * FROM bug_reports");
    expect(rows).toHaveLength(1);
    expect(Object.keys(rows[0]).sort()).toEqual(["app", "build", "channel", "created_at", "id", "note", "report", "system", "version"]);
    expect(rows[0]).toMatchObject({ app: "Windows app", version: "1.0.61", report: REPORT, note: "The folder went empty after I moved it" });
    expect(JSON.stringify(rows)).not.toContain("198.51.100.20");
    const mails = h.mailer.to(ADMIN);
    expect(mails).toHaveLength(1);
    expect(mails[0].subject).toBe(`New Pairnets bug report #${rows[0].id} from the Windows app 1.0.61`);
    expect(mails[0].text).toContain("The folder went empty after I moved it");
    expect(mails[0].text).toContain("/admin/reports");
  });

  it("keeps every report on its own row, with or without a note", async () => {
    const h = await Harness.create({ ADMIN_EMAILS: ADMIN });
    const app = h.client();
    expect((await app.call("POST", "/v1/bugreport", { body: report({ note: null }) })).status).toBe(204);
    expect((await app.call("POST", "/v1/bugreport", { body: report({ note: "   " }) })).status).toBe(204);
    expect((await app.call("POST", "/v1/bugreport", { body: report({ note: undefined, app: "Mac app" }) })).status).toBe(204);

    const rows = await h.query<{ app: string; note: string | null }>("SELECT app, note FROM bug_reports ORDER BY id");
    expect(rows).toEqual([
      { app: "Windows app", note: null },
      { app: "Windows app", note: null },
      { app: "Mac app", note: null },
    ]);
    expect(h.mailer.to(ADMIN)).toHaveLength(3);
    expect(h.mailer.to(ADMIN)[0].text).toContain("They did not add a note.");
  });

  it("refuses bad reports", async () => {
    const h = await Harness.create();
    const app = h.client();
    const bad = [
      {},
      report({ report: "" }),
      report({ report: "   " }),
      report({ report: 42 }),
      report({ report: "x".repeat(MAX_REPORT + 1) }),
      report({ version: "../etc" }),
      report({ version: undefined }),
      report({ app: "" }),
      report({ app: "x".repeat(41) }),
      report({ system: undefined }),
      report({ note: "x".repeat(1001) }),
      report({ note: 5 }),
    ];
    for (const body of bad) expect((await app.call("POST", "/v1/bugreport", { body })).status, JSON.stringify(body).slice(0, 80)).toBe(400);
    expect((await app.call("POST", "/v1/bugreport", { rawBody: "x".repeat(300 * 1024) })).status).toBe(413);
    expect(await h.query("SELECT * FROM bug_reports")).toEqual([]);
  });

  it("takes a full report, larger than other requests may be, with its line breaks and letters", async () => {
    const h = await Harness.create();
    const full = ("Mappe på skrivebordet <path> ø æ å\n").repeat(Math.floor(MAX_REPORT / 36)).slice(0, MAX_REPORT).trim();
    expect((await h.client().call("POST", "/v1/bugreport", { body: report({ report: full }) })).status).toBe(204);
    const [row] = await h.query<{ report: string }>("SELECT report FROM bug_reports");
    expect(row.report).toBe(full);
  });

  it("takes out hidden characters but keeps the line breaks", async () => {
    const h = await Harness.create();
    await h.client().call("POST", "/v1/bugreport", { body: report({ report: "one\r\ntwo‮\u0007\nthree" }) });
    const [row] = await h.query<{ report: string }>("SELECT report FROM bug_reports");
    expect(row.report).toBe("one\ntwo\nthree");
  });

  it("limits each address, and all reports together", async () => {
    const h = await Harness.create();
    const app = h.client("198.51.100.7");
    for (let i = 0; i < PER_ADDRESS_PER_HOUR; i++) expect((await app.call("POST", "/v1/bugreport", { body: report() })).status).toBe(204);
    expect((await app.call("POST", "/v1/bugreport", { body: report() })).status).toBe(429);
    expect((await h.client("198.51.100.8").call("POST", "/v1/bugreport", { body: report() })).status).toBe(204);
    expect(ALL_PER_DAY).toBeGreaterThan(PER_ADDRESS_PER_HOUR);
  });

  it("emails at most a few reports a day", async () => {
    const h = await Harness.create({ ADMIN_EMAILS: ADMIN });
    for (let i = 0; i < MAILS_PER_DAY + 3; i++) await h.client(`198.51.100.${i + 1}`).call("POST", "/v1/bugreport", { body: report() });
    expect(await h.query("SELECT id FROM bug_reports")).toHaveLength(MAILS_PER_DAY + 3);
    expect(h.mailer.to(ADMIN)).toHaveLength(MAILS_PER_DAY);
  });

  it("the hourly cron deletes reports older than 90 days", async () => {
    const h = await Harness.create();
    await h.client().call("POST", "/v1/bugreport", { body: report() });
    h.advance(REPORT_KEEP_DAYS * DAY - 60);
    await h.scheduled();
    expect(await h.query("SELECT id FROM bug_reports")).toHaveLength(1);
    h.advance(120);
    await h.scheduled();
    expect(await h.query("SELECT id FROM bug_reports")).toEqual([]);
  });
});

describe("GET /admin/reports", () => {
  it("shows the reports to an admin only", async () => {
    const h = await Harness.create({ ADMIN_EMAILS: `someone@example.com, ${ADMIN.toUpperCase()}` });
    await h.client().call("POST", "/v1/bugreport", { body: report({ note: "it broke <b>again</b>" }) });

    expect((await h.browser().call("GET", "/admin/reports")).location).toBe("/login?next=/admin/reports");

    const other = h.browser("203.0.113.11");
    await other.signInByEmail("other@example.com");
    expect((await other.call("GET", "/admin/reports")).status).toBe(404);

    const admin = h.browser("203.0.113.12");
    await admin.signInByEmail(ADMIN);
    const page = await admin.call("GET", "/admin/reports");
    expect(page.status).toBe(200);
    expect(page.text).toContain("Bug report #");
    expect(page.text).toContain("Windows app 1.0.61");
    expect(page.text).toContain("Pass blocked (FolderEmpty)");
    expect(page.text).toContain("Server:              &lt;url&gt;");
    expect(page.text).toContain("it broke &lt;b&gt;again&lt;/b&gt;");
    expect(page.text).toContain(`href="/admin/crashes"`);
    // ... and the crash page links back.
    expect((await admin.call("GET", "/admin/crashes")).text).toContain(`href="/admin/reports"`);
  });

  it("is nobody's when ADMIN_EMAILS is not set", async () => {
    const h = await Harness.create();
    const b = h.browser();
    await b.signInByEmail(ADMIN);
    expect((await b.call("GET", "/admin/reports")).status).toBe(404);
  });
});
