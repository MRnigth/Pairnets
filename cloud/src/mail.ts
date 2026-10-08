// Email (CONTRACT.md section 6.11): a small Mailer interface, a Resend implementation, the daily budgets and the texts.

import { audit } from "./audit";
import type { Ctx } from "./context";
import { escapeHtml } from "./http";
import { DAY, hit } from "./ratelimit";

export interface MailMessage {
  to: string;
  subject: string;
  text: string;
  html: string;
}

export interface Mailer {
  send(msg: MailMessage): Promise<void>;
}

export const RESEND_URL = "https://api.resend.com/emails";

/** Sends through Resend's HTTP API: POST https://api.resend.com/emails with `Authorization: Bearer RESEND_API_KEY`. */
export class ResendMailer implements Mailer {
  constructor(
    private readonly apiKey: string,
    private readonly from: string,
    private readonly fetchFn: typeof fetch,
  ) {}

  async send(msg: MailMessage): Promise<void> {
    const resp = await this.fetchFn(RESEND_URL, {
      method: "POST",
      headers: { Authorization: `Bearer ${this.apiKey}`, "Content-Type": "application/json" },
      body: JSON.stringify({ from: this.from, to: [msg.to], subject: msg.subject, text: msg.text, html: msg.html }),
    });
    if (!resp.ok) throw new Error(`Resend answered ${resp.status}`);
  }
}

/** All emails, notices included: 95 per UTC day (Resend's free plan is 100). */
export const ALL_MAIL_DAILY_LIMIT = 95;

export function loginMailLimit(ctx: Ctx): number {
  const n = Number.parseInt(ctx.env.EMAIL_DAILY_LIMIT ?? "", 10);
  return Number.isFinite(n) && n >= 0 ? Math.min(n, ALL_MAIL_DAILY_LIMIT) : 80;
}

/**
 * Takes one email from today's budget. Login emails also count against EMAIL_DAILY_LIMIT. Returns false when the
 * budget is used up (the caller then does not send).
 */
export async function reserveMail(ctx: Ctx, kind: "login" | "notice"): Promise<boolean> {
  if (kind === "login") {
    const login = await hit(ctx, "mail-login", "service", loginMailLimit(ctx), DAY);
    if (login.limited) return false;
  }
  const all = await hit(ctx, "mail-all", "service", ALL_MAIL_DAILY_LIMIT, DAY);
  return !all.limited;
}

/** Sends after the response (same timing whatever happens); failures are logged without the address. */
export function sendLater(ctx: Ctx, msg: MailMessage): void {
  ctx.defer(
    ctx.mailer.send(msg).catch((e: unknown) => {
      console.error("mail: sending failed", e instanceof Error ? e.message : "unknown error");
    }),
  );
}

function paragraphsHtml(lines: string[]): string {
  return lines.map((p) => `<p>${escapeHtml(p)}</p>`).join("\n");
}

function layout(title: string, before: string[], link?: { href: string; text: string }, after: string[] = []): string {
  const button = link
    ? `<p><a href="${escapeHtml(link.href)}" style="display:inline-block;padding:10px 18px;background:#1f6feb;color:#ffffff;text-decoration:none;border-radius:6px">${escapeHtml(link.text)}</a></p>
<p style="color:#555555;font-size:13px">Or copy this address into the browser: ${escapeHtml(link.href)}</p>`
    : "";
  return `<!doctype html><html><body style="font-family:system-ui,sans-serif;line-height:1.5;color:#111111">
<h2>${escapeHtml(title)}</h2>
${paragraphsHtml(before)}
${button}
${paragraphsHtml(after)}
<p style="color:#555555;font-size:13px">Pairnets account service (id.pairnets.app)</p>
</body></html>`;
}

export function loginEmail(to: string, link: string): MailMessage {
  const subject = "Your Pairnets sign-in link";
  const lines = [
    "Open the link below to sign in to your Pairnets account.",
    "It works once, for 15 minutes, in the browser where you asked for it.",
    "If you did not ask for it, you can ignore this email.",
  ];
  return {
    to,
    subject,
    text: `${lines[0]}\n\n${link}\n\n${lines[1]}\n${lines[2]}\n`,
    html: layout(subject, lines.slice(0, 1), { href: link, text: "Sign in" }, lines.slice(1)),
  };
}

export type NoticeKind = "identity_added_google" | "identity_added_email" | "nest_linked" | "nest_removed" | "nest_unlinked" | "account_deleted";

export function noticeEmail(to: string, kind: NoticeKind, url?: string): MailMessage {
  const help = "If this was not you, sign in at https://id.pairnets.app/account, check your nests and signed-in browsers, and remove anything you do not know.";
  let subject: string;
  let lines: string[];
  switch (kind) {
    case "identity_added_google":
      subject = "Google sign-in was added to your Pairnets account";
      lines = ["You can now sign in to your Pairnets account with Google.", help];
      break;
    case "identity_added_email":
      subject = "Email sign-in was added to your Pairnets account";
      lines = ["You can now sign in to your Pairnets account with a link sent to this address.", help];
      break;
    case "nest_linked":
      subject = `A nest was linked to your account: ${url}`;
      lines = [`The nest ${url} was linked to your Pairnets account. Anyone who can sign in to this account can now sign in to that nest's website.`, help];
      break;
    case "nest_removed":
      subject = `A nest was removed from your account: ${url}`;
      lines = [`The nest ${url} was removed from your Pairnets account.`, help];
      break;
    case "nest_unlinked":
      subject = `A nest was unlinked from your account: ${url}`;
      lines = [`The nest ${url} unlinked itself from your Pairnets account (someone ran "hosted unlink" on that server).`, help];
      break;
    case "account_deleted":
      subject = "Your Pairnets account was deleted";
      lines = ["Your Pairnets account and everything stored about it were deleted. Your nests keep working with their own sign-in methods."];
      break;
  }
  return { to, subject, text: `${lines.join("\n\n")}\n`, html: layout(subject, lines) };
}

/** A notice email within the daily budget; when the budget is used up it is skipped and audited. */
export async function sendNotice(ctx: Ctx, accountId: string | null, to: string, kind: NoticeKind, url?: string): Promise<void> {
  if (!(await reserveMail(ctx, "notice"))) {
    if (accountId) await audit(ctx, "notice_skipped", accountId, null, { kind });
    return;
  }
  sendLater(ctx, noticeEmail(to, kind, url));
}
