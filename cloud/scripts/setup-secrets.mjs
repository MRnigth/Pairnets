// Sets the pairnets-sync Worker's secrets (cloud/README.md, step 6), one at a time:
//
//   cd cloud && node scripts/setup-secrets.mjs            only the secrets that are not set yet
//   cd cloud && node scripts/setup-secrets.mjs --all      every secret again
//   cd cloud && node scripts/setup-secrets.mjs --only CF_API_TOKEN,TURNSTILE_SECRET   just these (set or not)
//
// The random ones (SIGNING_KEY, HB_MASTER, COOKIE_KEY) are made here and go straight to Cloudflare: they are never shown
// and never written to a file. The ones from other services are pasted by you, hidden as you type. The two public ids
// (GOOGLE_CLIENT_ID, TURNSTILE_SITE_KEY) go here too, so that no id or key is in the code at all. Each value reaches
// `wrangler secret put` on its standard input, never on a command line. Needs `npx wrangler login` first.
//
// Changing HB_MASTER later breaks every linked nest (their keys are derived from it); the script asks before it does.

import { spawnSync } from "node:child_process";
import { generateKeyPairSync, randomBytes } from "node:crypto";
import process from "node:process";

const WRANGLER = process.platform === "win32" ? "npx.cmd" : "npx";
const all = process.argv.includes("--all");
const onlyAt = process.argv.indexOf("--only");
const only = onlyAt >= 0 ? new Set((process.argv[onlyAt + 1] ?? "").split(",").map((n) => n.trim()).filter(Boolean)) : null;

function wrangler(args, input) {
  return spawnSync(WRANGLER, ["wrangler", ...args], { input, encoding: "utf8", shell: process.platform === "win32" });
}

function existing() {
  const r = wrangler(["secret", "list", "--format", "json"]);
  if (r.status !== 0) {
    console.error("Could not list the Worker's secrets. Run `npx wrangler login`, and deploy pairnets-sync once first.");
    process.exit(1);
  }
  try {
    return new Set(JSON.parse(r.stdout).map((s) => s.name));
  } catch {
    return new Set();
  }
}

/** One line from the terminal, not shown as it is typed. */
function askHidden(prompt) {
  return new Promise((resolve) => {
    process.stdout.write(prompt);
    const stdin = process.stdin;
    stdin.setRawMode?.(true);
    stdin.resume();
    stdin.setEncoding("utf8");
    let value = "";
    const onData = (chunk) => {
      for (const ch of chunk) {
        if (ch === "\r" || ch === "\n") {
          stdin.setRawMode?.(false);
          stdin.pause();
          stdin.off("data", onData);
          process.stdout.write("\n");
          resolve(value.trim());
          return;
        }
        if (ch === "\u0003") process.exit(130); // Ctrl+C
        if (ch === "\u007f" || ch === "\b") value = value.slice(0, -1);
        else value += ch;
      }
    };
    stdin.on("data", onData);
  });
}

function ask(prompt) {
  return new Promise((resolve) => {
    process.stdout.write(prompt);
    process.stdin.resume();
    process.stdin.setEncoding("utf8");
    process.stdin.once("data", (d) => {
      process.stdin.pause();
      resolve(String(d).trim());
    });
  });
}

function put(name, value) {
  const r = wrangler(["secret", "put", name], value);
  if (r.status !== 0) {
    console.error(`  ${name}: Cloudflare refused it (${(r.stderr || r.stdout).split("\n").find((l) => l.trim()) ?? "no message"})`);
    return false;
  }
  console.log(`  ${name}: set`);
  return true;
}

function signingKey() {
  const { privateKey } = generateKeyPairSync("ec", { namedCurve: "P-256" });
  const jwk = privateKey.export({ format: "jwk" });
  const now = new Date();
  const kid = `pnid-${now.getUTCFullYear()}-${String(now.getUTCMonth() + 1).padStart(2, "0")}`;
  return { value: JSON.stringify({ kty: "EC", crv: "P-256", x: jwk.x, y: jwk.y, d: jwk.d, kid }), kid, x: jwk.x, y: jwk.y };
}

const SECRETS = [
  { name: "HB_MASTER", kind: "random", warn: "Every linked nest's key comes from it: setting it again cuts off every nest." },
  { name: "COOKIE_KEY", kind: "random", warn: "Setting it again signs everyone out of the website." },
  { name: "SIGNING_KEY", kind: "signing" },
  { name: "CF_ACCOUNT_ID", kind: "paste", hint: "the Cloudflare account id (dash.cloudflare.com > the account > Overview, right column)" },
  { name: "CF_API_TOKEN", kind: "paste", hint: "the API token from step 2 of cloud/README.md" },
  { name: "RESEND_API_KEY", kind: "paste", hint: "a Resend sending key for pairnets.app (re_...)" },
  { name: "TURNSTILE_SITE_KEY", kind: "paste", hint: "the site key of the Turnstile widget for sync.pairnets.app" },
  { name: "TURNSTILE_SECRET", kind: "paste", hint: "the secret key of the same Turnstile widget" },
  { name: "GOOGLE_CLIENT_ID", kind: "paste", hint: "the Google OAuth client id (...apps.googleusercontent.com; Enter skips)", optional: true },
  { name: "GOOGLE_CLIENT_SECRET", kind: "paste", hint: "the Google OAuth client secret (Enter skips until Google sign-in is set up)", optional: true },
];

const have = existing();
console.log("Secrets of pairnets-sync. Nothing you paste is shown or saved on this computer.\n");
if (only) {
  const unknown = [...only].filter((n) => !SECRETS.some((s) => s.name === n));
  if (unknown.length) {
    console.error(`Unknown name(s): ${unknown.join(", ")}. Known: ${SECRETS.map((s) => s.name).join(", ")}`);
    process.exit(2);
  }
}
for (const s of SECRETS) {
  if (only && !only.has(s.name)) continue;
  const isSet = have.has(s.name);
  if (isSet && !all && !only) {
    console.log(`  ${s.name}: already set (run with --all to set it again)`);
    continue;
  }
  if (isSet && s.warn) {
    const sure = await ask(`  ${s.name} is set. ${s.warn} Set it again? Type yes: `);
    if (sure.toLowerCase() !== "yes") continue;
  }
  if (s.kind === "random") {
    put(s.name, randomBytes(32).toString("base64url"));
  } else if (s.kind === "signing") {
    // Only version 1's sign-in for nests on their own domain uses it; `npm run keygen` makes one with its public half
    // shown, for when that is set up.
    put(s.name, signingKey().value);
  } else {
    const value = await askHidden(`  ${s.name}: paste ${s.hint}: `);
    if (!value) {
      console.log(`  ${s.name}: skipped`);
      continue;
    }
    put(s.name, value);
  }
}
console.log("\nDone. Check the list with: npx wrangler secret list");
