// Worker bindings (CONTRACT.md appendix B).

export interface Env {
  DB: D1Database;

  // Secrets (dashboard / `wrangler secret put`; tests generate them).
  SIGNING_KEY: string;
  HB_MASTER: string;
  COOKIE_KEY: string;
  GOOGLE_CLIENT_SECRET: string;
  TURNSTILE_SECRET: string;
  RESEND_API_KEY: string;

  // Variables.
  PUBLIC_ORIGIN: string;
  GOOGLE_CLIENT_ID: string;
  TURNSTILE_SITE_KEY: string;
  MAIL_FROM: string;
  EMAIL_DAILY_LIMIT: string;
  HOSTED_LOGIN_DISABLED?: string;
  GOOGLE_AUTH_URL?: string;
  GOOGLE_TOKEN_URL?: string;
  GOOGLE_JWKS_URL?: string;
}
