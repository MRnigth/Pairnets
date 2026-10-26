// Static files under /assets/ (plain CSS and JavaScript; pages have no inline scripts or styles).
// Kept as strings so the Worker has no build step and no asset binding.

import { FONT_ASSETS } from "./fonts";
import { ICON_PNG } from "./icon";

const STYLE = String.raw`
/* The account pages in pairnets.app's look, "Made by one person": paper and ink, serif headings, hairlines instead of
   cards, and blue only for links and the page's main button. The "Sign in an app?" page (app.css, .q-page) keeps the
   apps' look and overrides the top bar, footer and buttons. */
@font-face { font-family: "Newsreader"; src: url(/assets/fonts/Newsreader.woff2) format("woff2"); font-weight: 400; font-style: normal; font-display: swap; }
@font-face { font-family: "Schibsted Grotesk"; src: url(/assets/fonts/SchibstedGrotesk.woff2) format("woff2"); font-weight: 400 700; font-style: normal; font-display: swap; }
@font-face { font-family: "IBM Plex Mono"; src: url(/assets/fonts/IBMPlexMono-Medium.woff2) format("woff2"); font-weight: 400 600; font-style: normal; font-display: swap; }
:root { color-scheme: light dark; --bg: #F7F8FA; --card: #FFFFFF; --text: #15181D; --muted: #4F5664; --faint: #656C7A;
  --line: #DDE0E6; --tint: #ECEEF2; --accent: #1F55C9; --blue: #2F6FEB; --blue-hover: #255FD4; --accent-text: #FFFFFF;
  --danger: #B3261E; --ok: #1E7B45; --warn: #8A4B00;
  --serif: "Newsreader", Georgia, "Times New Roman", serif;
  --sans: "Schibsted Grotesk", "Segoe UI", -apple-system, system-ui, sans-serif;
  --mono: "IBM Plex Mono", Consolas, Menlo, monospace; }
@media (prefers-color-scheme: dark) {
  :root { --bg: #0F1218; --card: #181C24; --text: #E8EBF0; --muted: #A3AAB6; --faint: #8A919E; --line: #262B34;
    --tint: #181C24; --accent: #8DB2FF; --blue: #2F6FEB; --blue-hover: #4580F0; --accent-text: #FFFFFF;
    --danger: #FF8A80; --ok: #6FD39A; --warn: #E9A85A; }
}
* { box-sizing: border-box; }
[hidden] { display: none !important; }
html { -webkit-text-size-adjust: 100%; }
body { margin: 0; font: 1.0625rem/1.6 var(--sans); background: var(--bg); color: var(--text); }
header.top { display: flex; align-items: center; gap: 6px; padding: 14px 20px; border-bottom: 1px solid var(--line); }
body:not(.q-page) header.top { padding: 18px max(24px, calc((100% - 1120px) / 2 + 24px)); border-bottom-color: transparent; }
.brand { display: inline-flex; align-items: center; gap: 10px; font-weight: 700; font-size: 1.125rem; letter-spacing: -.01em; color: var(--text); text-decoration: none; }
body:not(.q-page) .brand::before { content: ""; width: 28px; height: 28px; background: url(/assets/logo.svg) center / contain no-repeat; }
body:not(.q-page) header.top > .muted { font-size: 1.125rem; color: var(--faint); }
main { max-width: 808px; margin: 0 auto; padding: 32px 24px 16px; }
footer { max-width: 808px; margin: 0 auto; padding: 72px 24px 40px; font-size: .9375rem; }
.card { margin: 0; padding: 40px 0 0; }
main > .card:first-child { padding-top: 20px; }
body:not(.q-page) h1 { font: 400 clamp(2.25rem, 4.6vw, 3.5rem)/1.04 var(--serif); letter-spacing: -.02em; margin: 0 0 16px; text-wrap: balance; }
body:not(.q-page) h2 { font: 400 1.75rem/1.15 var(--serif); letter-spacing: -.01em; margin: 0 0 14px; }
p { margin: 0 0 1em; }
a { color: var(--accent); text-underline-offset: 3px; text-decoration-thickness: 1px; }
a:hover { text-decoration-thickness: 2px; }
a:focus-visible, button:focus-visible, input:focus-visible, summary:focus-visible { outline: 2px solid var(--blue); outline-offset: 3px; border-radius: 4px; }
code, pre { font-family: var(--mono); font-weight: 500; }
code { font-size: .9em; overflow-wrap: anywhere; }
.muted { color: var(--muted); font-size: .9375rem; }
/* Buttons: quiet by default; blue only for the page's main one. */
.button, button { display: inline-block; font: inherit; padding: 9px 16px; border-radius: 8px; border: 1px solid var(--line);
  background: transparent; color: var(--text); text-decoration: none; cursor: pointer; text-align: center; }
body:not(.q-page) .button:not(.q-btn), body:not(.q-page) button:not(.q-btn) { min-height: 44px; font-weight: 600; font-size: 1rem; line-height: 1.5; }
.button:hover, button:hover { border-color: var(--faint); }
button:disabled { opacity: 0.5; cursor: default; }
button.wide, a.button:not(.wide):not(.home):not(.q-btn), body:not(.q-page) #approve, #username-form button, form.rename button[type="submit"], form[action="/add"] button {
  background: var(--blue); border-color: var(--blue); color: var(--accent-text); }
button.wide:hover, a.button:not(.wide):not(.home):not(.q-btn):hover, body:not(.q-page) #approve:hover, #username-form button:hover, form.rename button[type="submit"]:hover, form[action="/add"] button:hover {
  background: var(--blue-hover); border-color: var(--blue-hover); }
button.link, button.secondary, button.danger { background: none; border: none; padding: 0; min-height: 0; text-decoration: underline; text-underline-offset: 3px; text-decoration-thickness: 1px; }
button.link, button.secondary { color: var(--accent); }
button.danger { color: var(--danger); }
#delete-account, body:not(.q-page) #deny { padding: 9px 16px; min-height: 44px; border: 1px solid var(--danger); text-decoration: none; }
button.small { font-size: .9375rem; vertical-align: baseline; margin-left: 10px; }
.button.home { margin-left: auto; padding: 5px 12px; font-size: 0.9rem; }
body:not(.q-page) .button.home { padding: 6px 14px; min-height: 38px; font-size: .9375rem; }
.wide { width: 100%; }
label { display: block; margin: 8px 0 6px; font-weight: 600; }
input, textarea { width: 100%; font: inherit; padding: 0 14px; border-radius: 8px; border: 1px solid var(--line); background: transparent; color: var(--text); }
input { height: 46px; }
textarea { padding: 10px 14px; resize: vertical; min-height: 4.5em; }
form button { margin-top: 12px; }
.input-row { display: flex; gap: 8px; align-items: center; max-width: 560px; }
.input-row input { flex: 1; min-width: 0; }
.input-row button { margin-top: 0; }
#username-form p.muted { margin-top: 8px; }
form.rename .input-row button.secondary { margin-left: 8px; }
.cf-turnstile { margin-top: 12px; }
.or { text-align: center; color: var(--faint); margin: 20px 0 4px; }
.error { color: var(--danger); }
.notice { color: var(--warn); }
.status { margin-top: 12px; }
.legal { color: var(--faint); font-size: .875rem; text-align: center; margin: 20px 0 0; }
footer nav { color: var(--faint); }
footer nav a { color: var(--muted); }
/* Lists: hairlines between the rows. */
.list { list-style: none; padding: 0; margin: 0; border-top: 1px solid var(--line); }
.list > li { border-bottom: 1px solid var(--line); padding: 18px 0; }
.list > li strong { font-weight: 650; }
.actions { margin-top: 10px; display: flex; gap: 10px 20px; flex-wrap: wrap; align-items: center; }
/* Online, offline: a coloured dot and a word, no box. */
.pill { display: inline-flex; align-items: center; gap: 6px; margin-left: 8px; font-size: .9375rem; font-weight: 600; color: var(--faint); }
.pill::before { content: ""; width: 8px; height: 8px; border-radius: 50%; background: currentColor; }
.pill.online { color: var(--ok); }
.pill.offline { color: var(--danger); }
.pill.pending { color: var(--warn); }
.server > .muted, .nest > .muted { margin-top: 2px; }
.server > p.muted { margin: 22px 0 6px; font-weight: 650; color: var(--text); }
.devices { border-top-color: var(--line); }
.devices > li { padding: 14px 0; }
.devices .actions { margin-top: 6px; }
.server > .actions { margin-top: 18px; }
#servers + p.muted { margin-top: 18px; }
pre { white-space: pre-wrap; word-break: break-all; background: var(--tint); border-radius: 6px; padding: 14px 16px; font-size: .9375rem; }
dl { display: grid; grid-template-columns: max-content 1fr; gap: 6px 20px; margin: 18px 0; }
dt { color: var(--faint); }
dd { margin: 0; }
fieldset { border: 1px solid var(--line); border-radius: 8px; margin: 12px 0; padding: 8px 12px; }
legend { color: var(--muted); padding: 0 4px; }
label.choice { display: flex; gap: 8px; align-items: center; margin: 6px 0; font-weight: 400; }
label.choice input { width: auto; height: auto; }
details.card { margin-top: 40px; padding: 14px 0; border-top: 1px solid var(--line); border-bottom: 1px solid var(--line); }
details.card > summary { font: 400 1.375rem/1.3 var(--serif); cursor: pointer; }
details.card[open] > summary { margin-bottom: 12px; }
form.rename { margin-top: 4px; }
form.rename label { margin-top: 0; font-size: 0.9375rem; }
form.rename p, .rename-done { margin: 6px 0 0; font-size: 0.9375rem; }
.saved { color: var(--ok); }
.people { margin-top: 14px; padding-top: 12px; border-top: 1px dashed var(--line); }
.people h3 { display: inline; font-size: 1rem; margin: 0; }
.people-list > li.person { display: flex; gap: 12px; align-items: flex-start; }
.person .who { flex: 1; min-width: 0; overflow-wrap: anywhere; }
.letter { flex: 0 0 auto; width: 30px; height: 30px; border-radius: 15px; display: inline-grid; place-items: center;
  background: var(--bg); border: 1px solid var(--line); color: var(--accent); font-weight: 700; }
.person.waiting .letter { color: var(--warn); border-color: var(--warn); }
.invite-form, form.change { margin-top: 10px; padding: 12px 14px; border: 1px solid var(--line); border-radius: 8px; }
.invite-form h3 { display: block; margin: 0 0 4px; font-size: 1rem; }
label.choice span .muted { display: block; }
.folder-ticks { max-height: 440px; overflow-y: auto; }
.folder-ticks label.choice[data-linked-in] { opacity: .55; }
.folder-ticks.off { opacity: .45; }
/* The beta program: words and hairlines, blue only for Send sign-up. */
.terms label.choice, form.signup label.choice { align-items: flex-start; gap: 12px; max-width: 600px; }
.terms label.choice input, form.signup label.choice input { margin-top: 6px; accent-color: var(--blue); }
.terms { font-size: .9375rem; }
form.signup fieldset { border: none; padding: 0; margin: 0 0 6px; }
form.signup legend { padding: 0; margin-bottom: 4px; font-weight: 650; color: var(--text); }
form.signup label.choice b { font-weight: 650; }
form.signup label.choice .muted { display: block; line-height: 1.45; }
form.signup .terms { border-top: 1px solid var(--line); margin-top: 12px; padding-top: 12px; }
form.signup .signup-note { margin-top: 12px; }
#beta .notice { margin: 0 0 8px; }
button.join { background: var(--blue); border-color: var(--blue); color: var(--accent-text); margin-top: 14px; }
button.join:hover:not(:disabled) { background: var(--blue-hover); border-color: var(--blue-hover); }
.beta-list > li { display: grid; grid-template-columns: 1fr auto; gap: 4px 24px; align-items: center; }
.beta-list > li.muted { display: block; }
.beta-list .who { min-width: 0; overflow-wrap: anywhere; }
.beta-list .note { grid-column: 1 / -1; margin: 2px 0 0; font-size: .9375rem; }
.beta-list .note.warn { color: var(--warn); }
select { font: inherit; font-size: 1rem; height: 44px; padding: 0 36px 0 12px; border-radius: 8px; border: 1px solid var(--line);
  background: var(--bg) url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='12' height='8' viewBox='0 0 12 8'%3E%3Cpath d='M1 1l5 5 5-5' fill='none' stroke='%23656C7A' stroke-width='1.6'/%3E%3C/svg%3E") no-repeat right 12px center;
  color: var(--text); appearance: none; min-width: 150px; cursor: pointer; }
select:disabled { opacity: .5; cursor: default; }
select:focus-visible { outline: 2px solid var(--blue); outline-offset: 3px; }
.leave { margin-top: 22px; }
.leave p { margin: 6px 0 0; }
form.inline { display: inline; }
form.inline button { margin-top: 0; }
/* The admin pages: the account pages' look, and a table where the reader compares rows. */
body.admin main { max-width: 1000px; }
.facts { display: flex; gap: 28px; flex-wrap: wrap; margin: 6px 0 4px; }
.facts div { font-size: .9375rem; color: var(--muted); }
.facts b { display: block; font: 400 2rem/1.1 var(--serif); color: var(--text); }
.member { display: grid; grid-template-columns: 1fr auto; gap: 2px 20px; align-items: start; }
.member .what { font-size: .9375rem; color: var(--muted); }
.member .what b { color: var(--text); font-weight: 600; }
.member .actions { grid-row: 1 / span 2; grid-column: 2; margin-top: 0; }
.member.gone strong { color: var(--faint); }
table { width: 100%; border-collapse: collapse; font-size: .9375rem; }
th { text-align: left; font-weight: 600; color: var(--faint); padding: 8px 12px 8px 0; border-bottom: 1px solid var(--line); }
td { padding: 12px 12px 12px 0; border-bottom: 1px solid var(--line); vertical-align: top; }
td .muted { font-size: .875rem; }
.ch-public { color: var(--accent); font-weight: 600; }
.ch-dev { color: var(--warn); font-weight: 600; }
.small-note { font-size: .9375rem; color: var(--muted); margin-top: 10px; }
.cookie-notice { position: fixed; left: 24px; bottom: calc(24px + env(safe-area-inset-bottom, 0px)); z-index: 20;
  width: 400px; max-width: calc(100% - 48px); background: var(--bg); color: var(--text); border: 1px solid var(--line);
  border-radius: 8px; padding: 16px 18px; font-size: 15px; line-height: 1.5;
  box-shadow: 0 1px 2px rgba(15, 18, 24, .05), 0 12px 32px rgba(15, 18, 24, .12); animation: cookie-notice-in .24s ease-out both; }
.cookie-notice p { margin: 0 0 12px; }
.cookie-notice-actions { display: flex; align-items: center; gap: 6px 14px; flex-wrap: wrap; }
.cookie-notice-actions button { padding: 7px 22px; font-weight: 600; }
.cookie-notice-actions a { padding: 8px 4px; font-weight: 600; border-radius: 6px; text-decoration: none; }
.cookie-notice-actions a:hover { text-decoration: underline; }
.cookie-notice button:focus-visible, .cookie-notice a:focus-visible { outline: 2px solid var(--blue); outline-offset: 2px; }
@keyframes cookie-notice-in { from { opacity: 0; transform: translateY(12px); } to { opacity: 1; transform: none; } }
@media (prefers-reduced-motion: reduce) { .cookie-notice { animation: none; } }
@media (prefers-color-scheme: dark) { .cookie-notice { box-shadow: 0 1px 2px rgba(0, 0, 0, .3), 0 12px 32px rgba(0, 0, 0, .4); } }
@media (max-width: 600px) {
  header.top { padding: 14px 16px; }
  main { padding: 16px 16px 8px; }
  footer { padding: 56px 16px 32px; }
  .input-row { flex-wrap: wrap; }
  .input-row input { flex-basis: 100%; }
  .beta-list > li { grid-template-columns: 1fr; }
  .beta-list select { width: 100%; }
  .member { grid-template-columns: 1fr; }
  .member .actions { grid-row: auto; grid-column: 1; }
  table { display: block; overflow-x: auto; }
  .cookie-notice { left: 16px; right: 16px; bottom: calc(16px + env(safe-area-inset-bottom, 0px)); width: auto; max-width: none; }
}
@media print { .cookie-notice { display: none; } }
`;

