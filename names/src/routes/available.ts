// GET /v1/available?name=alice: can this name be had right now? (The claim checks everything again.)

import type { Ctx } from "../context";
import { json } from "../http";
import { checkName } from "../names";
import { enforce, MINUTE } from "../ratelimit";
import { findName } from "../store";

export async function availableRoute(ctx: Ctx): Promise<Response> {
  await enforce(ctx, "available", ctx.ip, 60, MINUTE);
  const asked = ctx.url.searchParams.get("name") ?? "";
  const check = checkName(asked);
  if (!check.ok) return json(200, { name: asked, available: false, reason: check.code === "bad_name" ? "invalid" : "reserved" });
  if (await findName(ctx, check.name)) return json(200, { name: check.name, available: false, reason: "taken" });
  return json(200, { name: check.name, available: true });
}
