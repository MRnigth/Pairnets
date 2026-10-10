// Server-rendered HTML pages (no frameworks, no inline scripts or styles: CSP in http.ts).

import { escapeHtml as e } from "./http";
import { displayUserCode, iso } from "./formats";

interface LayoutOptions {
  scripts?: string[];
  turnstile?: boolean;
  /** Stylesheets after style.css, for a page with a look of its own (the "Sign in an app?" page: app.css). */
  styles?: string[];
  /** A class on <body>, which those stylesheets hang off. */
  bodyClass?: string;
}

export function layout(title: string, main: string, opts: LayoutOptions = {}): string {
  // Every page gets the cookie notice (assets.ts), after its own scripts.
  const scripts = [...(opts.scripts ?? []), "cookie-notice.js"].map((s) => `<script src="/assets/${e(s)}" defer></script>`).join("\n");
  const styles = ["style.css", ...(opts.styles ?? [])].map((s) => `<link rel="stylesheet" href="/assets/${e(s)}">`).join("\n");
  const turnstile = opts.turnstile ? `<script src="https://challenges.cloudflare.com/turnstile/v0/api.js" async defer></script>` : "";
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="robots" content="noindex">
<title>${e(title)} · Pairnets</title>
${styles}
${turnstile}
${scripts}
</head>
<body${opts.bodyClass ? ` class="${e(opts.bodyClass)}"` : ""}>
<header class="top"><a class="brand" href="https://pairnets.app/">Pairnets</a> <span class="muted">account</span> <a class="button home" href="https://pairnets.app/">← Home</a></header>
<main>
${main}
</main>
<footer><nav aria-label="About Pairnets"><a href="/privacy">Privacy</a> · <a href="/terms">Terms</a> · <a href="/guidelines">Guidelines</a> · <a href="/cookies">Cookie settings</a> · <a href="/help">Help</a></nav></footer>
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
<p class="legal">By continuing, you agree to the <a href="/terms">Terms</a> and the <a href="/privacy">Privacy Policy</a>.</p>
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
  servers: ServerView[];
  nests: NestView[];
  sessions: SessionView[];
}

/** A relayed server; the page's script fills in online, version, free space and computers from GET /v1/servers. */
export interface ServerView {
  id: string;
  label: string;
  status: "pending" | "active";
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
  const servers = v.servers.length
    ? v.servers
        .map(
          (n) => `<li class="server" data-server="${e(n.id)}">
<div class="row"><strong>${e(n.label)}</strong> <span class="pill" data-field="online">checking</span></div>
<div class="muted" data-field="details">${n.status === "pending" ? "Waiting for the nest to connect for the first time." : ""}</div>
<p class="muted">Computers using this nest:</p>
<ul class="list devices" data-field="devices"><li class="muted">Loading...</li></ul>
<div class="actions"><button type="button" class="danger" data-action="remove-server">Remove this nest from my account</button></div>
</li>`,
        )
        .join("\n")
    : `<li class="muted">No nest yet.</li>`;

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
    : `<li class="muted">None.</li>`;

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
<h2>${v.servers.length > 1 ? "Your nests" : "Your nest"}</h2>
<ul class="list" id="servers">
${servers}
</ul>
<p class="muted">To add a nest: on the Linux computer that will keep your files, run <code>curl -fsSL https://pairnets.app/get.sh | sudo bash</code>. It shows a link and a code: open the link and choose Add this nest. Then, on each computer, install Pairnets and choose Continue with email (or Google) with this account.</p>
</section>
<details class="card"${v.nests.length ? " open" : ""}>
<summary>Nests on their own address</summary>
<p class="muted">Only for a nest that has its own web address (installed with --public-url).</p>
<ul class="list" id="nests">
${nests}
</ul>
<p><button type="button" id="add-nest">Link a nest by its address</button></p>
<div id="claim" hidden>
<p>On the nest, run this command within 10 minutes:</p>
<pre id="claim-command"></pre>
<p class="muted">Code <strong id="claim-code"></strong>, valid until <span id="claim-expires"></span>. The command shows which account it links to and asks before it changes anything.</p>
<p><button type="button" id="cancel-codes" class="link">Cancel unused codes</button></p>
</div>
</details>
<section class="card">
<h2>Signed-in browsers and apps</h2>
<ul class="list" id="sessions">
${sessions}
</ul>
</section>
<section class="card">
<h2>Delete account</h2>
<p>This removes your account, your nests' links to Pairnets and everything stored about you here. Your nests keep their files.</p>
<p class="muted"><a href="/delete-account">What gets deleted, and what stays</a></p>
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
  expiresAt: number;
  decidedAt: number | null;
  status: string;
  /** The nest an allowed sign-in joins; null while the account had none (the app then waits for one). */
  nestId: string | null;
  /** When the page is drawn ("asked 2 min ago", "the code works for 8 more minutes"). */
  now: number;
}

