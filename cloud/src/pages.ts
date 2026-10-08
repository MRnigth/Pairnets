// Server-rendered HTML pages (no frameworks, no inline scripts or styles: CSP in http.ts).

import { escapeHtml as e } from "./http";
import { displayUserCode, iso } from "./formats";

interface LayoutOptions {
  scripts?: string[];
  turnstile?: boolean;
}

export function layout(title: string, main: string, opts: LayoutOptions = {}): string {
  const scripts = (opts.scripts ?? []).map((s) => `<script src="/assets/${e(s)}" defer></script>`).join("\n");
  const turnstile = opts.turnstile ? `<script src="https://challenges.cloudflare.com/turnstile/v0/api.js" async defer></script>` : "";
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex">
<title>${e(title)} · Pairnets</title>
<link rel="stylesheet" href="/assets/style.css">
${turnstile}
${scripts}
</head>
<body>
<header class="top"><span class="brand">Pairnets</span> <span class="muted">account</span></header>
<main>
${main}
</main>
<footer><a href="/privacy">Privacy</a></footer>
</body>
</html>
`;
}

export function errorPage(title: string, message: string, link: { href: string; text: string } = { href: "/account", text: "Go to your account" }): string {
  return layout(
    title,
    `<section class="card">
<h1>${e(title)}</h1>
<p>${e(message)}</p>
<p><a class="button" href="${e(link.href)}">${e(link.text)}</a></p>
</section>`,
  );
}

const LOGIN_ERRORS: Record<string, string> = {
  google_failed: "Google sign-in did not work. Try again.",
  google_denied: "Google sign-in was cancelled.",
  google_expired: "That Google sign-in took too long or was started in another browser. Try again.",
};

export function loginPage(opts: { next: string; reauth: boolean; error: string | null; siteKey: string }): string {
  const googleHref = `/login/google?next=${encodeURIComponent(opts.next)}${opts.reauth ? "&reauth=1" : ""}`;
  const error = opts.error && LOGIN_ERRORS[opts.error] ? `<p class="error" role="alert">${e(LOGIN_ERRORS[opts.error])}</p>` : "";
  const reauth = opts.reauth ? `<p class="notice">For your safety, sign in again to do this.</p>` : "";
  return layout(
    "Sign in",
    `<section class="card">
<h1>Sign in to Pairnets</h1>
${reauth}${error}
<p><a class="button wide" id="google" href="${e(googleHref)}">Continue with Google</a></p>
<p class="or">or get a sign-in link by email</p>
<form id="email-form" data-next="${e(opts.next)}">
<label for="email">Email address</label>
<input id="email" name="email" type="email" autocomplete="email" required maxlength="254">
<div class="cf-turnstile" data-sitekey="${e(opts.siteKey)}"></div>
<button type="submit" class="wide">Email me a sign-in link</button>
</form>
<p id="email-status" class="status" role="status" hidden></p>
</section>`,
    { scripts: ["login.js"], turnstile: true },
  );
}

export function emailLandingPage(): string {
  return layout(
    "Finish signing in",
    `<section class="card">
<h1>Finish signing in</h1>
<p id="email-intro">Press Continue to sign in to your Pairnets account in this browser.</p>
<p><button id="email-continue" type="button" class="wide" disabled>Continue</button></p>
<p id="email-status" class="status" role="status" hidden></p>
<p><a href="/login">Back to sign-in</a></p>
</section>`,
    { scripts: ["email.js"] },
  );
}

export interface AccountView {
  accountId: string;
  email: string;
  nests: NestView[];
  sessions: SessionView[];
}

export interface NestView {
  nestId: string;
  label: string;
  publicUrl: string;
  status: "pending" | "active";
  online: "online" | "offline" | "unknown";
  serverVersion: string | null;
  lastSeenAt: number | null;
  hostedLogin: boolean;
  addressWarning: string | null;
}

export interface SessionView {
  id: string;
  kind: "browser" | "app";
  createdAt: number;
  lastSeenAt: number;
  userAgent: string | null;
  deviceName: string | null;
  current: boolean;
}

function when(sec: number | null): string {
  return sec === null ? "never" : `<time datetime="${e(iso(sec))}">${e(iso(sec).replace("T", " ").replace("Z", " UTC"))}</time>`;
}

export function accountPage(v: AccountView): string {
  const nests = v.nests.length
    ? v.nests
        .map(
          (n) => `<li class="nest" data-nest="${e(n.nestId)}">
<div class="row"><strong>${e(n.label)}</strong> <span class="pill ${e(n.status === "pending" ? "pending" : n.online)}">${e(n.status === "pending" ? "linking" : n.online)}</span></div>
<div><a href="${e(n.publicUrl)}" rel="noreferrer">${e(n.publicUrl)}</a></div>
<div class="muted">Version ${e(n.serverVersion ?? "unknown")} · last seen ${when(n.lastSeenAt)}</div>
${n.addressWarning ? `<p class="error">${e(n.addressWarning)}</p>` : ""}
<div class="actions">
<button type="button" data-action="toggle" data-on="${n.hostedLogin ? "1" : "0"}">${n.hostedLogin ? "Turn sign-in off" : "Turn sign-in on"}</button>
<button type="button" class="danger" data-action="remove">Remove</button>
</div>
</li>`,
        )
        .join("\n")
    : `<li class="muted">No nests yet.</li>`;

  const sessions = v.sessions
    .map(
      (s) => `<li data-session="${e(s.id)}">
<div><strong>${e(s.kind === "app" ? `App: ${s.deviceName ?? "unknown computer"}` : "Browser")}</strong>${s.current ? ` <span class="pill online">this browser</span>` : ""}</div>
<div class="muted">${e(s.userAgent ?? "")}</div>
<div class="muted">Signed in ${when(s.createdAt)} · last used ${when(s.lastSeenAt)}</div>
<div class="actions"><button type="button" data-action="revoke">Sign out</button></div>
</li>`,
    )
    .join("\n");

  return layout(
    "Your account",
    `<section class="card">
<h1>Your Pairnets account</h1>
<p>Signed in as <strong>${e(v.email)}</strong>. <button type="button" id="logout" class="link">Sign out</button></p>
<p id="status" class="status" role="status" hidden></p>
</section>
<section class="card">
<h2>Your nests</h2>
<ul class="list" id="nests">
${nests}
</ul>
<p><button type="button" id="add-nest">Add a nest</button></p>
<div id="claim" hidden>
<p>On the server, run this command within 10 minutes:</p>
<pre id="claim-command"></pre>
<p class="muted">Code <strong id="claim-code"></strong>, valid until <span id="claim-expires"></span>. The command shows which account it links to and asks before it changes anything.</p>
<p><button type="button" id="cancel-codes" class="link">Cancel unused codes</button></p>
</div>
</section>
<section class="card">
<h2>Signed-in browsers and apps</h2>
<ul class="list" id="sessions">
${sessions}
</ul>
</section>
<section class="card">
<h2>Delete account</h2>
<p>This removes your account, the list of your nests and everything stored about you here. Your nests keep working with their own sign-in methods.</p>
<p><button type="button" id="delete-account" class="danger">Delete my account</button></p>
</section>`,
    { scripts: ["account.js"] },
  );
}

export interface AppRequestView {
  userCode: string;
  name: string;
  system: string | null;
  appVersion: string | null;
  createdAt: number;
  status: string;
}

export function appPage(req: AppRequestView | null, typed: string | null): string {
  if (!req) {
    const msg = typed ? `<p class="error" role="alert">That code is not valid or has expired. Check the code the app shows.</p>` : "";
    return layout(
      "Sign in an app",
      `<section class="card">
<h1>Sign in an app</h1>
${msg}
<form method="get" action="/app">
<label for="code">Code shown by the app</label>
<input id="code" name="code" required maxlength="16" autocomplete="off" placeholder="XXXX-XXXX">
<button type="submit" class="wide">Continue</button>
</form>
</section>`,
    );
  }
  const details = `<dl>
<dt>Computer</dt><dd>${e(req.name)}</dd>
${req.system ? `<dt>System</dt><dd>${e(req.system)}</dd>` : ""}
${req.appVersion ? `<dt>App version</dt><dd>${e(req.appVersion)}</dd>` : ""}
<dt>Code</dt><dd><strong>${e(displayUserCode(req.userCode))}</strong></dd>
<dt>Asked</dt><dd>${when(req.createdAt)}</dd>
</dl>`;
  const pending = req.status === "pending";
  return layout(
    "Sign in an app",
    `<section class="card" id="app-request" data-code="${e(displayUserCode(req.userCode))}">
<h1>Sign in an app?</h1>
<p>An app wants to use your Pairnets account to list your nests. Allow it only if this is your computer and the code matches the one the app shows.</p>
${details}
${
  pending
    ? `<div class="actions"><button type="button" id="approve">Allow</button> <button type="button" id="deny" class="danger">Not me</button></div>`
    : `<p class="notice">This sign-in was already answered.</p>`
}
<p id="status" class="status" role="status" hidden></p>
</section>`,
    { scripts: ["app.js"] },
  );
}
