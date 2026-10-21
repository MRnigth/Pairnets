// Linked nests and shared-nest offers (RELAY.md section 11; docs/DECISIONS.md, "4 nests per account, and linked nests"):
// an invite for a nest, the friend picking which of their nests joins, the key going from one nest to the other, a nest
// that is offline meanwhile, changing and ending a link from either side, the refusals, and the offers a friend's own
// nests are told about.

import { describe, expect, it } from "vitest";
import type { FakeNestServer } from "./fake-relay";
import { addServer, type Browser, Harness, type Res } from "./helpers";

async function setup() {
  const h = await Harness.create();
  const b = h.browser();
  const ownerId = await b.signInByEmail("you@example.com");
  expect((await b.call("PATCH", "/v1/me", { body: { username: "Jonas Lund" } })).status).toBe(200);
  const nest = await addServer(h, b, { label: "Home nest" });
  const anna = h.browser("203.0.113.40");
  const annaId = await anna.signInByEmail("anna.berg@example.com");
  expect((await anna.call("PATCH", "/v1/me", { body: { username: "Anna Berg" } })).status).toBe(200);
  const annaNest = await addServer(h, anna, { label: "Anna's Home", ip: "198.51.100.61" });
  return { h, b, ownerId, nest, anna, annaId, annaNest };
}

function tokenOf(link: string): string {
  return new URL(link).searchParams.get("t")!;
}

async function inviteNest(b: Browser, nest: FakeNestServer, body: Record<string, unknown> = { kind: "nest", access: "view", folders: ["Photos"] }): Promise<Res> {
  return b.call("POST", `/v1/servers/${nest.nestId}/invites`, { body });
}

/** Invite for a nest, Anna asks with her nest, the owner lets her in. */
async function link(b: Browser, nest: FakeNestServer, anna: Browser, annaNest: FakeNestServer, body?: Record<string, unknown>): Promise<Res> {
  const inv = await inviteNest(b, nest, body);
  expect(inv.status, inv.text).toBe(201);
  expect(inv.json.kind).toBe("nest");
  const asked = await anna.call("POST", "/v1/invite/request", { body: { t: tokenOf(inv.json.link), nestId: annaNest.nestId } });
  expect(asked.status, asked.text).toBe(200);
  const ok = await b.call("POST", `/v1/servers/${nest.nestId}/invites/${inv.json.id}/approve`);
  expect(ok.status, ok.text).toBe(200);
  return ok;
}