export interface ServerChoice {
  id: string;
  label: string;
}

// ------------------------------------------------------------------ "Sign in an app?" (/app)
// The apps' "Quiet" look (app.css, Instrument Sans). One calm column, and the code is the question. Every answer gets a
// screen of its own: app.js swaps to it after Allow or "This isn't me", and a reload draws the answered screen and says
// which answer was given and when.

/** Line icons on a 24 × 24 grid, drawn inline (no image request; they take the text colour). */
const ICONS = {
  computer: `<rect x="3" y="4" width="18" height="12" rx="1.8"/><path d="M8 20h8M12 16v4"/>`,
  server: `<rect x="4" y="3.5" width="16" height="7" rx="1.6"/><rect x="4" y="13.5" width="16" height="7" rx="1.6"/><path d="M8 7h.01M8 17h.01"/>`,
  folder: `<path d="M3.5 7.5a2 2 0 0 1 2-2h4l2 2h7a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z"/>`,
  lock: `<rect x="5" y="10.5" width="14" height="10" rx="2"/><path d="M8 10.5V7.5a4 4 0 0 1 8 0v3"/>`,
  clock: `<circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/>`,
};

function icon(name: keyof typeof ICONS): string {
  return `<svg class="q-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false">${ICONS[name]}</svg>`;
}

/** The apps' ring, closed and green: done. */
const RING_DONE = `<svg class="q-ring" viewBox="0 0 76 76" aria-hidden="true" focusable="false"><circle class="q-ring-fill" cx="38" cy="38" r="34"/><circle class="q-ring-line" cx="38" cy="38" r="34"/><path class="q-ring-mark" d="M26 39l8 8 16-17"/></svg>`;
/** The ring a quarter of the way round, around a computer: waiting. */
const RING_WAIT = `<svg class="q-ring" viewBox="0 0 76 76" aria-hidden="true" focusable="false"><circle class="q-ring-track" cx="38" cy="38" r="34"/><circle class="q-ring-arc" cx="38" cy="38" r="34" stroke-dasharray="60 214" transform="rotate(-90 38 38)"/><rect class="q-ring-glyph" x="25" y="27" width="26" height="16" rx="2.5"/><path class="q-ring-glyph" d="M32 50h12M38 43v7"/></svg>`;

const NEST_COMMAND = "curl -fsSL https://pairnets.app/get.sh | sudo bash";

function ago(sec: number): string {
  if (sec < 60) return "just now";
  const min = Math.floor(sec / 60);
  return min < 60 ? `${min} min ago` : `${Math.floor(min / 60)} h ago`;
}

function minutesLeft(sec: number): string {
  const min = Math.max(1, Math.ceil(sec / 60));
  return min === 1 ? "1 more minute" : `${min} more minutes`;
}

function screen(id: string, shown: boolean, inner: string): string {
  return `<section class="q-screen" id="screen-${id}" aria-labelledby="screen-${id}-title"${shown ? "" : " hidden"}>
${inner}
</section>`;
}

function codeForm(label: string): string {
  return `<form class="q-field" method="get" action="/app">
<label for="code">${e(label)}</label>
<div class="q-row"><input id="code" name="code" required maxlength="16" autocomplete="off" autocapitalize="characters" spellcheck="false" placeholder="XXXX-XXXX"><button type="submit" class="q-btn q-primary">Continue</button></div>
</form>`;
}

