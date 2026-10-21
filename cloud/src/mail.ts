// Email (CONTRACT.md section 6.11): a small Mailer interface, a Resend implementation, the daily budgets and the texts.

import { audit } from "./audit";
import type { Ctx } from "./context";
import { dottedEmail } from "./formats";
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
    /** Where replies go (the sending address takes no mail). Left out of the request when empty. */
    private readonly replyTo = "",
  ) {}

  async send(msg: MailMessage): Promise<void> {
    const resp = await this.fetchFn(RESEND_URL, {
      method: "POST",
      headers: { Authorization: `Bearer ${this.apiKey}`, "Content-Type": "application/json" },
      body: JSON.stringify({
        from: this.from,
        to: [msg.to],
        ...(this.replyTo ? { reply_to: this.replyTo } : {}),
        subject: msg.subject,
        text: msg.text,
        html: msg.html,
      }),
    });
    if (!resp.ok) throw new Error(`Resend answered ${resp.status}`);
  }
}

/** "Name <a@b>" or "a@b" as Cloudflare's address object. */
export function parseAddress(s: string): { email: string; name?: string } {
  const m = /^\s*(.*?)\s*<([^>]+)>\s*$/.exec(s);
  if (!m) return { email: s.trim() };
  return m[1] ? { email: m[2].trim(), name: m[1].replace(/^"|"$/g, "") } : { email: m[2].trim() };
}

/** The part of Cloudflare's `send_email` binding (Email Service) this Worker uses. */
export interface EmailSendBinding {
  send(msg: {
    from: { email: string; name?: string };
    to: string;
    replyTo?: { email: string; name?: string };
    subject: string;
    text: string;
    html: string;
  }): Promise<{ messageId: string }>;
}

/** Sends through Cloudflare Email Service (the EMAIL binding), from the mail.pairnets.app subdomain. */
export class CloudflareMailer implements Mailer {
  constructor(
    private readonly binding: EmailSendBinding,
    private readonly from: string,
    private readonly replyTo = "",
  ) {}

  async send(msg: MailMessage): Promise<void> {
    await this.binding.send({
      from: parseAddress(this.from),
      to: msg.to,
      ...(this.replyTo ? { replyTo: parseAddress(this.replyTo) } : {}),
      subject: msg.subject,
      text: msg.text,
      html: msg.html,
    });
  }
}

/** Tries the first mailer and, when it fails, sends the same email through the second (Cloudflare first, Resend after). */
export class FallbackMailer implements Mailer {
  constructor(
    private readonly first: Mailer,
    private readonly second: Mailer,
  ) {}

  async send(msg: MailMessage): Promise<void> {
    try {
      await this.first.send(msg);
    } catch (e) {
      console.warn(`mail: Cloudflare send failed, using Resend (${e instanceof Error ? e.message : String(e)})`);
      await this.second.send(msg);
    }
  }
}