const SHARED_JS = String.raw`
function pnApi(method, path, body) {
  var opts = { method: method, credentials: "same-origin", headers: {} };
  if (body !== undefined) {
    opts.headers["Content-Type"] = "application/json";
    opts.body = JSON.stringify(body);
  }
  return fetch(path, opts).then(function (r) {
    if (r.status === 204) return { ok: true, status: 204, body: {} };
    return r.json().catch(function () { return {}; }).then(function (b) { return { ok: r.ok, status: r.status, body: b || {} }; });
  }, function () {
    return { ok: false, status: 0, body: { message: "Could not reach Pairnets. Check the connection and try again." } };
  });
}
function pnShow(el, text, isError) {
  if (!el) return;
  el.hidden = false;
  el.textContent = text;
  el.className = isError ? "status error" : "status";
}
`;

const LOGIN_JS = String.raw`(function () {
  "use strict";
  var form = document.getElementById("email-form");
  var status = document.getElementById("email-status");
  if (!form) return;
  form.addEventListener("submit", function (ev) {
    ev.preventDefault();
    var email = form.elements.namedItem("email").value;
    var field = form.elements.namedItem("cf-turnstile-response");
    var button = form.querySelector("button");
    button.disabled = true;
    pnApi("POST", "/v1/login/email", { email: email, turnstile: field ? field.value : "", next: form.getAttribute("data-next") || "/account" })
      .then(function (res) {
        if (res.ok) {
          form.hidden = true;
          pnShow(status, "If that address can receive email, a sign-in link is on its way. Open it in this browser within 15 minutes.", false);
          return;
        }
        pnShow(status, res.body.message || "That did not work. Try again.", true);
        button.disabled = false;
        if (window.turnstile) { try { window.turnstile.reset(); } catch (e) { /* ignore */ } }
      });
  });
})();
`;

const EMAIL_JS = String.raw`(function () {
  "use strict";
  var code = new URLSearchParams(location.hash.slice(1)).get("code");
  history.replaceState(null, "", location.pathname);
  var button = document.getElementById("email-continue");
  var status = document.getElementById("email-status");
  if (!code) {
    pnShow(status, "This page needs the link from the sign-in email. Ask for a new one on the sign-in page.", true);
    return;
  }
  button.disabled = false;
  button.addEventListener("click", function () {
    button.disabled = true;
    pnApi("POST", "/v1/login/email/confirm", { code: code }).then(function (res) {
      if (res.ok) { location.replace(res.body.next || "/account"); return; }
      pnShow(status, res.body.message || "That did not work.", true);
    });
  });
})();
`;