describe("linked nests", () => {
  it("the whole way: Anna's nest gets one key on the owner's nest, with what was shared, and keeps the link", async () => {
    const { b, nest, anna, annaId, annaNest } = await setup();
    const ok = await link(b, nest, anna, annaNest);
    expect(ok.json.link.status).toBe("active");

    const keys = [...nest.devices.values()].filter((d) => d.personId === annaId);
    expect(keys).toHaveLength(1);
    expect(keys[0]).toMatchObject({ name: "Anna's Home (linked nest)", system: "Pairnets nest", access: "view", folders: ["Photos"] });

    const linked = [...annaNest.links.values()];
    expect(linked).toHaveLength(1);
    expect(linked[0]).toMatchObject({ remoteNestId: nest.nestId, deviceId: keys[0].id, key: keys[0].key, access: "view", folders: ["Photos"], ownerName: "Jonas Lund", nestName: "Home nest" });
    expect(linked[0].id).toBe(ok.json.link.id);

    const mine = await b.call("GET", "/v1/links");
    expect(mine.json.outgoing).toHaveLength(1);
    expect(mine.json.incoming).toHaveLength(0);
    expect(mine.json.outgoing[0]).toMatchObject({ status: "active", to: { nestId: annaNest.nestId, nest: "Anna's Home", owner: "Anna Berg" }, access: "view", folders: ["Photos"] });
    const hers = await anna.call("GET", "/v1/links");
    expect(hers.json.incoming[0]).toMatchObject({ from: { nestId: nest.nestId, nest: "Home nest", owner: "Jonas Lund" } });

    expect((await b.call("GET", "/account")).text).toContain("Home nest → Anna Berg&#39;s Anna&#39;s Home");
    expect((await anna.call("GET", "/account")).text).toContain("From Jonas Lund – Home nest");
    // A linked nest isn't offered again as a nest of its own.
    expect(annaNest.offers).toEqual([]);
  });

  it("the invite page asks which of your nests joins; asking needs one of your own", async () => {
    const { h, b, nest, anna, annaNest } = await setup();
    const inv = await inviteNest(b, nest);
    const t = tokenOf(inv.json.link);
    const page = await anna.call("GET", `/invite?t=${t}`);
    expect(page.text).toContain("Link one of your nests with Home nest?");
    expect(page.text).toContain("Which of your nests should join?");
    expect(page.text).toContain(`value="${annaNest.nestId}"`);
    expect(page.text).not.toContain(`value="${nest.nestId}"`);

    expect((await anna.call("POST", "/v1/invite/request", { body: { t } })).json.error).toBe("bad_nest");
    expect((await anna.call("POST", "/v1/invite/request", { body: { t, nestId: nest.nestId } })).json.error).toBe("bad_nest");

    // Someone without a nest of their own is told to set one up first.
    const oskar = h.browser("203.0.113.41");
    await oskar.signInByEmail("oskar@example.com");
    const lone = await oskar.call("GET", `/invite?t=${t}`);
    expect(lone.text).toContain("have a nest of your own yet.");
    expect(lone.text).toMatch(/id="ask" disabled/);
  });

  it("refused when inviting: an encrypted nest, a nest too old, folders that came in through a link, a fifth link", async () => {
    const { h, b, nest } = await setup();
    nest.sealed = true;
    expect((await inviteNest(b, nest, { kind: "nest", access: "view", folders: null })).json.error).toBe("sealed_cant_link");
    nest.sealed = false;

    nest.linksSupported = false;
    expect((await inviteNest(b, nest)).json.error).toBe("nest_too_old");
    nest.linksSupported = true;

    nest.linkedIn = ["From Ida – Work"];
    expect((await inviteNest(b, nest, { kind: "nest", access: "view", folders: null })).json.error).toBe("linked_in");
    expect((await inviteNest(b, nest, { kind: "nest", access: "view", folders: ["From Ida – Work"] })).json.error).toBe("linked_in");
    expect((await inviteNest(b, nest)).status).toBe(201); // Photos is fine
    nest.linkedIn = [];

    expect((await inviteNest(b, nest, { kind: "robot", access: "view" })).json.error).toBe("bad_request");

    for (let i = 0; i < 4; i++) {
      await h.env.DB.prepare("INSERT INTO nest_links (id, from_nest, to_nest, account_id, device_id, status, created_at) VALUES (?1, ?2, ?3, 'acc_x', NULL, 'active', 0)")
        .bind(`lnk_${String(i).repeat(26)}`, nest.nestId, `nst_other${i}`)
        .run();
    }
    expect((await inviteNest(b, nest)).json.error).toBe("too_many_links");
  });

  it("Anna's nest offline when she is let in: no key is left behind, and the hourly sweep finishes the link", async () => {
    const { h, b, nest, anna, annaId, annaNest } = await setup();
    annaNest.running = false;
    const ok = await link(b, nest, anna, annaNest);
    expect(ok.json.link.status).toBe("pending");
    expect([...nest.devices.values()].filter((d) => d.personId === annaId)).toHaveLength(0);

    annaNest.running = true;
    await h.scheduled();
    expect((await b.call("GET", "/v1/links")).json.outgoing[0].status).toBe("active");
    expect(annaNest.links.size).toBe(1);
    expect([...nest.devices.values()].filter((d) => d.personId === annaId)).toHaveLength(1);
  });

  it("an encrypted joining nest is refused, and nothing stays shared", async () => {
    const { b, nest, anna, annaId, annaNest } = await setup();
    annaNest.sealed = true;
    await link(b, nest, anna, annaNest);
    expect((await b.call("GET", "/v1/links")).json.outgoing).toHaveLength(0);
    expect([...nest.devices.values()].filter((d) => d.personId === annaId)).toHaveLength(0);
    expect((await b.call("GET", `/v1/servers/${nest.nestId}/people`)).json.members).toHaveLength(0);
  });

  it("two people who invite each other's whole nest: the second link is refused when it is let in, so nothing goes round", async () => {
    const { b, ownerId, nest, anna, annaId, annaNest } = await setup();
    const whole = { kind: "nest", access: "view", folders: null };
    // Both invites are made and asked for before either is let in, so the check when inviting finds nothing yet.
    const mine = await inviteNest(b, nest, whole);
    const hers = await anna.call("POST", `/v1/servers/${annaNest.nestId}/invites`, { body: whole });
    expect(hers.status, hers.text).toBe(201);
    expect((await anna.call("POST", "/v1/invite/request", { body: { t: tokenOf(mine.json.link), nestId: annaNest.nestId } })).status).toBe(200);
    expect((await b.call("POST", "/v1/invite/request", { body: { t: tokenOf(hers.json.link), nestId: nest.nestId } })).status).toBe(200);

    const first = await b.call("POST", `/v1/servers/${nest.nestId}/invites/${mine.json.id}/approve`);
    expect(first.json.link.status).toBe("active");
    expect(annaNest.linkedIn).toEqual(["From Jonas Lund – Home nest"]);

    const second = await anna.call("POST", `/v1/servers/${annaNest.nestId}/invites/${hers.json.id}/approve`);
    expect(second.status, second.text).toBe(200);
    expect(nest.links.size).toBe(0); // the owner's nest never starts copying Anna's nest back
    expect([...annaNest.devices.values()].filter((d) => d.personId === ownerId)).toHaveLength(0);
    expect((await anna.call("GET", "/v1/links")).json.outgoing).toHaveLength(0);
    expect((await b.call("GET", "/v1/links")).json.outgoing).toHaveLength(1); // the first link stays
    expect([...nest.devices.values()].filter((d) => d.personId === annaId)).toHaveLength(1);
  });

  it("changing what is shared reaches the link; removing Anna ends it on both nests", async () => {
    const { b, nest, anna, annaId, annaNest } = await setup();
    await link(b, nest, anna, annaNest);
    const changed = await b.call("PATCH", `/v1/servers/${nest.nestId}/people/${annaId}`, { body: { access: "edit", folders: ["Photos", "School"] } });
    expect(changed.status, changed.text).toBe(200);
    expect([...annaNest.links.values()][0]).toMatchObject({ access: "edit", folders: ["Photos", "School"] });

    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/people/${annaId}`)).status).toBe(204);
    expect([...annaNest.links.values()][0].key).toBeNull();
    expect([...nest.devices.values()].filter((d) => d.personId === annaId)).toHaveLength(0);
    expect((await b.call("GET", "/v1/links")).json.outgoing).toHaveLength(0);
  });

  it("removing the joining nest takes its key back on the owner's nest", async () => {
    const { b, nest, anna, annaId, annaNest } = await setup();
    await link(b, nest, anna, annaNest);
    expect([...nest.devices.values()].filter((d) => d.personId === annaId)).toHaveLength(1);

    expect((await anna.call("DELETE", `/v1/servers/${annaNest.nestId}`)).status).toBe(204);
    expect([...nest.devices.values()].filter((d) => d.personId === annaId)).toHaveLength(0);
    expect((await b.call("GET", `/v1/servers/${nest.nestId}/people`)).json.members).toHaveLength(0);
    expect((await b.call("GET", "/v1/links")).json.outgoing).toHaveLength(0);
  });

  it("a link ended while the joining nest is off: it is told once it is back, also when the owner's nest goes", async () => {
    const { h, b, nest, anna, annaId, annaNest } = await setup();
    await link(b, nest, anna, annaNest);
    annaNest.running = false;
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/people/${annaId}`)).status).toBe(204);
    expect([...annaNest.links.values()][0].key).not.toBeNull(); // not told yet
    await h.scheduled();
    expect([...annaNest.links.values()][0].key).not.toBeNull();
    annaNest.running = true;
    await h.scheduled();
    expect([...annaNest.links.values()][0].key).toBeNull();
    expect(await h.env.DB.prepare("SELECT COUNT(*) AS n FROM link_ends").first("n")).toBe(0);

    // Linked again, then the owner removes the whole nest while Anna's nest is off.
    await link(b, nest, anna, annaNest);
    const again = [...annaNest.links.values()].find((l) => l.key)!;
    annaNest.running = false;
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}`)).status).toBe(204);
    annaNest.running = true;
    await h.scheduled();
    expect(annaNest.links.get(again.id)!.key).toBeNull();
  });

  it("a key the owner's nest refuses for good ends the link at once instead of waiting in the hourly retries", async () => {
    const { b, nest, anna, annaId, annaNest } = await setup();
    const inv = await inviteNest(b, nest); // chosen folders
    expect((await anna.call("POST", "/v1/invite/request", { body: { t: tokenOf(inv.json.link), nestId: annaNest.nestId } })).status).toBe(200);
    nest.sealed = true; // encrypted meanwhile: it only shares whole now, so the key is refused (400 sealed_whole_only)
    await b.call("POST", `/v1/servers/${nest.nestId}/invites/${inv.json.id}/approve`);
    expect((await b.call("GET", "/v1/links")).json.outgoing).toHaveLength(0);
    expect([...nest.devices.values()].filter((d) => d.personId === annaId)).toHaveLength(0);
    expect(annaNest.links.size).toBe(0);
  });

  it("either owner can unlink; nobody else can", async () => {
    const { h, b, nest, anna, annaId, annaNest } = await setup();
    await link(b, nest, anna, annaNest);
    const id = (await b.call("GET", "/v1/links")).json.outgoing[0].id as string;

    const oskar = h.browser("203.0.113.41");
    await oskar.signInByEmail("oskar@example.com");
    expect((await oskar.call("DELETE", `/v1/links/${id}`)).status).toBe(404);
    expect((await anna.call("DELETE", "/v1/links/lnk_nope")).status).toBe(404);

    expect((await anna.call("DELETE", `/v1/links/${id}`)).status).toBe(204);
    expect([...annaNest.links.values()][0].key).toBeNull();
    expect([...nest.devices.values()].filter((d) => d.personId === annaId)).toHaveLength(0);
    expect((await anna.call("GET", "/v1/links")).json.incoming).toHaveLength(0);
  });
});

describe("shared nests reach the friend's computers (option B)", () => {
  it("a person let in: each of their own nests hears it; removed: it drops out", async () => {
    const { b, nest, anna, annaId, annaNest } = await setup();
    const inv = await b.call("POST", `/v1/servers/${nest.nestId}/invites`, { body: { access: "view", folders: ["Photos"] } });
    await anna.call("POST", "/v1/invite/request", { body: { t: tokenOf(inv.json.link) } });
    await b.call("POST", `/v1/servers/${nest.nestId}/invites/${inv.json.id}/approve`);
    expect(annaNest.offers).toEqual([{ nestId: nest.nestId, nestName: "Home nest", ownerName: "Jonas Lund", access: "view", folders: ["Photos"] }]);

    await b.call("PATCH", `/v1/servers/${nest.nestId}/people/${annaId}`, { body: { access: "edit" } });
    expect(annaNest.offers).toEqual([{ nestId: nest.nestId, nestName: "Home nest", ownerName: "Jonas Lund", access: "edit", folders: ["Photos"] }]);

    await anna.call("DELETE", `/v1/shared/${nest.nestId}`);
    expect(annaNest.offers).toEqual([]);
  });
});