/** Cloudflare Email Service when the EMAIL binding and MAIL_FROM_CF are set, with Resend as the fallback; Resend alone otherwise. */
export function makeMailer(
  env: { EMAIL?: EmailSendBinding; MAIL_FROM_CF?: string; RESEND_API_KEY?: string; MAIL_FROM?: string; MAIL_REPLY_TO?: string },
  fetchFn: typeof fetch,
): Mailer {
  const resend = new ResendMailer(env.RESEND_API_KEY ?? "", env.MAIL_FROM ?? "", fetchFn, env.MAIL_REPLY_TO ?? "");
  if (!env.EMAIL || !env.MAIL_FROM_CF) return resend;
  return new FallbackMailer(new CloudflareMailer(env.EMAIL, env.MAIL_FROM_CF, env.MAIL_REPLY_TO ?? ""), resend);
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
 * Invites a friend gets by email (RELAY.md 10) have a small budget of their own, outside the day's total, so they can
 * never use up the emails that sign-ins and security notices need. 95 + 5 is Resend's free 100 a day.
 */
export const INVITE_MAIL_DAILY_LIMIT = 5;

/**
 * Takes one email from today's budget. Sign-in emails also count against EMAIL_DAILY_LIMIT and may only use the
 * total up to LOGIN_MAIL_CEILING; notices may use all of it; invites only their own INVITE_MAIL_DAILY_LIMIT. Returns
 * false when the budget is used up (the caller then does not send).
 */
export async function reserveMail(ctx: Ctx, kind: "login" | "notice" | "invite"): Promise<boolean> {
  if (kind === "invite") return !(await hit(ctx, "mail-invite", "service", INVITE_MAIL_DAILY_LIMIT, DAY)).limited;
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

/** Every email ends with why the person got it, who sent it, where to ask and where the privacy policy is. */
export const PRIVACY_URL = "https://pairnets.app/privacy/";
export const SUPPORT_EMAIL = "support@pairnets.app";
/** The blue of the website. */
const ACCENT = "#2563EB";
/** The account page: the one address the help lines point to, and the only text that becomes a link. */
const ACCOUNT_URL = "https://sync.pairnets.app/account";
const REASON_LOGIN = "You got this because someone asked to sign in to Pairnets with this address.";
const REASON_ACCOUNT = "You got this because this address belongs to a Pairnets account.";

function textFooter(reason: string): string {
  return `\n--\n${reason}\nQuestions? Write to ${SUPPORT_EMAIL}\nPairnets account service (sync.pairnets.app)\nPrivacy: ${PRIVACY_URL}\n`;
}

/** Escaped text where only our own account page address becomes a link (never anything a computer's name could hold). */
function linkified(text: string): string {
  return escapeHtml(text).split(ACCOUNT_URL).join(`<a href="${ACCOUNT_URL}" style="color:${ACCENT}">${ACCOUNT_URL}</a>`);
}

function paragraphsHtml(lines: string[]): string {
  return lines.map((p) => `<p>${linkified(p)}</p>`).join("\n");
}

function layout(reason: string, title: string, before: string[], link?: { href: string; text: string }, after: string[] = []): string {
  const button = link
    ? `<p><a href="${escapeHtml(link.href)}" style="display:inline-block;padding:10px 18px;background:${ACCENT};color:#ffffff;text-decoration:none;border-radius:6px">${escapeHtml(link.text)}</a></p>
<p style="color:#555555;font-size:13px">Or copy this address into the browser: ${escapeHtml(link.href)}</p>`
    : "";
  return `<!doctype html><html lang="en"><head><meta charset="utf-8"><title>${escapeHtml(title)}</title></head><body style="font-family:system-ui,sans-serif;line-height:1.5;color:#111111">
<h2>${escapeHtml(title)}</h2>
${paragraphsHtml(before)}
${button}
${paragraphsHtml(after)}
<p style="color:#555555;font-size:13px">${escapeHtml(reason)}</p>
<p style="color:#555555;font-size:13px">Questions? Write to <a href="mailto:${SUPPORT_EMAIL}" style="color:#555555">${SUPPORT_EMAIL}</a><br>Pairnets account service (sync.pairnets.app) · <a href="${PRIVACY_URL}" style="color:#555555">Privacy</a></p>
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
    text: `${lines[0]}\n\n${link}\n\n${lines[1]}\n${lines[2]}\n${textFooter(REASON_LOGIN)}`,
    html: layout(REASON_LOGIN, subject, lines.slice(0, 1), { href: link, text: "Sign in" }, lines.slice(1)),
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
  const help = `If this was not you, sign in at ${ACCOUNT_URL}, check your nests and signed-in browsers, and remove anything you do not know.`;
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
        `If this wasn't you, remove it at ${ACCOUNT_URL} (open your nest and remove the computer), and remove anything else you do not know.`,
      ];
      break;
    }
    case "account_deleted":
      subject = "Your Pairnets account was deleted";
      lines = ["Your Pairnets account and everything stored about it were deleted. Your nests keep their files."];
      break;
  }
  return { to, subject, text: `${lines.join("\n\n")}\n${textFooter(REASON_ACCOUNT)}`, html: layout(REASON_ACCOUNT, subject, lines) };
}

/** The warning to an account nobody used for 11 months (inactive.ts); `deleteOn` is a date like "17 November 2026". */
export function inactiveWarningEmail(to: string, deleteOn: string): MailMessage {
  const subject = `Your Pairnets account will be deleted on ${deleteOn}`;
  const lines = [
    "Nobody has signed in to your Pairnets account for almost a year, and none of its computers or nests have used it.",
    `To keep it, sign in once at ${ACCOUNT_URL} before ${deleteOn}. That is all it takes.`,
    `Otherwise the account and everything stored about it are deleted on ${deleteOn}, as our privacy policy says. Your nests and computers keep their files.`,
  ];
  return { to, subject, text: `${lines.join("\n\n")}\n${textFooter(REASON_ACCOUNT)}`, html: layout(REASON_ACCOUNT, subject, lines) };
}

/** After an unused account was deleted (inactive.ts). */
export function inactiveDeletedEmail(to: string): MailMessage {
  const subject = "Your Pairnets account was deleted";
  const lines = [
    "Your Pairnets account wasn't used for a year, so it and everything stored about it were deleted, as we wrote to you 30 days ago. Your nests and computers keep their files.",
    "You can make a new account any time at https://pairnets.app.",
  ];
  return { to, subject, text: `${lines.join("\n\n")}\n${textFooter(REASON_ACCOUNT)}`, html: layout(REASON_ACCOUNT, subject, lines) };
}

