// WebDAV on pairnets.app (docs/WEBDAV.md): https://pairnets.app/<nest-name>.<nest id>/ is a nest's files for Finder and
// Explorer. This function passes such a request, whatever its method, on to the Pairnets Worker (cloud/src/relay.ts
// davRoute) with the caller's address signed, and answers nothing else: every other path of the site goes straight on
// to the static files with `context.next()`, exactly as if this file did not exist.
//
// Bindings (Pages project settings): SYNC = a service binding to the pairnets-sync Worker (optional; without it the
// public address is used) and DAV_EDGE_SECRET = the same secret the Worker has.

const NEST_SEGMENT = /^([a-z0-9-]{0,40})\.(nst_[0-9a-hjkmnp-tv-z]{26})$/;
const WORKER_ORIGIN = "https://sync.pairnets.app";
const OWN = ["x-pairnets-dav-base", "x-pairnets-dav-ip", "x-pairnets-dav-ts", "x-pairnets-dav-sig"];

async function sign(secret, message) {
  const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(secret), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
  const mac = new Uint8Array(await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(message)));
  return [...mac].map((x) => x.toString(16).padStart(2, "0")).join("");
}

export async function onRequest(context) {
  const { request, env } = context;
  const url = new URL(request.url);
  const slash = url.pathname.indexOf("/", 1);
  const segment = url.pathname.slice(1, slash === -1 ? undefined : slash);
  const m = NEST_SEGMENT.exec(segment);
  if (!m) return context.next();

  if (!env.DAV_EDGE_SECRET) {
    return new Response("WebDAV is not switched on here yet.\n", { status: 503, headers: { "Content-Type": "text/plain; charset=utf-8", "Cache-Control": "no-store" } });
  }
  const ip = request.headers.get("cf-connecting-ip") || "unknown";
  const ts = String(Math.floor(Date.now() / 1000));
  const headers = new Headers(request.headers);
  for (const name of OWN) headers.delete(name); // a caller's own copies never travel
  headers.set("x-pairnets-dav-ip", ip);
  headers.set("x-pairnets-dav-ts", ts);
  headers.set("x-pairnets-dav-sig", await sign(env.DAV_EDGE_SECRET, `${ts}|${ip}|${segment}`));

  const target = `${WORKER_ORIGIN}/dav/${segment}${slash === -1 ? "" : url.pathname.slice(slash)}${url.search}`;
  const method = request.method.toUpperCase();
  const forwarded = new Request(target, {
    method,
    headers,
    body: method === "GET" || method === "HEAD" ? undefined : request.body,
    redirect: "manual",
  });
  return env.SYNC ? env.SYNC.fetch(forwarded) : fetch(forwarded);
}
