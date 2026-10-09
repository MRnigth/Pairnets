// Per-request context handed to every handler.

import type { CloudflareApi } from "./cloudflare";
import type { Env } from "./env";
import type { Mailer } from "./mail";

/** What the Worker needs from the outside world; tests replace each part. */
export interface Deps {
  /** Sends email. Default: Resend (built from the environment). */
  mailer?: Mailer;
  /** Outbound HTTP (the Cloudflare API, Resend). */
  fetch: typeof fetch;
  /** Unix seconds. */
  now: () => number;
}

export interface Ctx {
  req: Request;
  url: URL;
  env: Env;
  now: number;
  /** CF-Connecting-IP (or "unknown"); only ever stored as a keyed hash. */
  ip: string;
  /** Raw request body (read once, at most 16 KiB) for requests that have one. */
  body: Uint8Array;
  mailer: Mailer;
  cf: CloudflareApi;
}

export function defaultDeps(): Deps {
  return {
    fetch: (input, init) => fetch(input, init),
    now: () => Math.floor(Date.now() / 1000),
  };
}
