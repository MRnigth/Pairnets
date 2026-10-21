// Server-rendered HTML pages (no frameworks, no inline scripts or styles: CSP in http.ts).

import { assetUrl } from "./assets";
import { escapeHtml as e } from "./http";
import { displayUserCode, emailName, iso, MAX_USERNAME } from "./formats";

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
  const scripts = [...(opts.scripts ?? []), "cookie-notice.js"].map((s) => `<script src="${e(assetUrl(s))}" defer></script>`).join("\n");
  const styles = ["style.css", ...(opts.styles ?? [])].map((s) => `<link rel="stylesheet" href="${e(assetUrl(s))}">`).join("\n");
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
  /** The username the owner chose, or null (then the part of the email before the @ is shown). */
  username: string | null;
  servers: ServerView[];
  nests: NestView[];
  sessions: SessionView[];
  /** Nests other accounts share with this one (RELAY.md 10). */
  shared?: SharedView[];
  /** /account?invite=nst_…&folders=…&access=…: that nest's invite form is drawn open and filled in (the apps hand over to it). */
  invite?: { nestId: string; folders: string[] | null; access: "view" | "edit" } | null;
}

/** A relayed server; the page's script fills in online, version, free space and computers from GET /v1/servers. */
export interface ServerView {
  id: string;
  label: string;
  status: "pending" | "active";
  /** Its People: friends let in, people waiting for an answer, and invite links still open. */
  people?: PeopleView;
}

export interface PersonShare {
  access: "view" | "edit";
  /** Top-level folders, or null for the whole nest. */
  folders: string[] | null;
}

export interface PeopleView {
  members: ({ accountId: string; name: string; email: string } & PersonShare)[];
  waiting: ({ id: string; name: string; email: string } & PersonShare)[];
  links: ({ id: string; link: string; email: string | null; expiresAt: number } & PersonShare)[];
}

