// Crash reports: POST /v1/crash (an app, when the user presses "Send crash report") and GET /admin/crashes (the list the
// people named in ADMIN_EMAILS read). A report is the app's cleaned crash cards and an optional note, nothing else: no
// sign-in, and nothing about who sent it is stored (the address only counts toward the limits, as a keyed hash).

import type { Ctx } from "../context";
import { stripHidden } from "../formats";
import { escapeHtml as e, HttpError, htmlResponse, redirect } from "../http";
import { sendLater } from "../mail";
import { layout } from "../pages";
import { DAY, enforce, hit, HOUR } from "../ratelimit";
import { jsonBody } from "../request";
import { loadBrowserSession } from "../sessions";

/** The apps send at most this much (CrashSender.MaxCards cards of CrashCard.MaxDetails each, and the note). */
export const MAX_CRASH_BODY = 64 * 1024;
export const MAX_CARDS = 5;
export const PER_ADDRESS_PER_HOUR = 10;
export const ALL_PER_DAY = 500;
/** New kinds of crash emailed to the admins a day; more still land in the list. */
export const MAILS_PER_DAY = 5;
/** Notes kept per row, newest last. */
export const MAX_NOTES = 20;
export const CRASH_KEEP_DAYS = 90;

const PLACES = new Set(["Screen", "Background", "Task"]);
const CRASH_ID_RE = /^[0-9a-f]{12}$/;
const VERSION_RE = /^[0-9A-Za-z.+-]{1,40}$/;
/** Hidden characters, but not line breaks (the details are a stack trace). */
const HIDDEN_BUT_NEWLINE = /[\u0000-\u0009\u000b-\u001f\u007f-\u009f­؜᠎​-‏‪-‮⁠-⁤⁦-⁩﻿]/g;

interface CrashCard {
  id: string;
  app: string;
  version: string;
  system: string;
  place: string;
  error: string;
  message: string;
  details: string;
  count: number;
  closed: boolean;
}

function line(v: unknown, max: number): string {
  if (typeof v !== "string") throw new HttpError(400, "bad_request");
  const s = stripHidden(v).trim();
  if (s.length === 0 || s.length > max) throw new HttpError(400, "bad_request");
  return s;
}

