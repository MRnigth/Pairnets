// Responses, uniform errors and the headers every response carries.
//
// The installer (deploy/pairnets-name.sh) reads these answers with a tiny parser, so every message is one line of
// plain text without double quotes or backslashes (a test checks this).

export const CSP = "default-src 'none'; style-src 'unsafe-inline'; img-src 'self' data:; form-action 'none'; base-uri 'none'; frame-ancestors 'none'";

export const SECURITY_HEADERS: Record<string, string> = {
  "Content-Security-Policy": CSP,
  "Strict-Transport-Security": "max-age=31536000",
  "X-Content-Type-Options": "nosniff",
  "X-Frame-Options": "DENY",
  "Referrer-Policy": "no-referrer",
  "Cross-Origin-Opener-Policy": "same-origin",
  "Cache-Control": "no-store",
};

export type ErrorCode =
  | "bad_request"
  | "not_found"
  | "rate_limited"
  | "bad_name"
  | "name_reserved"
  | "name_taken"
  | "bad_email"
  | "email_has_name"
  | "full"
  | "daily_limit"
  | "address_limit"
  | "paused"
  | "mail_limit"
  | "mail_failed"
  | "expired"
  | "wrong_code"
  | "too_many_tries"
  | "unauthorized"
  | "name_busy"
  | "cloudflare_failed"
  | "server_error";

export const MESSAGES: Record<ErrorCode, string> = {
  bad_request: "The request was not understood.",
  not_found: "Not found.",
  rate_limited: "Too many requests. Wait a little and try again.",
  bad_name: "A name is 3 to 32 lowercase letters, digits or hyphens, with no hyphen at the start or end and no two in a row (like alice or my-nest).",
  name_reserved: "That name is kept for Pairnets itself. Pick another one.",
  name_taken: "That name is taken. Pick another one.",
  bad_email: "That does not look like an email address.",
  email_has_name:
    "This email address already has a Pairnets name. Give that one back first (sudo /opt/pairnets/pairnets-name.sh release on that server), or use another address.",
  full: "All free names are taken for now. Use your own domain instead (see the Advanced part of docs/HOWTO.md), or try again later.",
  daily_limit: "Today's new names are used up. Try again tomorrow.",
  address_limit: "This internet address already has 3 names. Give one back first.",
  paused: "New names are paused for now. Try again later.",
  mail_limit: "Code emails are used up for today. Try again tomorrow.",
  mail_failed: "The email with the code could not be sent. Check the address and try again.",
  expired: "The code has expired. Run the installer again to get a new one.",
  wrong_code: "That code is not right.",
  too_many_tries: "Too many wrong codes. Run the installer again to get a new one.",
  unauthorized: "That key does not fit this name.",
  name_busy: "This name is being given back.",
  cloudflare_failed: "Cloudflare did not finish setting up the name, so nothing was kept. Try again in a few minutes.",
  server_error: "Something went wrong on our side.",
};

/** Thrown by handlers; turned into a JSON error under /v1 and a plain page elsewhere. */
export class HttpError extends Error {
  constructor(
    public readonly status: number,
    public readonly code: ErrorCode,
    message?: string,
    public readonly headers: Record<string, string> = {},
  ) {
    super(message ?? MESSAGES[code]);
  }
}

export function json(status: number, body: unknown, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json", ...headers },
  });
}

export function apiError(status: number, code: ErrorCode, message?: string, headers: Record<string, string> = {}): Response {
  return json(status, { error: code, message: message ?? MESSAGES[code] }, headers);
}

export function noContent(): Response {
  return new Response(null, { status: 204 });
}

export function htmlResponse(status: number, body: string, headers: Record<string, string> = {}): Response {
  return new Response(body, { status, headers: { "Content-Type": "text/html; charset=utf-8", ...headers } });
}

/** Adds the security headers to a response. CORS headers are never sent: browsers on other sites get nothing. */
export function finalize(resp: Response): Response {
  const headers = new Headers(resp.headers);
  for (const [k, v] of Object.entries(SECURITY_HEADERS)) headers.set(k, v);
  for (const name of [...headers.keys()]) {
    if (name.toLowerCase().startsWith("access-control-")) headers.delete(name);
  }
  return new Response(resp.body, { status: resp.status, statusText: resp.statusText, headers });
}

export function escapeHtml(s: string | null | undefined): string {
  return String(s ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}