const ACCOUNT_JS = String.raw`(function () {
  "use strict";
  var status = document.getElementById("status");
  function ok(res) {
    if (res.body && res.body.error === "reauth_required") { location.href = "/login?reauth=1&next=/account"; return false; }
    if (res.body && res.body.error === "unauthorized") { location.href = "/login?next=/account"; return false; }
    if (!res.ok) { pnShow(status, res.body.message || "That did not work.", true); return false; }
    return true;
  }
  var add = document.getElementById("add-nest");
  if (add) add.addEventListener("click", function () {
    add.disabled = true;
    pnApi("POST", "/v1/nests/claim-codes", {}).then(function (res) {
      add.disabled = false;
      if (!ok(res)) return;
      document.getElementById("claim").hidden = false;
      document.getElementById("claim-command").textContent = res.body.command;
      document.getElementById("claim-code").textContent = res.body.code;
      document.getElementById("claim-expires").textContent = new Date(res.body.expiresAt).toLocaleTimeString();
    });
  });
  var cancel = document.getElementById("cancel-codes");
  if (cancel) cancel.addEventListener("click", function () {
    pnApi("DELETE", "/v1/nests/claim-codes").then(function (res) {
      if (ok(res)) { document.getElementById("claim").hidden = true; pnShow(status, "Unused codes cancelled.", false); }
    });
  });
  function bytes(n) {
    if (typeof n !== "number") return "unknown";
    var units = ["bytes", "KB", "MB", "GB", "TB"];
    var i = 0;
    while (n >= 1000 && i < units.length - 1) { n = n / 1000; i++; }
    return (i === 0 ? n : n.toFixed(1)) + " " + units[i];
  }
  function el(tag, cls, text) {
    var x = document.createElement(tag);
    if (cls) x.className = cls;
    if (text !== undefined) x.textContent = text;
    return x;
  }
  var servers = document.getElementById("servers");
  function fillServer(li, s) {
    var pill = li.querySelector("[data-field=online]");
    pill.textContent = s.online ? "online" : "offline";
    pill.className = "pill " + (s.online ? "online" : "offline");
    var details = li.querySelector("[data-field=details]");
    var parts = [];
    if (s.serverVersion) parts.push("Version " + s.serverVersion);
    if (s.online) parts.push(bytes(s.freeBytes) + " free");
    else parts.push("Not connected right now" + (s.lastSeenAt ? " (last seen " + new Date(s.lastSeenAt).toLocaleString() + ")" : ""));
    details.textContent = parts.join(" \u00b7 ");
    fillPeople(li, s);
    var list = li.querySelector("[data-field=devices]");
    while (list.firstChild) list.removeChild(list.firstChild);
    // A nest that never connected has no computers to rename yet.
    li.querySelector("[data-field=rename-offline]").hidden = s.online || s.status === "pending";
    if (!s.online) { list.appendChild(el("li", "muted", "Shown when the nest is connected.")); return; }
    // Friends' computers are counted under their person in People, not listed here.
    var own = s.devices.filter(function (d) { return !d.personId; });
    if (!own.length) { list.appendChild(el("li", "muted", "None yet.")); return; }
    own.forEach(function (d) {
      var item = el("li");
      item.setAttribute("data-device", d.id);
      var line = el("div");
      var name = el("strong", "", d.name || "Computer");
      name.setAttribute("data-field", "name");
      line.appendChild(name);
      if (d.system) line.appendChild(el("span", "muted", " " + d.system));
      item.appendChild(line);
      item.appendChild(el("div", "muted", d.lastSeen ? "Last synced " + new Date(d.lastSeen).toLocaleString() : "Not synced yet"));
      var actions = el("div", "actions");
      var r = el("button", "secondary", "Rename");
      r.type = "button";
      r.setAttribute("data-action", "rename-device");
      actions.appendChild(r);
      var b = el("button", "danger", "Remove");
      b.type = "button";
      b.setAttribute("data-action", "remove-device");
      actions.appendChild(b);
      item.appendChild(actions);
      list.appendChild(item);
    });
  }
  var MAX_NAME = 64;
  var renames = 0;
  function clearDone(row) {
    var old = row.querySelector(":scope > .rename-done");
    if (old) old.parentNode.removeChild(old);
  }
  function done(row, after, cls, text) {
    clearDone(row);
    var p = el("p", "rename-done " + cls, text);
    p.setAttribute("role", "status");
    row.insertBefore(p, after.nextSibling);
  }
  // Rename in place: the row's own parts are hidden and a small form stands where they were until Save or Cancel.
  // send(name) answers an error to show in the form, or nothing when the new name was saved.
  function renameBox(row, parts, what, current, hint, opener, send) {
    if (row.querySelector(":scope > form.rename")) return;
    clearDone(row);
    var form = el("form", "rename");
    var id = "rename-" + (++renames);
    var label = el("label", "", "New name for this " + what);
    label.htmlFor = id;
    var input = el("input");
    input.id = id;
    input.maxLength = MAX_NAME;
    input.value = current;
    input.autocomplete = "off";
    var save = el("button", "", "Save");
    save.type = "submit";
    var cancel = el("button", "secondary", "Cancel");
    cancel.type = "button";
    var line = el("div", "input-row");
    line.appendChild(input);
    line.appendChild(save);
    line.appendChild(cancel);
    var error = el("p", "error");
    error.setAttribute("role", "alert");
    error.hidden = true;
    form.appendChild(label);
    form.appendChild(line);
    form.appendChild(el("p", "muted", "Was: " + current + " \u00b7 up to " + MAX_NAME + " letters" + hint));
    form.appendChild(error);
    row.insertBefore(form, parts[parts.length - 1].nextSibling);
    parts.forEach(function (p) { p.hidden = true; });
    function close() {
      form.parentNode.removeChild(form);
      parts.forEach(function (p) { p.hidden = false; });
      if (opener && !opener.disabled) opener.focus();
    }
    cancel.addEventListener("click", close);
    form.addEventListener("keydown", function (ev) {
      if (ev.key === "Escape") { ev.preventDefault(); close(); }
    });
    form.addEventListener("submit", function (ev) {
      ev.preventDefault();
      var name = input.value.trim();
      if (!name) { error.textContent = "Type a name."; error.hidden = false; input.focus(); return; }
      save.disabled = true;
      error.hidden = true;
      send(name).then(function (problem) {
        save.disabled = false;
        if (problem === undefined) { close(); return; }
        if (problem) { error.textContent = problem; error.hidden = false; }
      });
    });
    input.focus();
    input.select();
  }
  // The error to show in a rename form ("" when the page goes to the sign-in page instead).
  function renameFailed(res) {
    if (res.body && (res.body.error === "reauth_required" || res.body.error === "unauthorized")) { ok(res); return ""; }
    return res.body.message || "That did not work. Try again.";
  }
  function renameOffline(li) {
    li.querySelector("[data-field=rename-offline]").hidden = false;
    Array.prototype.forEach.call(li.querySelectorAll("button[data-action=rename-device]"), function (x) { x.disabled = true; });
  }
  var SAVED = "Saved. Your other computers show the new name within a few seconds.";
  var SOON = " \u00b7 your other computers show it within a few seconds.";
  function renameDevice(li, id, dev, opener) {
    var name = dev.querySelector("[data-field=name]");
    var parts = Array.prototype.filter.call(dev.children, function (c) { return !c.classList.contains("rename-done"); });
    renameBox(dev, parts, "computer", name.textContent, SOON, opener, function (typed) {
      var path = "/v1/servers/" + id + "/devices/" + encodeURIComponent(dev.getAttribute("data-device"));
      return pnApi("PATCH", path, { name: typed }).then(function (res) {
        if (!res.ok) {
          if (res.body.error === "nest_offline") renameOffline(li);
          return renameFailed(res);
        }
        name.textContent = res.body.name;
        var last = parts[parts.length - 1];
        if (res.body.name !== res.body.requested) {
          done(dev, last, "notice", "Another computer in this nest is already called " + res.body.requested + ", so this one was saved as " +
            res.body.name + ". Rename it again to pick a different name.");
        } else {
          done(dev, last, "saved", SAVED);
        }
      });
    });
  }
  // A nest's name, saved with PATCH to path ({label}); hint ends the form's small print, saved is said afterwards.
  function renameNest(li, path, hint, saved, opener) {
    var row = li.querySelector(".row");
    var name = row.querySelector("[data-field=label]");
    renameBox(li, [row], "nest", name.textContent, hint, opener, function (typed) {
      return pnApi("PATCH", path, { label: typed }).then(function (res) {
        if (!res.ok) return renameFailed(res);
        name.textContent = res.body.label;
        done(li, row, "saved", saved);
      });
    });
  }
  // ---- People (friends on a nest): computers per person, what the nest allows, inviting, answering, changing.
  function fillPeople(li, s) {
    var counts = {};
    (s.devices || []).forEach(function (d) { if (d.personId) counts[d.personId] = (counts[d.personId] || 0) + 1; });
    Array.prototype.forEach.call(li.querySelectorAll("[data-person]"), function (p) {
      var n = counts[p.getAttribute("data-person")] || 0;
      var field = p.querySelector("[data-field=computers]");
      if (field && s.online) field.textContent = " · " + (n === 1 ? "1 computer" : n + " computers");
    });
    var note = li.querySelector("[data-field=friends-note]");
    var open = li.querySelector("button[data-action=invite-open]");
    if (s.online && s.friends === false) {
      note.textContent = "Update your nest first to invite friends. Friends need its newest version.";
      note.hidden = false;
      if (open) open.disabled = true;
    }
    if (s.sealed) {
      var chosen = li.querySelector("[data-field=chosen]");
      if (chosen) chosen.disabled = true;
      li.querySelector("[data-field=sealed-note]").hidden = false;
    }
  }
  function copy(text, button) {
    function copied() {
      var was = button.textContent;
      button.textContent = "Copied";
      setTimeout(function () { button.textContent = was; }, 2000);
    }
    function fallback() { window.prompt("Copy this link:", text); }
    if (navigator.clipboard && navigator.clipboard.writeText) navigator.clipboard.writeText(text).then(copied, fallback);
    else fallback();
  }
  // One folder per line: a comma is part of a name.
  function folderList(text) {
    return text.split(/\r?\n/).map(function (f) { return f.trim(); }).filter(function (f) { return f.length > 0; });
  }
  // The folders a form names: the invite form's ticked boxes and its "Add another folder" box, or Change…'s lines.
  function chosenFolders(form) {
    var list = [];
    form.querySelectorAll("input[name=folder-pick]:checked").forEach(function (box) { list.push(box.value); });
    var typed = form.querySelector("[name=folders]");
    if (typed) list = list.concat(folderList(typed.value));
    var extra = form.querySelector("[name=folder-add]");
    if (extra && extra.value.trim()) list.push(extra.value.trim());
    return list.filter(function (f, i) { return list.indexOf(f) === i; });
  }
  // "Add" next to "Add another folder": the name becomes one more ticked box, and "Chosen folders" is picked.
  function addFolder(form) {
    var input = form.querySelector("[name=folder-add]");
    var name = input.value.trim();
    if (!name) { input.focus(); return; }
    var same = Array.prototype.filter.call(form.querySelectorAll("input[name=folder-pick]"), function (box) { return box.value === name; });
    if (same.length) same[0].checked = true;
    else {
      var label = el("label", "choice");
      var box = el("input");
      box.type = "checkbox";
      box.name = "folder-pick";
      box.value = name;
      box.checked = true;
      label.appendChild(box);
      label.appendChild(document.createTextNode(" " + name));
      form.querySelector("[data-field=folder-ticks]").appendChild(label);
    }
    input.value = "";
    pickChosen(form);
    input.focus();
  }
  function pickChosen(form) {
    var chosen = form.querySelector("[data-field=chosen]");
    if (chosen && !chosen.disabled) chosen.checked = true;
    syncWhat(form);
  }
  // "The whole nest" shares everything: the folder tick boxes (and the typed names) are greyed out and can't be ticked
  // then, so it is never unclear what a ticked folder would mean. "Add" still picks "Chosen folders" for the name.
  function syncWhat(form) {
    var whole = form.querySelector("input[type=radio][value=whole]");
    var off = !!(whole && whole.checked);
    Array.prototype.forEach.call(form.querySelectorAll(".folder-ticks"), function (ticks) { ticks.classList.toggle("off", off); });
    Array.prototype.forEach.call(form.querySelectorAll("input[name=folder-pick]"), function (box) {
      box.disabled = off || box.closest("label").hasAttribute("data-linked-in");
    });
    var typed = form.querySelector("textarea[name=folders]");
    if (typed) typed.disabled = off;
  }
  // ---- The nest's own top-level folders as tick boxes (1.0.61.3): asked of the nest once per nest when a form opens.
  // Typed names stay possible; when the nest can't be asked, typing is the way.
  var folderLists = {};
  function nestFolders(id) {
    if (!folderLists[id]) folderLists[id] = pnApi("GET", "/v1/servers/" + id + "/folders");
    return folderLists[id];
  }
  function folderBox(name, checked, detail) {
    var label = el("label", "choice");
    var box = el("input");
    box.type = "checkbox";
    box.name = "folder-pick";
    box.value = name;
    box.checked = checked;
    label.appendChild(box);
    var text = el("span", "", name);
    if (detail) text.appendChild(el("span", "muted", detail));
    label.appendChild(document.createTextNode(" "));
    label.appendChild(text);
    return label;
  }
  function folderDetail(f) {
    return f.linkedIn ? "Came from another nest, so it can't be shared" : bytes(f.bytes) + " · " + f.files.toLocaleString() + (f.files === 1 ? " file" : " files");
  }
  // Fills a form's tick boxes from the nest: its folders by name, the ones already ticked (from an app, or a friend's
  // share) kept ticked, and a ticked name the nest doesn't have kept as "Not on the nest yet". Calls done(true) once listed.
  function fillFolders(li, id, ticks, status, done) {
    var nest = li.querySelector("[data-field=label]").textContent;
    status.textContent = "Getting the folders on " + nest + "…";
    status.hidden = false;
    nestFolders(id).then(function (res) {
      if (res.body && (res.body.error === "reauth_required" || res.body.error === "unauthorized")) { ok(res); return; }
      var list = res.ok && res.body.ok ? res.body : null;
      if (!list) {
        status.textContent = "Couldn't reach " + nest + ", so its folders can't be listed. Type the folder names instead.";
        done(false, false);
        return;
      }
      if (list.sealed) { status.hidden = true; done(false, true); return; }
      var ticked = Array.prototype.map.call(ticks.querySelectorAll("input[name=folder-pick]:checked"), function (box) { return box.value; });
      ticks.textContent = "";
      list.folders.forEach(function (f) {
        // A folder that came in through a link is never shared on, with anyone: shown, but it can't be ticked.
        var label = folderBox(f.name, !f.linkedIn && ticked.indexOf(f.name) >= 0, folderDetail(f));
        if (f.linkedIn) {
          label.setAttribute("data-linked-in", "");
          label.querySelector("input").disabled = true;
        }
        ticks.appendChild(label);
      });
      var names = list.folders.map(function (f) { return f.name; });
      ticked.forEach(function (name) {
        if (names.indexOf(name) < 0) ticks.appendChild(folderBox(name, true, "Not on the nest yet"));
      });
      status.textContent = !list.folders.length ? nest + " has no folders yet. Share the whole nest, or type a folder name." :
        list.more ? "Showing the first " + list.folders.length + " folders. Type a name for another." : "";
      status.hidden = !status.textContent;
      done(true, false);
    });
  }
  // The invite form: its tick boxes from the nest, once; a sealed nest is shared whole only.
  function loadInviteFolders(li, id, form) {
    syncWhat(form);
    if (form.hasAttribute("data-folders-asked")) return;
    form.setAttribute("data-folders-asked", "");
    fillFolders(li, id, form.querySelector("[data-field=folder-ticks]"), form.querySelector("[data-field=folder-status]"), function (listed, sealed) {
      if (sealed) {
        var chosen = form.querySelector("[data-field=chosen]");
        chosen.disabled = true;
        if (chosen.checked) form.querySelector("input[name='what-" + id + "'][value=whole]").checked = true;
        form.querySelector("[data-field=sealed-note]").hidden = false;
        var add = form.querySelector("label[for='folder-add-" + id + "']");
        add.hidden = true;
        add.nextElementSibling.hidden = true;
      }
      if (listed) form.querySelector("label[for='folder-add-" + id + "']").textContent = "Or type a folder name";
      syncWhat(form);
    });
  }
  // What a form says: {access, folders?}, or the problem to show.
  function shareOf(form, whatName, accessName) {
    var what = form.querySelector("input[name='" + whatName + "']:checked");
    var access = form.querySelector("input[name='" + accessName + "']:checked");
    var body = { access: access ? access.value : "view", folders: null };
    if (what && what.value === "folders") {
      var list = chosenFolders(form);
      if (!list.length) return "Tick or type at least one folder, or choose The whole nest.";
      body.folders = list;
    }
    return body;
  }
  function sendInvite(li, id, form, withEmail, button) {
    var error = form.querySelector("[data-field=invite-error]");
    error.hidden = true;
    var body = shareOf(form, "what-" + id, "access-" + id);
    if (typeof body === "string") { error.textContent = body; error.hidden = false; return; }
    var kind = form.querySelector("input[name='kind-" + id + "']:checked");
    if (kind) body.kind = kind.value;
    var email = form.querySelector("input[name=email]").value.trim();
    if (withEmail) {
      if (!email) { error.textContent = "Type their email, or use Copy invite link."; error.hidden = false; return; }
      body.email = email;
    }
    button.disabled = true;
    pnApi("POST", "/v1/servers/" + id + "/invites", body).then(function (res) {
      button.disabled = false;
      if (!res.ok) {
        if (res.body && (res.body.error === "reauth_required" || res.body.error === "unauthorized")) { ok(res); return; }
        error.textContent = res.body.message || "That did not work. Try again.";
        error.hidden = false;
        return;
      }
      var said = res.body.emailed ? "Invite sent to " + email + ". You can copy the link too:" :
        withEmail ? "Too many invites sent today, try tomorrow. The link works now: copy it and send it yourself:" : "Copied. Send the link however you like; it works for 7 days:";
      form.querySelector("[data-field=invite-said]").textContent = said;
      form.querySelector("[data-field=invite-url]").value = res.body.link;
      form.querySelector("[data-field=invite-done]").hidden = false;
      if (!withEmail) copy(res.body.link, form.querySelector("button[data-action=invite-copy-new]"));
    });
  }
  var changes = 0;
  function radio(name, value, text, checked) {
    var label = el("label", "choice");
    var input = el("input");
    input.type = "radio";
    input.name = name;
    input.value = value;
    input.checked = checked;
    label.appendChild(input);
    label.appendChild(document.createTextNode(" " + text));
    return label;
  }
  // Change… opens a small form in the person's row: whole nest or folders, view only or can change.
  function changePerson(id, row, opener) {
    if (row.querySelector("form.change")) return;
    var n = ++changes;
    var form = el("form", "change");
    var folders = [];
    try { folders = JSON.parse(row.getAttribute("data-folders") || "[]"); } catch (e) { folders = []; }
    var access = row.getAttribute("data-access");
    form.appendChild(radio("change-what-" + n, "whole", "The whole nest", !folders.length));
    form.appendChild(radio("change-what-" + n, "folders", "Chosen folders", !!folders.length));
    // The shared folders are ticked boxes; the nest's other folders join them once it answers.
    var status = el("p", "muted");
    status.setAttribute("role", "status");
    status.hidden = true;
    var ticks = el("div", "folder-ticks");
    folders.forEach(function (f) { ticks.appendChild(folderBox(f, true)); });
    var label = el("label", "", "Other folder names, one per line");
    label.htmlFor = "change-folders-" + n;
    var input = el("textarea");
    input.id = "change-folders-" + n;
    input.name = "folders";
    input.rows = 2;
    input.autocomplete = "off";
    form.appendChild(status);
    form.appendChild(ticks);
    form.appendChild(label);
    form.appendChild(input);
    form.appendChild(radio("change-access-" + n, "view", "View only", access !== "edit"));
    form.appendChild(radio("change-access-" + n, "edit", "Can change", access === "edit"));
    var error = el("p", "error");
    error.setAttribute("role", "alert");
    error.hidden = true;
    var actions = el("div", "actions");
    var save = el("button", "", "Save");
    save.type = "submit";
    var cancel = el("button", "secondary", "Cancel");
    cancel.type = "button";
    actions.appendChild(save);
    actions.appendChild(cancel);
    form.appendChild(actions);
    form.appendChild(error);
    row.querySelector(".who").appendChild(form);
    syncWhat(form);
    fillFolders(row.closest("[data-server]"), id, ticks, status, function () { syncWhat(form); });
    cancel.addEventListener("click", function () { form.parentNode.removeChild(form); if (opener) opener.focus(); });
    form.addEventListener("submit", function (ev) {
      ev.preventDefault();
      var body = shareOf(form, "change-what-" + n, "change-access-" + n);
      if (typeof body === "string") { error.textContent = body; error.hidden = false; return; }
      save.disabled = true;
      pnApi("PATCH", "/v1/servers/" + id + "/people/" + row.getAttribute("data-person"), body).then(function (res) {
        save.disabled = false;
        if (!res.ok) {
          if (res.body && (res.body.error === "reauth_required" || res.body.error === "unauthorized")) { ok(res); return; }
          error.textContent = res.body.message || "That did not work. Try again.";
          error.hidden = false;
          return;
        }
        location.reload();
      });
    });
  }
  // The answers to People's buttons; true when the click was one of them.
  function peopleAction(b, li, id) {
    var action = b.getAttribute("data-action");
    var form = li.querySelector("[data-field=invite-form]");
    var invite = b.closest("[data-invite]");
    var person = b.closest("[data-person]");
    var inv = invite && invite.getAttribute("data-invite");
    if (action === "invite-open") { form.hidden = false; form.querySelector("input").focus(); loadInviteFolders(li, id, form); }
    else if (action === "invite-close") { form.hidden = true; }
    else if (action === "invite-add-folder") { addFolder(form); }
    else if (action === "invite-link") { sendInvite(li, id, form, false, b); }
    else if (action === "invite-copy-new") { copy(form.querySelector("[data-field=invite-url]").value, b); }
    else if (action === "invite-finish") { location.hash = "people-" + id; location.reload(); }
    else if (action === "invite-copy") { copy(invite.getAttribute("data-link"), b); }
    else if (action === "invite-approve" || action === "invite-decline") {
      b.disabled = true;
      pnApi("POST", "/v1/servers/" + id + "/invites/" + inv + "/" + (action === "invite-approve" ? "approve" : "decline")).then(function (res) {
        b.disabled = false;
        if (ok(res)) location.reload();
      });
    } else if (action === "invite-cancel") {
      if (!confirm("Cancel this invite? The link stops working.")) return true;
      pnApi("DELETE", "/v1/servers/" + id + "/invites/" + inv).then(function (res) { if (ok(res)) location.reload(); });
    } else if (action === "person-change") { changePerson(id, person, b); }
    else if (action === "person-remove") {
      var name = person.querySelector("[data-field=name]").textContent;
      var nest = li.querySelector("[data-field=label]").textContent;
      if (!confirm("Remove " + name + " from " + nest + "? Their computers stop syncing with your nest right away. Copies already on their computers stay there; nothing in your nest is deleted.")) return true;
      b.disabled = true;
      pnApi("DELETE", "/v1/servers/" + id + "/people/" + person.getAttribute("data-person")).then(function (res) {
        b.disabled = false;
        if (ok(res)) location.reload();
      });
    } else return false;
    return true;
  }
  // ---- The beta program: joining, leaving, and each own computer's choice (Off, Public beta, Developer beta).
  var beta = document.getElementById("beta");
  var betaList = document.getElementById("beta-list");
  var BETA_WORDS = { "off": "Off", "public-beta": "Public beta", "developer-beta": "Developer beta" };
  // The first app that knows beta updates (BETA_MIN_APP, from the page); an older one (or one the nest has no version
  // for) can't take them yet.
  var BETA_FIRST = ((beta && beta.getAttribute("data-min-app")) || "1.0.61.9").split(".").map(Number).concat([0, 0, 0, 0]).slice(0, 4);
  function betaReady(app) {
    var m = /^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?/.exec(app || "");
    if (!m) return false;
    for (var i = 0; i < 4; i++) {
      var n = Number(m[i + 1] || 0);
      if (n !== BETA_FIRST[i]) return n > BETA_FIRST[i];
    }
    return true;
  }
  function betaAllowed() {
    return ((beta && beta.getAttribute("data-channels")) || "").split(" ").filter(function (c) { return c; });
  }
  // The line under a row: what Off means for a computer still on a beta, or why the choice can't be made yet.
  function betaNote(item, d, value) {
    Array.prototype.forEach.call(item.querySelectorAll(".note"), function (n) { item.removeChild(n); });
    var text = "", cls = "note muted";
    if (!betaReady(d.app)) { text = "Can get beta updates after it updates to the newest Pairnets."; cls = "note warn"; }
    else if (value === "off" && /beta/i.test(d.app || "")) text = "Beta updates off. It keeps " + d.app + " until the next normal version is newer, then moves onto it by itself.";
    if (text) item.appendChild(el("p", cls, text));
  }
  function betaRow(s, d, many) {
    var item = el("li");
    item.setAttribute("data-device", d.id);
    item.setAttribute("data-server", s.id);
    var who = el("div", "who");
    who.appendChild(el("strong", "", d.name || "Computer"));
    var parts = [];
    if (d.system) parts.push(d.system);
    if (d.app) parts.push("Pairnets " + d.app);
    if (many) parts.push("on " + s.label);
    if (parts.length) who.appendChild(el("span", "muted", " " + parts.join(" · ")));
    item.appendChild(who);
    var current = d.beta && BETA_WORDS[d.beta.channel] ? d.beta.channel : "off";
    var select = el("select");
    select.setAttribute("data-field", "beta-channel");
    select.setAttribute("aria-label", "Beta updates for " + (d.name || "this computer"));
    var ready = betaReady(d.app);
    var choices = ready ? ["off"].concat(betaAllowed()) : ["off"];
    if (choices.indexOf(current) < 0) choices.push(current);
    choices.forEach(function (c) {
      var o = el("option", "", BETA_WORDS[c]);
      o.value = c;
      o.selected = c === current;
      select.appendChild(o);
    });
    select.disabled = !ready;
    select.setAttribute("data-was", current);
    item.appendChild(select);
    betaNote(item, d, current);
    select.addEventListener("change", function () {
      var was = select.getAttribute("data-was");
      var want = select.value;
      select.disabled = true;
      pnApi("PUT", "/v1/servers/" + s.id + "/devices/" + encodeURIComponent(d.id) + "/beta", { channel: want }).then(function (res) {
        select.disabled = false;
        if (!res.ok) {
          if (res.body && (res.body.error === "reauth_required" || res.body.error === "unauthorized")) { ok(res); return; }
          select.value = was;
          betaNote(item, d, was);
          item.appendChild(el("p", "note error", res.body.message || "That did not work. Try again."));
          return;
        }
        select.setAttribute("data-was", res.body.channel);
        select.value = res.body.channel;
        betaNote(item, d, res.body.channel);
      });
    });
    return item;
  }
  // One row per own computer on each connected nest (friends' computers are theirs to choose). The nest itself stays
  // on normal versions for now, so it has no row.
  function fillBeta(list) {
    if (!betaList) return;
    while (betaList.firstChild) betaList.removeChild(betaList.firstChild);
    var many = list.length > 1;
    list.forEach(function (s) {
      if (!s.online) {
        if (s.status !== "pending") betaList.appendChild(el("li", "muted", s.label + " is not connected right now, so its computers aren't listed."));
        return;
      }
      s.devices.filter(function (d) { return !d.personId; }).forEach(function (d) { betaList.appendChild(betaRow(s, d, many)); });
    });
    if (!betaList.firstChild) betaList.appendChild(el("li", "muted", "No computers yet. Sign in to Pairnets on a computer with this account."));
  }
  // The betas a sign-up asks for: the ticked ones on the form, or what the button names (agreeing to new terms).
  function betaPicks(button) {
    var boxes = beta.querySelectorAll("[data-field=beta-pick]");
    if (!boxes.length) return (button.getAttribute("data-asked") || "").split(" ").filter(function (c) { return c; });
    return Array.prototype.filter.call(boxes, function (x) { return x.checked; }).map(function (x) { return x.value; });
  }
  // Send sign-up waits for the terms tick and at least one beta.
  if (beta) beta.addEventListener("change", function (ev) {
    var field = ev.target.getAttribute("data-field");
    if (field !== "beta-terms" && field !== "beta-pick") return;
    var join = beta.querySelector("button[data-action=beta-join]");
    var tick = beta.querySelector("[data-field=beta-terms]");
    if (join) join.disabled = !(tick && tick.checked) || !betaPicks(join).length;
  });
  if (beta) beta.addEventListener("click", function (ev) {
    var b = ev.target.closest("button[data-action]");
    if (!b) return;
    var said = beta.querySelector("[data-field=beta-status]");
    var action = b.getAttribute("data-action");
    function answered(res) {
      b.disabled = false;
      if (res.body && (res.body.error === "reauth_required" || res.body.error === "unauthorized")) { ok(res); return; }
      if (!res.ok) { pnShow(said, res.body.message || "That did not work. Try again.", true); return; }
      location.reload();
    }
    if (action === "beta-join") {
      var tick = beta.querySelector("[data-field=beta-terms]");
      var picks = betaPicks(b);
      if (!tick || !tick.checked || !picks.length) return;
      b.disabled = true;
      pnApi("POST", "/v1/beta/join", { acceptTerms: true, termsVersion: b.getAttribute("data-terms-version"), channels: picks }).then(answered);
    } else if (action === "beta-add") {
      // One more beta, on the terms already agreed to.
      b.disabled = true;
      pnApi("POST", "/v1/beta/join", { acceptTerms: true, termsVersion: b.getAttribute("data-terms-version"), channels: [b.getAttribute("data-channel")] }).then(answered);
    } else if (action === "beta-leave") {
      if (!confirm("Leave the beta program? Every computer goes back to Off and keeps its beta until the next normal version is newer.")) return;
      b.disabled = true;
      pnApi("POST", "/v1/beta/leave").then(answered);
    }
  });
  function loadServers() {
    if ((!servers || !servers.querySelector("[data-server]")) && !betaList) return;
    pnApi("GET", "/v1/servers").then(function (res) {
      if (!ok(res) || !Array.isArray(res.body)) return;
      res.body.forEach(function (s) {
        var li = servers && servers.querySelector('[data-server="' + s.id + '"]');
        if (li) fillServer(li, s);
      });
      fillBeta(res.body);
    });
  }
  if (servers) servers.addEventListener("click", function (ev) {
    var b = ev.target.closest("button[data-action]");
    if (!b) return;
    var li = b.closest("[data-server]");
    var id = li && li.getAttribute("data-server");
    if (!id) return;
    if (peopleAction(b, li, id)) return;
    if (b.getAttribute("data-action") === "rename-device") {
      var renamed = b.closest("[data-device]");
      if (renamed) renameDevice(li, id, renamed, b);
    } else if (b.getAttribute("data-action") === "rename-server") {
      renameNest(li, "/v1/servers/" + id, SOON, SAVED, b);
    } else if (b.getAttribute("data-action") === "remove-device") {
      var dev = b.closest("[data-device]");
      if (!dev || !confirm("Remove this computer? It stops syncing until it signs in again.")) return;
      b.disabled = true;
      pnApi("DELETE", "/v1/servers/" + id + "/devices/" + encodeURIComponent(dev.getAttribute("data-device"))).then(function (res) {
        b.disabled = false;
        if (ok(res)) loadServers();
      });
    } else if (b.getAttribute("data-action") === "remove-server") {
      if (!confirm("Remove this nest from your account? Its computers stop syncing through Pairnets. The files stay on the nest.")) return;
      pnApi("DELETE", "/v1/servers/" + id).then(function (res) { if (ok(res)) location.reload(); });
    }
  });
  // Ticking a folder means "Chosen folders"; Enter in "Add another folder" adds it instead of sending the invite.
  if (servers) servers.addEventListener("change", function (ev) {
    var form = ev.target.closest("form.invite-form, form.change");
    if (form && ev.target.name === "folder-pick" && ev.target.checked) pickChosen(form);
    if (form && ev.target.type === "radio" && (ev.target.value === "whole" || ev.target.value === "folders")) syncWhat(form);
  });
  if (servers) servers.addEventListener("keydown", function (ev) {
    if (ev.key !== "Enter" || ev.target.name !== "folder-add") return;
    var form = ev.target.closest("form.invite-form");
    if (!form) return;
    ev.preventDefault();
    addFolder(form);
  });
  if (servers) servers.addEventListener("submit", function (ev) {
    var form = ev.target.closest("form.invite-form");
    if (!form) return;
    ev.preventDefault();
    var li = form.closest("[data-server]");
    sendInvite(li, li.getAttribute("data-server"), form, true, form.querySelector("button[type=submit]"));
  });
  loadServers();
  // Opened from an app's "Invite a friend" (/account?invite=…): that nest's form is open; bring it into view.
  var asked = document.querySelector("form.invite-form[data-open]");
  if (asked) {
    asked.scrollIntoView({ block: "center" });
    var askedLi = asked.closest("[data-server]");
    loadInviteFolders(askedLi, askedLi.getAttribute("data-server"), asked);
  }
  var linked = document.getElementById("links-list");
  if (linked) linked.addEventListener("click", function (ev) {
    var b = ev.target.closest("button[data-action=unlink]");
    if (!b) return;
    var li = b.closest("[data-link]");
    if (!confirm("Unlink " + li.querySelector("strong").textContent + "? The copy stays where it is and stops changing.")) return;
    b.disabled = true;
    pnApi("DELETE", "/v1/links/" + li.getAttribute("data-link")).then(function (res) {
      b.disabled = false;
      if (ok(res)) location.reload();
    });
  });
  var shared = document.getElementById("shared-list");
  if (shared) shared.addEventListener("click", function (ev) {
    var b = ev.target.closest("button[data-action=leave]");
    if (!b) return;
    var li = b.closest("[data-shared]");
    var label = li.querySelector("strong").textContent;
    if (!confirm("Leave " + label + "? It stops syncing on your computers. Files already on them stay there.")) return;
    b.disabled = true;
    pnApi("DELETE", "/v1/shared/" + li.getAttribute("data-shared")).then(function (res) {
      b.disabled = false;
      if (ok(res)) location.reload();
    });
  });
  var nests = document.getElementById("nests");
  if (nests) nests.addEventListener("click", function (ev) {
    var b = ev.target.closest("button[data-action]");
    if (!b) return;
    var li = b.closest("[data-nest]");
    var id = li && li.getAttribute("data-nest");
    if (!id) return;
    if (b.getAttribute("data-action") === "rename") {
      renameNest(li, "/v1/nests/" + id, ".", "Saved.", b);
    } else if (b.getAttribute("data-action") === "toggle") {
      pnApi("PATCH", "/v1/nests/" + id, { hostedLogin: b.getAttribute("data-on") !== "1" }).then(function (res) { if (ok(res)) location.reload(); });
    } else if (b.getAttribute("data-action") === "remove") {
      if (!confirm("Remove this nest from your account? Signing in to it with this account stops working.")) return;
      pnApi("DELETE", "/v1/nests/" + id).then(function (res) { if (ok(res)) location.reload(); });
    }
  });
  var sessions = document.getElementById("sessions");
  if (sessions) sessions.addEventListener("click", function (ev) {
    var b = ev.target.closest("button[data-action=revoke]");
    if (!b) return;
    var li = b.closest("[data-session]");
    var current = !!li.querySelector(".pill");
    pnApi("DELETE", "/v1/sessions/" + li.getAttribute("data-session")).then(function (res) {
      if (!ok(res)) return;
      if (current) location.href = "/login"; else location.reload();
    });
  });
  var logout = document.getElementById("logout");
  if (logout) logout.addEventListener("click", function () {
    pnApi("POST", "/v1/logout").then(function () { location.href = "/login"; });
  });
  var nameForm = document.getElementById("username-form");
  if (nameForm) nameForm.addEventListener("submit", function (ev) {
    ev.preventDefault();
    var input = document.getElementById("username");
    var save = nameForm.querySelector("button[type=submit]");
    save.disabled = true;
    pnApi("PATCH", "/v1/me", { username: input.value }).then(function (res) {
      save.disabled = false;
      if (!ok(res)) return;
      input.value = res.body.username || "";
      pnShow(status, res.body.username ? "Saved. You are shown as " + res.body.username + "." : "Saved. You are shown as " + input.placeholder + ".", false);
    });
  });
  var del = document.getElementById("delete-account");
  if (del) del.addEventListener("click", function () {
    if (!confirm("Delete your Pairnets account and everything stored about it? This cannot be undone.")) return;
    pnApi("DELETE", "/v1/me").then(function (res) { if (ok(res)) location.href = "/login"; });
  });
})();
`;

