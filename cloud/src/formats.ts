// Identifiers and formats, CONTRACT.md section 1 and RELAY.md section 1 (and the `next` rule of section 6.4).

export const ACCOUNT_ID_RE = /^acc_[0-9a-hjkmnp-tv-z]{26}$/;
export const NEST_ID_RE = /^nst_[0-9a-hjkmnp-tv-z]{26}$/;
export const SESSION_ID_RE = /^ses_[0-9a-hjkmnp-tv-z]{26}$/;
export const NONCE_RE = /^[A-Za-z0-9_-]{22,64}$/;
export const KID_RE = /^[A-Za-z0-9._-]{1,64}$/;
/** The installer's device code (RELAY.md 1): psd_ + base64url(32 bytes). */
export const SERVER_DEVICE_CODE_RE = /^psd_[A-Za-z0-9_-]{43}$/;
/** A device id as the nest hands it out (checked loosely: it only ever goes back to that nest in a path). */
export const DEVICE_ID_RE = /^[A-Za-z0-9_-]{1,64}$/;

const B32_UPPER_RE = /^[0-9ABCDEFGHJKMNPQRSTVWXYZ]+$/;

function stripAndUpper(input: string): string {
  // Step 1: remove every space, tab and "-". Step 2: ASCII uppercase only.
  return input.replace(/[ \t-]/g, "").replace(/[a-z]/g, (c) => c.toUpperCase());
}

function mapLookAlikes(s: string): string {
  // Step 4: I -> 1, L -> 1, O -> 0.
  return s.replace(/[ILO]/g, (c) => (c === "O" ? "0" : "1"));
}

/** Claim code normalisation (section 1.3). Returns the canonical "PN-XXXX-XXXX-XXXX" or null. */
export function normaliseClaimCode(input: unknown): string | null {
  if (typeof input !== "string" || input.length > 64) return null;
  let s = stripAndUpper(input);
  if (s.length === 14 && s.startsWith("PN")) s = s.slice(2);
  s = mapLookAlikes(s);
  if (s.length !== 12 || !B32_UPPER_RE.test(s)) return null;
  return `PN-${s.slice(0, 4)}-${s.slice(4, 8)}-${s.slice(8, 12)}`;
}

/** App and server user code normalisation (section 1.4). Returns the 8 characters without hyphen, or null. */
export function normaliseUserCode(input: unknown): string | null {
  if (typeof input !== "string" || input.length > 32) return null;
  const s = mapLookAlikes(stripAndUpper(input));
  if (s.length !== 8 || !B32_UPPER_RE.test(s)) return null;
  return s;
}

export function displayUserCode(code: string): string {
  return `${code.slice(0, 4)}-${code.slice(4, 8)}`;
}

const NEXT_RE = /^\/(account|app(\?code=[0-9A-Za-z-]{1,16})?|add(\?code=[0-9A-Za-z-]{1,16})?|nest-login\?[^#\s]{1,600})$/;

/** The `next` allow-list (section 6.4, plus RELAY.md's /add?code=); anything else becomes "/account". */
export function safeNext(next: unknown): string {
  return typeof next === "string" && NEXT_RE.test(next) ? next : "/account";
}

const IPV4_RE = /^\d{1,3}(\.\d{1,3}){3}$/;
const REFUSED_HOSTS = new Set(["localhost", "pairnets.app", "www.pairnets.app", "id.pairnets.app", "sync.pairnets.app"]);

/**
 * Normalised public URL check (section 1.8): the input must already equal `new URL(input).origin`, be https, and not be
 * an IP literal, a dotless host, localhost or one of Pairnets' own names; at most 200 characters.
 * Returns { url, host } or null.
 */
export function checkPublicUrl(input: unknown): { url: string; host: string } | null {
  if (typeof input !== "string" || input.length === 0 || input.length > 200) return null;
  let u: URL;
  try {
    u = new URL(input);
  } catch {
    return null;
  }
  if (u.protocol !== "https:") return null;
  if (input !== u.origin) return null;
  const host = u.hostname;
  if (host.startsWith("[") || IPV4_RE.test(host)) return null;
  if (!host.includes(".")) return null;
  // A trailing dot would slip past the name checks below ("pairnets.app."), so it is refused too.
  if (host.endsWith(".")) return null;
  if (REFUSED_HOSTS.has(host) || host.endsWith(".localhost")) return null;
  return { url: u.origin, host: u.host };
}

/** "y***@gmail.com": first character, "***", "@", the domain; ASCII only (section 4.2). */
export function maskEmail(email: string): string {
  const at = email.lastIndexOf("@");
  const local = at > 0 ? email.slice(0, at) : email;
  const domain = at > 0 ? email.slice(at + 1) : "";
  const first = /^[\x21-\x7e]/.test(local) ? local[0] : "*";
  let asciiDomain = "***";
  if (/^[\x21-\x7e]+$/.test(domain)) {
    asciiDomain = domain;
  } else if (domain) {
    try {
      asciiDomain = new URL(`https://${domain}`).hostname || "***";
    } catch {
      asciiDomain = "***";
    }
  }
  return `${first}***@${asciiDomain}`;
}

/**
 * A label: control characters dropped, trimmed, 1-64 characters. Returns null for "absent or empty after cleaning",
 * or the string "invalid" when it is not a string or too long.
 */
export function cleanLabel(input: unknown): string | null | "invalid" {
  if (input === undefined || input === null) return null;
  if (typeof input !== "string") return "invalid";
  // eslint-disable-next-line no-control-regex
  const s = input.replace(/[\u0000-\u001f\u007f-\u009f]/g, "").trim();
  if (s.length === 0) return null;
  if ([...s].length > 64) return "invalid";
  return s;
}

/** A short free-text field from an app or nest (name, system, version): same cleaning as labels, max length given. */
export function cleanText(input: unknown, max: number): string | null | "invalid" {
  if (input === undefined || input === null) return null;
  if (typeof input !== "string") return "invalid";
  // eslint-disable-next-line no-control-regex
  const s = input.replace(/[\u0000-\u001f\u007f-\u009f]/g, "").trim();
  if (s.length === 0) return null;
  if ([...s].length > max) return "invalid";
  return s;
}

/** Email addresses are compared and stored ASCII-lowercased; a simple shape check. */
export function normaliseEmail(input: unknown): string | null {
  if (typeof input !== "string") return null;
  const s = input.trim().replace(/[A-Z]/g, (c) => c.toLowerCase());
  if (s.length < 3 || s.length > 254) return null;
  if (!/^[^\s@"<>()\[\]\\,;:]+@[^\s@"<>()\[\]\\,;:]+\.[^\s@"<>()\[\]\\,;:.]+$/.test(s)) return null;
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001f\u007f]/.test(s)) return null;
  return s;
}

/** Unix seconds -> "2026-10-09T00:00:00Z". */
export function iso(sec: number): string;
export function iso(sec: number | null | undefined): string | null;
export function iso(sec: number | null | undefined): string | null {
  if (sec === null || sec === undefined) return null;
  return new Date(sec * 1000).toISOString().replace(/\.\d{3}Z$/, "Z");
}
