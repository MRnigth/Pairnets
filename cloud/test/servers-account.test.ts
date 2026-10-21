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
          { id: expect.any(String), name: "Laptop", system: "Windows 11", createdAt: expect.any(String), lastSeen: null, access: "owner", personId: null },
          { id: expect.any(String), name: "Desktop", system: "Windows 11", createdAt: expect.any(String), lastSeen: null, access: "owner", personId: null },
        ],
        lastSeenAt: "2026-10-09T00:00:12Z",
        friends: true,
        sealed: false,
      },
      { id: nas.nestId, label: "nas", status: "pending", online: false, serverVersion: null, freeBytes: null, devices: [], lastSeenAt: null, friends: null, sealed: null },
    ]);
    // The calls were signed and checked by the nest itself.
    expect(soro.requests.filter((q) => q.path.startsWith("/api/relay/")).map((q) => `${q.method} ${q.path}`)).toEqual([
      "POST /api/relay/devices",
      "POST /api/relay/devices",
      "GET /api/relay/status",
      "GET /api/relay/devices",
      // The nest had no name yet, so it is told the label.
      "PUT /api/relay/name",
    ]);
    expect(soro.refused).toEqual([]);
    expect(soro.name).toBe("soro");
    // Once it has it, it is not told again.
    await b.call("GET", "/v1/servers");
    expect(soro.requests.filter((q) => q.method === "PUT")).toHaveLength(1);

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
    // A device removal needs the Origin like every mutation, and a recent sign-in.
    h.advance(3600);
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/devices/${deviceId}`, { origin: null })).status).toBe(403);
    const old = await b.call("DELETE", `/v1/servers/${nest.nestId}/devices/${deviceId}`);
    expect(old.status).toBe(401);
    expect(old.json.error).toBe("reauth_required");
    await b.signInByEmail("you@example.com");
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/devices/${deviceId}`)).status).toBe(404);
  });

  it("renaming a computer: the nest renames it and says what it stored; only the owner, only from the account page", async () => {
    const { h, b, accountId } = await setup();
    const nest = await addServer(h, b);
    const { poll } = await appSignIn(h, b, nest.nestId, "Laptop");
    await appSignIn(h, b, nest.nestId, "Desktop", "198.51.100.81");
    const deviceId = poll.json.device.id as string;
    const path = `/v1/servers/${nest.nestId}/devices/${deviceId}`;

    const r = await b.call("PATCH", path, { body: { name: "  Work laptop " } });
    expect(r.status, r.text).toBe(200);
    expect(r.json).toEqual({ id: deviceId, name: "Work laptop", requested: "Work laptop" });
    expect(nest.devices.get(deviceId)!.name).toBe("Work laptop");
    expect(nest.requests.at(-1)).toMatchObject({ method: "PATCH", path: `/api/relay/devices/${deviceId}`, body: '{"name":"Work laptop"}' });
    expect(nest.refused).toEqual([]);
    const audit = await h.query<{ nest_id: string; detail: string }>("SELECT nest_id, detail FROM audit WHERE event = 'device_renamed' AND account_id = ?1", accountId);
    expect(audit).toEqual([{ nest_id: nest.nestId, detail: '{"to":"Work laptop"}' }]);
    expect((await b.call("GET", "/v1/servers")).json[0].devices.map((d: { name: string }) => d.name)).toEqual(["Work laptop", "Desktop"]);

    // Another computer already has the name: the nest adds " (2)" and the page is told both names.
    const dup = await b.call("PATCH", path, { body: { name: "desktop" } });
    expect(dup.json).toEqual({ id: deviceId, name: "desktop (2)", requested: "desktop" });

    // Empty (or only invisible characters) and too long are refused before the nest is asked.
    const asked = nest.requests.length;
    for (const name of ["", "   ", "\u200b", null]) {
      const bad = await b.call("PATCH", path, { body: { name } });
      expect(bad.status).toBe(400);
      expect(bad.json).toMatchObject({ error: "bad_name", message: "Type a name." });
    }
    expect((await b.call("PATCH", path, { body: {} })).json.message).toBe("Type a name.");
    const long = await b.call("PATCH", path, { body: { name: "x".repeat(65) } });
    expect(long.json).toMatchObject({ error: "bad_name", message: "A name is at most 64 letters." });
    expect((await b.call("PATCH", path, { body: { name: 7 } })).json.error).toBe("bad_request");
    expect(nest.requests.length).toBe(asked);
    expect((await b.call("PATCH", path, { body: { name: "x".repeat(64) } })).json.name).toBe("x".repeat(64));
    expect((await b.call("PATCH", path, { body: { name: "invalid" } })).json.name).toBe("invalid");
    // A name the nest itself refuses.
    const refused = await b.call("PATCH", path, { body: { name: "Unknown" } });
    expect(refused.status).toBe(400);
    expect(refused.json.error).toBe("bad_name");

    // Not the owner, not signed in, no Origin or a foreign one: refused, and the nest is never asked.
    const other = h.browser("203.0.113.70");
    await other.signInByEmail("other@example.com");
    expect((await other.call("PATCH", path, { body: { name: "Mine now" } })).status).toBe(404);
    expect((await h.browser("203.0.113.71").call("PATCH", path, { body: { name: "Mine now" } })).status).toBe(401);
    expect((await b.call("PATCH", path, { body: { name: "Mine now" }, origin: null })).json.error).toBe("bad_origin");
    expect((await b.call("PATCH", path, { body: { name: "Mine now" }, origin: "https://evil.example" })).status).toBe(403);
    expect(nest.devices.get(deviceId)!.name).toBe("invalid");

    // Unknown computers and servers.
    expect((await b.call("PATCH", `/v1/servers/${nest.nestId}/devices/no-such-device`, { body: { name: "A" } })).json.error).toBe("not_found");
    expect((await b.call("PATCH", `/v1/servers/${nest.nestId}/devices/${"x".repeat(65)}`, { body: { name: "A" } })).status).toBe(404);
    expect((await b.call("PATCH", `/v1/servers/nst_testnest000000000000000001/devices/${deviceId}`, { body: { name: "A" } })).status).toBe(404);
    // No recent sign-in needed.
    h.advance(3600);
    expect((await b.call("PATCH", path, { body: { name: "Later" } })).status).toBe(200);
  });

  it("renaming a computer on an offline nest is 503, and on a nest from before renames 409 nest_too_old", async () => {
    const { h, b } = await setup();
    const nest = await addServer(h, b);
    const { poll } = await appSignIn(h, b, nest.nestId);
    const path = `/v1/servers/${nest.nestId}/devices/${poll.json.device.id as string}`;

    nest.running = false;
    const offline = await b.call("PATCH", path, { body: { name: "Work" } });
    expect(offline.status).toBe(503);
    expect(offline.json.error).toBe("nest_offline");
    h.world.linkFailure = "502";
    expect((await b.call("PATCH", path, { body: { name: "Work" } })).status).toBe(503);
    nest.running = true;

    nest.old = true;
    const old = await b.call("PATCH", path, { body: { name: "Work" } });
    expect(old.status).toBe(409);
    expect(old.json).toEqual({ error: "nest_too_old", message: "Update your nest to rename computers here." });
    expect(nest.devices.get(poll.json.device.id as string)!.name).toBe("Laptop");
    expect(await h.query("SELECT * FROM audit WHERE event = 'device_renamed'")).toHaveLength(0);
  });

  it("renaming a nest: the label changes at once and the nest is told; offline or older nests are fine", async () => {
    const { h, b, accountId } = await setup();
    const nest = await addServer(h, b, { label: "soro" });
    const r = await b.call("PATCH", `/v1/servers/${nest.nestId}`, { body: { label: " Basement " } });
    expect(r.status, r.text).toBe(200);
    expect(r.json).toEqual({ id: nest.nestId, label: "Basement" });
    expect(nest.name).toBe("Basement");
    expect(nest.requests.at(-1)).toMatchObject({ method: "PUT", path: "/api/relay/name", body: '{"name":"Basement"}' });
    expect((await b.call("GET", "/v1/servers")).json[0].label).toBe("Basement");
    expect((await b.call("GET", "/account")).text).toContain('<strong data-field="label">Basement</strong>');
    const audit = await h.query<{ detail: string }>("SELECT detail FROM audit WHERE event = 'nest_renamed' AND account_id = ?1", accountId);
    expect(audit.map((a) => a.detail)).toEqual(['{"from":"soro","to":"Basement"}']);

    // Offline: saved here all the same; the nest is told when the account page next finds it online.
    nest.running = false;
    expect((await b.call("PATCH", `/v1/servers/${nest.nestId}`, { body: { label: "Attic" } })).json.label).toBe("Attic");
    expect(nest.name).toBe("Basement");
    nest.running = true;
    await b.call("GET", "/v1/servers");
    expect(nest.name).toBe("Attic");

    // A nest from before names: the label still changes, and nothing is sent to it on a list.
    nest.old = true;
    expect((await b.call("PATCH", `/v1/servers/${nest.nestId}`, { body: { label: "Cellar" } })).status).toBe(200);
    const sent = nest.requests.length;
    await b.call("GET", "/v1/servers");
    expect(nest.requests.slice(sent).map((q) => `${q.method} ${q.path}`)).toEqual(["GET /api/relay/status", "GET /api/relay/devices"]);
    expect((await h.query<{ label: string }>("SELECT label FROM nests WHERE id = ?1", nest.nestId))[0].label).toBe("Cellar");

    expect((await b.call("PATCH", `/v1/servers/${nest.nestId}`, { body: { label: "" } })).json).toMatchObject({ error: "bad_name", message: "Type a name." });
    expect((await b.call("PATCH", `/v1/servers/${nest.nestId}`, { body: { label: "x".repeat(65) } })).status).toBe(400);
    expect((await b.call("PATCH", `/v1/servers/${nest.nestId}`, { body: { label: "Mine" }, origin: null })).status).toBe(403);
    const other = h.browser("203.0.113.70");
    await other.signInByEmail("other@example.com");
    expect((await other.call("PATCH", `/v1/servers/${nest.nestId}`, { body: { label: "Mine" } })).status).toBe(404);
    expect((await b.call("PATCH", "/v1/servers/garbage", { body: { label: "Mine" } })).status).toBe(404);
    // An own-domain nest is not a relayed server.
    const own = await linkNest(h, b);
    expect((await b.call("PATCH", `/v1/servers/${own.nestId}`, { body: { label: "Mine" } })).status).toBe(404);
    // Its own rename (the page's "Rename nest" in that list) is logged too.
    expect((await b.call("PATCH", `/v1/nests/${own.nestId}`, { body: { label: "Office" } })).json.label).toBe("Office");
    expect((await h.query<{ detail: string }>("SELECT detail FROM audit WHERE event = 'nest_renamed' AND nest_id = ?1", own.nestId))[0].detail).toContain('"to":"Office"');
    expect((await h.query<{ label: string }>("SELECT label FROM nests WHERE id = ?1", nest.nestId))[0].label).toBe("Cellar");
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
    expect(page.text).toContain('<strong data-field="label">Basement &lt;1&gt;</strong>');
    expect(page.text).toContain('data-action="remove-server">Remove this nest from my account</button>');
    expect(page.text).toContain("Nests on their own address");
    expect(page.text).toContain("https://nest.example.com");
    expect(page.text).toContain(`<script src="${assetUrl("account.js")}" defer></script>`);
    const script = (await b.call("GET", "/assets/account.js")).text;
    for (const piece of ['"/v1/servers"', '"/v1/servers/" + id + "/devices/"', "remove-server", "remove-device"]) expect(script).toContain(piece);
    // Renaming: a button by each nest's name, one by each computer's Remove, and the note for an offline nest.
    expect(page.text).toContain(
      '<strong data-field="label">Basement &lt;1&gt;</strong> <span class="pill" data-field="online">checking</span> <button type="button" class="secondary small" data-action="rename-server">Rename nest</button>',
    );
    expect(page.text).toContain(`data-action="rename">Rename nest</button>`);
    expect(page.text).toContain("Your computers can be renamed when your nest is connected again. The nest's own name can be changed any time.");
    for (const piece of [
      '"rename-device"',
      '"Rename"',
      '"/v1/servers/" + id, SOON, SAVED',
      '"/v1/nests/" + id',
      '"New name for this " + what',
      "Type a name.",
      "Saved. Your other computers show the new name within a few seconds.",
      "Rename it again to pick a different name.",
      "input.maxLength = MAX_NAME",
    ]) {
      expect(script).toContain(piece);
    }
    expect(script).not.toContain("innerHTML");
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
