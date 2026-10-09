// GET /nest-login?nest=&nonce=&return= (CONTRACT.md section 3.2). Every failure is a page on this service; the only
// redirect to a nest carries a valid assertion to the registered address.

import { signAssertion, signingKeyFromSecret } from "../assertion";
import { audit } from "../audit";
import type { NestRow } from "./account";
import type { Ctx } from "../context";
import { NEST_ID_RE, NONCE_RE } from "../formats";
import { htmlResponse, redirect } from "../http";
import { errorPage } from "../pages";
import { hit, HOUR } from "../ratelimit";
import { loadBrowserSession } from "../sessions";

export async function nestLoginRoute(ctx: Ctx): Promise<Response> {
  const p = ctx.url.searchParams;
  const nestId = p.get("nest") ?? "";
  const nonce = p.get("nonce") ?? "";
  const ret = p.get("return");

  // 1. Parameters.
  if (!NEST_ID_RE.test(nestId) || !NONCE_RE.test(nonce) || !ret || ret.length > 300) {
    return htmlResponse(400, errorPage("This sign-in link is not valid", "Start again from your nest's sign-in page."));
  }

  // 2. Signed in here? Otherwise sign in first and come back to exactly this request.
  const s = await loadBrowserSession(ctx);
  if (!s) return redirect(`/login?next=${encodeURIComponent(`/nest-login?${ctx.url.search.slice(1)}`)}`);

  // 3. The nest exists and is this account's (one answer for both). Only nests on their own domain have a website to
  //    sign in to; a relayed one (RELAY.md) gets the same answer as an unknown nest.
  const nest = await ctx.env.DB.prepare("SELECT * FROM nests WHERE id = ?1 AND mode = 'url'").bind(nestId).first<NestRow>();
  if (!nest || nest.account_id !== s.accountId) {
    return htmlResponse(404, errorPage("Not your nest", "This nest is not linked to your Pairnets account."));
  }

  // 4. Linking finished.
  if (nest.status !== "active") {
    return htmlResponse(409, errorPage("Not linked yet", "This nest has not finished linking."));
  }

  // 5. Sign-in switched on for this nest, and the emergency switch is off.
  if (nest.hosted_login !== 1 || ctx.env.HOSTED_LOGIN_DISABLED === "1") {
    return htmlResponse(
      403,
      errorPage("Sign-in is turned off", "Signing in to this nest with your Pairnets account is turned off. Use the nest's own sign-in, or turn it back on in your account."),
    );
  }

  // 6. The return address is exactly the registered one.
  if (ret !== `${nest.public_url}/hosted-return`) {
    return htmlResponse(400, errorPage("Address changed", "This nest's address changed. Link it again."));
  }

  // 7. 60 per hour per account.
  const limit = await hit(ctx, "nest-login", s.accountId, 60, HOUR);
  if (limit.limited) {
    return htmlResponse(429, errorPage("Too many sign-ins", "Too many nest sign-ins in the last hour. Wait a little and try again."), {
      "Retry-After": String(limit.retryAfter),
    });
  }

  // 8. The assertion.
  const sk = await signingKeyFromSecret(ctx.env.SIGNING_KEY);
  const jws = await signAssertion(sk, {
    nestId: nest.id,
    accountId: s.accountId,
    nonce,
    amr: s.amr,
    authTime: s.authTime,
    iat: ctx.now,
  });
  await audit(ctx, "nest_login", s.accountId, nest.id);
  return new Response(null, {
    status: 302,
    headers: { Location: `${nest.public_url}/hosted-return#assertion=${jws}`, "Cache-Control": "no-store", "Referrer-Policy": "no-referrer" },
  });
}
