// Pairnets nest: the website on your own server (sign in, approve computers, devices, security).
// Plain JavaScript, no libraries. Everything shown comes from /web/api and is inserted as text,
// never as HTML, so a computer's name cannot run code here.
"use strict";

(() => {
  // ------------------------------------------------------------------ helpers

  const $ = (id) => document.getElementById(id);

  async function api(method, path, body) {
    const init = { method, credentials: "same-origin", headers: {} };
    if (body !== undefined) {
      init.headers["Content-Type"] = "application/json";
      init.body = JSON.stringify(body);
    }
    const response = await fetch("/web/api" + path, init);
    if (response.status === 204) return null;
    let data = null;
    try {
      data = await response.json();
    } catch {
      data = null;
    }
    if (!response.ok) {
      const error = new Error((data && data.message) || `Something went wrong (${response.status}).`);
      error.status = response.status;
      error.code = data && data.code;
      throw error;
    }
    return data;
  }

  /** Creates an element: h("div", { class: "x" }, "text", child, ...). Strings become text nodes. */
  function h(tag, attrs, ...children) {
    const el = document.createElement(tag);
    for (const [key, value] of Object.entries(attrs || {})) {
      if (value === false || value === null || value === undefined) continue;
      if (key.startsWith("on")) el.addEventListener(key.slice(2), value);
      else if (value === true) el.setAttribute(key, "");
      else el.setAttribute(key, value);
    }
    for (const child of children.flat()) {
      if (child === null || child === undefined || child === false) continue;
      el.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }
    return el;
  }

  const ICONS = {
    computer: "M3 5.5A1.5 1.5 0 0 1 4.5 4h15A1.5 1.5 0 0 1 21 5.5v10a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 15.5zM9 20h6M12 17v3",
    laptop: "M5 6.5A1.5 1.5 0 0 1 6.5 5h11A1.5 1.5 0 0 1 19 6.5V15H5zM3 15h18l-1 3H4z",
    key: "M14.5 3.5a6 6 0 1 1-5.6 8.1L3.5 17v3.5H7v-2h2v-2h2l1.4-1.4A6 6 0 0 1 14.5 3.5zM16.5 7.5h.01",
    browser: "M3 6.5A1.5 1.5 0 0 1 4.5 5h15A1.5 1.5 0 0 1 21 6.5v11a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 17.5zM3 9h18",
    more: "M6 12h.01M12 12h.01M18 12h.01",
    wait: "M12 7v5l3 2M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18z",
    mail: "M3 6.5A1.5 1.5 0 0 1 4.5 5h15A1.5 1.5 0 0 1 21 6.5v11a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 17.5zM3.5 7l8.5 6 8.5-6",
    globe: "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM3 12h18M12 3c2.5 2.5 3.8 5.5 3.8 9s-1.3 6.5-3.8 9c-2.5-2.5-3.8-5.5-3.8-9S9.5 5.5 12 3z",
  };

  function icon(name) {
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("fill", "none");
    svg.setAttribute("stroke", "currentColor");
    svg.setAttribute("stroke-width", name === "more" ? "3" : "1.9");
    svg.setAttribute("stroke-linecap", "round");
    svg.setAttribute("stroke-linejoin", "round");
    svg.setAttribute("aria-hidden", "true");
    const path = document.createElementNS("http://www.w3.org/2000/svg", "path");
    path.setAttribute("d", ICONS[name] || ICONS.computer);
    svg.append(path);
    return svg;
  }

  function ring(name, online) {
    const el = h("div", { class: "ring" }, icon(name));
    if (online !== undefined) el.append(h("span", { class: online ? "dot on" : "dot", title: online ? "Online" : "Offline" }));
    return el;
  }

  function showNotice(text, id = "notice") {
    const box = $(id);
    if (!box) return;
    box.textContent = text || "";
    box.hidden = !text;
  }

  /** Messages for the short codes the nest puts in /signin?error=… and /security?error=… after a Google round trip. */
  const GOOGLE_PROBLEMS = {
    "google-none": "Google sign-in is not set up for this nest. Connect Google under Security first.",
    "google-mismatch": "That Google account is not the one connected to this nest.",
    "google-denied": "Google did not let you in (or you cancelled).",
    "google-expired": "That Google sign-in took too long. Try again.",
    "google-failed": "Google sign-in did not work. Check the Google settings on the server.",
  };

  function showError(error, id = "error") {
    const box = $(id);
    if (!box) return;
    box.textContent = error ? (error.message || String(error)) : "";
    box.hidden = !error;
  }

  // "just now", "5 min ago", "3 h ago", "yesterday", "7 Oct"
  function ago(iso) {
    if (!iso) return "";
    const then = new Date(iso);
    const seconds = Math.max(0, (Date.now() - then.getTime()) / 1000);
    if (seconds < 60) return "just now";
    if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`;
    if (seconds < 86400) return `${Math.floor(seconds / 3600)} h ago`;
    if (seconds < 172800) return "yesterday";
    return day(iso);
  }

  function day(iso) {
    const d = new Date(iso);
    const sameYear = d.getFullYear() === new Date().getFullYear();
    return d.toLocaleDateString(undefined, sameYear ? { day: "numeric", month: "short" } : { day: "numeric", month: "short", year: "numeric" });
  }

  function isToday(iso) {
    return iso && new Date(iso).toDateString() === new Date().toDateString();
  }

  // The device icon by system: a laptop for Macs and the like is guesswork, so computers all look alike.
  const systemIcon = () => "computer";

  /** A path on this site to go back to after signing in (never another site). */
  function safeNext(value) {
    if (typeof value !== "string" || !value.startsWith("/")) return null;
    try {
      // The browser's own reading of the address (it drops tabs and newlines, turns "\\" into "/"): it must stay on this site.
      const url = new URL(value, location.origin);
      const path = url.pathname + url.search;
      return url.origin === location.origin && !path.startsWith("//") ? path : null; // "/..//host" resolves to "//host"
    } catch {
      return null;
    }
  }

  function goSignIn() {
    location.replace("/signin?next=" + encodeURIComponent(location.pathname + location.search));
  }

  // ------------------------------------------------------------------ passkeys

  const passkeysSupported = () => Boolean(window.PublicKeyCredential && navigator.credentials);

  function toBuffer(b64u) {
    const s = b64u.replace(/-/g, "+").replace(/_/g, "/");
    const bin = atob(s + "=".repeat((4 - (s.length % 4)) % 4));
    return Uint8Array.from(bin, (c) => c.charCodeAt(0)).buffer;
  }

  function toB64u(buffer) {
    let bin = "";
    for (const b of new Uint8Array(buffer)) bin += String.fromCharCode(b);
    return btoa(bin).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  }

  /** What the browser reports in plain words ("Chrome on Windows"), used to name a new passkey. */
  function browserName(ua) {
    if (!ua) return "A browser";
    const browser = /Edg\//.test(ua) ? "Edge" : /Firefox\//.test(ua) ? "Firefox" : /Chrome\//.test(ua) ? "Chrome" : /Safari\//.test(ua) ? "Safari" : "A browser";
    const system = /iPhone|iPad/.test(ua) ? "iPhone or iPad" : /Android/.test(ua) ? "Android" : /Mac OS X/.test(ua) ? "Mac" : /Windows/.test(ua) ? "Windows" : /Linux/.test(ua) ? "Linux" : null;
    return system ? `${browser} on ${system}` : browser;
  }

  function passkeyProblem(error) {
    const name = error && error.name;
    const text = name === "NotAllowedError" ? "The passkey was cancelled or timed out. Try again."
      : name === "InvalidStateError" ? "This passkey is already added."
      : name === "SecurityError" ? "Your browser would not use a passkey on this address. Open the nest by its own name, over https."
      : name === "NotSupportedError" ? "This browser or device cannot make a passkey of a kind this nest accepts."
      : (error && error.message) || "The passkey did not work.";
    const problem = new Error(text);
    problem.status = error && error.status;
    return problem;
  }

  /** Makes a passkey on this device and gives it to the nest (signed in). */
  async function addPasskey() {
    const options = await api("POST", "/passkeys/register/options");
    const publicKey = options.publicKey;
    publicKey.challenge = toBuffer(publicKey.challenge);
    publicKey.user.id = toBuffer(publicKey.user.id);
    publicKey.excludeCredentials = publicKey.excludeCredentials.map((c) => ({ ...c, id: toBuffer(c.id) }));
    let credential;
    try {
      credential = await navigator.credentials.create({ publicKey });
    } catch (error) {
      throw passkeyProblem(error);
    }
    await api("POST", "/passkeys/register", {
      challengeId: options.challengeId,
      name: browserName(navigator.userAgent),
      id: toB64u(credential.rawId),
      clientDataJson: toB64u(credential.response.clientDataJSON),
      attestationObject: toB64u(credential.response.attestationObject),
    });
  }

  /** Signs in with a passkey (the browser asks for it); the nest then sets the session cookie. */
  async function signInWithPasskey() {
    const options = await api("POST", "/signin/passkey/options");
    const publicKey = options.publicKey;
    publicKey.challenge = toBuffer(publicKey.challenge);
    publicKey.allowCredentials = publicKey.allowCredentials.map((c) => ({ ...c, id: toBuffer(c.id) }));
    let credential;
    try {
      credential = await navigator.credentials.get({ publicKey });
    } catch (error) {
      throw passkeyProblem(error);
    }
    await api("POST", "/signin/passkey", {
      challengeId: options.challengeId,
      id: toB64u(credential.rawId),
      clientDataJson: toB64u(credential.response.clientDataJSON),
      authenticatorData: toB64u(credential.response.authenticatorData),
      signature: toB64u(credential.response.signature),
    });
  }

  function setBusy(button, busy) {
    if (!button) return;
    button.disabled = busy;
  }

  function wireDialogs() {
    for (const button of document.querySelectorAll("dialog [data-close]")) {
      button.addEventListener("click", () => button.closest("dialog").close());
    }
  }

  function wireBar(page) {
    const current = document.querySelector(`[data-nav="${page}"]`);
    if (current) current.setAttribute("aria-current", "page");
    const signOut = $("signout");
    if (signOut) {
      signOut.addEventListener("click", async () => {
        try {
          await api("POST", "/signout");
        } finally {
          location.replace("/signin");
        }
      });
    }
  }

  /** Signed-in pages: send the browser to sign in when it is not. */
  async function requireSignIn() {
    const state = await api("GET", "/state");
    if (!state.signedIn) {
      if (!state.hasSignIn) location.replace("/setup");
      else goSignIn();
      return null;
    }
    return state;
  }

  // ------------------------------------------------------------------ sign in

  async function signinPage() {
    const state = await api("GET", "/state");
    const next = safeNext(new URLSearchParams(location.search).get("next")) || "/devices";
    if (state.signedIn) return location.replace(next);
    if (!state.hasSignIn) return location.replace("/setup");
    $("nest-name").textContent = state.nestName || location.hostname;
    const passkeys = state.methods.passkeys > 0 && passkeysSupported();
    $("passkey-signin").hidden = !passkeys;
    $("google-signin").hidden = !state.methods.google;
    $("email-signin").hidden = !state.methods.email;
    const top = passkeys || state.methods.google || state.methods.email;
    $("top-methods").hidden = !top;
    $("passkey-divider").hidden = !state.methods.password;
    if (!state.methods.password) $("password-form").hidden = true;
    if (!state.methods.password && !top) $("no-password").hidden = false;
    if (state.methods.passkeys > 0 && !passkeysSupported()) {
      showError(new Error("This browser cannot use passkeys here. Open the nest over https, in a browser that supports them."));
    }
    const problem = GOOGLE_PROBLEMS[new URLSearchParams(location.search).get("error")];
    if (problem) showError(new Error(problem));
    $("email-signin").addEventListener("click", async () => {
      const button = $("email-signin");
      setBusy(button, true);
      showError(null);
      try {
        await api("POST", "/signin/email/request");
        showNotice("Check your inbox. The link works once, for 15 minutes.");
      } catch (error) {
        showError(error);
      } finally {
        setBusy(button, false);
      }
    });
    $("passkey-signin").addEventListener("click", async () => {
      const button = $("passkey-signin");
      setBusy(button, true);
      showError(null);
      try {
        await signInWithPasskey();
        location.replace(next);
      } catch (error) {
        showError(error);
      } finally {
        setBusy(button, false);
      }
    });
    $("password-form").addEventListener("submit", async (event) => {
      event.preventDefault();
      const button = $("password-submit");
      setBusy(button, true);
      showError(null);
      try {
        await api("POST", "/signin/password", { password: $("password").value });
        location.replace(next);
      } catch (error) {
        showError(error);
        $("password").select();
      } finally {
        setBusy(button, false);
      }
    });
  }

  // ------------------------------------------------------------------ setup

  async function setupPage() {
    const code = new URLSearchParams(location.hash.slice(1)).get("code");
    // The code is in the fragment so it never reaches a server log; take it out of the address bar too.
    if (location.hash) history.replaceState(null, "", location.pathname);
    let state = await api("GET", "/state");

    if (code) {
      $("claim-checking").hidden = false;
      try {
        await api("POST", "/setup", { code });
        state = await api("GET", "/state");
      } catch (error) {
        $("claim-checking").hidden = true;
        $("claim-missing").hidden = false;
        showError(error);
        return;
      }
    } else if (!state.signedIn) {
      if (state.hasSignIn) return location.replace("/signin");
      $("claim-missing").hidden = false;
      return;
    }

    $("step-claim").hidden = true;
    $("step-method").hidden = false;
    $("passkey-block").hidden = !passkeysSupported();
    $("add-passkey").addEventListener("click", async () => {
      const button = $("add-passkey");
      setBusy(button, true);
      showError(null, "passkey-error");
      try {
        await addPasskey();
        location.replace("/devices");
      } catch (error) {
        showError(error, "passkey-error");
      } finally {
        setBusy(button, false);
      }
    });
    if (state.hasSignIn) {
      $("has-methods").hidden = false;
      $("skip").hidden = false;
    }
    $("new-password-form").addEventListener("submit", async (event) => {
      event.preventDefault();
      const first = $("new-password").value;
      if (first !== $("new-password-2").value) return showError(new Error("The two passwords are not the same."), "method-error");
      try {
        await api("POST", "/password", { password: first });
        location.replace("/devices");
      } catch (error) {
        showError(error, "method-error");
      }
    });
    $("new-password").focus();
  }

  // ------------------------------------------------------------------ approve a computer

  async function linkPage() {
    wireBar("");
    if (!(await requireSignIn())) return;
    const code = new URLSearchParams(location.search).get("code");
    if (!code) {
      $("enter-code").hidden = false;
      $("code-form").addEventListener("submit", (event) => {
        event.preventDefault();
        const typed = $("code").value.trim().toUpperCase();
        location.assign("/link?code=" + encodeURIComponent(typed));
      });
      $("code").focus();
      return;
    }

    let request;
    try {
      request = await api("GET", "/pair/" + encodeURIComponent(code));
    } catch (error) {
      showError(error);
      $("enter-code").hidden = false;
      $("code-form").addEventListener("submit", (event) => {
        event.preventDefault();
        location.assign("/link?code=" + encodeURIComponent($("code").value.trim().toUpperCase()));
      });
      return;
    }

    $("request").hidden = false;
    $("request-icon").replaceWith(ring(systemIcon(request.system)));
    $("request-name").textContent = request.name;
    $("request-detail").textContent = [request.system, request.appVersion ? "Pairnets " + request.appVersion : null].filter(Boolean).join(" · ");
    $("request-from").textContent = `${request.address ? "From " + request.address + " · " : ""}asked ${ago(request.created)}`;
    $("request-same").hidden = !request.sameComputer;
    $("request-code").textContent = request.code;
    $("request-warning").textContent = `${request.name} will be able to read, change and delete the files in your synced folder.`;
    $("allow").textContent = "Allow " + request.name;

    // The way back to the app on this computer. The link carries nothing: the app fetches its key
    // through its own secret poll, so declining the browser's "Open Pairnets?" changes nothing.
    const backLink = "pairnets://signed-in";
    const show = (status, name, sameComputer, fresh) => {
      const outcome = $("outcome");
      $("ask").hidden = true;
      outcome.hidden = false;
      outcome.className = "message " + (status === "approved" || status === "delivered" ? "ok" : status === "denied" ? "info" : "error");
      const back = status === "approved" && sameComputer;
      outcome.textContent = {
        approved: back
          ? `✓ ${name} is let in. Sending you back to Pairnets, where it asks which folder to sync…`
          : `✓ ${name} is let in. Go back to it: it finishes signing in by itself, then asks which folder to sync.`,
        delivered: `✓ ${name} is connected.`,
        denied: `${name} was not let in.`,
        expired: "This code has expired (codes last 10 minutes). Press Sign in on the computer again.",
      }[status] || status;
      $("outcome-links").hidden = false;
      if (back) {
        $("outcome-links").prepend(h("a", { class: "button accent", href: backLink }, "Open Pairnets"));
        if (fresh) location.assign(backLink); // only right after Allow, never again on a reload
      }
      $("request-title").textContent = status === "pending" ? "A computer wants to join" : "Approve a computer";
    };
    if (request.status !== "pending") return show(request.status, request.name, request.sameComputer, false);

    const decide = async (approve) => {
      setBusy($("allow"), true);
      setBusy($("deny"), true);
      showError(null);
      try {
        const result = await api("POST", `/pair/${encodeURIComponent(request.code)}/${approve ? "approve" : "deny"}`);
        show(result.status, result.name, result.sameComputer, true);
      } catch (error) {
        showError(error);
        setBusy($("allow"), false);
        setBusy($("deny"), false);
      }
    };
    $("allow").addEventListener("click", () => decide(true));
    $("deny").addEventListener("click", () => decide(false));
  }

  // ------------------------------------------------------------------ devices

  async function devicesPage() {
    wireBar("devices");
    wireDialogs();
    if (!(await requireSignIn())) return;
    $("add-host").textContent = location.hostname;
    $("add").addEventListener("click", () => $("add-dialog").showModal());

    let removing = null;
    let renaming = null;
    $("remove-confirm").addEventListener("click", async () => {
      $("remove-dialog").close();
      try {
        await api("DELETE", "/devices/" + encodeURIComponent(removing.id));
        await load();
      } catch (error) {
        showError(error);
      }
    });
    $("rename-form").addEventListener("submit", async (event) => {
      event.preventDefault();
      $("rename-dialog").close();
      try {
        await api("PATCH", "/devices/" + encodeURIComponent(renaming.id), { name: $("rename-input").value });
        await load();
      } catch (error) {
        showError(error);
      }
    });

    let openMenu = null;
    document.addEventListener("click", (event) => {
      if (openMenu && !openMenu.contains(event.target)) {
        openMenu.querySelector(".items").hidden = true;
        openMenu = null;
      }
    });

    function menu(device) {
      const items = h("div", { class: "items", hidden: true },
        h("button", { type: "button", onclick: () => { renaming = device; $("rename-input").value = device.name; $("rename-dialog").showModal(); } }, "Rename…"),
        h("button", { type: "button", class: "danger", onclick: () => { removing = device; $("remove-name").textContent = device.name; $("remove-dialog").showModal(); } }, "Remove from Pairnets…"));
      const wrap = h("div", { class: "menu" });
      const toggle = h("button", { type: "button", class: "icon", "aria-label": `More for ${device.name}`, onclick: (event) => {
        event.stopPropagation();
        const open = items.hidden;
        if (openMenu && openMenu !== wrap) openMenu.querySelector(".items").hidden = true;
        items.hidden = !open;
        openMenu = open ? wrap : null;
      } }, icon("more"));
      wrap.append(toggle, items);
      return wrap;
    }

    function deviceRow(d) {
      const parts = [d.system, d.appVersion ? "Pairnets " + d.appVersion : null];
      parts.push(d.lastChange ? "last change " + ago(d.lastChange) : isToday(d.firstSeen) ? "joined today" : "added " + day(d.firstSeen));
      return h("li", {},
        ring(systemIcon(d.system), d.online),
        h("div", { class: "grow" },
          h("div", { class: "title" }, d.name),
          h("div", { class: "detail" }, parts.filter(Boolean).join(" · ")),
          d.ownKey ? null : h("div", { class: "note" }, "ⓘ Still uses the old shared token. It switches to its own key by itself at its next start.")),
        h("span", { class: d.online ? "pill ok" : "pill" }, d.online ? "Online" : "Last seen " + ago(d.lastSeen)),
        d.ownKey ? menu(d) : null);
    }

    function pendingRow(r) {
      const allow = h("button", { type: "button", class: "accent", onclick: () => location.assign("/link?code=" + encodeURIComponent(r.code)) }, "Review");
      return h("li", {},
        ring("wait"),
        h("div", { class: "grow" },
          h("div", { class: "title" }, r.name),
          h("div", { class: "detail" }, [r.system, "code " + r.code, ago(r.created)].filter(Boolean).join(" · "))),
        allow);
    }

    async function load() {
      showError(null);
      try {
        const data = await api("GET", "/devices");
        const list = $("devices");
        list.replaceChildren(...(data.devices.length ? data.devices.map(deviceRow) : [h("li", { class: "empty" }, "No computers yet. Add one: open Pairnets on it and press Sign in.")]));
        $("pending-section").hidden = data.pending.length === 0;
        $("pending").replaceChildren(...data.pending.map(pendingRow));
      } catch (error) {
        if (error.status === 401) return goSignIn();
        showError(error);
      }
    }

    await load();
    setInterval(() => { if (!document.hidden && !document.querySelector("dialog[open]")) load(); }, 5000);
  }

  // ------------------------------------------------------------------ security

  async function securityPage() {
    wireBar("security");
    wireDialogs();
    if (!(await requireSignIn())) return;
    let data;

    $("password-change").addEventListener("click", () => {
      $("current-field").hidden = !data.methods.password;
      $("current-password").required = data.methods.password;
      $("password-dialog-title").textContent = data.methods.password ? "Change password" : "Set a password";
      $("password-form").reset();
      showError(null, "password-error");
      $("password-dialog").showModal();
    });
    $("password-form").addEventListener("submit", async (event) => {
      event.preventDefault();
      if ($("new-password").value !== $("new-password-2").value) return showError(new Error("The two passwords are not the same."), "password-error");
      try {
        await api("POST", "/password", { password: $("new-password").value, current: $("current-password").value || null });
        $("password-dialog").close();
        await load();
      } catch (error) {
        showError(error, "password-error");
      }
    });
    $("password-remove").addEventListener("click", async () => {
      try {
        await api("DELETE", "/password");
        await load();
      } catch (error) {
        showError(error);
      }
    });
    $("shared-token").addEventListener("change", async (event) => {
      try {
        await api("POST", "/shared-token", { allowed: event.target.checked });
        await load();
      } catch (error) {
        showError(error);
      }
    });

    $("add-passkey").hidden = !passkeysSupported();
    $("add-passkey").addEventListener("click", async () => {
      const button = $("add-passkey");
      setBusy(button, true);
      showError(null);
      try {
        await addPasskey();
        await load();
      } catch (error) {
        showError(error);
      } finally {
        setBusy(button, false);
      }
    });

    function passkeyRow(k, onlyWay) {
      return h("li", { class: "passkey" },
        ring("key"),
        h("div", { class: "grow" },
          h("div", { class: "title" }, k.name),
          h("div", { class: "detail" }, `Added ${day(k.created)} · ${k.lastUsed ? "used " + ago(k.lastUsed) : "never used"}`)),
        onlyWay ? null : h("button", { type: "button", class: "flat", onclick: async () => {
          try {
            await api("DELETE", "/passkeys/" + encodeURIComponent(k.id));
            await load();
          } catch (error) {
            showError(error);
          }
        } }, "Remove"));
    }

    function sessionRow(s) {
      return h("li", {},
        ring("browser"),
        h("div", { class: "grow" },
          h("div", { class: "title" }, browserName(s.userAgent), s.current ? h("span", { class: "muted" }, " · this browser") : null),
          h("div", { class: "detail" }, `Signed in with ${s.method} · since ${day(s.created)} · last used ${ago(s.lastSeen)}`)),
        s.current ? null : h("button", { type: "button", onclick: async () => {
          try {
            await api("DELETE", "/sessions/" + encodeURIComponent(s.id));
            await load();
          } catch (error) {
            showError(error);
          }
        } }, "Sign out"));
    }

    async function load() {
      showError(null);
      try {
        data = await api("GET", "/security");
      } catch (error) {
        if (error.status === 401) return goSignIn();
        return showError(error);
      }
      $("password-state").textContent = data.methods.password ? "Set " + day(data.passwordSetAt) : "Not set";
      $("password-change").textContent = data.methods.password ? "Change" : "Set";
      const others = data.methods.passkeys > 0 || data.methods.email || data.methods.google;
      $("password-remove").hidden = !data.methods.password || !others; // the last way in cannot be removed
      for (const row of document.querySelectorAll("#methods li.passkey")) row.remove();
      const onlyWay = !data.methods.password && data.passkeys.length <= 1;
      $("methods").prepend(...data.passkeys.map((k) => passkeyRow(k, onlyWay)));
      $("sessions").replaceChildren(...data.sessions.map(sessionRow));
      // Email sign-in links
      $("email-state").textContent = !data.emailAvailable ? "Not set up on the server yet (see DEPLOY.md)."
        : data.emailAddress ? "Sign-in links go to " + data.emailAddress
        : "Add an address and confirm it with the link we send you.";
      emailForm.hidden = !data.emailAvailable || Boolean(data.emailAddress);
      $("email-change").hidden = !data.emailAvailable || !data.emailAddress;
      $("email-remove").hidden = !data.emailAvailable || !data.emailAddress || !(data.methods.password || data.methods.passkeys > 0 || data.methods.google);
      // Google
      $("google-state").textContent = !data.googleAvailable ? "Not set up on the server yet (see DEPLOY.md)."
        : data.googleEmail ? "Connected: " + data.googleEmail : "Sign in with your Google account.";
      $("google-connect").hidden = !data.googleAvailable || Boolean(data.googleEmail);
      $("google-remove").hidden = !data.googleAvailable || !data.googleEmail || !(data.methods.password || data.methods.passkeys > 0 || data.methods.email);
      $("shared-token").checked = data.sharedTokenAllowed;
      const total = data.devicesWithOwnKey + data.devicesOnSharedToken;
      $("shared-token-detail").textContent = total === 0 ? "No computers have connected yet."
        : data.devicesOnSharedToken === 0
        ? `All ${total} computer${total === 1 ? " has its" : "s have their"} own key. You can turn this off.`
        : `${data.devicesWithOwnKey} of ${total} computers have switched to their own key. Turn this off when all have.`;
    }

    for (const ringEl of document.querySelectorAll("[data-icon]")) ringEl.append(icon(ringEl.dataset.icon));

    const emailForm = $("email-form");
    $("email-change").addEventListener("click", () => {
      emailForm.hidden = false;
      $("email-change").hidden = true;
      $("email-input").focus();
    });
    emailForm.addEventListener("submit", async (event) => {
      event.preventDefault();
      setBusy($("email-send"), true);
      showError(null);
      try {
        await api("POST", "/email", { email: $("email-input").value });
        showNotice(`We sent a confirmation link to ${$("email-input").value.trim()}. Open it to finish: it works once, for 15 minutes.`);
        emailForm.hidden = true;
        $("email-input").value = "";
        $("email-change").hidden = false;
      } catch (error) {
        showError(error);
      } finally {
        setBusy($("email-send"), false);
      }
    });
    $("email-remove").addEventListener("click", async () => {
      try {
        await api("DELETE", "/email");
        await load();
      } catch (error) {
        showError(error);
      }
    });
    $("google-remove").addEventListener("click", async () => {
      try {
        await api("DELETE", "/google");
        await load();
      } catch (error) {
        showError(error);
      }
    });

    const query = new URLSearchParams(location.search);
    if (query.get("connected") === "google") showNotice("Google is connected. You can now sign in with it.");
    if (GOOGLE_PROBLEMS[query.get("error")]) showError(new Error(GOOGLE_PROBLEMS[query.get("error")]));
    if (location.search) history.replaceState(null, "", location.pathname);
    await load();
  }

  // ------------------------------------------------------------------ start

  // ------------------------------------------------------------------ email link

  async function emailLinkPage() {
    const code = new URLSearchParams(location.hash.slice(1)).get("code");
    // The code is in the fragment so neither a mail scanner nor a server log ever sees it.
    if (location.hash) history.replaceState(null, "", location.pathname);
    if (!code) {
      $("link-signin").hidden = true;
      $("link-text").hidden = true;
      $("link-missing").hidden = false;
      return;
    }
    $("link-signin").addEventListener("click", async () => {
      const button = $("link-signin");
      setBusy(button, true);
      showError(null);
      try {
        await api("POST", "/signin/email/confirm", { code });
        location.replace("/devices");
      } catch (error) {
        showError(error);
        setBusy(button, false);
      }
    });
  }

  const pages = { signin: signinPage, setup: setupPage, link: linkPage, devices: devicesPage, security: securityPage, "email-link": emailLinkPage };
  const start = pages[document.body.dataset.page];
  if (start) {
    start().catch((error) => {
      console.error(error);
      showError(error);
    });
  }
})();