// The apps' "Quiet" look, for the "Sign in an app?" page only (pages.ts: <body class="q-page">, after style.css).
// Warm paper and ink, hairlines instead of cards, Instrument Sans (fonts.ts). Colours follow the system's light or dark.
const APP_STYLE = String.raw`
@font-face { font-family: "Instrument Sans"; font-style: normal; font-weight: 400; font-display: swap; src: url(/assets/fonts/InstrumentSans-Regular.woff2) format("woff2"); }
@font-face { font-family: "Instrument Sans"; font-style: normal; font-weight: 500; font-display: swap; src: url(/assets/fonts/InstrumentSans-Medium.woff2) format("woff2"); }
@font-face { font-family: "Instrument Sans"; font-style: normal; font-weight: 600; font-display: swap; src: url(/assets/fonts/InstrumentSans-SemiBold.woff2) format("woff2"); }

.q-page {
  --q-bg: #F7F6F3; --q-card: #FFFFFF; --q-text: #1B1A17; --q-muted: #69655D; --q-line: #E3E0D9; --q-field-line: #D6D2CA;
  --q-hover: #EFEDE8; --q-subtle: #ECE9E3; --q-accent: #2F47C4; --q-track: #E6E3DC;
  --q-primary: #1B1A17; --q-primary-hover: #37342F; --q-primary-ink: #F7F6F3;
  --q-ok: #2D7A4B; --q-ok-soft: #E2EFE6; --q-warn: #8A4B00; --q-warn-soft: #F5E9D6; --q-bad: #B42318; --q-bad-soft: #F7E2DF;
  color-scheme: light;
}
@media (prefers-color-scheme: dark) {
  .q-page {
    --q-bg: #161513; --q-card: #22211E; --q-text: #ECEAE4; --q-muted: #A39E94; --q-line: #2D2B27; --q-field-line: #3A3833;
    --q-hover: #1F1E1B; --q-subtle: #262420; --q-accent: #8D9DFF; --q-track: #2F2D29;
    --q-primary: #ECEAE4; --q-primary-hover: #FFFFFF; --q-primary-ink: #161513;
    --q-ok: #6CC991; --q-ok-soft: #18271E; --q-warn: #E9A85A; --q-warn-soft: #2E2417; --q-bad: #F0776B; --q-bad-soft: #301B18;
    color-scheme: dark;
  }
}
.q-page { min-height: 100vh; display: flex; flex-direction: column; background: var(--q-bg); color: var(--q-text);
  font: 15px/1.55 "Instrument Sans", "Segoe UI", system-ui, -apple-system, sans-serif; font-variant-numeric: tabular-nums;
  -webkit-font-smoothing: antialiased; }
.q-page [hidden] { display: none !important; }
.q-page a { color: inherit; }
.q-page :focus-visible { outline: 2px solid var(--q-accent); outline-offset: 2px; }

/* The shared top bar and footer, in this look. */
.q-page header.top { gap: 10px; padding: 14px 28px; border-bottom-color: var(--q-line); }
.q-page header.top .brand { display: inline-flex; align-items: center; gap: 10px; font-size: 17px; font-weight: 600; letter-spacing: -0.01em; color: var(--q-text); }
.q-page header.top .brand::before { content: ""; width: 28px; height: 28px; background: url(/assets/logo.svg) center / contain no-repeat; }
.q-page header.top .muted { color: var(--q-muted); font-size: 14px; }
.q-page header.top .button.home { padding: 6px 12px; border-radius: 10px; border-color: var(--q-field-line); color: var(--q-text); font-size: 13.5px; font-weight: 500; }
.q-page header.top .button.home:hover { background: var(--q-hover); }
.q-page main { flex: 1; box-sizing: border-box; width: 100%; max-width: none; margin: 0; padding: 56px 16px 32px; display: flex; justify-content: center; }
.q-page footer { order: 4; box-sizing: border-box; width: 100%; max-width: none; padding: 18px 16px 22px; text-align: center; font-size: 12.5px; }
.q-page footer nav, .q-page footer a { color: var(--q-muted); }
.q-page footer a { text-decoration: none; }
.q-page footer a:hover { text-decoration: underline; }

/* The cookie notice sits in the page, under the content, so it never covers the buttons (on a phone above all). */
.q-page .cookie-notice { position: static; order: 3; box-sizing: border-box; width: calc(100% - 32px); max-width: 440px; margin: 8px auto 4px;
  background: var(--q-card); color: var(--q-text); border-color: var(--q-line); border-radius: 12px; box-shadow: none; }
.q-page .cookie-notice-actions button { background: var(--q-primary); border-color: var(--q-primary); color: var(--q-primary-ink); border-radius: 10px; }
.q-page .cookie-notice-actions a { color: var(--q-text); }

/* One column; each answer is a screen of its own. */
.q-col { width: 100%; max-width: 440px; }
.q-screen { display: flex; flex-direction: column; }
.q-icon { display: block; }
.q-badge { align-self: center; width: 56px; height: 56px; border-radius: 28px; display: grid; place-items: center; background: var(--q-subtle); color: var(--q-text); }
.q-badge .q-icon { width: 26px; height: 26px; }
.q-badge.q-warn { background: var(--q-warn-soft); color: var(--q-warn); }
.q-page h1 { margin: 18px 0 0; text-align: center; font-size: 28px; line-height: 1.2; font-weight: 600; letter-spacing: -0.02em; overflow-wrap: anywhere; }
.q-page h1:focus { outline: none; }
.q-lead { margin: 8px 0 0; text-align: center; color: var(--q-muted); overflow-wrap: anywhere; text-wrap: balance; }
.q-lead b { color: var(--q-text); font-weight: 600; }
.q-earlier { align-self: center; margin: 12px 0 0; padding: 3px 12px; border-radius: 999px; background: var(--q-subtle); color: var(--q-muted); font-size: 13px; }
.q-codebox { margin-top: 26px; padding: 20px 16px 22px; border: 1px solid var(--q-line); border-radius: 14px; background: var(--q-card); text-align: center; }
.q-q { margin: 0; font-size: 14px; color: var(--q-muted); overflow-wrap: anywhere; }
.q-code { margin: 6px 0 0; font-size: 40px; line-height: 1.1; font-weight: 600; letter-spacing: 0.14em; padding-left: 0.14em; white-space: nowrap; }
.q-syncwith { margin: 14px 0 0; display: flex; align-items: center; gap: 10px; padding: 12px 14px; border: 1px solid var(--q-line); border-radius: 12px; font-size: 14px; overflow-wrap: anywhere; }
.q-syncwith .q-icon { width: 18px; height: 18px; flex-shrink: 0; color: var(--q-muted); }
.q-nests { margin: 14px 0 0; padding: 0; border: 0; min-width: 0; }
.q-nests legend { margin-bottom: 8px; padding: 0; color: var(--q-text); font-size: 13px; font-weight: 600; }
.q-choices { border: 1px solid var(--q-line); border-radius: 12px; overflow: hidden; background: var(--q-card); }
.q-page label.q-choice { display: flex; align-items: center; gap: 12px; margin: 0; padding: 12px 14px; font-size: 14px; cursor: pointer; overflow-wrap: anywhere; }
.q-choice + .q-choice { border-top: 1px solid var(--q-line); }
.q-page label.q-choice.q-have { cursor: default; color: var(--q-muted); }
.q-have-note { font-size: 13px; }
.q-page .q-choice input { flex-shrink: 0; width: 16px; height: 16px; margin: 0; padding: 0; accent-color: var(--q-primary); }
.q-note { margin: 14px 0 0; padding: 12px 14px; border-radius: 12px; background: var(--q-warn-soft); color: var(--q-warn); font-size: 13.5px; }
.q-longcode { margin: 22px 0 0; padding: 16px 12px; border: 1px solid var(--q-line); border-radius: 14px; background: var(--q-card); display: grid; grid-template-columns: repeat(4, auto); justify-content: center; gap: 6px 16px; font-family: var(--mono); font-size: 22px; font-weight: 600; letter-spacing: 0.06em; font-variant-numeric: tabular-nums; user-select: all; }
.q-longcode .q-check { color: var(--q-accent); }
.q-note b { font-weight: 600; }
.q-error { margin: 14px 0 0; padding: 12px 14px; border-radius: 12px; background: var(--q-bad-soft); color: var(--q-bad); font-size: 14px; }

.q-actions { margin-top: 22px; display: flex; flex-direction: column; gap: 10px; }
.q-page .q-btn { box-sizing: border-box; width: 100%; min-height: 46px; margin: 0; padding: 10px 18px; border-radius: 10px; font: inherit; font-size: 15px;
  font-weight: 600; line-height: 1.3; display: flex; align-items: center; justify-content: center; text-align: center; text-decoration: none; cursor: pointer; }
.q-page .q-primary { border: 1px solid var(--q-primary); background: var(--q-primary); color: var(--q-primary-ink); }
.q-page .q-primary:hover { background: var(--q-primary-hover); border-color: var(--q-primary-hover); }
.q-page .q-ghost { border: 1px solid var(--q-field-line); background: transparent; color: var(--q-text); font-weight: 500; }
.q-page .q-ghost:hover { background: var(--q-hover); }
.q-page .q-btn:disabled { opacity: 0.5; cursor: default; }
.q-page .q-btn.q-working { opacity: 0.7; cursor: progress; }

.q-more { margin-top: 18px; text-align: center; font-size: 13px; color: var(--q-muted); }
.q-more summary { cursor: pointer; }
.q-more p { margin: 6px 0 0; overflow-wrap: anywhere; }
.q-fine { margin: 18px 0 0; text-align: center; font-size: 13px; color: var(--q-muted); text-wrap: balance; }
.q-close { margin: 14px 0 0; text-align: center; font-size: 13.5px; color: var(--q-muted); text-wrap: balance; }
.q-sep { margin: 26px 0 0; padding-top: 16px; border-top: 1px solid var(--q-line); text-align: center; font-size: 12.5px; color: var(--q-muted); }
.q-cmd { margin: 8px 0 0; padding: 10px 12px; border: 1px solid var(--q-line); border-radius: 10px; background: var(--q-card); color: var(--q-text);
  font: 13px/1.5 "Cascadia Mono", Consolas, Menlo, monospace; white-space: pre-wrap; word-break: break-all; }

.q-ring { align-self: center; width: 76px; height: 76px; display: block; }
.q-ring-fill { fill: var(--q-ok-soft); }
.q-ring-line { fill: none; stroke: var(--q-ok); stroke-width: 3; }
.q-ring-mark { fill: none; stroke: var(--q-ok); stroke-width: 3.4; stroke-linecap: round; stroke-linejoin: round; }
.q-ring-track { fill: none; stroke: var(--q-track); stroke-width: 3; }
.q-ring-arc { fill: none; stroke: var(--q-accent); stroke-width: 3; stroke-linecap: round; }
.q-ring-glyph { fill: none; stroke: var(--q-text); stroke-width: 2.2; stroke-linecap: round; stroke-linejoin: round; }
.q-next { margin-top: 24px; padding: 16px 18px; border: 1px solid var(--q-line); border-radius: 14px; background: var(--q-card); display: flex; gap: 14px; align-items: flex-start; }
.q-next-icon { width: 34px; height: 34px; flex-shrink: 0; border-radius: 10px; display: grid; place-items: center; background: var(--q-subtle); }
.q-next-icon .q-icon { width: 18px; height: 18px; }
.q-next-title { margin: 0; font-size: 14.5px; font-weight: 600; overflow-wrap: anywhere; }
.q-next-text { margin: 2px 0 0; font-size: 13.5px; color: var(--q-muted); }

/* Nests shared with you, in the nest choice (a heading, then each with who shares it and what). */
.q-group { margin: 0; padding: 10px 14px 6px; border-top: 1px solid var(--q-line); background: var(--q-bg); color: var(--q-muted);
  font-size: 11.5px; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; }
.q-choice-text { display: flex; flex-direction: column; min-width: 0; }
.q-choice-sub { font-size: 13px; color: var(--q-muted); }

/* The invite page (/invite): who it is from, what is shared, and the steps after asking. */
.q-from { margin: 0; text-align: center; font-size: 14px; color: var(--q-muted); overflow-wrap: anywhere; }
.q-facts { margin: 22px 0 0; display: block; border: 1px solid var(--q-line); border-radius: 12px; background: var(--q-card); }
.q-facts > div { display: flex; gap: 12px; padding: 12px 16px; font-size: 14.5px; }
.q-facts > div + div { border-top: 1px solid var(--q-line); }
.q-facts dt { flex: 0 0 120px; color: var(--q-muted); }
.q-facts dd { margin: 0; min-width: 0; overflow-wrap: anywhere; }
.q-facts b { font-weight: 600; }
.q-fact-sub { display: block; margin-top: 2px; font-size: 13px; color: var(--q-muted); }
.q-page button.q-link { display: inline; margin: 0; padding: 0; border: 0; background: none; color: var(--q-accent); font: inherit; text-decoration: underline; cursor: pointer; }
.q-note.q-info { background: var(--q-subtle); color: var(--q-text); }
.q-steps { margin: 18px 0 0; padding: 16px 18px 16px 36px; border: 1px solid var(--q-line); border-radius: 14px; background: var(--q-card); font-size: 14px; }
.q-steps li + li { margin-top: 8px; }
.q-steps b { font-weight: 600; }

.q-field { margin-top: 26px; display: flex; flex-direction: column; gap: 6px; }
.q-page .q-field label { margin: 0; font-size: 13px; font-weight: 600; }
.q-row { display: flex; gap: 10px; }
.q-page .q-field input { flex: 1; min-width: 0; height: 46px; padding: 0 14px; border-radius: 10px; border: 1px solid var(--q-field-line); background: var(--q-card);
  color: var(--q-text); font: inherit; font-size: 16px; letter-spacing: 0.12em; text-transform: uppercase; }
.q-page .q-field input::placeholder { color: var(--q-muted); opacity: 0.7; }
.q-page .q-field .q-btn { width: auto; flex-shrink: 0; }

@media (prefers-reduced-motion: no-preference) {
  .q-screen { animation: q-in 0.3s cubic-bezier(0.33, 1, 0.68, 1) both; }
  @keyframes q-in { from { opacity: 0; transform: translateY(-8px); } to { opacity: 1; transform: none; } }
}
@media (max-width: 560px) {
  .q-page header.top { padding: 12px 16px; }
  .q-page main { padding-top: 32px; }
  .q-page h1 { font-size: 25px; }
  .q-code { font-size: 34px; }
}
`;

