// Request body and D1 value helpers shared by the handlers.

import type { Ctx } from "./context";
import { HttpError } from "./http";

/** The body as a JSON object, or 400 bad_request. An empty body is `{}` only when `allowEmpty`. */
export function jsonBody(ctx: Ctx, allowEmpty = false): Record<string, unknown> {
  if (ctx.body.length === 0) {
    if (allowEmpty) return {};
    throw new HttpError(400, "bad_request");
  }
  let v: unknown;
  try {
    v = JSON.parse(new TextDecoder("utf-8", { fatal: true, ignoreBOM: false }).decode(ctx.body));
  } catch {
    throw new HttpError(400, "bad_request");
  }
  if (!v || typeof v !== "object" || Array.isArray(v)) throw new HttpError(400, "bad_request");
  return v as Record<string, unknown>;
}

/** D1 hands BLOB columns back as number arrays (or buffers); this makes bytes of either. */
export function blobBytes(v: unknown): Uint8Array {
  if (v instanceof Uint8Array) return v;
  if (v instanceof ArrayBuffer) return new Uint8Array(v);
  if (Array.isArray(v)) return Uint8Array.from(v as number[]);
  return new Uint8Array(0);
}
