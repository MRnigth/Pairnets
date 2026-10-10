// Responses, uniform errors and the headers every response carries (CONTRACT.md sections 6.1 and 6.8, RELAY.md 5).

export const CSP =
  "default-src 'none'; script-src 'self' https://challenges.cloudflare.com; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
  "connect-src 'self'; frame-src https://challenges.cloudflare.com; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";

export const SECURITY_HEADERS: Record<string, string> = {
  "Content-Security-Policy": CSP,
  "Strict-Transport-Security": "max-age=31536000",
  "X-Content-Type-Options": "nosniff",
  "X-Frame-Options": "DENY",
  "Referrer-Policy": "no-referrer",
  "Cross-Origin-Opener-Policy": "same-origin",
  "Permissions-Policy": "camera=(), microphone=(), geolocation=()",
};

export type ErrorCode =
  | "bad_request"
  | "bad_origin"
  | "unauthorized"
  | "reauth_required"
  | "not_found"
  | "rate_limited"
  | "turnstile_failed"
  | "email_unavailable"
  | "invalid_code"
  | "expired"
  | "used"
  | "wrong_browser"
  | "bad_url"
  | "nest_limit"
  | "too_many_codes"
  | "already_decided"
  | "slow_down"
  | "unlinked"
  | "bad_signature"
  | "stale"
  | "not_confirmed"
  | "nest_unknown"
  | "nest_offline"
  | "full"
  | "cloudflare_failed"
  | "server_error";

export const MESSAGES: Record<ErrorCode, string> = {
  bad_request: "The request was not understood.",
  bad_origin: "This request must come from the Pairnets account page.",
  unauthorized: "Sign in first.",
  reauth_required: "Sign in again to do this.",
  not_found: "Not found.",
  rate_limited: "Too many requests. Wait a little and try again.",
  turnstile_failed: "The check that you are a person did not work. Try again.",
  email_unavailable: "Sign-in emails are paused for today. Try again tomorrow, or continue with Google.",
  invalid_code: "That code is not valid.",
  expired: "That code has expired.",
  used: "That code was already used.",
  wrong_browser: "Open the link in the browser where you asked for it.",
  bad_url: "That is not a public https address for a nest (like https://nest.example.com).",
  nest_limit: "This account already has 3 servers. Remove one first.",
  too_many_codes: "Too many link codes. Use or cancel the ones you have, or try again tomorrow.",
  already_decided: "This sign-in was already answered.",
  slow_down: "Asking too often. Wait a few seconds.",
  unlinked: "This nest is not linked to a Pairnets account.",
  bad_signature: "The request signature did not check out.",
  stale: "The request is too old or was already used.",
  not_confirmed: "This nest has not finished linking.",
  nest_unknown: "This server is not linked to Pairnets any more.",
  nest_offline: "Your server is not connected right now. Check that it is on.",
  full: "Pairnets cannot take more servers right now. Try again later.",
  cloudflare_failed: "Cloudflare did not finish setting up the server, so nothing was kept. Try again in a few minutes.",
  server_error: "Something went wrong on our side.",
};

/** Thrown by handlers; turned into a JSON error under /v1 and an HTML page elsewhere. */
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

export function redirect(location: string, status = 302): Response {
  return new Response(null, { status, headers: { Location: location } });
}

export function htmlResponse(status: number, body: string, headers: Record<string, string> = {}): Response {
  return new Response(body, { status, headers: { "Content-Type": "text/html; charset=utf-8", ...headers } });
}

/**
 * Adds the security headers (and Cache-Control) to a response and appends the Set-Cookie lines collected while
 * handling the request. CORS headers are never sent.
 */
export function finalize(resp: Response, isAsset: boolean, setCookies: string[]): Response {
  const headers = new Headers(resp.headers);
  for (const [k, v] of Object.entries(SECURITY_HEADERS)) headers.set(k, v);
  if (!isAsset) headers.set("Cache-Control", "no-store");
  for (const name of [...headers.keys()]) {
    if (name.toLowerCase().startsWith("access-control-")) headers.delete(name);
  }
  for (const c of setCookies) headers.append("Set-Cookie", c);
  return new Response(resp.body, { status: resp.status, statusText: resp.statusText, headers });
}

/**
 * Relayed answers come from people's own servers but are served on this service's origin. So nothing a server sends
 * may act on that origin: its cookies, Clear-Site-Data and CORS headers are dropped, and the CSP makes any document a
 * sandbox without scripts (an app ignores all of this; a browser that opens a relayed URL gets an inert page).
 */
export const RELAY_CSP = "default-src 'none'; sandbox; frame-ancestors 'none'";

const NULL_BODY = new Set([101, 204, 205, 304]);

export function finalizeRelay(resp: Response): Response {
  // A WebSocket upgrade is handed back untouched (its headers no longer matter and the socket must stay attached).
  if (resp.status === 101 || resp.webSocket) return resp;
  const headers = new Headers(resp.headers);
  for (const name of [...headers.keys()]) {
    const n = name.toLowerCase();
    if (n === "set-cookie" || n === "clear-site-data" || n.startsWith("access-control-")) headers.delete(name);
  }
  for (const [k, v] of Object.entries(SECURITY_HEADERS)) headers.set(k, v);
  headers.set("Content-Security-Policy", RELAY_CSP);
  headers.set("Cache-Control", "no-store");
  return new Response(NULL_BODY.has(resp.status) ? null : resp.body, { status: resp.status, statusText: resp.statusText, headers });
}

export function escapeHtml(s: string | null | undefined): string {
  return String(s ?? "")
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}