// The ring logo (assets/pairnets-logo.svg), for the top bar of pages in that look.
const LOGO_SVG = `<svg xmlns="http://www.w3.org/2000/svg" width="120" height="120" viewBox="0 0 120 120"><defs><linearGradient id="bg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#6aa5ff"/><stop offset="1" stop-color="#2a63d8"/></linearGradient></defs><circle cx="60" cy="60" r="60" fill="url(#bg)"/><path d="M75.2,92.6 A36,36 0 1 1 92.6,75.2" fill="none" stroke="#ffffff" stroke-opacity=".72" stroke-width="9" stroke-linecap="round"/><path d="M47,38 L47,84" fill="none" stroke="#ffffff" stroke-width="11" stroke-linecap="round"/><path d="M47,38 L61,38 A14,14 0 0 1 61,66 L47,66" fill="none" stroke="#ffffff" stroke-width="11" stroke-linecap="round" stroke-linejoin="round"/><circle cx="94" cy="92" r="7" fill="#ffffff"/></svg>
`;

// The "Sign in an app?" page (pages.ts appPage). Allow or "This isn't me" swaps in that answer's own screen. An error
// says what went wrong in plain words and leaves both buttons working, so the person can simply try again. Opened from
// a sign-in link (data-link), Allow brings the long code to paste into the app; the page then checks every few seconds
// and says when the computer is signed in.
const APP_JS = String.raw`(function () {
  "use strict";
  var box = document.getElementById("app-request");
  var allow = document.getElementById("approve");
  var deny = document.getElementById("deny");
  if (!box || !allow || !deny) return;
  var code = box.getAttribute("data-code");
  var link = box.getAttribute("data-link");
  var request = box.getAttribute("data-request");
  var here = link ? "/app/link?r=" + link : "/app?code=" + code;
  var error = document.getElementById("app-error");
  var SCREENS = ["ask", "longcode", "allowed", "allowed-nonest", "refused", "expired"];
  var SAY = {
    not_found: "Pairnets could not find this sign-in or that nest any more. Reload the page and try again.",
    bad_request: "That did not work. Reload the page and try again.",
    rate_limited: "That was a lot of tries in a short time. Wait a minute, then try again.",
    server_error: "Something went wrong on our side. Try again in a moment."
  };
  var busy = false;

  function show(name) {
    for (var i = 0; i < SCREENS.length; i++) {
      var s = document.getElementById("screen-" + SCREENS[i]);
      if (s) s.hidden = SCREENS[i] !== name;
    }
    var title = document.getElementById("screen-" + name + "-title");
    if (title) title.focus();
  }
  function setBusy(on, button) {
    busy = on;
    allow.disabled = on;
    deny.disabled = on;
    if (on) button.classList.add("q-working");
    else { allow.classList.remove("q-working"); deny.classList.remove("q-working"); }
  }
  function fail(text) {
    error.textContent = text;
    error.hidden = false;
  }
  // pairnets://signed-in brings Pairnets to the front. It carries nothing (the key only travels through the app's own
  // poll), but on a computer without Pairnets, or on a phone, a browser may show an error for it. So it is only tried
  // by itself when this browser looks like it runs on the computer that asked: not a phone or tablet, and the same
  // system. Otherwise the "Open Pairnets" button is there to press.
  function sameComputer() {
    var ua = navigator.userAgent || "";
    if (/Android|iPhone|iPad|iPod|Mobile/i.test(ua)) return false;
    if (/Macintosh/.test(ua) && navigator.maxTouchPoints > 1) return false;
    var system = box.getAttribute("data-system");
    if (system === "Windows") return /Windows/.test(ua);
    if (system === "macOS") return /Macintosh|Mac OS X/.test(ua);
    if (system === "Linux") return /Linux|X11/.test(ua) && !/CrOS/.test(ua);
    return false;
  }
  // The long code, in its 8 groups, and a Copy button for all of it.
  function showLongCode(text) {
    var groups = text.split("-");
    for (var i = 0; i < groups.length; i++) {
      var g = document.getElementById("long-code-" + (i + 1));
      if (g) g.textContent = groups[i];
    }
    var copy = document.getElementById("copy-code");
    if (copy) copy.addEventListener("click", function () {
      var clip = navigator.clipboard;
      if (!clip || !clip.writeText) return;
      clip.writeText(text).then(function () { copy.textContent = "Copied"; }, function () {});
    });
    show("longcode");
    setTimeout(watch, 4000);
  }
  // Until the app has used the code: signed in, stopped (No, or too many wrong codes) or run out.
  function watch() {
    pnApi("GET", "/v1/app/requests/" + request).then(function (res) {
      var st = res.body && res.body.status;
      if (res.ok && st === "delivered") { show("allowed"); return; }
      if (res.ok && st === "denied") { show("refused"); return; }
      if (res.status === 404) { show("expired"); return; }
      setTimeout(watch, 4000);
    });
  }
  function chosenNest() {
    var radios = document.getElementsByName("nest");
    for (var i = 0; i < radios.length; i++) if (radios[i].checked) return radios[i];
    return null;
  }
  function answer(approve, button) {
    if (busy) return;
    error.hidden = true;
    var body = link ? { link: link, approve: approve } : { userCode: code, approve: approve };
    var label = null;
    if (approve) {
      if (document.getElementsByName("nest").length) {
        var chosen = chosenNest();
        if (!chosen) { fail("Choose a nest first."); return; }
        body.nestId = chosen.value;
        label = chosen.getAttribute("data-label");
      } else if (box.getAttribute("data-nest")) {
        body.nestId = box.getAttribute("data-nest");
      } // else the account has no nest yet: the app waits for one
    }
    setBusy(true, button);
    pnApi("POST", "/v1/app/approve", body).then(function (res) {
      var err = res.body && res.body.error;
      if (err === "unauthorized" || err === "reauth_required") {
        location.href = "/login?" + (err === "reauth_required" ? "reauth=1&" : "") + "next=" + encodeURIComponent(here);
        return;
      }
      if (res.ok) {
        if (!approve) { show("refused"); return; }
        var field = document.getElementById("allowed-nest");
        if (field && label) field.textContent = label;
        if (link) { if (res.body && res.body.code) showLongCode(res.body.code); return; }
        show(body.nestId ? "allowed" : "allowed-nonest");
        if (body.nestId && sameComputer()) setTimeout(function () { location.href = "pairnets://signed-in"; }, 600);
        return;
      }
      if (err === "expired") { show("expired"); return; }
      // Answered meanwhile (in another tab, or by an earlier try whose reply got lost): the page then says which answer.
      if (err === "already_decided") { location.reload(); return; }
      setBusy(false, button);
      var said = typeof err === "string" && Object.prototype.hasOwnProperty.call(SAY, err) ? SAY[err] : null;
      fail(said || (res.body && res.body.message) || "That did not work. Try again.");
    });
  }
  allow.addEventListener("click", function () { answer(true, allow); });
  deny.addEventListener("click", function () { answer(false, deny); });
})();
`;

