// The service's signed calls to a relayed nest (RELAY.md section 4), straight through the ROUTER service binding (never
// through the relay, which refuses /api/relay/* from everyone).
//
//   K = the 32-byte nest key (CONTRACT.md 5.1's heartbeat key: same derivation, same key_version)
//   M = UTF-8("ra1\n" + nestId + "\n" + ts + "\n" + nonce + "\n" + METHOD + " " + pathAndQuery + "\n"
//             + lowercase_hex(SHA-256(exact body bytes)))
//   X-Pairnets-Sig = base64url(HMAC-SHA256(K, M))

import { b64urlEncode, utf8 } from "./b64";
import { hmacSha256, randomToken, sha256Hex } from "./crypto";
import type { Env } from "./env";
import { decodeMaster, deriveHeartbeatKey } from "./nestsig";
import { linkFailed } from "./router";

/** The account page waits this long for a server (RELAY.md 7); the other admin calls use the same. */
export const ADMIN_TIMEOUT_MS = 5000;

/** Where admin calls are addressed; the router uses only the path and query. */
const ROUTER_BASE = "https://pairnets-router";

/** The nest key: derived from HB_MASTER each time, never stored. */
export function nestKey(env: Env, nestId: string, keyVersion: number): Promise<Uint8Array> {
  return deriveHeartbeatKey(decodeMaster(env.HB_MASTER), nestId, keyVersion);
}

/** The signed message M (RELAY.md 4). `method` is uppercased; `pathAndQuery` is what the nest receives. */
export async function adminMessage(nestId: string, ts: string, nonce: string, method: string, pathAndQuery: string, body: Uint8Array): Promise<Uint8Array> {
  return utf8(`ra1\n${nestId}\n${ts}\n${nonce}\n${method.toUpperCase()} ${pathAndQuery}\n${await sha256Hex(body)}`);
}

export async function signAdmin(
  key: Uint8Array,
  nestId: string,
  ts: string,
  nonce: string,
  method: string,
  pathAndQuery: string,
  body: Uint8Array,
): Promise<string> {
  return b64urlEncode(await hmacSha256(key, await adminMessage(nestId, ts, nonce, method, pathAndQuery, body)));
}

/** The nest could not be reached (no link, link down, timeout, or the router does not know it yet). */
export class NestOffline extends Error {
  constructor() {
    super("nest offline");
  }
}

export interface AdminAnswer {
  status: number;
  json: unknown;
}

export interface AdminTarget {
  id: string;
  key_version: number;
}

/** One signed admin call. Throws NestOffline when the nest cannot be reached; any answer of the nest is returned. */
export async function adminCall(env: Env, now: number, nest: AdminTarget, method: "GET" | "POST" | "DELETE", pathAndQuery: string, body?: unknown): Promise<AdminAnswer> {
  const bytes = body === undefined ? new Uint8Array(0) : utf8(JSON.stringify(body));
  const ts = String(now);
  const nonce = randomToken(16);
  const sig = await signAdmin(await nestKey(env, nest.id, nest.key_version), nest.id, ts, nonce, method, pathAndQuery, bytes);
  const headers: Record<string, string> = {
    "X-Pairnets-Route": nest.id,
    "X-Pairnets-Nest": nest.id,
    "X-Pairnets-Ts": ts,
    "X-Pairnets-Nonce": nonce,
    "X-Pairnets-Sig": sig,
    Accept: "application/json",
  };
  if (body !== undefined) headers["Content-Type"] = "application/json";
  let resp: Response;
  try {
    resp = await env.ROUTER.fetch(`${ROUTER_BASE}${pathAndQuery}`, {
      method,
      headers,
      body: body === undefined ? undefined : bytes,
      signal: AbortSignal.timeout(ADMIN_TIMEOUT_MS),
    });
  } catch {
    throw new NestOffline();
  }
  if (linkFailed(resp)) {
    await resp.body?.cancel().catch(() => undefined);
    throw new NestOffline();
  }
  let json: unknown = null;
  try {
    json = await resp.json();
  } catch {
    json = null;
  }
  return { status: resp.status, json };
}

/** A relayed nest answered: a 'pending' one becomes 'active' (one write, ever). */
export async function markReached(env: Env, now: number, nestId: string): Promise<boolean> {
  const r = await env.DB.prepare("UPDATE nests SET status = 'active', confirmed_at = ?1 WHERE id = ?2 AND mode = 'relay' AND status = 'pending'")
    .bind(now, nestId)
    .run();
  return r.meta.changes === 1;
}
