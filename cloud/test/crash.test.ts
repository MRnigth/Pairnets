import { describe, expect, it } from "vitest";
import { ALL_PER_DAY, CRASH_KEEP_DAYS, MAILS_PER_DAY, PER_ADDRESS_PER_HOUR } from "../src/routes/crash";
import { DAY } from "../src/ratelimit";
import { Harness } from "./helpers";

const ADMIN = "boss@example.com";

function card(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: "3f9a1c0e72b4",
    app: "Windows app",
    version: "1.0.60",
    system: "Microsoft Windows 10.0.26100 (X64), .NET 8.0.10",
    place: "Background",
    error: "System.NullReferenceException",
    message: "Object reference not set to an instance of an object.",
    details: "System.NullReferenceException: Object reference not set\n   at Pairnets.Core.Sync.SyncRunner.RunPassAsync(CancellationToken ct)",
    count: 2,
    firstSeen: "2026-10-20T09:42:00+00:00",
    lastSeen: "2026-10-21T09:42:00+00:00",
    closed: true,
    ...over,
  };
}

describe("POST /v1/crash", () => {
  it("keeps the report without anything about who sent it, and emails the admins about a new kind of crash", async () => {
    const h = await Harness.create({ ADMIN_EMAILS: ADMIN });
    const res = await h.client().call("POST", "/v1/crash", { body: { cards: [card()], note: "I renamed a folder" } });

    expect(res.status).toBe(204);
    const rows = await h.query("SELECT * FROM crash_reports");
    expect(rows).toHaveLength(1);
    expect(Object.keys(rows[0]).sort()).toEqual(
      ["app", "closed", "count", "crash_id", "details", "error", "first_at", "id", "last_at", "message", "notes", "place", "reports", "system", "version"].sort(),
    );
    expect(rows[0]).toMatchObject({ crash_id: "3f9a1c0e72b4", count: 2, reports: 1, closed: 1, notes: JSON.stringify(["I renamed a folder"]) });
    expect(JSON.stringify(rows)).not.toContain("198.51.100.20");
    expect(h.mailer.to(ADMIN)).toHaveLength(1);
    expect(h.mailer.to(ADMIN)[0].subject).toContain("NullReferenceException");
  });

  it("adds the same crash to its row and does not email again", async () => {
    const h = await Harness.create({ ADMIN_EMAILS: ADMIN });
    const app = h.client();
    await app.call("POST", "/v1/crash", { body: { cards: [card()] } });
    await app.call("POST", "/v1/crash", { body: { cards: [card({ count: 3 })], note: "again" } });

    const [row] = await h.query<{ count: number; reports: number; notes: string }>("SELECT count, reports, notes FROM crash_reports");
    expect(row).toEqual({ count: 5, reports: 2, notes: JSON.stringify(["again"]) });
    expect(h.mailer.to(ADMIN)).toHaveLength(1);
  });

  it("refuses bad reports", async () => {
    const h = await Harness.create();
    const app = h.client();
    const bad = [
      {},
      { cards: [] },
      { cards: Array.from({ length: 6 }, (_, i) => card({ id: `00000000000${i}` })) },
      { cards: [card({ id: "../etc" })] },
      { cards: [card({ place: "Somewhere" })] },
      { cards: [card({ details: "x".repeat(9000) })] },
      { cards: [card()], note: "x".repeat(1001) },
    ];
    for (const body of bad) expect((await app.call("POST", "/v1/crash", { body })).status).toBe(400);
    expect((await app.call("POST", "/v1/crash", { rawBody: "x".repeat(70 * 1024) })).status).toBe(413);
    expect(await h.query("SELECT * FROM crash_reports")).toEqual([]);
  });

  it("takes five full cards, larger than other requests may be", async () => {
    const h = await Harness.create();
    const cards = Array.from({ length: 5 }, (_, i) => card({ id: `00000000000${i}`, details: "y".repeat(8000) }));
    expect((await h.client().call("POST", "/v1/crash", { body: { cards } })).status).toBe(204);
    expect(await h.query("SELECT crash_id FROM crash_reports")).toHaveLength(5);
  });

  it("limits each address, and all reports together", async () => {
    const h = await Harness.create();
    const app = h.client("198.51.100.7");
    for (let i = 0; i < PER_ADDRESS_PER_HOUR; i++) expect((await app.call("POST", "/v1/crash", { body: { cards: [card()] } })).status).toBe(204);
    expect((await app.call("POST", "/v1/crash", { body: { cards: [card()] } })).status).toBe(429);
    expect((await h.client("198.51.100.8").call("POST", "/v1/crash", { body: { cards: [card()] } })).status).toBe(204);
    expect(ALL_PER_DAY).toBeGreaterThan(PER_ADDRESS_PER_HOUR);
  });

  it("emails at most a few new kinds of crash a day", async () => {
    const h = await Harness.create({ ADMIN_EMAILS: ADMIN });
    for (let i = 0; i < MAILS_PER_DAY + 3; i++)
      await h.client(`198.51.100.${i + 1}`).call("POST", "/v1/crash", { body: { cards: [card({ id: `00000000001${i}` })] } });
    expect(await h.query("SELECT id FROM crash_reports")).toHaveLength(MAILS_PER_DAY + 3);
    expect(h.mailer.to(ADMIN)).toHaveLength(MAILS_PER_DAY);
  });

  it("the hourly cron deletes reports not seen for 90 days", async () => {
    const h = await Harness.create();
    await h.client().call("POST", "/v1/crash", { body: { cards: [card()] } });
    h.advance(CRASH_KEEP_DAYS * DAY - 60);
    await h.scheduled();
    expect(await h.query("SELECT id FROM crash_reports")).toHaveLength(1);
    h.advance(120);
    await h.scheduled();
    expect(await h.query("SELECT id FROM crash_reports")).toEqual([]);
  });
});

describe("GET /admin/crashes", () => {
  it("shows the reports to an admin only", async () => {
    const h = await Harness.create({ ADMIN_EMAILS: `someone@example.com, ${ADMIN.toUpperCase()}` });
    await h.client().call("POST", "/v1/crash", { body: { cards: [card()], note: "renamed <b>a</b> folder" } });

    expect((await h.browser().call("GET", "/admin/crashes")).location).toBe("/login?next=/admin/crashes");

    const other = h.browser("203.0.113.11");
    await other.signInByEmail("other@example.com");
    expect((await other.call("GET", "/admin/crashes")).status).toBe(404);

    const admin = h.browser("203.0.113.12");
    await admin.signInByEmail(ADMIN);
    const page = await admin.call("GET", "/admin/crashes");
    expect(page.status).toBe(200);
    expect(page.text).toContain("Crash 3f9a1c0e72b4: System.NullReferenceException (Pairnets had to close)");
    expect(page.text).toContain("at Pairnets.Core.Sync.SyncRunner.RunPassAsync");
    expect(page.text).toContain("renamed &lt;b&gt;a&lt;/b&gt; folder");
  });

  it("is nobody's when ADMIN_EMAILS is not set", async () => {
    const h = await Harness.create();
    const b = h.browser();
    await b.signInByEmail(ADMIN);
    expect((await b.call("GET", "/admin/crashes")).status).toBe(404);
  });
});
