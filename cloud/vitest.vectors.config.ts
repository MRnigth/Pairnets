// `npm run vectors`: regenerates test/vectors/assertion-v1.json with new throwaway keys (CONTRACT.md section 8.1).
// Runs in plain Node (not the Workers pool) because it writes a file. Only run it on purpose.

import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    environment: "node",
    include: ["test/vectors.gen.test.ts"],
  },
});
