// pairnets-router (RELAY.md section 5).
//
// It has no route and no workers.dev address: the only way in is the ROUTER service binding of pairnets-sync. Its
// bindings are one Workers VPC link per relayed nest, N_<nestId> -> that nest's Cloudflare Tunnel, set by pairnets-sync
// through the Cloudflare API (the full list every time). Through a link, http://127.0.0.1:5075 is the nest itself.
//
// A request names its nest in X-Pairnets-Route; the router removes that header and passes the request on to the nest
// with the same method, headers and body (redirects are not followed), and returns the nest's answer as it is. Its own
// answers (unknown route, failing link) are JSON and carry X-Pairnets-Router, so pairnets-sync can tell them from the
// nest's.

/** Where the nest listens on its own machine (install.sh: ASPNETCORE_URLS=http://127.0.0.1:5075). */
export const NEST_ORIGIN = "http://127.0.0.1:5075";

const NEST_ID_RE = /^nst_[0-9a-hjkmnp-tv-z]{26}$/;

/** A Workers VPC link (a `vpc_network` binding): it fetches inside the tunnel's network. */
export interface Link {
  fetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response>;
}

/** The router's bindings: N_<nestId> for every relayed nest, nothing else. */
export type RouterEnv = Record<string, unknown>;

function isLink(v: unknown): v is Link {
  return !!v && typeof (v as Link).fetch === "function";
}

function error(status: number, code: "nest_unknown" | "nest_offline"): Response {
  return new Response(JSON.stringify({ error: code }), {
    status,
    headers: { "Content-Type": "application/json", "Cache-Control": "no-store", "X-Pairnets-Router": code },
  });
}

export async function route(req: Request, env: RouterEnv): Promise<Response> {
  const nestId = req.headers.get("X-Pairnets-Route") ?? "";
  const link = NEST_ID_RE.test(nestId) ? env[`N_${nestId}`] : undefined;
  if (!isLink(link)) return error(404, "nest_unknown");

  const url = new URL(req.url);
  const headers = new Headers(req.headers);
  headers.delete("X-Pairnets-Route");
  headers.delete("Host"); // the runtime sets it for 127.0.0.1:5075
  // Hop-by-hop headers belong to the previous connection; Expect: 100-continue would draw a 1xx answer from the nest.
  for (const name of ["Expect", "Keep-Alive", "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding"]) headers.delete(name);
  const method = req.method.toUpperCase();
  try {
    return await link.fetch(`${NEST_ORIGIN}${url.pathname}${url.search}`, {
      method,
      headers,
      body: method === "GET" || method === "HEAD" ? undefined : req.body,
      redirect: "manual",
    });
  } catch {
    return error(503, "nest_offline");
  }
}

export default {
  fetch(req: Request, env: RouterEnv): Promise<Response> {
    return route(req, env);
  },
};
