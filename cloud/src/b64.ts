// Strict base64url (RFC 4648 section 5, never padded), CONTRACT.md section 1.7.
// Pure functions: also used by the Node-side golden-vector generator.

export const B64URL_ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

const LOOKUP: Int16Array = (() => {
  const t = new Int16Array(128).fill(-1);
  for (let i = 0; i < B64URL_ALPHABET.length; i++) t[B64URL_ALPHABET.charCodeAt(i)] = i;
  return t;
})();

export function b64urlEncode(bytes: Uint8Array | ArrayBuffer): string {
  const b = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
  let out = "";
  let i = 0;
  for (; i + 2 < b.length; i += 3) {
    const n = (b[i] << 16) | (b[i + 1] << 8) | b[i + 2];
    out += B64URL_ALPHABET[(n >> 18) & 63] + B64URL_ALPHABET[(n >> 12) & 63] + B64URL_ALPHABET[(n >> 6) & 63] + B64URL_ALPHABET[n & 63];
  }
  const rest = b.length - i;
  if (rest === 1) {
    const n = b[i] << 16;
    out += B64URL_ALPHABET[(n >> 18) & 63] + B64URL_ALPHABET[(n >> 12) & 63];
  } else if (rest === 2) {
    const n = (b[i] << 16) | (b[i + 1] << 8);
    out += B64URL_ALPHABET[(n >> 18) & 63] + B64URL_ALPHABET[(n >> 12) & 63] + B64URL_ALPHABET[(n >> 6) & 63];
  }
  return out;
}

/**
 * Decodes strict base64url. Returns null unless: every character is in the alphabet, length % 4 != 1, and
 * re-encoding the bytes gives the same string back (no non-zero unused bits).
 */
export function b64urlDecodeStrict(s: string): Uint8Array | null {
  if (typeof s !== "string") return null;
  if (s.length % 4 === 1) return null;
  const vals = new Uint8Array(s.length);
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    const v = c < 128 ? LOOKUP[c] : -1;
    if (v < 0) return null;
    vals[i] = v;
  }
  const outLen = Math.floor((s.length * 3) / 4);
  const out = new Uint8Array(outLen);
  let o = 0;
  let i = 0;
  for (; i + 3 < s.length; i += 4) {
    const n = (vals[i] << 18) | (vals[i + 1] << 12) | (vals[i + 2] << 6) | vals[i + 3];
    out[o++] = (n >> 16) & 255;
    out[o++] = (n >> 8) & 255;
    out[o++] = n & 255;
  }
  const rest = s.length - i;
  if (rest === 2) {
    const n = (vals[i] << 18) | (vals[i + 1] << 12);
    out[o++] = (n >> 16) & 255;
  } else if (rest === 3) {
    const n = (vals[i] << 18) | (vals[i + 1] << 12) | (vals[i + 2] << 6);
    out[o++] = (n >> 16) & 255;
    out[o++] = (n >> 8) & 255;
  }
  if (b64urlEncode(out) !== s) return null;
  return out;
}

export function isStrictB64url(s: string, byteLength?: number): boolean {
  const d = b64urlDecodeStrict(s);
  return d !== null && (byteLength === undefined || d.length === byteLength);
}

const encoder = new TextEncoder();

export function utf8(s: string): Uint8Array {
  return encoder.encode(s);
}

export function b64urlEncodeText(s: string): string {
  return b64urlEncode(utf8(s));
}
