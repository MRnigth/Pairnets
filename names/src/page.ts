// The one web page: what this service is, and where to report abuse. Everything else is the installer's API.

import type { Env } from "./env";
import { escapeHtml } from "./http";

export function infoPage(env: Env): string {
  const domain = escapeHtml(env.BASE_DOMAIN);
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Pairnets names</title>
<style>
  :root { color-scheme: light dark; --fg: #1a1a1a; --muted: #5c5c5c; --bg: #fbfaf8; --line: #e4e1db; }
  @media (prefers-color-scheme: dark) { :root { --fg: #ecebe8; --muted: #a3a29e; --bg: #191919; --line: #333230; } }
  body { margin: 0; background: var(--bg); color: var(--fg); font: 16px/1.6 system-ui, sans-serif; }
  main { max-width: 640px; margin: 0 auto; padding: 48px 16px; }
  h1 { font-size: 28px; margin: 0 0 16px; }
  h2 { font-size: 18px; margin: 32px 0 8px; }
  p, li { color: var(--fg); }
  .muted { color: var(--muted); font-size: 14px; border-top: 1px solid var(--line); padding-top: 16px; margin-top: 40px; }
  code { font: 14px ui-monospace, monospace; overflow-wrap: anywhere; }
  a { color: inherit; }
</style>
</head>
<body>
<main>
<h1>Pairnets names</h1>
<p>This service gives <a href="https://pairnets.app">Pairnets</a> servers a free name like
<code>alice.${domain}</code>, so they can be set up with one command and no domain of their own.</p>
<p>It only makes the name and a Cloudflare Tunnel for it. Each person's files stay on their own server: they never
pass through this service and are never stored here.</p>
<h2>Report abuse</h2>
<p>If a <code>*.${domain}</code> name is used for something harmful, write to
<a href="mailto:support@pairnets.app">support@pairnets.app</a> with the name and what you saw.</p>
<h2>Set up a server</h2>
<p>See <a href="https://github.com/MRnigth/Pairnets#quick-start">the quick start</a>.</p>
<p class="muted">&copy; 2026 Pairnets. Names are free, and can be taken back if they are used for abuse.</p>
</main>
</body>
</html>
`;
}
