// Cloudflare Turnstile server-side check for the email sign-in form.

import type { Ctx } from "./context";

export const TURNSTILE_VERIFY_URL = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

export async function verifyTurnstile(ctx: Ctx, token: string): Promise<boolean> {
  if (!token || token.length > 2048) return false;
  const form = new URLSearchParams();
  form.set("secret", ctx.env.TURNSTILE_SECRET ?? "");
  form.set("response", token);
  if (ctx.ip !== "unknown") form.set("remoteip", ctx.ip);
  try {
    const resp = await ctx.deps.fetch(TURNSTILE_VERIFY_URL, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: form.toString(),
    });
    if (!resp.ok) return false;
    const data = (await resp.json()) as { success?: unknown };
    return data.success === true;
  } catch {
    return false;
  }
}
