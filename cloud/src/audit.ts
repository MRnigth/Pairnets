// The 90-day audit log. `detail` is small JSON and never holds codes, tokens, keys or assertions.

import type { Ctx } from "./context";

export async function audit(
  ctx: Ctx,
  event: string,
  accountId: string | null,
  nestId: string | null = null,
  detail: Record<string, unknown> | null = null,
): Promise<void> {
  await ctx.env.DB.prepare("INSERT INTO audit (at, account_id, event, nest_id, detail) VALUES (?1, ?2, ?3, ?4, ?5)")
    .bind(ctx.now, accountId, event, nestId, detail ? JSON.stringify(detail) : null)
    .run();
}
