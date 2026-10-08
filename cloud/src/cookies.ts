// Cookies (CONTRACT.md section 6.2): all `Secure; HttpOnly; Path=/; SameSite=Lax`, no Domain (the __Host- rule).

export const SESSION_COOKIE = "__Host-pn_id";
export const GOOGLE_COOKIE = "__Host-pn_g";
export const EMAIL_COOKIE = "__Host-pn_el";

export function readCookie(req: Request, name: string): string | null {
  const header = req.headers.get("Cookie");
  if (!header) return null;
  for (const part of header.split(";")) {
    const eq = part.indexOf("=");
    if (eq < 0) continue;
    if (part.slice(0, eq).trim() === name) return part.slice(eq + 1).trim();
  }
  return null;
}

export function setCookie(name: string, value: string, maxAge: number): string {
  return `${name}=${value}; Max-Age=${Math.max(0, Math.floor(maxAge))}; Path=/; Secure; HttpOnly; SameSite=Lax`;
}

export function clearCookie(name: string): string {
  return setCookie(name, "", 0);
}