// The invite page (pages.ts invitePage). "Ask to join" sends the request and swaps to the waiting screen; an invite
// that changed meanwhile (run out, cancelled, used by someone else) reloads, and the page then says where it stands.
const INVITE_JS = String.raw`(function () {
  "use strict";
  var box = document.getElementById("invite-request");
  var ask = document.getElementById("ask");
  if (!box || !ask) return;
  var token = box.getAttribute("data-token");
  var no = document.getElementById("no-thanks");
  var notYou = document.getElementById("not-you");
  var error = document.getElementById("invite-error");
  var busy = false;
  var RELOAD = ["expired", "cancelled", "used", "own_nest", "already_member", "already_decided", "not_found"];
  function show(name) {
    ["ask", "no", "waiting"].forEach(function (s) {
      var el = document.getElementById("screen-" + s);
      if (el) el.hidden = s !== name;
    });
    var title = document.getElementById("screen-" + name + "-title");
    if (title) title.focus();
  }
  function here() {
    return "/invite?t=" + token;
  }
  ask.addEventListener("click", function () {
    if (busy) return;
    busy = true;
    ask.disabled = true;
    no.disabled = true;
    error.hidden = true;
    var body = { t: token };
    var nests = document.getElementById("joining-nests");
    var picked = nests ? nests.querySelector("input[name=joining]:checked") : null;
    if (picked) body.nestId = picked.value;
    pnApi("POST", "/v1/invite/request", body).then(function (res) {
      var err = res.body && res.body.error;
      if (err === "unauthorized") { location.href = "/login?next=" + encodeURIComponent(here()); return; }
      // Let in already (asked from another tab, and the owner was quick): the page says so.
      if (res.ok && res.body.status === "in") { location.reload(); return; }
      if (res.ok) { show("waiting"); return; }
      if (RELOAD.indexOf(err) >= 0) { location.reload(); return; }
      busy = false;
      ask.disabled = false;
      no.disabled = false;
      error.textContent = err === "rate_limited" ? "That was a lot of tries in a short time. Wait a minute, then try again." :
        (res.body && res.body.message) || "That did not work. Try again.";
      error.hidden = false;
    });
  });
  no.addEventListener("click", function () { if (!busy) show("no"); });
  if (notYou) notYou.addEventListener("click", function () {
    pnApi("POST", "/v1/logout").then(function () { location.href = "/login?next=" + encodeURIComponent(here()); });
  });
})();
`;

