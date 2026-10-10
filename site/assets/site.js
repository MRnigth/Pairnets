// Picks the download button for the visitor's system, and shows the cookie notice. Everything works without it
// (the links are plain).
"use strict";
(() => {
  const ua = navigator.userAgent || "";
  const platform = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || "";
  const text = `${platform} ${ua}`;
  let target = null;
  if (/iPhone|iPad|Android/i.test(text)) target = null; // computers only for now: phone app later
  else if (/Win/i.test(text)) target = { href: "/download/windows", label: "Download for Windows" };
  else if (/Mac/i.test(text)) target = { href: "/download/mac-apple-silicon", label: "Download for Mac" };
  else if (/Linux|X11/i.test(text)) target = { href: "/download/linux", label: "Download for Linux" };
  const button = document.getElementById("download-main");
  if (button && target) {
    button.href = target.href;
    button.textContent = target.label;
  }
  const note = document.getElementById("phone-note");
  if (note && /iPhone|iPad|Android/i.test(text)) note.hidden = false;
})();

// Cookie notice: tells visitors Pairnets only uses the cookies needed to sign in. It is a notice, not a consent wall:
// closing it is remembered in local storage ("pn-cookie-notice" = "ok"), which is never sent anywhere.
(() => {
  const KEY = "pn-cookie-notice";
  if (/^\/cookies(\/(index\.html)?)?$/.test(location.pathname)) return; // the cookie page says it all already
  try {
    if (window.localStorage.getItem(KEY) === "ok") return;
  } catch (e) { /* storage blocked: show the notice, closing it lasts for this page view */ }

  const box = document.createElement("div");
  box.className = "cookie-notice";
  box.setAttribute("role", "region");
  box.setAttribute("aria-label", "Cookie notice");
  const text = document.createElement("p");
  text.textContent = "Pairnets only uses cookies that are needed to sign you in. No tracking, no ads.";
  const actions = document.createElement("div");
  actions.className = "cookie-notice-actions";
  const ok = document.createElement("button");
  ok.type = "button";
  ok.className = "button accent";
  ok.textContent = "OK";
  const more = document.createElement("a");
  more.href = "/cookies/";
  more.textContent = "Cookie settings";
  actions.append(ok, more);
  box.append(text, actions);

  const close = () => {
    try { window.localStorage.setItem(KEY, "ok"); } catch (e) { /* storage blocked: hidden for this page view only */ }
    document.removeEventListener("keydown", onKey);
    box.remove();
  };
  const onKey = (ev) => {
    if (ev.key === "Escape" && !ev.defaultPrevented) close();
  };
  ok.addEventListener("click", close);
  document.addEventListener("keydown", onKey);
  document.body.prepend(box); // first in the tab order, so keyboard users reach it at once
})();

// Signed in at sync.pairnets.app? Then the top bar shows your username (a round letter and the name) and an
// "Account" button instead of "Sign in". sync.pairnets.app answers this one question for pairnets.app only; the
// browser sends its sign-in cookie with it, and pairnets.app itself stores nothing. Any failure keeps "Sign in".
(() => {
  const button = document.getElementById("account-button");
  if (!button || !window.fetch) return;
  const ACCOUNT = "https://sync.pairnets.app/account";
  fetch("https://sync.pairnets.app/v1/signed-in", { credentials: "include", cache: "no-store" })
    .then((res) => (res.ok ? res.json() : null))
    .then((me) => {
      if (!me || me.signedIn !== true || typeof me.name !== "string" || me.name.length === 0) return;
      const chip = document.createElement("a");
      chip.className = "account-chip";
      chip.href = ACCOUNT;
      chip.title = `Signed in as ${me.name}`;
      const letter = document.createElement("span");
      letter.className = "account-letter";
      letter.setAttribute("aria-hidden", "true");
      letter.textContent = Array.from(me.name)[0].toUpperCase();
      const name = document.createElement("span");
      name.className = "account-name";
      name.textContent = me.name;
      chip.append(letter, name);
      button.before(chip);
      button.href = ACCOUNT;
      button.textContent = "Account →";
    })
    .catch(() => { /* offline, blocked or not deployed yet: keep "Sign in" */ });
})();
