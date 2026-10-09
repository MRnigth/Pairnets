// The 90-day event log, in D1 and in the Worker's log. `detail` never holds tokens, keys, codes or email addresses.

import type { Env } from "./env";

export async function logEvent(env: Env, now: number, event: string, name: string | null, detail: Record<string, unknown> | null = null): Promise<void> {
  console.log("event", JSON.stringify({ event, name, ...(detail ?? {}) }));
  await env.DB.prepare("INSERT INTO events (at, event, name, detail) VALUES (?1, ?2, ?3, ?4)")
    .bind(now, event, name, detail ? JSON.stringify(detail) : null)
    .run();
}
