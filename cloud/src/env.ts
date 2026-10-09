// Worker bindings (CONTRACT.md appendix B, RELAY.md section 8).

export interface Env {
  DB: D1Database;
  /** Service binding to the pairnets-router Worker (cloud/router), which holds one VPC link per relayed nest. */
  ROUTER: Fetcher;

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
  MAIL_FROM: string;
  EMAIL_DAILY_LIMIT: string;
  /** Relay nests in all (default 900, under Cloudflare's 1,000 tunnels per account). */
  MAX_NESTS?: string;
  HOSTED_LOGIN_DISABLED?: string;
  GOOGLE_AUTH_URL?: string;
  GOOGLE_TOKEN_URL?: string;
  GOOGLE_JWKS_URL?: string;
  /** Test seam for local end-to-end runs (a stand-in Cloudflare API). Never set in production. */
  CF_API_URL?: string;
}

/** A whole-number variable, or the fallback when it is missing or not a number. */
export function intVar(value: string | undefined, fallback: number): number {
  const n = Number.parseInt(value ?? "", 10);
  return Number.isFinite(n) && n >= 0 ? n : fallback;
}