const ADD_JS = String.raw`(function () {
  "use strict";
  var box = document.getElementById("server-request");
  if (!box) return;
  var code = box.getAttribute("data-code");
  var status = document.getElementById("status");
  function answer(approve) {
    var buttons = box.querySelectorAll("button");
    for (var i = 0; i < buttons.length; i++) buttons[i].disabled = true;
    var label = document.getElementById("label");
    var body = { userCode: code, approve: approve };
    if (approve && label && label.value.trim()) body.label = label.value.trim();
    if (approve) pnShow(status, "Adding the nest...", false);
    pnApi("POST", "/v1/servers/approve", body).then(function (res) {
      if (res.body && (res.body.error === "unauthorized" || res.body.error === "reauth_required")) {
        location.href = "/login?" + (res.body.error === "reauth_required" ? "reauth=1&" : "") + "next=" + encodeURIComponent("/add?code=" + code);
        return;
      }
      if (!res.ok) {
        pnShow(status, res.body.message || "That did not work.", true);
        if (res.status >= 500) for (var j = 0; j < buttons.length; j++) buttons[j].disabled = false;
        return;
      }
      pnShow(status, approve ? "Added. The installer on the nest finishes by itself in a minute or two." : "Refused. The nest was not added.", false);
    });
  }
  var a = document.getElementById("approve");
  var d = document.getElementById("deny");
  if (a) a.addEventListener("click", function () { answer(true); });
  if (d) d.addEventListener("click", function () { answer(false); });
})();
`;

