# pairnets.app (the public site)

Plain static pages for **pairnets.app**: home and downloads, "Add a computer", help. No accounts, no
data, nothing secret: the person's own server (their *nest*, `nest.pairnets.app`) does all the signing in.

## Hosting on Cloudflare Pages (free)

1. Cloudflare dashboard → **Workers & Pages** → **Create** → **Pages** → **Connect to Git** → this
   repository (`MRnigth/Pairnets`).
2. Build settings: **Framework preset: None**, **Build command: (empty)**, **Build output directory: `site`**.
3. After the first deploy: **Custom domains** → add `pairnets.app` (and `www.pairnets.app` if you like;
   Cloudflare sets the DNS record itself because the domain is on Cloudflare).

`_headers` adds a strict content security policy and other security headers; `_redirects` points
`/download/...` at the files of the latest GitHub release (they keep the names the apps update
themselves with, so the links never break) and `/get.sh` at the server installer.

The nest's own name, `nest.pairnets.app`, is **not** served from here: it is a DNS-only record that
points at your server's Tailscale address (`install.sh --domain` creates it).

## Changing it

Edit the HTML by hand; there is no build step. `assets/site.css` uses the same colours as the apps. Open
`index.html` in a browser, or run `python3 -m http.server -d site` (redirects and headers only work on
Cloudflare Pages). `tests/Pairnets.Tests/Unit/SiteTests.cs` checks that every local link and image
exists and that the download redirects point at files the release workflow builds.