export interface SharedView extends PersonShare {
  nestId: string;
  label: string;
  sharedBy: string;
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

/** "Photos, School · view only" / "Whole nest · can change". */
function shareWords(p: PersonShare): string {
  return `${p.folders ? p.folders.join(", ") : "Whole nest"} · ${p.access === "view" ? "view only" : "can change"}`;
}

/** The round letter before a person's name. */
function letter(name: string): string {
  return `<span class="letter" aria-hidden="true">${e(([...name.trim()][0] ?? "?").toUpperCase())}</span>`;
}

function day(sec: number): string {
  return new Date(sec * 1000).toLocaleDateString("en-GB", { day: "numeric", month: "long", timeZone: "UTC" });
}

/** The invite form under a nest's People: drawn hidden, or open and filled in when the apps handed over to it. */
function inviteForm(n: ServerView, open: { folders: string[] | null; access: "view" | "edit" } | null): string {
  const id = e(n.id);
  const chosen = !!open?.folders;
  const edit = open?.access === "edit";
  return `<form class="invite-form" data-field="invite-form"${open ? " data-open" : " hidden"}>
<h3>Invite a friend to ${e(n.label)}</h3>
<p class="muted">They sign in with their own Pairnets account. You can change or take back access any time.</p>
<fieldset><legend>What they get</legend>
<label class="choice"><input type="radio" name="what-${id}" value="whole"${chosen ? "" : " checked"}> The whole nest</label>
<label class="choice"><input type="radio" name="what-${id}" value="folders" data-field="chosen"${chosen ? " checked" : ""}> Chosen folders</label>
<label for="folders-${id}">Folder names, separated by commas (top-level folders of the nest)</label>
<input id="folders-${id}" name="folders" maxlength="2000" autocomplete="off" placeholder="Photos, School" value="${e(open?.folders ? open.folders.join(", ") : "")}">
<p class="muted" data-field="sealed-note" hidden>An encrypted nest can only be shared whole.</p>
</fieldset>
<fieldset><legend>What they can do</legend>
<label class="choice"><input type="radio" name="access-${id}" value="view"${edit ? "" : " checked"}> <span><strong>View only</strong> <span class="muted">They get your files and your changes. What they change stays on their computer.</span></span></label>
<label class="choice"><input type="radio" name="access-${id}" value="edit"${edit ? " checked" : ""}> <span><strong>Can change</strong> <span class="muted">They can add, edit and delete in these folders. Deleted files still go to your nest's history.</span></span></label>
</fieldset>
<label for="invite-email-${id}">Their email (or leave it empty and copy the link)</label>
<input id="invite-email-${id}" name="email" type="email" maxlength="254" autocomplete="off" placeholder="friend@example.com">
<div class="actions"><button type="submit">Send invite</button> <button type="button" class="secondary" data-action="invite-link">Copy invite link</button> <button type="button" class="link" data-action="invite-close">Close</button></div>
<p class="muted">Whoever uses the link still waits for you: you let each person in before they get any files. It ends after 7 days.</p>
<p class="error" data-field="invite-error" role="alert" hidden></p>
<div data-field="invite-done" hidden><p class="saved" data-field="invite-said" role="status"></p><div class="input-row"><input data-field="invite-url" readonly aria-label="Invite link"><button type="button" data-action="invite-copy-new">Copy</button></div><p><button type="button" class="secondary" data-action="invite-finish">Done</button></p></div>
</form>`;
}

/** A nest's People (RELAY.md 10): its friends, the people waiting for an answer, open links, and the invite form. */
function peopleSection(n: ServerView, invite: AccountView["invite"]): string {
  const p = n.people ?? { members: [], waiting: [], links: [] };
  const rows = [
    ...p.waiting.map(
      (w) => `<li class="person waiting" data-invite="${e(w.id)}">${letter(w.name)}<div class="who">
<div><strong>${e(w.name)} wants to join</strong> <span class="muted">· ${e(w.email)}</span></div>
<div class="muted">Used your invite link · ${e(shareWords(w))}</div>
<div class="actions"><button type="button" class="secondary" data-action="invite-decline">Decline</button> <button type="button" data-action="invite-approve">Let ${e(w.name)} in</button></div>
</div></li>`,
    ),
    ...p.members.map(
      (m) => `<li class="person" data-person="${e(m.accountId)}" data-access="${e(m.access)}" data-folders="${e(m.folders ? m.folders.join(", ") : "")}">${letter(m.name)}<div class="who">
<div><strong data-field="name">${e(m.name)}</strong> <span class="muted">· ${e(m.email)}</span></div>
<div class="muted"><span data-field="share">${e(shareWords(m))}</span><span data-field="computers"></span></div>
<div class="actions"><button type="button" class="secondary" data-action="person-change">Change…</button> <button type="button" class="danger" data-action="person-remove">Remove</button></div>
</div></li>`,
    ),
    ...p.links.map(
      (l) => `<li class="person link" data-invite="${e(l.id)}" data-link="${e(l.link)}">${letter("@")}<div class="who">
<div><strong>${l.email ? `Invite sent to ${e(l.email)}` : "Invite link"}</strong></div>
<div class="muted">${e(shareWords(l))} · ends ${e(day(l.expiresAt))}</div>
<div class="actions"><button type="button" class="secondary" data-action="invite-copy">Copy link</button> <button type="button" class="danger" data-action="invite-cancel">Cancel</button></div>
</div></li>`,
    ),
  ];
  const friends = p.members.length;
  const summary = [
    friends ? `${friends} ${friends === 1 ? "friend" : "friends"}` : "",
    p.waiting.length ? `${p.waiting.length} waiting` : "",
  ].filter(Boolean);
  const open = invite && invite.nestId === n.id ? invite : null;
  return `<section class="people" id="people-${e(n.id)}" aria-labelledby="people-${e(n.id)}-title">
<div class="row"><h3 id="people-${e(n.id)}-title">People</h3> <span class="muted">${e(summary.length ? summary.join(" · ") : rows.length ? "" : "Only you")}</span> <button type="button" class="secondary small" data-action="invite-open">Invite a friend…</button></div>
<p class="notice" data-field="friends-note" hidden></p>
${rows.length ? `<ul class="list people-list">\n${rows.join("\n")}\n</ul>` : ""}
${inviteForm(n, open)}
</section>`;
}

/** "Shared with you": the nests other accounts let this one in to, each with Leave. */
function sharedSection(shared: SharedView[]): string {
  if (!shared.length) return "";
  const rows = shared
    .map(
      (s) => `<li data-shared="${e(s.nestId)}">
<div class="row"><strong>${e(s.label)}</strong> <span class="muted">· shared by ${e(s.sharedBy)}</span></div>
<div class="muted">${e(shareWords(s))}</div>
<div class="actions"><button type="button" class="danger" data-action="leave">Leave</button></div>
</li>`,
    )
    .join("\n");
  return `<section class="card" id="shared">
<h2>Shared with you</h2>
<ul class="list" id="shared-list">
${rows}
</ul>
<p class="muted">To add one of these on a computer: open Pairnets, choose Add a nest and sign in with this account.</p>
</section>`;
}

export function accountPage(v: AccountView): string {
  const servers = v.servers.length
    ? v.servers
        .map(
          (n) => `<li class="server" data-server="${e(n.id)}">
<div class="row"><strong data-field="label">${e(n.label)}</strong> <span class="pill" data-field="online">checking</span> <button type="button" class="secondary small" data-action="rename-server">Rename nest</button></div>
<div class="muted" data-field="details">${n.status === "pending" ? "Waiting for the nest to connect for the first time." : ""}</div>
<p class="muted">Computers using this nest:</p>
<ul class="list devices" data-field="devices"><li class="muted">Loading...</li></ul>
<p class="muted" data-field="rename-offline" hidden>Your computers can be renamed when your nest is connected again. The nest's own name can be changed any time.</p>
${peopleSection(n, v.invite ?? null)}
<div class="actions"><button type="button" class="danger" data-action="remove-server">Remove this nest from my account</button></div>
</li>`,
        )
        .join("\n")
    : `<li class="muted">No nest yet.</li>`;

  const nests = v.nests.length
    ? v.nests
        .map(
          (n) => `<li class="nest" data-nest="${e(n.nestId)}">
<div class="row"><strong data-field="label">${e(n.label)}</strong> <span class="pill ${e(n.status === "pending" ? "pending" : n.online)}">${e(n.status === "pending" ? "linking" : n.online)}</span> <button type="button" class="secondary small" data-action="rename">Rename nest</button></div>
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
<form id="username-form">
<label for="username">Username</label>
<div class="input-row"><input id="username" name="username" maxlength="${MAX_USERNAME}" autocomplete="nickname" value="${e(v.username ?? "")}" placeholder="${e(emailName(v.email))}"><button type="submit">Save</button></div>
<p class="muted">Shown on pairnets.app while you are signed in. Leave it empty to be shown as <strong>${e(emailName(v.email))}</strong>.</p>
</form>
<p id="status" class="status" role="status" hidden></p>
</section>
<section class="card">
<h2>${v.servers.length > 1 ? "Your nests" : "Your nest"}</h2>
<ul class="list" id="servers">
${servers}
</ul>
<p class="muted">To add a nest: on the Linux computer that will keep your files, run <code>curl -fsSL https://pairnets.app/get.sh | sudo bash</code>. It shows a link and a code: open the link and choose Add this nest. Then, on each computer, install Pairnets and choose Continue with email (or Google) with this account.</p>
</section>
${sharedSection(v.shared ?? [])}
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
  /** "Copenhagen, Denmark": where the request came from, or null when Cloudflare could not tell. */
  place: string | null;
  /** When the page is drawn ("asked 2 min ago", "the code works for 8 more minutes"). */
  now: number;
}

export interface ServerChoice {
  id: string;
  label: string;
  /** The computer asking already syncs this nest (the app says so when it adds one more nest): shown, not offered. */
  have?: boolean;
  /** A nest shared with this account (RELAY.md 10): its owner's name and what was shared. Absent for the account's own. */
  shared?: { by: string; folders: string[] | null; access: "view" | "edit" };
}

/** "Shared by Jonas · Photos, School · view only" / "Shared by Jonas · whole nest · can change". */
function sharedLine(sh: NonNullable<ServerChoice["shared"]>): string {
  return `Shared by ${sh.by} · ${sh.folders ? sh.folders.join(", ") : "whole nest"} · ${sh.access === "view" ? "view only" : "can change"}`;
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
    const only = servers[0];
    const by = only.shared ? ` <span class="q-have-note">· shared by ${e(only.shared.by)}</span>` : "";
    nest = `<p class="q-syncwith">${icon("server")}<span>It will sync with <b>${e(only.label)}</b>${by}</span></p>`;
  } else {
    // Nests this computer already syncs stay in the list, greyed out, unless that would leave nothing to pick. The
    // account's own nests come first, then the ones shared with it, under a heading of their own.
    const offered = servers.some((n) => !n.have) ? servers : servers.map((n) => ({ ...n, have: false }));
    const first = offered.findIndex((n) => !n.have);
    const firstShared = offered.findIndex((n) => n.shared);
    const options = offered
      .map((n, i) => {
        const heading = i === firstShared ? `<p class="q-group">Shared with you</p>\n` : "";
        if (n.shared) {
          const sub = `${sharedLine(n.shared)}${n.have ? " · already on this computer" : ""}`;
          const text = `<span class="q-choice-text">${e(n.label)}<span class="q-choice-sub">${e(sub)}</span></span>`;
          return `${heading}<label class="q-choice${n.have ? " q-have" : ""}"><input type="radio" name="nest" value="${e(n.id)}" data-label="${e(n.label)}"${n.have ? " disabled" : i === first ? " checked" : ""}> ${text}</label>`;
        }
        return n.have
          ? `<label class="q-choice q-have"><input type="radio" name="nest" value="${e(n.id)}" data-label="${e(n.label)}" disabled> ${e(n.label)} <span class="q-have-note">· already on this computer</span></label>`
          : `<label class="q-choice"><input type="radio" name="nest" value="${e(n.id)}" data-label="${e(n.label)}"${i === first ? " checked" : ""}> ${e(n.label)}</label>`;
      })
      .join("\n");
    nest = `<fieldset class="q-nests"><legend>Sync ${name} with</legend>
<div class="q-choices">
${options}
</div>
</fieldset>`;
  }
  const where = req.place
    ? `<p class="q-note">This request came from <b>${e(req.place)}</b>. If that isn't where your computer is, don't allow it.</p>`
    : `<p class="q-note">We couldn't tell where this request came from. Only allow it if you just started Pairnets on that computer.</p>`;
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
${where}
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

// ------------------------------------------------------------------ An invite to a friend's nest (/invite?t=)
// The same Quiet look as /app. Open: what is shared and "Ask to join". After asking, and on every visit while waiting,
// "Asked to join" with how to get ready. Every other state is one screen that says where the invite stands.

export interface InvitePageView {
  state: "unknown" | "own" | "open" | "waiting" | "in" | "declined" | "cancelled" | "expired" | "used" | "member";
  token: string;
  nestId: string;
  /** The nest's name, its owner's name and the owner's email with dots ("m•••@gmail.com"). */
  nest: string;
  owner: string;
  ownerEmail: string;
  folders: string[] | null;
  access: "view" | "edit";
  /** Who is signed in: their name (what the owner will see) and email. */
  me: string;
  myEmail: string;
}

/** How to add a shared nest on a computer: the steps on the waiting and "You're in" screens. */
function addNestSteps(v: InvitePageView): string {
  return `<ol class="q-steps">
<li>Open Pairnets and choose <b>Add a nest</b> (in Settings, or the list on the left).</li>
<li>Sign in as <b>${e(v.myEmail)}</b> and pick <b>${e(v.nest)} · shared by ${e(v.owner)}</b>.</li>
<li>Choose where the folders go on your computer.</li>
</ol>
<div class="q-actions"><a class="q-btn q-primary" href="pairnets://open">Open Pairnets</a></div>
<p class="q-close">No Pairnets yet? <a href="https://pairnets.app/">Download it</a></p>`;
}

function inviteAskScreen(v: InvitePageView): string {
  const owner = e(v.owner);
  const what = v.folders ? `these folders from their nest` : `their whole nest`;
  const them = v.folders && v.folders.length === 1 ? "it" : "them";
  const can =
    v.access === "view"
      ? `<b>View only</b><span class="q-fact-sub">Your own changes stay on your computer.</span>`
      : `<b>Can change</b><span class="q-fact-sub">You can add, change and delete files. Deleted files still go to the nest's history.</span>`;
  return screen(
    "ask",
    true,
    `<p class="q-from">An invite from ${owner} · ${e(v.ownerEmail)}</p>
<h1 id="screen-ask-title" tabindex="-1">Join ${e(v.nest)}?</h1>
<p class="q-lead">${owner} shares ${what} with you. Pairnets will keep ${them} in sync on the computers you choose.</p>
<dl class="q-facts">
<div><dt>${v.folders ? "Folders" : "You get"}</dt><dd><b>${e(v.folders ? v.folders.join(", ") : "The whole nest")}</b></dd></div>
<div><dt>You can</dt><dd>${can}</dd></div>
<div><dt>Signed in as</dt><dd>${e(v.myEmail)} · <button type="button" class="q-link" id="not-you">not you?</button></dd></div>
</dl>
<p class="q-error" id="invite-error" role="alert" hidden></p>
<div class="q-actions">
<button type="button" class="q-btn q-primary" id="ask">Ask to join</button>
<button type="button" class="q-btn q-ghost" id="no-thanks">No thanks</button>
</div>
<p class="q-fine">${owner} sees your name (${e(v.me)}) and your email, and lets you in. You can leave the nest any time.</p>`,
  );
}

function inviteWaitingScreen(v: InvitePageView, shown: boolean): string {
  const owner = e(v.owner);
  return screen(
    "waiting",
    shown,
    `<p class="q-note q-info">Waiting for ${owner} to let you in. We'll email you when that happens.</p>
<h1 id="screen-waiting-title" tabindex="-1">Asked to join ${e(v.nest)}</h1>
<p class="q-lead">You can get ready now. Once ${owner} says yes, add the nest on each computer you want it on:</p>
${addNestSteps(v)}`,
  );
}

/** One screen for an invite that can't be asked with (any more), or was already answered. */
function inviteStateScreen(v: InvitePageView): string {
  const owner = e(v.owner);
  const nest = e(v.nest);
  const one = (badge: string, title: string, lead: string, after = "") =>
    screen(v.state, true, `${badge}\n<h1 id="screen-${v.state}-title" tabindex="-1">${title}</h1>\n<p class="q-lead">${lead}</p>${after ? `\n${after}` : ""}`);
  const lock = `<span class="q-badge">${icon("lock")}</span>`;
  const clock = `<span class="q-badge q-warn">${icon("clock")}</span>`;
  const home = `<div class="q-actions"><a class="q-btn q-ghost" href="/account">Go to your account</a></div>`;
  switch (v.state) {
    case "in":
      return one(RING_DONE, `You're in ${nest}`, `${owner} let you in. Add the nest on each computer you want it on:`, addNestSteps(v));
    case "member":
      return one(RING_DONE, `You already have ${nest}`, `${owner} shares it with you. To add it on another computer:`, addNestSteps(v));
    case "declined":
      return one(lock, `${owner} didn't let you in`, `Nothing was shared. If you think that's a mistake, ask ${owner}.`, home);
    case "cancelled":
      return one(lock, "This invite was cancelled", `${owner} took it back. Ask them if they meant to share ${nest} with you.`, home);
    case "expired":
      return one(clock, "This invite has run out", `Invites work for 7 days. Ask ${owner} for a new one.`, home);
    case "used":
      return one(lock, "This invite was already used", `Someone else asked to join with it. An invite works once: ask ${owner} for a new one.`, home);
    case "own":
      return one(
        `<span class="q-badge">${icon("server")}</span>`,
        "This is your own invite",
        "Send the link to the friend you want to invite. They ask to join, and you let them in on your account page.",
        `<div class="q-actions"><a class="q-btn q-ghost" href="/account#people-${e(v.nestId)}">Go to your nest's people</a></div>`,
      );
    default:
      return one(clock, "This invite link doesn't work", "Check that the whole link was copied, or ask for a new one.", home);
  }
}

export function invitePage(v: InvitePageView): string {
  if (v.state === "open" || v.state === "waiting") {
    const screens: string[] = [];
    if (v.state === "open") {
      screens.push(inviteAskScreen(v));
      screens.push(
        screen(
          "no",
          false,
          `<span class="q-badge">${icon("lock")}</span>
<h1 id="screen-no-title" tabindex="-1">No problem</h1>
<p class="q-lead">Nothing was shared, and ${e(v.owner)} wasn't told. The link keeps working until it runs out, if you change your mind.</p>
<p class="q-close">You can close this tab.</p>`,
        ),
      );
    }
    screens.push(inviteWaitingScreen(v, v.state === "waiting"));
    const title = v.state === "open" ? `Join ${v.nest}?` : `Asked to join ${v.nest}`;
    return layout(title, `<div id="invite-request" class="q-col" data-token="${e(v.token)}">\n${screens.join("\n")}\n</div>`, {
      scripts: v.state === "open" ? ["invite.js"] : [],
      styles: ["app.css"],
      bodyClass: "q-page",
    });
  }
  const titles: Record<string, string> = {
    in: `You're in ${v.nest}`,
    member: `You already have ${v.nest}`,
    declined: "Not let in",
    cancelled: "Invite cancelled",
    expired: "Invite ran out",
    used: "Invite already used",
    own: "Your own invite",
  };
  return layout(titles[v.state] ?? "Invite", `<div class="q-col">\n${inviteStateScreen(v)}\n</div>`, { styles: ["app.css"], bodyClass: "q-page" });
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
