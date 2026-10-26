// Bug reports: POST /v1/bugreport (an app, when the user presses "Send bug report") and GET /admin/reports (the list the
// people named in ADMIN_EMAILS read). A report is the app's "Report a bug" text, cleaned of file, folder and computer
// names on the computer before it is sent, and an optional note; no sign-in, and nothing about who sent it is stored (the
// address only counts toward the limits, as a keyed hash).

import type { Ctx } from "../context";
import { stripHidden } from "../formats";
import { escapeHtml as e, HttpError, htmlResponse } from "../http";
import { sendLater } from "../mail";
import { layout } from "../pages";
import { DAY, enforce, hit, HOUR } from "../ratelimit";
import { jsonBody } from "../request";
import { adminEmails, adminNav, adminOnly, HIDDEN_BUT_NEWLINE, VERSION_RE } from "./crash";

/** The longest report the apps send (BugReportSender.MaxReport), in characters. */
export const MAX_REPORT = 64_000;
export const MAX_NOTE = 1000;
/** The body may be larger than the report: JSON escapes line breaks, "<", ">" and letters like "ø". */
export const MAX_BUGREPORT_BODY = 256 * 1024;
export const PER_ADDRESS_PER_HOUR = 5;
export const ALL_PER_DAY = 300;
/** Bug reports emailed to the admins a day; more still land in the list. */
export const MAILS_PER_DAY = 20;
export const REPORT_KEEP_DAYS = 90;
/** Reports on the admin page, newest first. */
const LIST_LIMIT = 100;

function line(v: unknown, max: number): string {
  if (typeof v !== "string") throw new HttpError(400, "bad_request");
  const s = stripHidden(v).trim();
  if (s.length === 0 || s.length > max) throw new HttpError(400, "bad_request");
  return s;
}

function text(v: unknown, max: number, optional: boolean): string | null {
  if (optional && (v === undefined || v === null)) return null;
  if (typeof v !== "string") throw new HttpError(400, "bad_request");
  const s = v.replace(/\r\n?/g, "\n").replace(HIDDEN_BUT_NEWLINE, "").trim();
  if (s.length > max) throw new HttpError(400, "bad_request");
  if (s.length === 0) {
    if (optional) return null;
    throw new HttpError(400, "bad_request");
  }
  return s;
}

/** The app's update channel (designs/beta-channels/CONTRACT.md); anything else is ignored. */
const CHANNELS = ["stable", "public-beta", "developer-beta", "main"];

/**
 * POST /v1/bugreport: {"app", "version", "system", "report", "note": "…" | null, "channel"?, "build"?}. 204 when kept. An
 * unknown channel or a build that is not a whole number is dropped, not refused.
 */
export async function bugReportRoute(ctx: Ctx): Promise<Response> {
  const body = jsonBody(ctx);
  const version = typeof body.version === "string" && VERSION_RE.test(body.version) ? body.version : null;
  if (!version) throw new HttpError(400, "bad_request");
  const app = line(body.app, 40);
  const system = line(body.system, 200);
  const report = text(body.report, MAX_REPORT, false)!;
  const note = text(body.note, MAX_NOTE, true);
  const channel = typeof body.channel === "string" && CHANNELS.includes(body.channel) ? body.channel : null;
  const build = typeof body.build === "number" && Number.isSafeInteger(body.build) && body.build >= 0 && body.build <= 1e9 ? body.build : null;

  await enforce(ctx, "bugreport", ctx.ipKey, PER_ADDRESS_PER_HOUR, HOUR);
  await enforce(ctx, "bugreport-all", "service", ALL_PER_DAY, DAY);

  const row = await ctx.env.DB.prepare(
    "INSERT INTO bug_reports (app, version, system, report, note, created_at, channel, build) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8) RETURNING id",
  )
    .bind(app, version, system, report, note, ctx.now, channel, build)
    .first<{ id: number }>();
  if (row) await mailAdmins(ctx, row.id, app, version, note);
  return new Response(null, { status: 204 });
}

/** A new bug report: tell the admins, at most MAILS_PER_DAY a day. */
async function mailAdmins(ctx: Ctx, id: number, app: string, version: string, note: string | null): Promise<void> {
  const to = adminEmails(ctx);
  if (to.length === 0) return;
  if ((await hit(ctx, "bugreport-mail", "service", MAILS_PER_DAY, DAY)).limited) return;
  const url = `${ctx.env.PUBLIC_ORIGIN}/admin/reports`;
  const subject = `New Pairnets bug report #${id} from the ${app} ${version}`;
  const said = note ? `They wrote: ${note}` : "They did not add a note.";
  const text = `Someone sent a bug report from the ${app} ${version}.\n${said}\n\nAll bug reports: ${url}\n`;
  const html = `<p>Someone sent a bug report from the ${e(app)} ${e(version)}.<br>${e(said)}</p><p><a href="${e(url)}">All bug reports</a></p>`;
  for (const addr of to) sendLater(ctx, { to: addr, subject, text, html });
}

interface ReportRow {
  id: number;
  app: string;
  version: string;
  system: string;
  report: string;
  note: string | null;
  created_at: number;
  channel: string | null;
  build: number | null;
}

/** " · beta public-beta build 3" for a report from a beta build. */
function betaMark(r: ReportRow): string {
  if (r.channel !== "public-beta" && r.channel !== "developer-beta") return "";
  return ` · <b class="beta">beta</b> ${e(r.channel)}${r.build !== null ? ` build ${r.build}` : ""}`;
}

function when(sec: number): string {
  return new Date(sec * 1000).toISOString().slice(0, 16).replace("T", " ") + " UTC";
}

/** GET /admin/reports: newest first. Only for a signed-in account whose email is in ADMIN_EMAILS; others get 404. */
export async function bugReportListRoute(ctx: Ctx): Promise<Response> {
  const login = await adminOnly(ctx, "/admin/reports");
  if (login) return login;
  const rows = (await ctx.env.DB.prepare(`SELECT * FROM bug_reports ORDER BY created_at DESC, id DESC LIMIT ${LIST_LIMIT}`).all<ReportRow>()).results;
  const items = rows
    .map(
      (r) => `<section class="card">
<h2>Bug report #${r.id} <span class="muted">· ${e(r.app)} ${e(r.version)}${betaMark(r)} · ${e(when(r.created_at))}</span></h2>
<p>${r.note ? `<b>They wrote:</b> ${e(r.note)}` : `<span class="muted">No note.</span>`}</p>
<p class="muted">${e(r.system)}</p>
<details><summary>The report</summary><pre>${e(r.report)}</pre></details>
</section>`,
    )
    .join("\n");
  return htmlResponse(
    200,
    layout(
      "Bug reports",
      `<section class="card"><h1>Bug reports</h1>${adminNav("reports")}<p class="muted">${rows.length} report${rows.length === 1 ? "" : "s"}, newest first${rows.length === LIST_LIMIT ? ` (the newest ${LIST_LIMIT})` : ""}. Reports are deleted after ${REPORT_KEEP_DAYS} days.</p></section>
${items || `<section class="card"><p>No bug reports yet.</p></section>`}`,
    ),
  );
}

/** For tests and the cron: reports older than REPORT_KEEP_DAYS. */
export function bugReportSweep(db: D1Database, now: number): D1PreparedStatement {
  return db.prepare("DELETE FROM bug_reports WHERE created_at < ?1").bind(now - REPORT_KEEP_DAYS * DAY);
}