function askScreen(req: AppRequestView, servers: ServerChoice[]): string {
  const name = e(req.name);
  let nest: string;
  if (!servers.length) {
    nest = `<p class="q-note"><b>You don't have a nest yet.</b> Allow ${name} now and it waits. Next you set up your nest, and ${name} joins it by itself.</p>`;
  } else if (servers.length === 1) {
    nest = `<p class="q-syncwith">${icon("server")}<span>It will sync with <b>${e(servers[0].label)}</b></span></p>`;
  } else {
    const options = servers
      .map(
        (n, i) =>
          `<label class="q-choice"><input type="radio" name="nest" value="${e(n.id)}" data-label="${e(n.label)}"${i === 0 ? " checked" : ""}> ${e(n.label)}</label>`,
      )
      .join("\n");
    nest = `<fieldset class="q-nests"><legend>Sync ${name} with</legend>
<div class="q-choices">
${options}
</div>
</fieldset>`;
  }
  const facts = [
    req.system,
    req.appVersion ? `Pairnets ${req.appVersion}` : null,
    `asked ${ago(req.now - req.createdAt)}`,
    `the code works for ${minutesLeft(req.expiresAt - req.now)}`,
  ]
    .filter((f): f is string => !!f)
    .map((f) => e(f))
    .join(" · ");
  return screen(
    "ask",
    true,
    `<span class="q-badge">${icon("computer")}</span>
<h1 id="screen-ask-title" tabindex="-1">Allow ${name} to sign in?</h1>
<p class="q-lead">A computer called <b>${name}</b> is asking to use your Pairnets account.</p>
<div class="q-codebox">
<p class="q-q">Does Pairnets on ${name} show this code?</p>
<p class="q-code">${e(displayUserCode(req.userCode))}</p>
</div>
${nest}
<p class="q-error" id="app-error" role="alert" hidden></p>
<div class="q-actions">
<button type="button" class="q-btn q-primary" id="approve">Yes, allow ${name}</button>
<button type="button" class="q-btn q-ghost" id="deny">No, this isn't me</button>
</div>
<details class="q-more"><summary>Details</summary><p>${facts}</p></details>`,
  );
}

function allowedScreen(req: AppRequestView, nest: ServerChoice | null, earlier: string | null, shown: boolean): string {
  const name = e(req.name);
  return screen(
    "allowed",
    shown,
    `${RING_DONE}
<h1 id="screen-allowed-title" tabindex="-1">${name} is signed in</h1>
<p class="q-lead">It syncs with <b id="allowed-nest">${e(nest ? nest.label : "your nest")}</b> from now on.</p>
${earlier ? `<p class="q-earlier">You allowed this ${e(earlier)}.</p>` : ""}
<div class="q-next">
<span class="q-next-icon">${icon("folder")}</span>
<div><p class="q-next-title">Next, on ${name}</p><p class="q-next-text">Go back to Pairnets. If it asks which folder to keep in sync, pick one, and you're done.</p></div>
</div>
<div class="q-actions"><a class="q-btn q-ghost" id="open-app" href="pairnets://signed-in">Open Pairnets</a></div>
<p class="q-close">You can close this tab.</p>
<p class="q-sep">Not you after all? <a href="/account">Remove ${name} in your account</a></p>`,
  );
}

function allowedNoNestScreen(req: AppRequestView, earlier: string | null, shown: boolean): string {
  const name = e(req.name);
  return screen(
    "allowed-nonest",
    shown,
    `${RING_WAIT}
<h1 id="screen-allowed-nonest-title" tabindex="-1">${name} is waiting for your nest</h1>
<p class="q-lead">${name} is allowed. Set up your nest and it joins by itself, within 30 minutes.</p>
${earlier ? `<p class="q-earlier">You allowed this ${e(earlier)}.</p>` : ""}
<div class="q-actions"><a class="q-btn q-primary" href="/account">Set up your nest</a></div>
<p class="q-close">It takes one command on a Linux computer that stays on:</p>
<pre class="q-cmd"><code>${e(NEST_COMMAND)}</code></pre>
<p class="q-close">Then open the link it shows.</p>`,
  );
}

function refusedScreen(req: AppRequestView, earlier: string | null, shown: boolean): string {
  const name = e(req.name);
  return screen(
    "refused",
    shown,
    `<span class="q-badge">${icon("lock")}</span>
<h1 id="screen-refused-title" tabindex="-1">${name} was not let in</h1>
<p class="q-lead">Nothing changed. ${name} can't use your account or see your files.</p>
${earlier ? `<p class="q-earlier">You said no to this ${e(earlier)}.</p>` : ""}
<p class="q-fine">If you didn't start this, someone may have sent you the link. You can ignore it.</p>
<div class="q-actions"><a class="q-btn q-ghost" href="/account">Go to your account</a></div>
<p class="q-close">You can close this tab.</p>`,
  );
}

function expiredScreen(req: AppRequestView): string {
  return screen(
    "expired",
    false,
    `<span class="q-badge q-warn">${icon("clock")}</span>
<h1 id="screen-expired-title" tabindex="-1">This code has run out</h1>
<p class="q-lead">Codes work for 10 minutes. In Pairnets on ${e(req.name)}, start signing in again to get a new one.</p>
${codeForm("Got a new code? Type it here")}`,
  );
}