/** What a friend gets (RELAY.md 10), as the emails say it. */
export interface ShareDetail {
  /** The nest's name. */
  nest: string;
  /** The top-level folders shared, or null for the whole nest. */
  folders: string[] | null;
  access: "view" | "edit";
}

/** "Photos", "Photos and School", "Photos, School and Music". */
function listed(names: string[]): string {
  return names.length < 2 ? (names[0] ?? "") : `${names.slice(0, -1).join(", ")} and ${names[names.length - 1]}`;
}

/** "the folders Photos and School, and you can view them" / "the whole nest, and you can add, change and delete in it". */
function shareText(d: ShareDetail): string {
  const what = d.folders ? `the folder${d.folders.length > 1 ? "s" : ""} ${listed(d.folders)}` : "the whole nest";
  const them = d.folders && d.folders.length > 1 ? "them" : "it";
  return `${what}, and you can ${d.access === "view" ? `view ${them}` : `add, change and delete files in ${them}`}`;
}

/** "28 October": the day an invite ends. */
function dayOf(at: number): string {
  return new Date(at * 1000).toLocaleDateString("en-GB", { day: "numeric", month: "long", timeZone: "UTC" });
}

/** The invite Pairnets sends for an owner who typed a friend's address (sent within the "invite" budget). */
export function inviteEmail(to: string, owner: { name: string; email: string }, share: ShareDetail, link: string, expiresAt: number): MailMessage {
  const subject = `${owner.name} invited you to ${share.nest} on Pairnets`;
  const title = `${owner.name} wants to share files with you`;
  const reason = `Sent because ${owner.name} (${dottedEmail(owner.email)}) typed your address in Pairnets. We don't keep it after the invite ends.`;
  const before = [`${owner.name} invited you to their nest, ${share.nest}. You'll get ${shareText(share)}. Pairnets keeps it all in sync on your computer.`];
  const after = [
    `The link works once and ends on ${dayOf(expiresAt)}. You'll sign in with Google or your email; it's free.`,
    `If you don't know ${owner.name}, ignore this email and nothing happens.`,
  ];
  return {
    to,
    subject,
    text: `${title}\n\n${before[0]}\n\nSee the invite: ${link}\n\n${after.join("\n")}\n${textFooter(reason)}`,
    html: layout(reason, title, before, { href: link, text: "See the invite" }, after),
  };
}

/** To the owner: someone used an invite link and asks to join (a security notice: it uses the notice budget). */
export function joinRequestEmail(to: string, friend: { name: string; email: string }, share: ShareDetail, nestId: string): MailMessage {
  const subject = `${friend.name} wants to join ${share.nest}`;
  const page = `${ACCOUNT_URL}#people-${nestId}`;
  const what = share.folders ? listed(share.folders) : "the whole nest";
  const before = [
    `${friend.name} (${friend.email}) used your invite link and asks to join your nest ${share.nest}: ${what}, ${share.access === "view" ? "view only" : "can change"}.`,
    "Nothing is shared until you let them in. You can let them in or decline on your account page.",
  ];
  const after = [`If you don't know ${friend.name}, decline. Nothing else happens.`];
  return {
    to,
    subject,
    text: `${before.join("\n\n")}\n\n${page}\n\n${after[0]}\n${textFooter(REASON_ACCOUNT)}`,
    html: layout(REASON_ACCOUNT, subject, before, { href: page, text: "Let in or decline" }, after),
  };
}

/** To the friend, once the owner let them in (sent within the "invite" budget). */
export function friendInEmail(to: string, owner: string, share: ShareDetail): MailMessage {
  const subject = `You're in ${share.nest}`;
  const lines = [
    `${owner} let you in to ${share.nest}. You get ${shareText(share)}.`,
    `To add it on a computer: open Pairnets and choose Add a nest, sign in as ${to} and pick ${share.nest} · shared by ${owner}.`,
    "No Pairnets yet? Get it at https://pairnets.app.",
  ];
  return { to, subject, text: `${lines.join("\n\n")}\n${textFooter(REASON_ACCOUNT)}`, html: layout(REASON_ACCOUNT, subject, lines) };
}

/** A notice email within the daily budget; when the budget is used up it is skipped and audited. */
export async function sendNotice(ctx: Ctx, accountId: string | null, to: string, kind: NoticeKind, url?: string, detail?: AppAllowedDetail): Promise<void> {
  if (!(await reserveMail(ctx, "notice"))) {
    if (accountId) await audit(ctx, "notice_skipped", accountId, null, { kind });
    return;
  }
  sendLater(ctx, noticeEmail(to, kind, url, detail));
}
