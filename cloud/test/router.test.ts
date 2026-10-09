import { describe, expect, it } from "vitest";
import { route } from "../router/src/index";
import { runRouter } from "../src/router";
import { addServer, Harness, linkNest, startServer } from "./helpers";

async function setup() {
  const h = await Harness.create();
  const b = h.browser();
  await b.signInByEmail("you@example.com");
  return { h, b };
}

/** A fake VPC link that remembers what it was asked. */
function recordingLink(answer: () => Response | Promise<Response>) {
  const seen: Request[] = [];
  return {
    seen,
    fetch: async (input: RequestInfo | URL, init?: RequestInit) => {
      seen.push(new Request(input, init));
      return answer();
    },
  };
}

const NEST = "nst_testnest000000000000000001";

describe("the router Worker (cloud/router)", () => {
  it("picks N_<nestId>, drops X-Pairnets-Route and Host, keeps method, headers, query and body, follows no redirect", async () => {
    const link = recordingLink(() => new Response("from the nest", { status: 207, headers: { "X-From": "nest" } }));
    const other = recordingLink(() => new Response("wrong nest"));
    const req = new Request("https://pairnets-router/api/files/a%20b?x=1&y=2", {
      method: "PUT",
      headers: { "X-Pairnets-Route": NEST, Host: "sync.pairnets.app", Authorization: "Bearer k", "X-Pairnets-Client-IP": "198.51.100.1", Expect: "100-continue" },
      body: "payload",
    });
    const resp = await route(req, { [`N_${NEST}`]: link, N_nst_testnest000000000000000002: other });
    expect(resp.status).toBe(207);
    expect(await resp.text()).toBe("from the nest");
    expect(resp.headers.get("X-From")).toBe("nest");
    expect(other.seen).toHaveLength(0);
    const sent = link.seen[0];
    expect(sent.url).toBe("http://127.0.0.1:5075/api/files/a%20b?x=1&y=2");
    expect(sent.method).toBe("PUT");
    expect(sent.redirect).toBe("manual");
    expect(await sent.text()).toBe("payload");
    expect(sent.headers.get("X-Pairnets-Route")).toBeNull();
    expect(sent.headers.get("Authorization")).toBe("Bearer k");
    expect(sent.headers.get("X-Pairnets-Client-IP")).toBe("198.51.100.1");
    expect(sent.headers.get("Host")).not.toBe("sync.pairnets.app");
    expect(sent.headers.get("Expect")).toBeNull(); // hop-by-hop: it would draw a 100 Continue from the nest
  });

  it("answers 404 nest_unknown without a link, 503 nest_offline when the link fails, both marked as its own", async () => {
    const req = (route_?: string) => new Request("https://pairnets-router/api/health", { headers: route_ ? { "X-Pairnets-Route": route_ } : {} });
    for (const [r, env] of [
      [undefined, {}],
      ["garbage", { N_garbage: recordingLink(() => new Response("x")) }],
      [NEST, {}],
      [NEST, { [`N_${NEST}`]: "not a binding" }],
    ] as const) {
      const resp = await route(req(r), env as Record<string, unknown>);
      expect(resp.status).toBe(404);
      expect(await resp.json()).toEqual({ error: "nest_unknown" });
      expect(resp.headers.get("X-Pairnets-Router")).toBe("nest_unknown");
    }
    const broken = { fetch: async () => Promise.reject(new Error("tunnel down")) };
    const resp = await route(req(NEST), { [`N_${NEST}`]: broken });
    expect(resp.status).toBe(503);
    expect(await resp.json()).toEqual({ error: "nest_offline" });
    expect(resp.headers.get("X-Pairnets-Router")).toBe("nest_offline");
  });
});

