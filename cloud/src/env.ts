// Worker bindings (CONTRACT.md appendix B, RELAY.md section 8).

import type { EmailSendBinding } from "./mail";

export interface Env {
  DB: D1Database;
  /** Service binding to the pairnets-router Worker (cloud/router), which holds one VPC link per relayed nest. */
  ROUTER: Fetcher;
  /**
   * Workers Rate Limiting bindings (wrangler.toml `[[ratelimits]]`, relay.ts): the flood ceiling per address
   * (RELAY_PER_MINUTE), each computer's own budget (RELAY_DEVICE_PER_MINUTE) and the budget for keyless and failing
   * requests per address (RELAY_IP_PER_MINUTE) and each server's own budget (RELAY_NEST_PER_MINUTE). Missing (a local
   * run without them): the relay does not limit and warns once.
   */
  RELAY_LIMIT?: RateLimit;
  RELAY_DEVICE_LIMIT?: RateLimit;
  RELAY_FAIL_LIMIT?: RateLimit;
  RELAY_NEST_LIMIT?: RateLimit;

  // Secrets (dashboard / `wrangler secret put`; tests generate them).
  SIGNING_KEY: string;
  HB_MASTER: string;
  COOKIE_KEY: string;
  GOOGLE_CLIENT_SECRET: string;
  TURNSTILE_SECRET: string;
  RESEND_API_KEY: string;
  /** Cloudflare API token: Tunnel Edit, Workers Scripts Edit and the Connectivity Directory permission (RELAY.md 8). */
  CF_API_TOKEN: string;
  CF_ACCOUNT_ID: string;

  // Variables.
  PUBLIC_ORIGIN: string;
  GOOGLE_CLIENT_ID: string;
  TURNSTILE_SITE_KEY: string;
  /** The Resend sender (pairnets.app); used alone, or as the fallback when Cloudflare's send fails. */
  MAIL_FROM: string;
  /** The Cloudflare Email Service sender, on mail.pairnets.app. Used first when it and the EMAIL binding are set. */
  MAIL_FROM_CF?: string;
  /** Cloudflare Email Service binding (`[[send_email]]`). */
  EMAIL?: EmailSendBinding;
  /** Where replies to the service's emails go (support@). */
  MAIL_REPLY_TO?: string;
  EMAIL_DAILY_LIMIT: string;
  /** Relay nests in all (default 900, under Cloudflare's 1,000 tunnels per account). */
  MAX_NESTS?: string;
  /** Nests one account may have, both kinds together (default 3); a removed one does not count. */
  MAX_NESTS_PER_ACCOUNT?: string;
  HOSTED_LOGIN_DISABLED?: string;
  GOOGLE_AUTH_URL?: string;
  GOOGLE_TOKEN_URL?: string;
  GOOGLE_JWKS_URL?: string;
  /**
   * Who may read the crash reports (/admin/crashes) and is emailed about new kinds of crash: account emails, comma
   * separated. A secret, so the address is not in the repository. Unset: nobody.
   */
  ADMIN_EMAILS?: string;
  /** Test seam for local end-to-end runs (a stand-in Cloudflare API). Never set in production. */
  CF_API_URL?: string;
}

/**
 * A test seam's address (CF_API_URL, GOOGLE_*_URL), used only when it points at this machine or a `.test` host;
 * anything else gets the real address. A mistaken setting in production can then never send a secret (the Cloudflare
 * token, the Google client secret) to another server, or take Google's signing keys from one.
 */
export function seamUrl(value: string | undefined, real: string): string {
  if (!value) return real;
  try {
    const u = new URL(value);
    const host = u.hostname.toLowerCase();
    const local = host === "127.0.0.1" || host === "localhost" || host === "[::1]";
    if ((local && (u.protocol === "http:" || u.protocol === "https:")) || (host.endsWith(".test") && u.protocol === "https:")) return value;
  } catch {
    // not a URL
  }
  return real;
}

/** A whole-number variable, or the fallback when it is missing or not a number. */
export function intVar(value: string | undefined, fallback: number): number {
  const n = Number.parseInt(value ?? "", 10);
  return Number.isFinite(n) && n >= 0 ? n : fallback;
}
