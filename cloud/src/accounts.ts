// Accounts and identities (CONTRACT.md section 6.6).

import { audit } from "./audit";
import type { Ctx } from "./context";
import { newId } from "./crypto";
import { sendNotice } from "./mail";

export type Provider = "google" | "email";

/**
 * Deletes an account and everything stored about it ("Delete account", and accounts unused for a year, inactive.ts).
 * Returns the tunnel ids of its relayed servers: their rows go with the account, and the caller runs the router,
 * which drops their links and deletes the tunnels (the hourly cron deletes any tunnel left over).
 */
export async function deleteAccountRows(ctx: Ctx, id: string, email: string): Promise<string[]> {
  const db = ctx.env.DB;
  const tunnels = (
    await db.prepare("SELECT tunnel_id FROM nests WHERE account_id = ?1 AND mode = 'relay' AND tunnel_id IS NOT NULL").bind(id).all<{ tunnel_id: string }>()
  ).results.map((r) => r.tunnel_id);
  // Foreign keys cascade from accounts; the explicit deletes make the result independent of that setting. Friends
  // (RELAY.md 10): the people and invites of its own nests go with them; where it was a friend, it is removed as if
  // the owner had done it (the cron tells those nests, which then drop its computers).
  await db.batch([
    db.prepare("DELETE FROM nest_invites WHERE created_by = ?1 OR requested_by = ?1 OR nest_id IN (SELECT id FROM nests WHERE account_id = ?1)").bind(id),
    db.prepare("DELETE FROM nest_members WHERE nest_id IN (SELECT id FROM nests WHERE account_id = ?1)").bind(id),
    db.prepare("UPDATE nest_members SET removed_at = COALESCE(removed_at, ?2), synced = 0 WHERE account_id = ?1").bind(id, ctx.now),
    db.prepare("DELETE FROM sessions WHERE account_id = ?1").bind(id),
    db.prepare("DELETE FROM identities WHERE account_id = ?1").bind(id),
    db.prepare("DELETE FROM nests WHERE account_id = ?1").bind(id),
    db.prepare("DELETE FROM claim_codes WHERE account_id = ?1").bind(id),
    db.prepare("DELETE FROM device_logins WHERE account_id = ?1").bind(id),
    db.prepare("DELETE FROM server_links WHERE account_id = ?1").bind(id),
    db.prepare("DELETE FROM audit WHERE account_id = ?1").bind(id),
    db.prepare("DELETE FROM login_challenges WHERE email = ?1").bind(email),
    db.prepare("DELETE FROM accounts WHERE id = ?1").bind(id),
  ]);
  return tunnels;
}

/**
 * Finds or makes the account for a verified identity:
 *  1. the identity exists -> its account (the identity's email and last_used_at are updated);
 *  2. an account has that verified email -> the identity is added to it, a notice goes to the account address;
 *  3. otherwise a new account.
 * `email` must already be ASCII-lowercased.
 */
export async function accountForIdentity(ctx: Ctx, provider: Provider, subject: string, email: string): Promise<string> {
  for (let attempt = 0; attempt < 2; attempt++) {
    const existing = await ctx.env.DB.prepare("SELECT account_id FROM identities WHERE provider = ?1 AND subject = ?2")
      .bind(provider, subject)
      .first<{ account_id: string }>();
    if (existing) {
      await ctx.env.DB.prepare("UPDATE identities SET email = ?1, last_used_at = ?2 WHERE provider = ?3 AND subject = ?4")
        .bind(email, ctx.now, provider, subject)
        .run();
      return existing.account_id;
    }

    const byEmail = await ctx.env.DB.prepare("SELECT id, email FROM accounts WHERE email = ?1").bind(email).first<{ id: string; email: string }>();
    if (byEmail) {
      const r = await ctx.env.DB.prepare(
        `INSERT INTO identities (provider, subject, account_id, email, created_at, last_used_at)
         VALUES (?1, ?2, ?3, ?4, ?5, ?5) ON CONFLICT DO NOTHING`,
      )
        .bind(provider, subject, byEmail.id, email, ctx.now)
        .run();
      if (r.meta.changes !== 1) continue; // someone else added it at the same moment: start over
      await audit(ctx, "identity_linked", byEmail.id, null, { provider });
      await sendNotice(ctx, byEmail.id, byEmail.email, provider === "google" ? "identity_added_google" : "identity_added_email");
      return byEmail.id;
    }

    const id = newId("acc");
    try {
      await ctx.env.DB.batch([
        ctx.env.DB.prepare("INSERT INTO accounts (id, email, created_at, updated_at) VALUES (?1, ?2, ?3, ?3)").bind(id, email, ctx.now),
        ctx.env.DB.prepare(
          `INSERT INTO identities (provider, subject, account_id, email, created_at, last_used_at)
           VALUES (?1, ?2, ?3, ?4, ?5, ?5)`,
        ).bind(provider, subject, id, email, ctx.now),
      ]);
    } catch {
      continue; // a parallel sign-in created the account or identity first: look again
    }
    await audit(ctx, "account_created", id, null, { provider });
    return id;
  }
  throw new Error("could not settle the account for this sign-in");
}
