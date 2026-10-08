// Every test starts from the migrated schema with empty tables.

import { applyD1Migrations } from "cloudflare:test";
import { env } from "cloudflare:workers";
import { beforeEach } from "vitest";
import { resetGoogleKeyCache } from "../src/google";

export const TABLES = [
  "audit",
  "rate_counters",
  "sessions",
  "identities",
  "login_challenges",
  "device_logins",
  "claim_codes",
  "nests",
  "accounts",
];

beforeEach(async () => {
  await applyD1Migrations(env.DB, env.TEST_MIGRATIONS);
  await env.DB.batch(TABLES.map((t) => env.DB.prepare(`DELETE FROM ${t}`)));
  resetGoogleKeyCache();
});
