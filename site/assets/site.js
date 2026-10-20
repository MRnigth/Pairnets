// Picks the download button for the visitor's system. Everything works without it (the links are plain).
"use strict";
(() => {
  const ua = navigator.userAgent || "";
  const platform = (navigator.userAgentData && navigator.userAgentData.platform) || navigator.platform || "";
  const text = `${platform} ${ua}`;
  // iPads report "Macintosh" like a Mac; only the touch screen tells them apart.
  const isPhone = /iPhone|iPad|Android/i.test(text) || (/Mac/i.test(text) && navigator.maxTouchPoints > 1);
  let target = null;
  // Computers only for now (phone app later): phones and tablets go to the list of downloads to get it elsewhere.
  if (isPhone) target = { href: "/#download", label: "Get it for your computer" };
  else if (/Win/i.test(text)) target = { system: "windows", href: "/download/windows", label: "Download for Windows" };
  else if (/Mac/i.test(text)) target = { system: "mac", href: "/download/mac-apple-silicon", label: "Download for Mac" };
  else if (/Linux|X11/i.test(text)) target = { system: "linux", href: "/download/linux", label: "Download for Linux" };
  const button = document.getElementById("download-main");
  if (button && target) {
    button.href = target.href;
    button.textContent = target.label;
  }
  // "Add a computer" shows all three systems as plain buttons; only this computer's one is blue.
  if (target && target.system) {
    for (const b of document.querySelectorAll(`[data-system="${target.system}"]`)) b.classList.add("accent");
  }
  const note = document.getElementById("phone-note");
  if (note && isPhone) note.hidden = false;
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
