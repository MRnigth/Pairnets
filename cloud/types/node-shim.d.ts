// The few Node pieces the two vitest config files and the Node-side vector generator use. Declared here so the
// project needs no @types/node (dev dependencies stay: typescript, wrangler, vitest, vitest-pool-workers, workers-types).

interface ImportMeta {
  readonly url: string;
}

// Vite's `?raw` imports (tests read CONTRACT.md and the migration as text).
declare module "*?raw" {
  const text: string;
  export default text;
}

declare module "node:fs" {
  export function writeFileSync(path: string | URL, data: string, encoding?: "utf8"): void;
  export function readFileSync(path: string | URL, encoding: "utf8"): string;
}
