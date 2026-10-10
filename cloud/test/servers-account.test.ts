import { describe, expect, it } from "vitest";
import { assetUrl } from "../src/assets";
import { addServer, appSignIn, Harness, linkNest } from "./helpers";

async function setup() {
  const h = await Harness.create();
  const b = h.browser();
  const accountId = await b.signInByEmail("you@example.com");
  return { h, b, accountId };
}

describe("the account's servers", () => {
  it("GET /v1/servers: label, status, online, version, free space and computers of each server", async () => {
    const { h, b } = await setup();
    const soro = await addServer(h, b, { label: "soro" });
    const nas = await addServer(h, b, { label: "nas", ip: "198.51.100.41" });
    await appSignIn(h, b, soro.nestId, "Laptop");
    await appSignIn(h, b, soro.nestId, "Desktop", "198.51.100.81");
    nas.running = false;

    const r = await b.call("GET", "/v1/servers");
    expect(r.status).toBe(200);
    expect(r.json).toEqual([
      {
        id: soro.nestId,
        label: "soro",
        status: "active",
        online: true,
        serverVersion: "1.0.48",
        freeBytes: 123456789,
        devices: [
          { id: expect.any(String), name: "Laptop", system: "Windows 11", createdAt: expect.any(String), lastSeen: null },
          { id: expect.any(String), name: "Desktop", system: "Windows 11", createdAt: expect.any(String), lastSeen: null },
        ],
        lastSeenAt: "2026-10-09T00:00:12Z",
      },
      { id: nas.nestId, label: "nas", status: "pending", online: false, serverVersion: null, freeBytes: null, devices: [], lastSeenAt: null },
    ]);
    // The calls were signed and checked by the nest itself.
    expect(soro.requests.filter((q) => q.path.startsWith("/api/relay/")).map((q) => `${q.method} ${q.path}`)).toEqual([
      "POST /api/relay/devices",
      "POST /api/relay/devices",
      "GET /api/relay/status",
      "GET /api/relay/devices",
    ]);
    expect(soro.refused).toEqual([]);

    // Once seen, an offline server still shows its last version and when it was last seen.
    soro.running = false;
    h.advance(60);
    const later = (await b.call("GET", "/v1/servers")).json[0];
    expect(later).toMatchObject({ online: false, serverVersion: "1.0.48", freeBytes: null, devices: [], lastSeenAt: "2026-10-09T00:00:12Z" });
  });

  it("only the account's own servers, only with a browser session", async () => {
    const { h, b } = await setup();
    await addServer(h, b);
    await linkNest(h, b);
    const other = h.browser("203.0.113.70");
    await other.signInByEmail("other@example.com");
    expect((await other.call("GET", "/v1/servers")).json).toEqual([]);
    expect((await h.browser("203.0.113.71").call("GET", "/v1/servers")).status).toBe(401);
    expect((await b.call("GET", "/v1/servers")).json).toHaveLength(1);
  });

  it("removing a computer: the server revokes it; unknown ones are 404, an offline server 503", async () => {
    const { h, b, accountId } = await setup();
    const nest = await addServer(h, b);
    const { poll } = await appSignIn(h, b, nest.nestId);
    const deviceId = poll.json.device.id as string;
    const other = h.browser("203.0.113.70");
    await other.signInByEmail("other@example.com");
    expect((await other.call("DELETE", `/v1/servers/${nest.nestId}/devices/${deviceId}`)).status).toBe(404);
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/devices/no-such-device`)).json.error).toBe("not_found");
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/devices/${"x".repeat(65)}`)).status).toBe(404);
    expect((await b.call("DELETE", `/v1/servers/nst_testnest000000000000000001/devices/${deviceId}`)).status).toBe(404);

    nest.running = false;
    const offline = await b.call("DELETE", `/v1/servers/${nest.nestId}/devices/${deviceId}`);
    expect(offline.status).toBe(503);
    expect(offline.json.error).toBe("nest_offline");
    nest.running = true;

    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/devices/${deviceId}`)).status).toBe(204);
    expect(nest.devices.size).toBe(0);
    expect(nest.requests.at(-1)).toMatchObject({ method: "DELETE", path: `/api/relay/devices/${deviceId}` });
    expect(await h.query("SELECT event FROM audit WHERE event = 'device_removed' AND account_id = ?1", accountId)).toHaveLength(1);
    // A device removal needs the Origin like every mutation, but not a recent sign-in.
    h.advance(3600);
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/devices/${deviceId}`, { origin: null })).status).toBe(403);
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/devices/${deviceId}`)).status).toBe(404);
  });

  it("removing a server needs a recent sign-in; then its link, tunnel and row go, with a notice", async () => {
    const { h, b, accountId } = await setup();
    const nest = await addServer(h, b, { label: "soro" });
    expect((await h.client().call("GET", `/n/${nest.nestId}/api/health`)).text).toBe("ok");
    const other = h.browser("203.0.113.70");
    await other.signInByEmail("other@example.com");
    expect((await other.call("DELETE", `/v1/servers/${nest.nestId}`)).status).toBe(404);
    expect((await b.call("DELETE", "/v1/servers/garbage")).status).toBe(404);

    h.advance(901);
    const old = await b.call("DELETE", `/v1/servers/${nest.nestId}`);
    expect(old.status).toBe(401);
    expect(old.json.error).toBe("reauth_required");
    expect(h.cf.tunnels.size).toBe(1);

    await b.signInByEmail("you@example.com");
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}`)).status).toBe(204);
    expect(h.cf.routerBindings).toEqual([]);
    expect(h.cf.tunnels.size).toBe(0);
    expect(await h.query("SELECT * FROM nests")).toHaveLength(0);
    expect((await b.call("GET", "/v1/servers")).json).toEqual([]);
    expect((await h.client().call("GET", `/n/${nest.nestId}/api/health`)).json.error).toBe("nest_unknown");
    expect(h.mailer.to("you@example.com").some((m) => m.subject === "A nest was removed from your account: soro")).toBe(true);
    expect(await h.query("SELECT event FROM audit WHERE event = 'server_removed' AND account_id = ?1", accountId)).toHaveLength(1);
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}`)).status).toBe(404);
  });

  it("the account page lists the servers for its script, and keeps own-domain nests in their own section", async () => {
    const { h, b } = await setup();
    const empty = await b.call("GET", "/account");
    expect(empty.text).toContain("No nest yet.");
    const nest = await addServer(h, b, { label: "Basement <1>" });
    await linkNest(h, b);
    const page = await b.call("GET", "/account");
    expect(page.status).toBe(200);
    expect(page.text).toContain(`<li class="server" data-server="${nest.nestId}">`);
    expect(page.text).toContain("<strong>Basement &lt;1&gt;</strong>");
    expect(page.text).toContain('data-action="remove-server">Remove this nest from my account</button>');
    expect(page.text).toContain("Nests on their own address");
    expect(page.text).toContain("https://nest.example.com");
    expect(page.text).toContain(`<script src="${assetUrl("account.js")}" defer></script>`);
    const script = (await b.call("GET", "/assets/account.js")).text;
    for (const piece of ['"/v1/servers"', '"/v1/servers/" + id + "/devices/"', "remove-server", "remove-device"]) expect(script).toContain(piece);
    // A removed server leaves the page at once.
    await b.call("DELETE", `/v1/servers/${nest.nestId}`);
    expect((await b.call("GET", "/account")).text).not.toContain(nest.nestId);
  });

  it("deleting the account removes its servers' links and tunnels too", async () => {
    const { h, b } = await setup();
    const mine = await addServer(h, b);
    const other = h.browser("203.0.113.70");
    await other.signInByEmail("other@example.com");
    const theirs = await addServer(h, other, { ip: "198.51.100.41" });
    expect((await b.call("DELETE", "/v1/me")).status).toBe(204);
    expect(h.cf.routerBindings.map((x) => x.name)).toEqual([`N_${theirs.nestId}`]);
    expect([...h.cf.tunnels.keys()]).toEqual([theirs.tunnelId]);
    expect((await h.query<{ id: string }>("SELECT id FROM nests")).map((r) => r.id)).toEqual([theirs.nestId]);
    expect(await h.query("SELECT * FROM server_links WHERE nest_id = ?1", mine.nestId)).toHaveLength(0);
    expect((await h.client().call("GET", `/n/${mine.nestId}/api/health`)).json.error).toBe("nest_unknown");
  });
});
