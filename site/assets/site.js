// Picks the download button for the visitor's system. Everything works without it (the links are plain).
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
