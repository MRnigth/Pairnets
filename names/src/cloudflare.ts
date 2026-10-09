// The few Cloudflare API calls this service makes (https://developers.cloudflare.com/api/). Each user gets their own
// tunnel: a tunnel's token lets whoever holds it receive that tunnel's traffic, so tunnels are never shared.
//
// The API token (CF_API_TOKEN) never leaves the Worker and never appears in an error or a log line: errors carry only
// the step, the HTTP status and Cloudflare's numeric error codes.

import { b64Encode, randomBytes } from "./crypto";
import type { Env } from "./env";

export const CF_API = "https://api.cloudflare.com/client/v4";

/** Where every tunnel delivers its traffic: the nest listens there (deploy/install.sh, --name). */
export const NEST_SERVICE = "http://localhost:5075";

/** Cloudflare's codes for "a record with that name is already there". */
const DNS_CONFLICT_CODES = new Set([81053, 81054, 81057, 81058]);

/** The shape of a tunnel token, checked the same way by deploy/install.sh. */
export const TUNNEL_TOKEN_RE = /^[A-Za-z0-9+/=_-]{40,}$/;

const TUNNEL_ID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const RECORD_ID_RE = /^[0-9a-f]{32}$/;

export class CloudflareError extends Error {
  constructor(
    readonly step: string,
    readonly status: number,
    readonly codes: number[],
  ) {
    super(`${step}: Cloudflare answered ${status || "nothing"}${codes.length ? ` (${codes.join(", ")})` : ""}`);
  }

  /** The DNS record could not be made because the name is already in the zone. */
  get isConflict(): boolean {
    return this.step === "dns" && this.codes.some((c) => DNS_CONFLICT_CODES.has(c));
  }
}

interface ApiAnswer {
  success?: boolean;
  errors?: { code?: number }[];
  result?: unknown;
}

export class CloudflareApi {
  constructor(
    private readonly env: Env,
    private readonly fetchFn: typeof fetch,
  ) {}

  private async call(step: string, method: string, path: string, body?: unknown, missingIsDone = false): Promise<unknown> {
    let resp: Response;
    try {
      resp = await this.fetchFn(CF_API + path, {
        method,
        headers: { Authorization: `Bearer ${this.env.CF_API_TOKEN}`, "Content-Type": "application/json" },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
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

  private zone(path: string): string {
    return `/zones/${encodeURIComponent(this.env.CF_ZONE_ID)}${path}`;
  }

  /** A new tunnel managed from Cloudflare (no config file on the server). Returns its id. */
  async createTunnel(label: string): Promise<string> {
    const result = (await this.call("tunnel", "POST", this.account("/cfd_tunnel"), {
      name: label,
      config_src: "cloudflare",
      tunnel_secret: b64Encode(randomBytes(32)),
    })) as { id?: unknown } | null;
    const id = result?.id;
    if (typeof id !== "string" || !TUNNEL_ID_RE.test(id)) throw new CloudflareError("tunnel", 200, []);
    return id;
  }

  /** The token cloudflared runs the tunnel with (the server keeps it in /etc/pairnets/tunnel.env). */
  async tunnelToken(tunnelId: string): Promise<string> {
    const token = await this.call("token", "GET", this.account(`/cfd_tunnel/${tunnelId}/token`));
    if (typeof token !== "string" || !TUNNEL_TOKEN_RE.test(token)) throw new CloudflareError("token", 200, []);
    return token;
  }

  /** The tunnel sends the name's traffic to the nest and answers 404 to anything else (a catch-all rule is required). */
  async setIngress(tunnelId: string, hostname: string): Promise<void> {
    await this.call("ingress", "PUT", this.account(`/cfd_tunnel/${tunnelId}/configurations`), {
      config: { ingress: [{ hostname, service: NEST_SERVICE }, { service: "http_status:404" }] },
    });
  }

  /** The public name: a proxied CNAME to the tunnel. Returns the record's id. */
  async createDns(hostname: string, tunnelId: string): Promise<string> {
    const result = (await this.call("dns", "POST", this.zone("/dns_records"), {
      type: "CNAME",
      proxied: true,
      name: hostname,
      content: `${tunnelId}.cfargotunnel.com`,
      comment: "Pairnets name (names.pairnets.app)",
    })) as { id?: unknown } | null;
    const id = result?.id;
    if (typeof id !== "string" || !RECORD_ID_RE.test(id)) throw new CloudflareError("dns", 200, []);
    return id;
  }

  /** A new tunnel secret: the old token cannot open new connections any more. */
  async newSecret(tunnelId: string): Promise<void> {
    await this.call("rotate", "PATCH", this.account(`/cfd_tunnel/${tunnelId}`), { tunnel_secret: b64Encode(randomBytes(32)) });
  }

  /** Closes the tunnel's open connections (after a new secret: whoever still runs the old token is cut off). */
  async dropConnections(tunnelId: string): Promise<void> {
    await this.call("connections", "DELETE", this.account(`/cfd_tunnel/${tunnelId}/connections`), undefined, true);
  }

  async deleteTunnel(tunnelId: string): Promise<void> {
    await this.call("delete tunnel", "DELETE", this.account(`/cfd_tunnel/${tunnelId}`), undefined, true);
  }

  async deleteDns(recordId: string): Promise<void> {
    await this.call("delete dns", "DELETE", this.zone(`/dns_records/${recordId}`), undefined, true);
  }
}

/**
 * Deletes what exists of a name in Cloudflare: first the DNS record (the name stops working), then the tunnel's
 * connections and the tunnel. Every step is tried; true only when all of them worked (or there was nothing to do).
 */
export async function teardown(cf: CloudflareApi, tunnelId: string | null, dnsRecordId: string | null): Promise<{ done: boolean; failed: string[] }> {
  const failed: string[] = [];
  const attempt = async (step: string, fn: () => Promise<void>) => {
    try {
      await fn();
    } catch {
      failed.push(step);
    }
  };
  if (dnsRecordId) await attempt("delete dns", () => cf.deleteDns(dnsRecordId));
  if (tunnelId) {
    await attempt("connections", () => cf.dropConnections(tunnelId));
    await attempt("delete tunnel", () => cf.deleteTunnel(tunnelId));
  }
  return { done: failed.length === 0, failed };
}
