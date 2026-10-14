# pairnets.app (the public site)

Plain static pages for **pairnets.app** (the `site/` folder): home and downloads, "Add a computer", help, and
the legal pages. This file lives in `docs/` on purpose: everything inside `site/` is published, so a README
there would be public at `/README.md`.

The site holds no accounts and no data of its own, and nothing secret. Accounts and sign-in live at
**sync.pairnets.app** (one shared address for everyone; the "Sign in" button and `/nest` go to its `/account`
page, which sends visitors who are not signed in to the sign-in page and back). `assets/site.js` asks
`sync.pairnets.app/v1/signed-in` on every page, with the visitor's sign-in cookie for that site, to show the
username in the top bar; the only thing pairnets.app keeps is a note in local storage that the cookie notice was
closed. People's own servers are called *nests*.

## Hosting on Cloudflare Pages (free)

1. Cloudflare dashboard → **Workers & Pages** → **Create** → **Pages** → **Connect to Git** → this
   repository (`MRnigth/Pairnets`).
2. Build settings: **Framework preset: None**, **Build command: (empty)**, **Build output directory: `site`**.
3. After the first deploy: **Custom domains** → add `pairnets.app` (and `www.pairnets.app` if you like;
   Cloudflare sets the DNS record itself because the domain is on Cloudflare).

`_headers` adds a strict content security policy and other security headers (including HSTS); `_redirects`
points `/download/...` at the files of the latest GitHub release (they keep the names the apps update
themselves with, so the links never break) and `/get.sh` at the copy of the server installer that the release
workflow uploads to the latest release (so what people pipe into `sudo bash` is a released, checksummed
file, not whatever is on `main`).

`nest.pairnets.app` is **not** served from here and is not the sign-in: it is the developer's own server's
tunnel (a public hostname on a Cloudflare Tunnel to `localhost:5075`). Other people's nests are reached
through sync.pairnets.app.

## Changing it

Edit the HTML by hand; there is no build step. `assets/site.css` uses the same colours as the apps. Open
`site/index.html` in a browser, or run `python3 -m http.server -d site` (redirects and headers only work on
Cloudflare Pages). `tests/Pairnets.Tests/Unit/SiteTests.cs` checks that every local link and image
exists and that the download redirects point at files the release workflow builds.

The legal and help pages (`site/privacy/`, `site/terms/`, `guidelines/`, `cookies/`, `security/`, `faq/`,
`contact/`, `delete-account/`, `licenses/`) say exactly what the apps, the nest and sync.pairnets.app
do. When one of those changes what it stores, sends or sets, change these pages with it, and move
their "Last updated" date on. Every page's footer links all of them (a test checks this), and
`site/.well-known/security.txt` needs a new `Expires` date within a year.
