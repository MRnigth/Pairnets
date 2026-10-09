// Email: a small Mailer interface, a Resend implementation, the daily budget and the one email this service sends.

import type { Ctx } from "./context";
import { intVar } from "./env";
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

/** Resend's free plan sends 100 a day, and the account service shares it: never more than 95 from here. */
export const MAIL_HARD_LIMIT = 95;

/** Takes one email from today's budget (MAIL_DAILY_LIMIT). False when it is used up. */
export async function reserveMail(ctx: Ctx): Promise<boolean> {
  const limit = Math.min(intVar(ctx.env.MAIL_DAILY_LIMIT, 40), MAIL_HARD_LIMIT);
  return !(await hit(ctx, "mail", "service", limit, DAY)).limited;
}

export function codeEmail(to: string, host: string, code: string): MailMessage {
  const subject = `Your code for ${host}: ${code}`;
  const lines = [
    `Someone is setting up a Pairnets server with the name ${host}, and gave this email address.`,
    `If that is you, type this code into the installer. It works once, for 15 minutes.`,
    "If it is not you, ignore this email: nothing happens without the code.",
  ];
  const text = `${lines[0]}\n\n    ${code}\n\n${lines[1]}\n${lines[2]}\n\nPairnets names (names.pairnets.app). Questions or abuse: support@pairnets.app\n`;
  const html = `<!doctype html><html><body style="font-family:system-ui,sans-serif;line-height:1.5;color:#111111">
<h2>${escapeHtml(subject)}</h2>
<p>${escapeHtml(lines[0])}</p>
<p style="font-size:28px;letter-spacing:6px;font-weight:600">${escapeHtml(code)}</p>
<p>${escapeHtml(lines[1])}</p>
<p>${escapeHtml(lines[2])}</p>
<p style="color:#555555;font-size:13px">Pairnets names (names.pairnets.app). Questions or abuse: support@pairnets.app</p>
</body></html>`;
  return { to, subject, text, html };
}