/** /app with no code, or a code that is unknown, ran out, or belongs to someone else's answered sign-in. */
function appCodePage(failed: boolean): string {
  const inner = failed
    ? `<span class="q-badge q-warn">${icon("clock")}</span>
<h1 id="screen-code-title">That code didn't work</h1>
<p class="q-lead">It may have run out: codes work for 10 minutes. In Pairnets on your computer, start signing in again to get a new one, or check the code for a typo.</p>
${codeForm("Type the code Pairnets shows")}`
    : `<span class="q-badge">${icon("computer")}</span>
<h1 id="screen-code-title">Sign in an app</h1>
<p class="q-lead">When you sign in, Pairnets on your computer shows a code like ABCD-EFGH. Type it here to let that computer in.</p>
${codeForm("Code shown by Pairnets")}`;
  return layout("Sign in an app", `<div class="q-col">\n${screen("code", true, inner)}\n</div>`, { styles: ["app.css"], bodyClass: "q-page" });
}

export function appPage(req: AppRequestView | null, typed: string | null, servers: ServerChoice[] = []): string {
  if (!req) return appCodePage(!!typed);
  const answered = req.status !== "pending";
  const allowed = req.status === "approved" || req.status === "delivered";
  // The nest it joins: the one chosen (answered), else the first on offer (app.js puts in the one picked).
  const nest = answered ? (servers.find((n) => n.id === req.nestId) ?? null) : (servers[0] ?? null);
  const noNest = answered ? req.nestId === null : servers.length === 0;
  const earlier = answered && req.decidedAt !== null ? ago(Math.max(0, req.now - req.decidedAt)) : null;

  const screens: string[] = [];
  if (!answered) screens.push(askScreen(req, servers));
  if (!answered || allowed) screens.push(noNest ? allowedNoNestScreen(req, earlier, answered) : allowedScreen(req, nest, earlier, answered));
  if (!answered || !allowed) screens.push(refusedScreen(req, earlier, answered));
  if (!answered) screens.push(expiredScreen(req));

  const attrs = [`id="app-request"`, `class="q-col"`, `data-code="${e(displayUserCode(req.userCode))}"`, `data-system="${e(req.system ?? "")}"`];
  if (!answered && servers.length === 1) attrs.push(`data-nest="${e(servers[0].id)}"`);
  const title = !answered ? `Allow ${req.name} to sign in?` : allowed ? `${req.name} is signed in` : `${req.name} was not let in`;
  return layout(title, `<div ${attrs.join(" ")}>\n${screens.join("\n")}\n</div>`, {
    scripts: answered ? [] : ["app.js"],
    styles: ["app.css"],
    bodyClass: "q-page",
  });
}

export interface ServerRequestView {
  userCode: string;
  hostname: string | null;
  serverVersion: string | null;
  createdAt: number;
  status: string;
}

/** /add?code= (RELAY.md 2): approve a server that runs the installer. Unknown codes are answered with status 404. */
export function addPage(req: ServerRequestView | null, typed: string | null): string {
  if (!req) {
    const msg = typed ? `<p class="error" role="alert">That code is not valid or has expired. Check the code the installer shows.</p>` : "";
    return layout(
      "Add a nest",
      `<section class="card">
<h1>Add a nest</h1>
${msg}
<form method="get" action="/add">
<label for="code">Code shown by the installer</label>
<input id="code" name="code" required maxlength="16" autocomplete="off" placeholder="XXXX-XXXX">
<button type="submit" class="wide">Continue</button>
</form>
</section>`,
    );
  }
  const details = `<dl>
<dt>Nest</dt><dd>${e(req.hostname ?? "unknown")}</dd>
${req.serverVersion ? `<dt>Version</dt><dd>${e(req.serverVersion)}</dd>` : ""}
<dt>Code</dt><dd><strong>${e(displayUserCode(req.userCode))}</strong></dd>
<dt>Asked</dt><dd>${when(req.createdAt)}</dd>
</dl>`;
  const pending = req.status === "pending";
  return layout(
    "Add a nest",
    `<section class="card" id="server-request" data-code="${e(displayUserCode(req.userCode))}">
<h1>Add this nest to your account?</h1>
<p>A computer where the Pairnets nest is being installed wants to join your account. Your files stay on it; Pairnets connects your other computers to it. Add it only if you are installing it yourself and the code matches the one the installer shows.</p>
${details}
${
  pending
    ? `<label for="label">Name</label>
<input id="label" maxlength="64" autocomplete="off" value="${e(req.hostname ?? "My nest")}">
<div class="actions"><button type="button" id="approve">Add this nest</button> <button type="button" id="deny" class="danger">Not mine</button></div>`
    : `<p class="notice">This request was already answered.</p>`
}
<p id="status" class="status" role="status" hidden></p>
</section>`,
    { scripts: ["add.js"] },
  );
}
