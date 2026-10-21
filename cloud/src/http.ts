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
  | "nest_too_old"
  | "bad_name"
  | "full"
  | "cloudflare_failed"
  | "bad_access"
  | "sealed_whole_only"
  | "too_many_friends"
  | "own_nest"
  | "already_member"
  | "cancelled"
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
  nest_limit: "This account already has 4 nests. Remove one first.",
  too_many_codes: "Too many link codes. Use or cancel the ones you have, or try again tomorrow.",
  already_decided: "This sign-in was already answered.",
  slow_down: "Asking too often. Wait a few seconds.",
  unlinked: "This nest is not linked to a Pairnets account.",
  bad_signature: "The request signature did not check out.",
  stale: "The request is too old or was already used.",
  not_confirmed: "This nest has not finished linking.",
  nest_unknown: "This server is not linked to Pairnets any more.",
  nest_offline: "Your server is not connected right now. Check that it is on.",
  nest_too_old: "Update your nest to rename computers here.",
  bad_name: "Type a name.",
  full: "Pairnets cannot take more servers right now. Try again later.",
  cloudflare_failed: "Cloudflare did not finish setting up the server, so nothing was kept. Try again in a few minutes.",
  bad_access: "Choose View only or Can change, and the whole nest or 1 to 20 of its top-level folders.",
  sealed_whole_only: "An encrypted nest can only be shared whole.",
  too_many_friends: "A nest can have at most 10 friends, open invites included. Remove someone or cancel an invite first.",
  own_nest: "This is your own nest.",
  already_member: "You already have this nest.",
  cancelled: "This invite was cancelled.",
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

/** The one route the website (pairnets.app) may read, with the sign-in cookie: it shows who is signed in. */
export const SIGNED_IN_PATH = "/v1/signed-in";
export const WEBSITE_ORIGINS: ReadonlySet<string> = new Set(["https://pairnets.app", "https://www.pairnets.app"]);

/**
 * Adds the security headers (and Cache-Control) to a response and appends the Set-Cookie lines collected while
 * handling the request. CORS headers are only sent for SIGNED_IN_PATH (`website` is then the request's Origin), and
 * only to the website's own origins.
 */
export function finalize(resp: Response, isAsset: boolean, setCookies: string[], website: { origin: string | null } | null = null): Response {
  const headers = new Headers(resp.headers);
  for (const [k, v] of Object.entries(SECURITY_HEADERS)) headers.set(k, v);
  if (!isAsset) headers.set("Cache-Control", "no-store");
  for (const name of [...headers.keys()]) {
    if (name.toLowerCase().startsWith("access-control-")) headers.delete(name);
  }
  if (website) {
    headers.set("Vary", "Origin");
    if (website.origin !== null && WEBSITE_ORIGINS.has(website.origin)) {
      headers.set("Access-Control-Allow-Origin", website.origin);
      headers.set("Access-Control-Allow-Credentials", "true");
    }
  }
  for (const c of setCookies) headers.append("Set-Cookie", c);
  return new Response(resp.body, { status: resp.status, statusText: resp.statusText, headers });
}

/**
 * Relayed answers come from people's own servers (anyone can run one) but are served on this service's origin. So only
 * the response headers the apps need get through (an allowlist; everything else a server sends is dropped, so it cannot
 * add cookies, Clear-Site-Data, CORS, NEL/Report-To, Content-Disposition downloads and the like to our origin), a
 * redirect may only point inside the same nest, and the CSP makes any document a sandbox without scripts (an app
 * ignores all of this; a browser that opens a relayed URL gets an inert page).
 *
 * What the allowlist is made of: the body's type, length, encoding and byte ranges (resumable downloads send
 * Range/If-Range and read Content-Range), the validators the nest's file endpoint sets (ETag, Last-Modified), Retry-After
 * (the apps wait that long), Allow on a 405, and the nest's own X-Pairnets-* answer headers (server id, version, file
 * hash, modified time) under their old X-Tether-* names too, for apps that are not updated yet.
 */
const RELAY_PASS_HEADERS = new Set([
  "content-type",
  "content-length",
  "content-encoding",
  "content-range",
  "accept-ranges",
  "etag",
  "last-modified",
  "retry-after",
  "allow",
  "x-pairnets-server-id",
  "x-pairnets-version",
  "x-pairnets-hash",
  "x-pairnets-modified-ms",
  "x-tether-server-id",
  "x-tether-version",
  "x-tether-hash",
  "x-tether-modified-ms",
]);
export const RELAY_CSP = "default-src 'none'; sandbox; frame-ancestors 'none'";

const NULL_BODY = new Set([101, 204, 205, 304]);

/**
 * A redirect the nest sends is kept only if it lands inside the same nest on this origin (`/n/<nestId>/...`), as a
 * relative address; anything else would make this service's address bounce people to wherever a nest host likes.
 */
export function safeRelayLocation(location: string, requestUrl: URL, nestId: string): string | null {
  let target: URL;
  try {
    target = new URL(location, requestUrl);
  } catch {
    return null;
  }
  if (target.origin !== requestUrl.origin) return null;
  // Dot segments are already resolved by URL; a backslash or encoded slash cannot smuggle a second prefix.
  if (!target.pathname.startsWith(`/n/${nestId}/`)) return null;
  return target.pathname + target.search;
}

/** `where` is the relayed request's address and nest, to judge a redirect by; without it no Location passes. */
export function finalizeRelay(resp: Response, where?: { url: URL; nestId: string }): Response {
  // A WebSocket upgrade is handed back untouched (its headers no longer matter and the socket must stay attached).
  if (resp.status === 101 || resp.webSocket) return resp;
  const headers = new Headers();
  for (const [name, value] of resp.headers) {
    if (RELAY_PASS_HEADERS.has(name.toLowerCase())) headers.append(name, value);
  }
  const location = resp.headers.get("Location");
  if (location !== null && where) {
    const safe = safeRelayLocation(location, where.url, where.nestId);
    if (safe !== null) headers.set("Location", safe);
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
