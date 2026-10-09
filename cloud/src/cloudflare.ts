// The few Cloudflare API calls this service makes (https://developers.cloudflare.com/api/), RELAY.md sections 2, 5, 7.
// Each relayed nest gets its own tunnel, with no hostname routed: the only way in is the router's private VPC link.
// A tunnel's token lets whoever holds it receive that tunnel's traffic, so tunnels are never shared.
//
// The API token (CF_API_TOKEN) never leaves the Worker and never appears in an error or a log line: errors carry only
// the step, the HTTP status and Cloudflare's numeric error codes.

import type { Env } from "./env";

export const CF_API = "https://api.cloudflare.com/client/v4";

/** The router Worker whose bindings this service sets (cloud/router). */
export const ROUTER_SCRIPT = "pairnets-router";

/** Every relay tunnel answers 404 to any public hostname: nothing is ever routed to the nest from the internet. */
export const RELAY_INGRESS = [{ service: "http_status:404" }];

/** The shape of a tunnel token (base64 of a small JSON object). */
export const TUNNEL_TOKEN_RE = /^[A-Za-z0-9+/=_-]{40,}$/;

const TUNNEL_ID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/** A relay tunnel's name: "pairnets-" + the nest id. */
export function tunnelName(nestId: string): string {
  return `pairnets-${nestId}`;
}

/** The nest id in a relay tunnel's name, or null for any other tunnel in the account. */
export function nestIdOfTunnel(name: unknown): string | null {
  const m = typeof name === "string" ? /^pairnets-(nst_[0-9a-hjkmnp-tv-z]{26})$/.exec(name) : null;
  return m ? m[1] : null;
}

export class CloudflareError extends Error {
  constructor(
    readonly step: string,
    readonly status: number,
    readonly codes: number[],
  ) {
    super(`${step}: Cloudflare answered ${status || "nothing"}${codes.length ? ` (${codes.join(", ")})` : ""}`);
  }
}

interface ApiAnswer {
  success?: boolean;
  errors?: { code?: number }[];
  result?: unknown;
}

/** One binding of the router: the VPC link to a nest's tunnel. */
export interface RouterBinding {
  type: "vpc_network";
  name: string;
  tunnel_id: string;
}

export interface TunnelInfo {
  id: string;
  name: string;
}

export class CloudflareApi {
  constructor(
    private readonly env: Env,
    private readonly fetchFn: typeof fetch,
  ) {}

  private async call(step: string, method: string, path: string, body?: unknown, missingIsDone = false): Promise<unknown> {
    let resp: Response;
    const headers: Record<string, string> = { Authorization: `Bearer ${this.env.CF_API_TOKEN}` };
    let payload: BodyInit | undefined;
    if (body instanceof FormData) {
      payload = body; // fetch sets the multipart Content-Type with its boundary
    } else if (body !== undefined) {
      headers["Content-Type"] = "application/json";
      payload = JSON.stringify(body);
    }
    try {
      resp = await this.fetchFn(CF_API + path, { method, headers, body: payload });
    } catch {
      throw new CloudflareError(step, 0, []);
    }
    if (missingIsDone && resp.status === 404) return null;
    let answer: ApiAnswer | null = null;
    try {
      answer = (await resp.json()) as ApiAnswer;
    } catch {
      answer = null;
    }
    if (!resp.ok || !answer || answer.success !== true) {
      const codes = (answer?.errors ?? []).map((e) => e.code).filter((c): c is number => typeof c === "number");
      throw new CloudflareError(step, resp.status, codes);
    }
    return answer.result;
  }

  private account(path: string): string {
    return `/accounts/${encodeURIComponent(this.env.CF_ACCOUNT_ID)}${path}`;
  }

  /** A new tunnel managed from Cloudflare (no config file on the server), named after the nest. Returns its id. */
  async createTunnel(nestId: string): Promise<string> {
    const result = (await this.call("tunnel", "POST", this.account("/cfd_tunnel"), {
      name: tunnelName(nestId),
      config_src: "cloudflare",
    })) as { id?: unknown } | null;
    const id = result?.id;
    if (typeof id !== "string" || !TUNNEL_ID_RE.test(id)) throw new CloudflareError("tunnel", 200, []);
    return id;
  }

