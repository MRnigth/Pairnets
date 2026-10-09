// Worker bindings (wrangler.toml).

export interface Env {
  DB: D1Database;

  // Secrets (dashboard / `wrangler secret put`; tests make up their own).
  CF_API_TOKEN: string;
  CF_ACCOUNT_ID: string;
  CF_ZONE_ID: string;
  HASH_KEY: string;
  RESEND_API_KEY: string;

  // Variables.
  BASE_DOMAIN: string;
  MAIL_FROM: string;
  MAX_NAMES: string;
  DAILY_NAMES: string;
  MAIL_DAILY_LIMIT: string;
  NAMES_PAUSED?: string;
}

/** A whole-number variable, or the fallback when it is missing or not a number. */
export function intVar(value: string | undefined, fallback: number): number {
  const n = Number.parseInt(value ?? "", 10);
  return Number.isFinite(n) && n >= 0 ? n : fallback;
}
