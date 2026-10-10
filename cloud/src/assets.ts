// Static files under /assets/ (plain CSS and JavaScript; pages have no inline scripts or styles).
// Kept as strings so the Worker has no build step and no asset binding.

import { FONT_ASSETS } from "./fonts";

const STYLE = String.raw`
:root { color-scheme: light dark; --bg: #f6f7f9; --card: #ffffff; --text: #16181d; --muted: #5b6270; --line: #d9dde3;
  --accent: #1f6feb; --accent-text: #ffffff; --danger: #c62828; --ok: #2e7d32; --warn: #b26a00; }
@media (prefers-color-scheme: dark) {
  :root { --bg: #111317; --card: #1a1d23; --text: #e8eaee; --muted: #a0a7b4; --line: #2c313a; --accent: #4c8dff;
    --accent-text: #0b0d10; --danger: #ef6b6b; --ok: #66bb6a; --warn: #f0a640; }
}
* { box-sizing: border-box; }
body { margin: 0; font: 16px/1.5 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; background: var(--bg); color: var(--text); }
header.top { display: flex; align-items: center; gap: 6px; padding: 14px 20px; border-bottom: 1px solid var(--line); }
.brand { font-weight: 700; color: var(--text); text-decoration: none; }
main { max-width: 640px; margin: 0 auto; padding: 16px; }
footer { max-width: 640px; margin: 0 auto; padding: 16px; font-size: 14px; }
.card { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 18px 20px; margin: 16px 0; }
h1 { font-size: 1.4rem; margin: 0 0 12px; }
h2 { font-size: 1.1rem; margin: 0 0 12px; }
a { color: var(--accent); }
.muted { color: var(--muted); font-size: 0.9rem; }
.button, button { display: inline-block; font: inherit; padding: 9px 16px; border-radius: 8px; border: 1px solid var(--accent);
  background: var(--accent); color: var(--accent-text); text-decoration: none; cursor: pointer; text-align: center; }
button:disabled { opacity: 0.5; cursor: default; }
button.link { background: none; border: none; color: var(--accent); padding: 0; text-decoration: underline; }
button.danger { background: transparent; color: var(--danger); border-color: var(--danger); }
.button.home { margin-left: auto; padding: 5px 12px; font-size: 0.9rem; background: transparent; color: var(--accent); }
.wide { width: 100%; }
label { display: block; margin: 8px 0 4px; }
input { width: 100%; font: inherit; padding: 9px 10px; border-radius: 8px; border: 1px solid var(--line); background: var(--bg); color: var(--text); }
form button { margin-top: 12px; }
.cf-turnstile { margin-top: 12px; }
.or { text-align: center; color: var(--muted); margin: 16px 0 4px; }
.error { color: var(--danger); }
.notice { color: var(--warn); }
.status { margin-top: 12px; }
.legal { color: var(--muted); font-size: 0.85rem; text-align: center; margin: 16px 0 0; }
footer nav { color: var(--muted); }
.list { list-style: none; padding: 0; margin: 0; }
.list > li { border-top: 1px solid var(--line); padding: 12px 0; }
.list > li:first-child { border-top: none; }
.actions { margin-top: 8px; display: flex; gap: 8px; flex-wrap: wrap; }
.pill { font-size: 0.8rem; padding: 1px 8px; border-radius: 999px; border: 1px solid var(--line); color: var(--muted); }
.pill.online { color: var(--ok); border-color: var(--ok); }
.pill.offline { color: var(--danger); border-color: var(--danger); }
.pill.pending { color: var(--warn); border-color: var(--warn); }
pre { white-space: pre-wrap; word-break: break-all; background: var(--bg); border: 1px solid var(--line); border-radius: 8px; padding: 10px; }
dl { display: grid; grid-template-columns: max-content 1fr; gap: 4px 16px; }
dt { color: var(--muted); }
dd { margin: 0; }
fieldset { border: 1px solid var(--line); border-radius: 8px; margin: 12px 0; padding: 8px 12px; }
legend { color: var(--muted); padding: 0 4px; }
label.choice { display: flex; gap: 8px; align-items: center; margin: 6px 0; }
label.choice input { width: auto; }
details.card > summary { font-size: 1.1rem; font-weight: 600; cursor: pointer; }
details.card[open] > summary { margin-bottom: 12px; }
.devices > li { padding: 8px 0; }
.cookie-notice { position: fixed; left: 24px; bottom: calc(24px + env(safe-area-inset-bottom, 0px)); z-index: 20;
  width: 400px; max-width: calc(100% - 48px); background: var(--card); color: var(--text); border: 1px solid var(--line);
  border-radius: 10px; padding: 16px 18px; font-size: 15px; line-height: 1.5;
  box-shadow: 0 1px 2px rgba(16, 24, 40, .05), 0 12px 32px rgba(16, 24, 40, .14); animation: cookie-notice-in .24s ease-out both; }
.cookie-notice p { margin: 0 0 12px; }
.cookie-notice-actions { display: flex; align-items: center; gap: 6px 14px; flex-wrap: wrap; }
.cookie-notice-actions button { padding: 7px 22px; font-weight: 600; }
.cookie-notice-actions a { padding: 8px 4px; font-weight: 600; border-radius: 6px; text-decoration: none; }
.cookie-notice-actions a:hover { text-decoration: underline; }
.cookie-notice button:focus-visible, .cookie-notice a:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
@keyframes cookie-notice-in { from { opacity: 0; transform: translateY(12px); } to { opacity: 1; transform: none; } }
@media (prefers-reduced-motion: reduce) { .cookie-notice { animation: none; } }
@media (prefers-color-scheme: dark) { .cookie-notice { box-shadow: 0 1px 2px rgba(0, 0, 0, .3), 0 12px 32px rgba(0, 0, 0, .4); } }
@media (max-width: 600px) {
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
    var list = li.querySelector("[data-field=devices]");
    while (list.firstChild) list.removeChild(list.firstChild);
    if (!s.online) { list.appendChild(el("li", "muted", "Shown when the nest is connected.")); return; }
    if (!s.devices.length) { list.appendChild(el("li", "muted", "None yet.")); return; }
    s.devices.forEach(function (d) {
      var item = el("li");
      item.setAttribute("data-device", d.id);
      var line = el("div");
      line.appendChild(el("strong", "", d.name || "Computer"));
      if (d.system) line.appendChild(el("span", "muted", " " + d.system));
      item.appendChild(line);
      item.appendChild(el("div", "muted", d.lastSeen ? "Last synced " + new Date(d.lastSeen).toLocaleString() : "Not synced yet"));
      var actions = el("div", "actions");
      var b = el("button", "danger", "Remove");
      b.type = "button";
      b.setAttribute("data-action", "remove-device");
      actions.appendChild(b);
      item.appendChild(actions);
      list.appendChild(item);
    });
  }
  function loadServers() {
    if (!servers || !servers.querySelector("[data-server]")) return;
    pnApi("GET", "/v1/servers").then(function (res) {
      if (!ok(res) || !Array.isArray(res.body)) return;
      res.body.forEach(function (s) {
        var li = servers.querySelector('[data-server="' + s.id + '"]');
        if (li) fillServer(li, s);
      });
    });
  }
  if (servers) servers.addEventListener("click", function (ev) {
    var b = ev.target.closest("button[data-action]");
    if (!b) return;
    var li = b.closest("[data-server]");
    var id = li && li.getAttribute("data-server");
    if (!id) return;
    if (b.getAttribute("data-action") === "remove-device") {
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
  loadServers();
  var nests = document.getElementById("nests");
  if (nests) nests.addEventListener("click", function (ev) {
    var b = ev.target.closest("button[data-action]");
    if (!b) return;
    var li = b.closest("[data-nest]");
    var id = li && li.getAttribute("data-nest");
    if (!id) return;
    if (b.getAttribute("data-action") === "toggle") {
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
.q-page .q-choice input { flex-shrink: 0; width: 16px; height: 16px; margin: 0; padding: 0; accent-color: var(--q-primary); }
.q-note { margin: 14px 0 0; padding: 12px 14px; border-radius: 12px; background: var(--q-warn-soft); color: var(--q-warn); font-size: 13.5px; }
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
// says what went wrong in plain words and leaves both buttons working, so the person can simply try again.
const APP_JS = String.raw`(function () {
  "use strict";
  var box = document.getElementById("app-request");
  var allow = document.getElementById("approve");
  var deny = document.getElementById("deny");
  if (!box || !allow || !deny) return;
  var code = box.getAttribute("data-code");
  var error = document.getElementById("app-error");
  var SCREENS = ["ask", "allowed", "allowed-nonest", "refused", "expired"];
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
  function chosenNest() {
    var radios = document.getElementsByName("nest");
    for (var i = 0; i < radios.length; i++) if (radios[i].checked) return radios[i];
    return null;
  }
  function answer(approve, button) {
    if (busy) return;
    error.hidden = true;
    var body = { userCode: code, approve: approve };
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
        location.href = "/login?next=" + encodeURIComponent("/app?code=" + code);
        return;
      }
      if (res.ok) {
        if (!approve) { show("refused"); return; }
        var field = document.getElementById("allowed-nest");
        if (field && label) field.textContent = label;
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
      if (res.body && res.body.error === "unauthorized") { location.href = "/login?next=" + encodeURIComponent("/add?code=" + code); return; }
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
  "app.css": { type: "text/css; charset=utf-8", body: APP_STYLE },
  "logo.svg": { type: "image/svg+xml", body: LOGO_SVG },
  ...FONT_ASSETS,
  "cookie-notice.js": { type: "text/javascript; charset=utf-8", body: COOKIE_NOTICE_JS },
};