  /** No hostname is ever routed: the catch-all rule alone (the router reaches the nest through Workers VPC). */
  async setIngress(tunnelId: string): Promise<void> {
    await this.call("ingress", "PUT", this.account(`/cfd_tunnel/${tunnelId}/configurations`), { config: { ingress: RELAY_INGRESS } });
  }

  /** The token cloudflared runs the tunnel with. Read when the installer collects it; never stored here. */
  async tunnelToken(tunnelId: string): Promise<string> {
    const token = await this.call("token", "GET", this.account(`/cfd_tunnel/${tunnelId}/token`));
    if (typeof token !== "string" || !TUNNEL_TOKEN_RE.test(token)) throw new CloudflareError("token", 200, []);
    return token;
  }

  /** Closes the tunnel's open connections (a tunnel with connections cannot be deleted). */
  async dropConnections(tunnelId: string): Promise<void> {
    await this.call("connections", "DELETE", this.account(`/cfd_tunnel/${tunnelId}/connections`), undefined, true);
  }

  async deleteTunnel(tunnelId: string): Promise<void> {
    await this.call("delete tunnel", "DELETE", this.account(`/cfd_tunnel/${tunnelId}`), undefined, true);
  }

  /** The account's tunnels that are not deleted, optionally only those with an exact name (100 a page, 20 pages). */
  async listTunnels(name?: string): Promise<TunnelInfo[]> {
    const out: TunnelInfo[] = [];
    for (let page = 1; page <= 20; page++) {
      const q = new URLSearchParams({ is_deleted: "false", per_page: "100", page: String(page) });
      if (name !== undefined) q.set("name", name);
      const result = await this.call("list tunnels", "GET", this.account(`/cfd_tunnel?${q}`));
      if (!Array.isArray(result)) throw new CloudflareError("list tunnels", 200, []);
      for (const t of result as { id?: unknown; name?: unknown }[]) {
        if (t && typeof t.id === "string" && TUNNEL_ID_RE.test(t.id) && typeof t.name === "string") out.push({ id: t.id, name: t.name });
      }
      if (result.length < 100) break;
    }
    return out;
  }

  /**
   * Replaces the router's bindings with this full list (RELAY.md 5): PATCH .../workers/scripts/pairnets-router/settings,
   * multipart with one part "settings" = {"bindings":[...]}. The router's code is kept.
   */
  async setRouterBindings(bindings: RouterBinding[]): Promise<void> {
    const form = new FormData();
    form.append("settings", new Blob([JSON.stringify({ bindings })], { type: "application/json" }), "settings.json");
    await this.call("router", "PATCH", this.account(`/workers/scripts/${ROUTER_SCRIPT}/settings`), form);
  }
}

/**
 * Deletes a nest's tunnel: its connections first, then the tunnel. With no tunnel id (the request that made it died
 * before storing the id) the tunnel is looked up by its name. True when nothing of it is left.
 */
export async function teardown(cf: CloudflareApi, tunnelId: string | null, nestId: string): Promise<{ done: boolean; failed: string[] }> {
  const failed: string[] = [];
  const attempt = async (step: string, fn: () => Promise<void>) => {
    try {
      await fn();
    } catch {
      failed.push(step);
    }
  };
  let ids: string[] = tunnelId ? [tunnelId] : [];
  if (!tunnelId) {
    try {
      ids = (await cf.listTunnels(tunnelName(nestId))).filter((t) => t.name === tunnelName(nestId)).map((t) => t.id);
    } catch {
      return { done: false, failed: ["list tunnels"] };
    }
  }
  for (const id of ids) {
    await attempt("connections", () => cf.dropConnections(id));
    await attempt("delete tunnel", () => cf.deleteTunnel(id));
  }
  return { done: failed.length === 0, failed };
}