describe("router deploys", () => {
  it("send the full list of relayed servers with a tunnel, every time, and count", async () => {
    const { h, b } = await setup();
    await linkNest(h, b); // a nest on its own domain has no link in the router
    const one = await addServer(h, b);
    const two = await addServer(h, b, { ip: "198.51.100.41" });
    const expected = [one, two]
      .map((n) => ({ type: "vpc_network", name: `N_${n.nestId}`, tunnel_id: n.tunnelId }))
      .sort((x, y) => x.name.localeCompare(y.name));
    expect(h.cf.routerBindings).toEqual(expected);
    expect(h.cf.routerDeploys).toHaveLength(2);
    expect(h.cf.routerDeploys[0]).toEqual([{ type: "vpc_network", name: `N_${one.nestId}`, tunnel_id: one.tunnelId }]);
    expect(await h.query("SELECT version, lease_id, lease_until, deployed_at, last_error FROM router_state")).toEqual([
      { version: 2, lease_id: null, lease_until: null, deployed_at: h.clock.now - 3, last_error: null },
    ]);
    expect((await h.query<{ routed_version: number }>("SELECT routed_version FROM nests WHERE mode = 'relay' ORDER BY routed_version")).map((r) => r.routed_version)).toEqual([2, 2]);
    // The PATCH is multipart with the one part "settings" and goes to the router's script settings.
    const patch = h.net.calls.filter((c) => c.method === "PATCH").at(-1)!;
    expect(patch.url).toBe("https://api.cloudflare.com/client/v4/accounts/test-account/workers/scripts/pairnets-router/settings");
    expect(patch.headers.get("content-type")).toMatch(/^multipart\/form-data; boundary=/);
    expect(patch.body).toContain('name="settings"');
    expect(patch.headers.get("authorization")).toBe("Bearer test-cf-api-token");
  });

  it("a deploy in progress for less than 2 minutes makes the next one stand aside; the cron catches up", async () => {
    const { h, b } = await setup();
    await h.env.DB.prepare("UPDATE router_state SET lease_id = 'someone-else', lease_until = ?1").bind(h.clock.now + 120).run();
    await h.env.DB.prepare("INSERT OR IGNORE INTO router_state (id, version, lease_id, lease_until) VALUES (1, 0, 'someone-else', ?1)").bind(h.clock.now + 120).run();
    const nest = await addServer(h, b);
    expect(h.cf.routerDeploys).toHaveLength(0);
    expect(await h.query("SELECT routed_version FROM nests")).toEqual([{ routed_version: null }]);
    expect((await runRouter(h.env, h.clock.now, h.net.fetch)).busy).toBe(true);
    // The installer waits: until the router has the link, the relay says the server is not connected.
    expect((await h.client().call("GET", `/n/${nest.nestId}/api/health`)).json.error).toBe("nest_offline");

    h.advance(120);
    await h.scheduled();
    expect(h.cf.routerBindings.map((x) => x.name)).toEqual([`N_${nest.nestId}`]);
    expect(await h.query("SELECT routed_version, status FROM nests")).toEqual([{ routed_version: 1, status: "pending" }]);
    expect((await h.client().call("GET", `/n/${nest.nestId}/api/health`)).text).toBe("ok");
  });

  it("a server approved while a deploy goes out gets a second round of that deploy", async () => {
    const { h, b } = await setup();
    const second = await startServer(h, "second", "198.51.100.41");
    let secondApproval = 0;
    h.cf.duringRouterPatch = async () => {
      // This approval's own deploy finds the lease taken and stands aside.
      secondApproval = (await b.call("POST", "/v1/servers/approve", { body: { userCode: second.userCode, approve: true } })).status;
    };
    await addServer(h, b);
    expect(secondApproval).toBe(200);
    expect(h.cf.routerDeploys.map((d) => d.length)).toEqual([1, 2]);
    expect(await h.query("SELECT routed_version FROM nests ORDER BY routed_version")).toEqual([{ routed_version: 2 }, { routed_version: 2 }]);
  });

  it("a failed deploy keeps the error (never the token) and leaves the server for the cron", async () => {
    const { h, b } = await setup();
    h.cf.failing.add("router");
    const nest = await addServer(h, b);
    expect(await h.query("SELECT routed_version FROM nests")).toEqual([{ routed_version: null }]);
    const state = await h.query<{ last_error: string; lease_id: string | null }>("SELECT last_error, lease_id FROM router_state");
    expect(state).toEqual([{ last_error: "router: Cloudflare answered 500 (10000)", lease_id: null }]);
    expect(state[0].last_error).not.toContain(h.env.CF_API_TOKEN);
    h.cf.failing.clear();
    h.advance(1800); // within the hour a new server has to connect in
    await h.scheduled();
    expect(h.cf.routerBindings.map((x) => x.name)).toEqual([`N_${nest.nestId}`]);
    expect(await h.query("SELECT last_error FROM router_state")).toEqual([{ last_error: null }]);
  });

  it("the relay puts back a router that lost its links (a code deploy with wrangler), at most once a minute", async () => {
    const { h, b } = await setup();
    const nest = await addServer(h, b);
    const app = h.client();
    expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).text).toBe("ok");
    // `wrangler deploy` of cloud/router: the code is new and the bindings are gone.
    h.cf.routerBindings = [];
    // Right after a deploy the router may just not be live yet: no redeploy within 2 minutes of the last one.
    expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).json.error).toBe("nest_offline");
    expect(h.cf.routerDeploys).toHaveLength(1);
    h.advance(121);
    expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).json.error).toBe("nest_offline");
    expect(h.cf.routerDeploys).toHaveLength(2);
    expect((await app.call("GET", `/n/${nest.nestId}/api/health`)).text).toBe("ok");

    // Lost again at once: the next try waits for the next minute (and the 2 minutes since the last deploy).
    h.cf.routerBindings = [];
    h.advance(121);
    await app.call("GET", `/n/${nest.nestId}/api/health`);
    expect(h.cf.routerDeploys).toHaveLength(3);
    h.cf.routerBindings = [];
    await app.call("GET", `/n/${nest.nestId}/api/health`);
    expect(h.cf.routerDeploys).toHaveLength(3);
  });

  it("removing a server: the router drops its link first, then its tunnel goes (connections first), then its row", async () => {
    const { h, b } = await setup();
    const keep = await addServer(h, b, { label: "keep" });
    const gone = await addServer(h, b, { label: "gone", ip: "198.51.100.41" });
    const before = h.net.calls.length;
    expect((await b.call("DELETE", `/v1/servers/${gone.nestId}`)).status).toBe(204);
    const calls = h.net.calls.slice(before).filter((c) => c.url.startsWith("https://api.cloudflare.com/"));
    expect(calls.map((c) => `${c.method} ${new URL(c.url).pathname.replace("/client/v4/accounts/test-account", "")}`)).toEqual([
      "PATCH /workers/scripts/pairnets-router/settings",
      `DELETE /cfd_tunnel/${gone.tunnelId}/connections`,
      `DELETE /cfd_tunnel/${gone.tunnelId}`,
    ]);
    expect(h.cf.routerBindings.map((x) => x.name)).toEqual([`N_${keep.nestId}`]);
    expect([...h.cf.tunnels.keys()]).toEqual([keep.tunnelId]);
    expect((await h.query<{ id: string }>("SELECT id FROM nests")).map((r) => r.id)).toEqual([keep.nestId]);
  });

  it("a removal while another deploy holds the lease waits for that deploy's next round or the cron", async () => {
    const { h, b } = await setup();
    const nest = await addServer(h, b);
    await h.env.DB.prepare("UPDATE router_state SET lease_id = 'someone-else', lease_until = ?1").bind(h.clock.now + 120).run();
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}`)).status).toBe(204);
    expect(await h.query("SELECT status FROM nests")).toEqual([{ status: "broken" }]);
    expect(h.cf.tunnels.size).toBe(1);
    // Already out of the relay and the account's list.
    expect((await h.client().call("GET", `/n/${nest.nestId}/api/health`)).json.error).toBe("nest_unknown");
    expect((await b.call("GET", "/v1/servers")).json).toEqual([]);
    h.advance(120);
    await h.scheduled();
    expect(h.cf.routerBindings).toEqual([]);
    expect(h.cf.tunnels.size).toBe(0);
    expect(await h.query("SELECT * FROM nests")).toHaveLength(0);
  });
});
