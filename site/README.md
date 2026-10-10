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

The nest's own name, `nest.pairnets.app`, is **not** served from here: it is a public hostname on
your server's Cloudflare Tunnel (Zero Trust → Networks → Tunnels → public hostname `nest` →
`HTTP` `localhost:5075`), so requests for it go straight through the tunnel to your server.

## Changing it

Edit the HTML by hand; there is no build step. `assets/site.css` uses the same colours as the apps. Open
`index.html` in a browser, or run `python3 -m http.server -d site` (redirects and headers only work on
Cloudflare Pages). `tests/Pairnets.Tests/Unit/SiteTests.cs` checks that every local link and image
exists and that the download redirects point at files the release workflow builds.

The legal and help pages (`privacy/`, `terms/`, `guidelines/`, `cookies/`, `security/`, `faq/`,
`contact/`, `delete-account/`, `licenses/`) say exactly what the apps, the nest and sync.pairnets.app
do. When one of those changes what it stores, sends or sets, change these pages with it, and move
their "Last updated" date on. Every page's footer links all of them (a test checks this), and
`.well-known/security.txt` needs a new `Expires` date within a year.
