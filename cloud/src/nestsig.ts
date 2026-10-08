// Nest-signed requests and signed answers, CONTRACT.md sections 5.1 and 5.2.

import { b64urlDecodeStrict, b64urlEncode, utf8 } from "./b64";
import { hmacSha256, hmacVerify, sha256Hex } from "./crypto";

export type NestPurpose = "hb1" | "nc1" | "nu1";

/** HB_MASTER: base64url of 32 bytes; the HMAC key is the decoded bytes. */
export function decodeMaster(hbMaster: string): Uint8Array {
  const b = b64urlDecodeStrict(hbMaster ?? "");
  if (!b || b.length !== 32) throw new Error("HB_MASTER must be base64url of 32 bytes");
  return b;
}

/** heartbeatKey = HMAC-SHA256(HB_MASTER, "pn-hb-key\n" + nestId + "\n" + keyVersion) (section 5.1). */
export async function deriveHeartbeatKey(master: Uint8Array, nestId: string, keyVersion: number): Promise<Uint8Array> {
  return hmacSha256(master, `pn-hb-key\n${nestId}\n${keyVersion}`);
}

/** M = UTF-8(P + "\n" + nestId + "\n" + ts + "\n" + lowercase_hex(SHA-256(body))) (section 5.2). */
export async function requestMessage(purpose: string, nestId: string, ts: string, body: Uint8Array): Promise<Uint8Array> {
  return utf8(`${purpose}\n${nestId}\n${ts}\n${await sha256Hex(body)}`);
}

export async function signRequest(key: Uint8Array, purpose: NestPurpose, nestId: string, ts: string, body: Uint8Array): Promise<string> {
  return b64urlEncode(await hmacSha256(key, await requestMessage(purpose, nestId, ts, body)));
}

/** Checks X-Pairnets-Sig: 43-character strict base64url (32 bytes) and a constant-time HMAC verify. */
export async function verifyRequest(
  key: Uint8Array,
  purpose: NestPurpose,
  nestId: string,
  ts: string,
  body: Uint8Array,
  sig: string | null,
): Promise<boolean> {
  if (typeof sig !== "string" || sig.length !== 43) return false;
  const mac = b64urlDecodeStrict(sig);
  if (!mac || mac.length !== 32) return false;
  return hmacVerify(key, await requestMessage(purpose, nestId, ts, body), mac);
}

/** Signature of a 200 answer: P + "-resp" with the service's ts (section 5.2). */
export async function signResponse(key: Uint8Array, purpose: NestPurpose, nestId: string, respTs: number, body: string): Promise<string> {
  return b64urlEncode(await hmacSha256(key, await requestMessage(`${purpose}-resp`, nestId, String(respTs), utf8(body))));
}
