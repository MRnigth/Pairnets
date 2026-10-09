// The normal suite: every test file runs inside the Workers runtime (Miniflare) with a local D1. Nothing talks to
// Cloudflare: remote bindings are off and every key is generated while the tests run.

import { cloudflareTest, readD1Migrations } from "@cloudflare/vitest-pool-workers";
import { defineConfig } from "vitest/config";

/** A path next to this file, as a plain file-system path (Windows drive letters included). */
const here = (p: string) => decodeURIComponent(new URL(p, import.meta.url).pathname).replace(/^\/([A-Za-z]:)/, "$1");

export default defineConfig(async () => {
  const migrations = await readD1Migrations(here("./migrations"));
  return {
    plugins: [
      cloudflareTest({
        main: "./src/index.ts",
        remoteBindings: false,
        wrangler: { configPath: "./wrangler.toml" },
        miniflare: {
          bindings: { TEST_MIGRATIONS: migrations },
          // wrangler.toml binds ROUTER to the pairnets-router Worker, which is not part of this test runtime. Each
          // test gives the Worker its own fake router instead (test/fake-relay.ts); this stand-in only lets the
          // runtime start.
          serviceBindings: { ROUTER: () => new Response("not used: the tests use a fake router", { status: 500 }) },
        },
      }),
    ],
    test: {
      include: ["test/**/*.test.ts"],
      exclude: ["test/vectors.gen.test.ts"],
      setupFiles: ["./test/setup.ts"],
      // One shared local D1: files run one after another and each test starts from empty tables.
      fileParallelism: false,
      // Some tests make RSA keys or send hundreds of requests; CI machines are slower than a desktop.
      testTimeout: 60_000,
    },
  };
});
