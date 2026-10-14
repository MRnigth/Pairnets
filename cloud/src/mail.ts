// Email (CONTRACT.md section 6.11): a small Mailer interface, a Resend implementation, the daily budgets and the texts.

import { audit } from "./audit";
import type { Ctx } from "./context";
import { escapeHtml } from "./http";
import { DAY, hit, peek } from "./ratelimit";

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

/**
 * The last part of the day's total is kept for security notices ("a computer was allowed", "a nest was removed").
 * Sign-in emails can never use it, however many are asked for, so an attacker who floods sign-in requests cannot
 * make the notices of what they then do go unsent.
 */
export const NOTICE_RESERVE = 15;
/** The most sign-in emails (together with the notices already sent) that fit in a day. */
export const LOGIN_MAIL_CEILING = ALL_MAIL_DAILY_LIMIT - NOTICE_RESERVE;

export function loginMailLimit(ctx: Ctx): number {
  const n = Number.parseInt(ctx.env.EMAIL_DAILY_LIMIT ?? "", 10);
  return Number.isFinite(n) && n >= 0 ? Math.min(n, LOGIN_MAIL_CEILING) : LOGIN_MAIL_CEILING;
}

/** Whether a sign-in email could still be sent today, without taking one (the sign-in page says so when not). */
export async function loginMailAvailable(ctx: Ctx): Promise<boolean> {
  if ((await peek(ctx, "mail-login", "service", loginMailLimit(ctx), DAY)).limited) return false;
  return !(await peek(ctx, "mail-all", "service", LOGIN_MAIL_CEILING, DAY)).limited;
}

/**
 * Takes one email from today's budget. Sign-in emails also count against EMAIL_DAILY_LIMIT and may only use the
 * total up to LOGIN_MAIL_CEILING; notices may use all of it. Returns false when the budget is used up (the caller then
 * does not send).
 */
export async function reserveMail(ctx: Ctx, kind: "login" | "notice"): Promise<boolean> {
  if (kind === "login") {
    const login = await hit(ctx, "mail-login", "service", loginMailLimit(ctx), DAY);
    if (login.limited) return false;
  }
  const all = await hit(ctx, "mail-all", "service", kind === "login" ? LOGIN_MAIL_CEILING : ALL_MAIL_DAILY_LIMIT, DAY);
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

/** Every email ends with who sent it and where the privacy policy is. */
export const PRIVACY_URL = "https://pairnets.app/privacy/";
const TEXT_FOOTER = `\n--\nPairnets account service (sync.pairnets.app)\nPrivacy: ${PRIVACY_URL}\n`;

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
<p style="color:#555555;font-size:13px">Pairnets account service (sync.pairnets.app) · <a href="${PRIVACY_URL}" style="color:#555555">Privacy</a></p>
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
    text: `${lines[0]}\n\n${link}\n\n${lines[1]}\n${lines[2]}\n${TEXT_FOOTER}`,
    html: layout(subject, lines.slice(0, 1), { href: link, text: "Sign in" }, lines.slice(1)),
  };
}

export type NoticeKind =
  | "identity_added_google"
  | "identity_added_email"
  | "nest_linked"
  | "nest_removed"
  | "nest_unlinked"
  | "server_added"
  | "server_removed"
  | "app_allowed"
  | "account_deleted";

/** What an "app_allowed" notice says besides the computer's name: which nest, when, and where the request came from. */
export interface AppAllowedDetail {
  nest: string | null;
  at: number;
  place: string | null;
}

/** `url` is the nest's address for own-domain nests, and the server's name for relayed servers. */
export function noticeEmail(to: string, kind: NoticeKind, url?: string, detail?: AppAllowedDetail): MailMessage {
  const help = "If this was not you, sign in at https://sync.pairnets.app/account, check your nests and signed-in browsers, and remove anything you do not know.";
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
    case "server_added":
      subject = `A nest was added to your account: ${url}`;
      lines = [`The nest "${url}" was added to your Pairnets account. Computers you sign in with this account can now sync with it.`, help];
      break;
    case "server_removed":
      subject = `A nest was removed from your account: ${url}`;
      lines = [`The nest "${url}" was removed from your Pairnets account. Its computers no longer reach it through Pairnets; its files stay on it.`, help];
      break;
    case "app_allowed": {
      const when = detail ? `${new Date(detail.at * 1000).toISOString().slice(0, 16).replace("T", " ")} UTC` : "just now";
      const nest = detail?.nest ? `the nest "${detail.nest}"` : "your nest (it joins the first nest you add)";
      const place = detail?.place ? `The request came from ${detail.place}.` : "We could not tell where the request came from.";
      subject = `A computer was allowed to use your account: ${url}`;
      lines = [
        `The computer "${url}" was allowed to sign in to your Pairnets account at ${when}. It can now sync with ${nest}. ${place}`,
        "If this wasn't you, remove it at https://sync.pairnets.app/account (open your nest and remove the computer), and remove anything else you do not know.",
      ];
      break;
    }
    case "account_deleted":
      subject = "Your Pairnets account was deleted";
      lines = ["Your Pairnets account and everything stored about it were deleted. Your nests keep their files."];
      break;
  }
  return { to, subject, text: `${lines.join("\n\n")}\n${TEXT_FOOTER}`, html: layout(subject, lines) };
}

/** A notice email within the daily budget; when the budget is used up it is skipped and audited. */
export async function sendNotice(ctx: Ctx, accountId: string | null, to: string, kind: NoticeKind, url?: string, detail?: AppAllowedDetail): Promise<void> {
  if (!(await reserveMail(ctx, "notice"))) {
    if (accountId) await audit(ctx, "notice_skipped", accountId, null, { kind });
    return;
  }
  sendLater(ctx, noticeEmail(to, kind, url, detail));
}