// On every page (layout() in pages.ts): says Pairnets only uses the cookies needed to sign in. A notice, not a consent
// wall. Closing it is remembered in local storage ("pn-cookie-notice" = "ok"), which is never sent anywhere.
const COOKIE_NOTICE_JS = String.raw`(function () {
  "use strict";
  var KEY = "pn-cookie-notice";
  if (/^\/cookies\/?$/.test(location.pathname)) return;
  try {
    if (window.localStorage.getItem(KEY) === "ok") return;
  } catch (e) { /* storage blocked: show the notice, closing it lasts for this page view */ }
  var box = document.createElement("div");
  box.className = "cookie-notice";
  box.setAttribute("role", "region");
  box.setAttribute("aria-label", "Cookie notice");
  var text = document.createElement("p");
  text.textContent = "Pairnets only uses cookies that are needed to sign you in. No tracking, no ads.";
  var actions = document.createElement("div");
  actions.className = "cookie-notice-actions";
  var ok = document.createElement("button");
  ok.type = "button";
  ok.textContent = "OK";
  var more = document.createElement("a");
  more.href = "https://pairnets.app/cookies/";
  more.textContent = "Cookie settings";
  actions.appendChild(ok);
  actions.appendChild(more);
  box.appendChild(text);
  box.appendChild(actions);
  function close() {
    try { window.localStorage.setItem(KEY, "ok"); } catch (e) { /* storage blocked: hidden for this page view only */ }
    document.removeEventListener("keydown", onKey);
    if (box.parentNode) box.parentNode.removeChild(box);
  }
  function onKey(ev) {
    if (ev.key === "Escape" && !ev.defaultPrevented) close();
  }
  ok.addEventListener("click", close);
  document.addEventListener("keydown", onKey);
  document.body.insertBefore(box, document.body.firstChild); // first in the tab order, so keyboard users reach it at once
})();
`;

export const ASSETS: Record<string, { type: string; body: string | Uint8Array }> = {
  "style.css": { type: "text/css; charset=utf-8", body: STYLE },
  "login.js": { type: "text/javascript; charset=utf-8", body: SHARED_JS + LOGIN_JS },
  "email.js": { type: "text/javascript; charset=utf-8", body: SHARED_JS + EMAIL_JS },
  "account.js": { type: "text/javascript; charset=utf-8", body: SHARED_JS + ACCOUNT_JS },
  "app.js": { type: "text/javascript; charset=utf-8", body: SHARED_JS + APP_JS },
  "add.js": { type: "text/javascript; charset=utf-8", body: SHARED_JS + ADD_JS },
  "invite.js": { type: "text/javascript; charset=utf-8", body: SHARED_JS + INVITE_JS },
  "app.css": { type: "text/css; charset=utf-8", body: APP_STYLE },
  "logo.svg": { type: "image/svg+xml", body: LOGO_SVG },
  "icon.png": { type: "image/png", body: ICON_PNG },
  ...FONT_ASSETS,
  "cookie-notice.js": { type: "text/javascript; charset=utf-8", body: COOKIE_NOTICE_JS },
};

const versions = new Map<string, string>();

/** A short fingerprint of a file, its text or its bytes (fonts) (32-bit FNV-1a, base 36). */
export function fingerprint(body: string | Uint8Array): string {
  let h = 0x811c9dc5;
  for (let i = 0; i < body.length; i++) {
    h ^= typeof body === "string" ? body.charCodeAt(i) : (body[i] ?? 0);
    h = Math.imul(h, 0x01000193);
  }
  return (h >>> 0).toString(36);
}

/**
 * The address pages link an asset by: `/assets/<name>?v=<fingerprint>`. Assets are cached for an hour, so a changed
 * file needs a new address, or browsers keep showing the old one after a deploy. Serving ignores the query.
 */
export function assetUrl(name: string): string {
  let v = versions.get(name);
  if (v === undefined) {
    const asset = ASSETS[name];
    if (!asset) throw new Error(`There is no asset ${name}.`);
    v = fingerprint(asset.body);
    versions.set(name, v);
  }
  return `/assets/${name}?v=${v}`;
}
