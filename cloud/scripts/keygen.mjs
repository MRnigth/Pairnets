#!/usr/bin/env node
// npm run keygen [-- --kid pnid-2026-10] [-- --random]
//
// Prints a new ES256 (P-256) key pair for the nest-login assertions (CONTRACT.md section 2.5):
//   - the private JWK, for the Worker secret SIGNING_KEY (npx wrangler secret put SIGNING_KEY, or the dashboard);
//   - the public half as a line for src/Pairnets.Server/Auth/HostedKeys.cs.
// `--random` instead prints one base64url value of 32 random bytes (for HB_MASTER or COOKIE_KEY).
// Nothing is written to disk. Copy what you need and clear the terminal afterwards.

const args = process.argv.slice(2);

function b64url(bytes) {
  return Buffer.from(bytes).toString("base64url");
}

if (args.includes("--random")) {
  const b = new Uint8Array(32);
  crypto.getRandomValues(b);
  console.log(b64url(b));
  process.exit(0);
}

const kidArg = args.indexOf("--kid");
const now = new Date();
const kid = kidArg >= 0 ? args[kidArg + 1] : `pnid-${now.getUTCFullYear()}-${String(now.getUTCMonth() + 1).padStart(2, "0")}`;
if (!kid || !/^[A-Za-z0-9._-]{1,64}$/.test(kid)) {
  console.error("The kid must be 1-64 characters from A-Z a-z 0-9 . _ -");
  process.exit(2);
}

const pair = await crypto.subtle.generateKey({ name: "ECDSA", namedCurve: "P-256" }, true, ["sign", "verify"]);
const priv = await crypto.subtle.exportKey("jwk", pair.privateKey);
const privateJwk = { kty: "EC", crv: "P-256", x: priv.x, y: priv.y, d: priv.d, kid };

// Self-check: sign and verify once, and the public coordinates are 32 bytes each.
const probe = new TextEncoder().encode("pairnets keygen self-check");
const sig = await crypto.subtle.sign({ name: "ECDSA", hash: "SHA-256" }, pair.privateKey, probe);
if (!(await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, pair.publicKey, sig, probe))) throw new Error("self-check failed");
if (Buffer.from(priv.x, "base64url").length !== 32 || Buffer.from(priv.y, "base64url").length !== 32) throw new Error("bad coordinates");

console.log(`New Pairnets Cloud signing key, kid ${kid}.

1) PRIVATE key: store it ONLY as the Worker secret SIGNING_KEY (never in a file, chat or commit):
   cd cloud && npx wrangler secret put SIGNING_KEY      (then paste this one line)

${JSON.stringify(privateJwk)}

2) PUBLIC half: add this line to Pinned in src/Pairnets.Server/Auth/HostedKeys.cs:

    new("${kid}", "${priv.x}", "${priv.y}"),

Keep a second ("next") key the same way in a password manager for rotation (CONTRACT.md section 2.5).`);
