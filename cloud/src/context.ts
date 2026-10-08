// Per-request context handed to every handler.

import type { Env } from "./env";
import type { Mailer } from "./mail";

/** What the Worker needs from the outside world; tests replace each part. */
export interface Deps {
  /** Sends email. Default: Resend (built from the environment). */
  mailer?: Mailer;
  /** Outbound HTTP (Google, Turnstile, Resend). */
  fetch: typeof fetch;
  /** Unix seconds. */
  now: () => number;
}

export interface Ctx {
  req: Request;
  url: URL;
  env: Env;
  deps: Deps;
  exec: ExecutionContext;
  now: number;
  /** CF-Connecting-IP (or "unknown"); only ever stored as a keyed hash. */
  ip: string;
  /** Raw request body (read once, at most 16 KiB) for requests that have one. */
  body: Uint8Array;
  /** Set-Cookie lines to add to the response. */
  setCookies: string[];
  mailer: Mailer;
  /** Work that may finish after the response (email sending). */
  defer(p: Promise<unknown>): void;
}

export function defaultDeps(): Deps {
  return {
    fetch: (input, init) => fetch(input, init),
    now: () => Math.floor(Date.now() / 1000),
  };
}
