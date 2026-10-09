// What a name may look like, which names are kept back, and email addresses.

/**
 * 3 to 32 characters of a-z, 0-9 and "-", no hyphen at the start or end. Two hyphens in a row are refused too
 * (that shape, like "xn--", is kept for international names). deploy/install.sh and deploy/pairnets-name.sh check
 * the same rule before they ask.
 */
export const NAME_RE = /^[a-z0-9][a-z0-9-]{1,30}[a-z0-9]$/;

/**
 * Names the service never hands out: the names Pairnets itself uses or may use under BASE_DOMAIN (the website, the
 * account service id.pairnets.app, this service, email), common service and admin names people would trust, and
 * names that look official. A name already in the zone's DNS is refused too: Cloudflare will not add a second record
 * for it (routes/claim.ts).
 */
export const RESERVED = new Set([
  "www", "id", "names", "name", "nest", "nests", "api", "app", "apps", "admin", "administrator", "root", "sudo",
  "mail", "email", "smtp", "imap", "pop", "pop3", "mx", "ns", "ns1", "ns2", "dns", "send", "rsend", "resend",
  "noreply", "no-reply", "postmaster", "hostmaster", "webmaster", "abuse", "security", "support", "help", "hello",
  "info", "contact", "status", "docs", "doc", "blog", "news", "download", "downloads", "get", "install", "update",
  "updates", "dash", "dashboard", "account", "accounts", "login", "signin", "sign-in", "signup", "sign-up", "auth",
  "oauth", "sso", "cdn", "static", "assets", "media", "files", "cloud", "hosted", "tunnel", "tunnels", "sync",
  "autoconfig", "autodiscover", "ftp", "git", "shop", "store", "billing", "pay", "legal", "privacy", "terms",
  "test", "testing", "dev", "staging", "stage", "beta", "alpha", "demo", "example", "localhost", "cloudflare",
  "official", "team", "staff",
]);

export type NameCheck = { ok: true; name: string } | { ok: false; code: "bad_name" | "name_reserved" };

export function checkName(v: unknown): NameCheck {
  if (typeof v !== "string" || !NAME_RE.test(v) || v.includes("--")) return { ok: false, code: "bad_name" };
  // Anything with "pairnets" in it would look like it is ours.
  if (RESERVED.has(v) || v.includes("pairnets")) return { ok: false, code: "name_reserved" };
  return { ok: true, name: v };
}

/**
 * A plain email address, trimmed and lowercased, or null. Kept simple on purpose: the code sent to it is the real
 * check. No quotes, spaces, angle brackets or commas, so it can never change the shape of the email's headers.
 */
export function normalizeEmail(v: unknown): string | null {
  if (typeof v !== "string") return null;
  const email = v.trim().toLowerCase();
  if (email.length > 254) return null;
  const m = /^([a-z0-9.!#$%&'*+/=?^_`{|}~-]{1,64})@([a-z0-9-]+(?:\.[a-z0-9-]+)+)$/.exec(email);
  if (!m || m[1].startsWith(".") || m[1].endsWith(".") || m[1].includes("..")) return null;
  if (!/\.[a-z]{2,}$/.test(m[2])) return null;
  return email;
}
