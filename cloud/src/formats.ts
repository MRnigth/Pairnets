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
/** An invite (RELAY.md 10): its id, and the token in its link (pni_ + 32 base32-lower, 160 bits). */
export const INVITE_ID_RE = /^inv_[0-9a-hjkmnp-tv-z]{26}$/;
export const INVITE_TOKEN_RE = /^pni_[0-9a-hjkmnp-tv-z]{32}$/;

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

const NEXT_RE =
  /^\/(account(\?invite=nst_[0-9a-hjkmnp-tv-z]{26}(&[A-Za-z0-9%._~,+=&!*'()-]{0,600})?)?|app(\?code=[0-9A-Za-z-]{1,16})?|add(\?code=[0-9A-Za-z-]{1,16})?|invite\?t=pni_[0-9a-hjkmnp-tv-z]{32}|nest-login\?[^#\s]{1,600})$/;

/**
 * The `next` allow-list (section 6.4, plus RELAY.md's /add?code=, and for friends the invite link /invite?t= and the
 * apps' hand-over to the invite form, /account?invite=nst_…&folders=…&access=…); anything else becomes "/account".
 */
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

/** "y•••@gmail.com": maskEmail with dots, for the lines people read (a friend's name on the nest, an invite). */
export function dottedEmail(email: string): string {
  return maskEmail(email).replace("***@", "•••@");
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
 * Control characters plus invisible "format" characters: soft hyphen, Arabic letter mark, Mongolian vowel separator,
 * zero-width spaces and joiners, left-to-right/right-to-left marks and overrides, word joiner and invisible
 * operators, bidi isolates and the byte-order mark. They let a name read as something it is not (a flipped or padded
 * computer name on the Allow page), and no ordinary text needs them. Letters of every script stay.
 */
const HIDDEN_RANGES: [number, number][] = [
  [0x00, 0x1f],
  [0x7f, 0x9f],
  [0xad, 0xad],
  [0x61c, 0x61c],
  [0x180e, 0x180e],
  [0x200b, 0x200f],
  [0x202a, 0x202e],
  [0x2060, 0x2064],
  [0x2066, 0x2069],
  [0xfeff, 0xfeff],
];
const HIDDEN_CHARS = new RegExp(`[${HIDDEN_RANGES.map(([a, b]) => `${esc(a)}-${esc(b)}`).join("")}]`, "g");
function esc(n: number): string {
  return "\\" + "u" + n.toString(16).padStart(4, "0");
}

export function stripHidden(s: string): string {
  return s.replace(HIDDEN_CHARS, "");
}

/** "Copenhagen, Denmark" from what Cloudflare knows about a request; null when it knows nothing usable. */
export function placeName(country: string | null, city: string | null): string | null {
  let countryName: string | null = null;
  // XX means unknown and T1 is the Tor network: neither says where a computer is.
  if (country && /^[A-Z]{2}$/.test(country) && country !== "XX" && country !== "T1") {
    try {
      countryName = new Intl.DisplayNames(["en"], { type: "region" }).of(country) ?? country;
    } catch {
      countryName = country;
    }
  }
  const parts = [city, countryName].filter((x): x is string => !!x);
  return parts.length ? parts.join(", ") : null;
}

/**
 * A label: control and invisible characters dropped, trimmed, 1-64 characters. Returns null for "absent or empty after cleaning",
 * or the string "invalid" when it is not a string or too long.
 */
export function cleanLabel(input: unknown): string | null | "invalid" {
  if (input === undefined || input === null) return null;
  if (typeof input !== "string") return "invalid";
  const s = stripHidden(input).trim();
  if (s.length === 0) return null;
  if ([...s].length > 64) return "invalid";
  return s;
}

/** Usernames are a display name only (never used to sign in), so they need not be unique. */
export const MAX_USERNAME = 32;

/** The part of an email address before the @: what an account without a username is called. */
export function emailName(email: string): string {
  return email.slice(0, email.lastIndexOf("@"));
}

/** What an account is called on screen and in emails: its username, or else the part of its email before the @. */
export function personName(account: { username: string | null; email: string }): string {
  return account.username ?? emailName(account.email);
}

/** What a friend may do on a nest (RELAY.md 10): "view" (View only) or "edit" (Can change). The owner is "owner". */
export type FriendAccess = "view" | "edit";

/** `access` from a request: "view" or "edit"; missing means "view" (the default); anything else is null (400). */
export function cleanAccess(input: unknown): FriendAccess | null {
  if (input === undefined || input === null) return "view";
  return input === "view" || input === "edit" ? input : null;
}

export const MAX_SHARED_FOLDERS = 20;
const MAX_FOLDER_NAME = 255;

/**
 * `folders` from a request (RELAY.md 10): null or missing = the whole nest; else 1-20 top-level folder names, exact
 * case, each 1-255 characters with no "/", "\", control or invisible character, and not "." or "..". Repeats are
 * dropped. Returns "invalid" for anything else (400 bad_access).
 */
export function cleanFolders(input: unknown): string[] | null | "invalid" {
  if (input === undefined || input === null) return null;
  if (!Array.isArray(input) || input.length === 0 || input.length > MAX_SHARED_FOLDERS) return "invalid";
  const out: string[] = [];
  for (const f of input) {
    if (typeof f !== "string" || f.length === 0 || f.length > MAX_FOLDER_NAME) return "invalid";
    if (f === "." || f === ".." || /[/\\]/.test(f) || stripHidden(f) !== f || f.trim() !== f) return "invalid";
    if (!out.includes(f)) out.push(f);
  }
  return out;
}

/** The folders as stored (a JSON array, or NULL for the whole nest) and back. A bad stored value reads as no folders. */
export function foldersJson(folders: string[] | null): string | null {
  return folders === null ? null : JSON.stringify(folders);
}

export function parseFolders(stored: string | null): string[] | null {
  if (stored === null) return null;
  try {
    const v: unknown = JSON.parse(stored);
    if (Array.isArray(v) && v.every((x) => typeof x === "string")) return v as string[];
  } catch {
    // fall through
  }
  return [];
}

/** A short free-text field from an app or nest (name, system, version): same cleaning as labels, max length given. */
export function cleanText(input: unknown, max: number): string | null | "invalid" {
  if (input === undefined || input === null) return null;
  if (typeof input !== "string") return "invalid";
  const s = stripHidden(input).trim();
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