function lines(v: unknown, max: number, optional = false): string | null {
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

function card(v: unknown): CrashCard {
  if (!v || typeof v !== "object" || Array.isArray(v)) throw new HttpError(400, "bad_request");
  const c = v as Record<string, unknown>;
  const id = typeof c.id === "string" && CRASH_ID_RE.test(c.id) ? c.id : null;
  const version = typeof c.version === "string" && VERSION_RE.test(c.version) ? c.version : null;
  const place = typeof c.place === "string" && PLACES.has(c.place) ? c.place : null;
  const count = typeof c.count === "number" && Number.isSafeInteger(c.count) && c.count >= 1 ? Math.min(c.count, 1_000_000) : null;
  if (!id || !version || !place || count === null || typeof c.closed !== "boolean") throw new HttpError(400, "bad_request");
  return {
    id,
    app: line(c.app, 40),
    version,
    system: line(c.system, 200),
    place,
    error: line(c.error, 300),
    message: lines(c.message, 600)!,
    details: lines(c.details, 8200)!,
    count,
    closed: c.closed,
  };
}

/** POST /v1/crash: {"cards":[…], "note": "…" | null}. 204 when kept. */
export async function crashRoute(ctx: Ctx): Promise<Response> {
  const body = jsonBody(ctx);
  if (!Array.isArray(body.cards) || body.cards.length === 0 || body.cards.length > MAX_CARDS) throw new HttpError(400, "bad_request");
  const cards = body.cards.map(card);
  const note = lines(body.note, 1000, true);

  await enforce(ctx, "crash", ctx.ipKey, PER_ADDRESS_PER_HOUR, HOUR);
  await enforce(ctx, "crash-all", "service", ALL_PER_DAY, DAY);

  const db = ctx.env.DB;
  for (const c of cards) {
    const row = await db
      .prepare(
        `INSERT INTO crash_reports (crash_id, app, version, system, place, error, message, details, closed, count, reports, notes, first_at, last_at)
         VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, 1, ?11, ?12, ?12)
         ON CONFLICT (crash_id, version) DO UPDATE SET
           app = ?2, system = ?4, place = ?5, error = ?6, message = ?7, details = ?8, closed = MAX(closed, ?9),
           count = count + ?10, reports = reports + 1, last_at = ?12
         RETURNING id, reports, notes`,
      )
      .bind(c.id, c.app, c.version, c.system, c.place, c.error, c.message, c.details, c.closed ? 1 : 0, c.count, "[]", ctx.now)
      .first<{ id: number; reports: number; notes: string }>();
    if (!row) continue;
    if (note) {
      const notes = [...(JSON.parse(row.notes) as string[]), note].slice(-MAX_NOTES);
      await db.prepare("UPDATE crash_reports SET notes = ?1 WHERE id = ?2").bind(JSON.stringify(notes), row.id).run();
    }
    if (row.reports === 1) await mailAdmins(ctx, c);
  }
  return new Response(null, { status: 204 });
}

/** The admins' addresses (ADMIN_EMAILS, comma separated, lower case); none when unset. */
export function adminEmails(ctx: Ctx): string[] {
  return (ctx.env.ADMIN_EMAILS ?? "")
    .split(",")
    .map((s) => s.trim().toLowerCase())
    .filter((s) => s.includes("@"));
}

/** A kind of crash (and version) not seen before: tell the admins, at most MAILS_PER_DAY a day. */
async function mailAdmins(ctx: Ctx, c: CrashCard): Promise<void> {
  const to = adminEmails(ctx);
  if (to.length === 0) return;
  if ((await hit(ctx, "crash-mail", "service", MAILS_PER_DAY, DAY)).limited) return;
  const url = `${ctx.env.PUBLIC_ORIGIN}/admin/crashes`;
  const subject = `New Pairnets crash: ${c.error.split(".").pop()} in ${c.app} ${c.version}`;
  const text = `${c.error}${c.closed ? " (the app had to close)" : ""}\n${c.message}\n\nAll crash reports: ${url}\n`;
  const html = `<p><b>${e(c.error)}</b>${c.closed ? " (the app had to close)" : ""}<br>${e(c.message)}</p><p><a href="${e(url)}">All crash reports</a></p>`;
  for (const addr of to) sendLater(ctx, { to: addr, subject, text, html });
}

interface CrashRow {
  crash_id: string;
  app: string;
  version: string;
  system: string;
  place: string;
  error: string;
  message: string;
  details: string;
  closed: number;
  count: number;
  reports: number;
  notes: string;
  first_at: number;
  last_at: number;
}

function day(sec: number): string {
  return new Date(sec * 1000).toISOString().slice(0, 16).replace("T", " ") + " UTC";
}

/** The report as plain text, ready to paste into a chat that fixes it. */
function reportText(r: CrashRow): string {
  const notes = JSON.parse(r.notes) as string[];
  return [
    `Crash ${r.crash_id}: ${r.error}${r.closed ? " (Pairnets had to close)" : ""}`,
    `Seen:    ${r.count} times, reported ${r.reports} times, first ${day(r.first_at)}, last ${day(r.last_at)}`,
    `Where:   ${r.place}, ${r.app} ${r.version}`,
    `System:  ${r.system}`,
    `Message: ${r.message}`,
    r.details,
    ...(notes.length ? ["", "What users were doing:", ...notes.map((n) => `- ${n}`)] : []),
  ].join("\n");
}

/** GET /admin/crashes: newest first. Only for a signed-in account whose email is in ADMIN_EMAILS; others get 404. */
export async function crashListRoute(ctx: Ctx): Promise<Response> {
  const s = await loadBrowserSession(ctx);
  if (!s) return redirect("/login?next=/admin/crashes");
  const account = await ctx.env.DB.prepare("SELECT email FROM accounts WHERE id = ?1").bind(s.accountId).first<{ email: string }>();
  if (!account || !adminEmails(ctx).includes(account.email.toLowerCase())) throw new HttpError(404, "not_found");
  const rows = (await ctx.env.DB.prepare("SELECT * FROM crash_reports ORDER BY last_at DESC LIMIT 200").all<CrashRow>()).results;
  const items = rows
    .map(
      (r) => `<section class="card">
<h2>${e(r.error)} <span class="muted">· ${e(r.app)} ${e(r.version)} · ${r.count}× · last ${e(day(r.last_at))}</span></h2>
<pre>${e(reportText(r))}</pre>
</section>`,
    )
    .join("\n");
  return htmlResponse(
    200,
    layout(
      "Crash reports",
      `<section class="card"><h1>Crash reports</h1><p class="muted">${rows.length} kind${rows.length === 1 ? "" : "s"} of crash, newest first. Reports not seen for ${CRASH_KEEP_DAYS} days are deleted.</p></section>
${items || `<section class="card"><p>No crash reports yet.</p></section>`}`,
    ),
  );
}

/** For tests and the cron: rows not seen for CRASH_KEEP_DAYS. */
export function crashSweep(db: D1Database, now: number): D1PreparedStatement {
  return db.prepare("DELETE FROM crash_reports WHERE last_at < ?1").bind(now - CRASH_KEEP_DAYS * DAY);
}
