// Static files under /assets/ (plain CSS and JavaScript; pages have no inline scripts or styles).
// Kept as strings so the Worker has no build step and no asset binding.

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
.input-row { display: flex; gap: 8px; }
.input-row input { flex: 1; min-width: 0; }
.input-row button { margin-top: 0; }
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

const APP_JS = String.raw`(function () {
  "use strict";
  var box = document.getElementById("app-request");
  if (!box) return;
  var code = box.getAttribute("data-code");
  var status = document.getElementById("status");
  function answer(approve) {
    var body = { userCode: code, approve: approve };
    if (approve) {
      // No choice at all: the account has no nest yet, and the app waits for one.
      if (box.querySelector("input[name=nest]")) {
        var chosen = box.querySelector("input[name=nest]:checked");
        if (!chosen) { pnShow(status, "Choose a nest first.", true); return; }
        body.nestId = chosen.value;
      }
    }
    var buttons = box.querySelectorAll("button");
    for (var i = 0; i < buttons.length; i++) buttons[i].disabled = true;
    pnApi("POST", "/v1/app/approve", body).then(function (res) {
      if (res.body && res.body.error === "unauthorized") { location.href = "/login?next=" + encodeURIComponent("/app?code=" + code); return; }
      if (!res.ok) { pnShow(status, res.body.message || "That did not work.", true); return; }
      var waits = approve && !box.querySelector("input[name=nest]");
      pnShow(status, waits ? "Allowed. The app waits for your nest; set it up now." : approve ? "Allowed. Go back to the app." : "Refused. The app will not be signed in.", false);
    });
  }
  var a = document.getElementById("approve");
  var d = document.getElementById("deny");
  if (a) a.addEventListener("click", function () { answer(true); });
  if (d) d.addEventListener("click", function () { answer(false); });
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

export const ASSETS: Record<string, { type: string; body: string }> = {
  "style.css": { type: "text/css; charset=utf-8", body: STYLE },
  "login.js": { type: "text/javascript; charset=utf-8", body: SHARED_JS + LOGIN_JS },
  "email.js": { type: "text/javascript; charset=utf-8", body: SHARED_JS + EMAIL_JS },
  "account.js": { type: "text/javascript; charset=utf-8", body: SHARED_JS + ACCOUNT_JS },
  "app.js": { type: "text/javascript; charset=utf-8", body: SHARED_JS + APP_JS },
  "add.js": { type: "text/javascript; charset=utf-8", body: SHARED_JS + ADD_JS },
  "cookie-notice.js": { type: "text/javascript; charset=utf-8", body: COOKIE_NOTICE_JS },
};
