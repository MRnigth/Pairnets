import { describe, expect, it } from "vitest";
import { BETA_TERMS_VERSION } from "../src/routes/beta";
import { BETA_MIN_APP } from "../src/formats";
import { DAY } from "../src/ratelimit";
import type { FakeNestServer } from "./fake-relay";
import { addServer, appSignIn, type Browser, Harness } from "./helpers";

const ADMIN = "boss@example.com";
const JOIN = { acceptTerms: true, termsVersion: BETA_TERMS_VERSION, channels: ["public-beta"] };
const BOTH = { ...JOIN, channels: ["public-beta", "developer-beta"] };

/** A signed-in owner with one connected nest and one computer on it. */
async function setup(env: Parameters<typeof Harness.create>[0] = {}) {
  const h = await Harness.create({ ADMIN_EMAILS: ADMIN, ...env });
  const b = h.browser();
  const accountId = await b.signInByEmail("you@example.com");
  const nest = await addServer(h, b, { label: "Home nest" });
  const { poll } = await appSignIn(h, b, nest.nestId, "StudyPC");
  const deviceId = poll.json.device.id as string;
  expect((await b.call("GET", "/v1/servers")).json[0].status).toBe("active");
  return { h, b, accountId, nest, deviceId };
}

/** The beta calls the nest received, as "METHOD path body". */
function betaCalls(nest: FakeNestServer): string[] {
  return nest.requests.filter((q) => q.path.includes("beta")).map((q) => `${q.method} ${q.path} ${q.body}`);
}

async function member(h: Harness, accountId: string) {
  return (await h.query("SELECT * FROM beta_members WHERE account_id = ?1", accountId))[0];
}

const NOT_MEMBER = {
  member: false,
  joinedAt: null,
  termsVersion: null,
  termsCurrent: BETA_TERMS_VERSION,
  channels: [],
  requested: [],
  pending: [],
  blocked: false,
  blockedSignup: [],
  autoApprove: true,
  needsNewTerms: false,
};

describe("signing up for and leaving the beta program", () => {
  it("GET /v1/beta: not a member until signed up; a sign-up needs the tick, today's terms and at least one known beta", async () => {
    const { h, b, accountId } = await setup();
    expect((await b.call("GET", "/v1/beta")).json).toEqual(NOT_MEMBER);
    expect((await h.browser("203.0.113.99").call("GET", "/v1/beta")).status).toBe(401);
    expect((await b.call("POST", "/v1/beta/join", { body: { termsVersion: BETA_TERMS_VERSION } })).status).toBe(400);
    expect((await b.call("POST", "/v1/beta/join", { body: { acceptTerms: "yes", termsVersion: BETA_TERMS_VERSION } })).status).toBe(400);
    const stale = await b.call("POST", "/v1/beta/join", { body: { acceptTerms: true, termsVersion: "2026-01-01" } });
    expect(stale.status).toBe(400);
    expect(stale.json.error).toBe("terms_changed");
    for (const channels of [undefined, [], ["nightly"], ["public-beta", "stable"], "public-beta"]) {
      expect((await b.call("POST", "/v1/beta/join", { body: { ...JOIN, channels } })).status, JSON.stringify(channels)).toBe(400);
    }
    expect(await member(h, accountId)).toBeUndefined();
    // Like every other change, it needs our Origin.
    expect((await b.call("POST", "/v1/beta/join", { body: JOIN, origin: "https://evil.example" })).status).toBe(403);

    const joined = await b.call("POST", "/v1/beta/join", { body: JOIN });
    expect(joined.status, joined.text).toBe(200);
    expect(joined.json).toEqual({
      member: true,
      joinedAt: new Date(h.clock.now * 1000).toISOString().replace(/\.\d{3}Z$/, "Z"),
      termsVersion: BETA_TERMS_VERSION,
      termsCurrent: BETA_TERMS_VERSION,
      channels: ["public-beta"],
      requested: ["public-beta"],
      pending: [],
      blocked: false,
      blockedSignup: [],
      autoApprove: true,
      needsNewTerms: false,
    });
    expect(await member(h, accountId)).toMatchObject({
      left_at: null,
      terms_version: BETA_TERMS_VERSION,
      public_allowed: 1,
      developer_allowed: 0,
      public_requested_at: h.clock.now,
      developer_requested_at: null,
      blocked_at: null,
      rev: 1,
    });
  });

  it("signing up for both betas approves both at once; the nest may then offer both", async () => {
    const { h, b, accountId, nest } = await setup();
    expect((await b.call("POST", "/v1/beta/join", { body: BOTH })).json).toMatchObject({ channels: ["public-beta", "developer-beta"], pending: [] });
    expect(await member(h, accountId)).toMatchObject({ public_allowed: 1, developer_allowed: 1, developer_requested_at: h.clock.now });
    expect(nest.beta).toMatchObject({ member: true, channels: ["public-beta", "developer-beta"] });
  });

  it("signing up later for the other beta adds it and keeps the sign-up date", async () => {
    const { h, b, accountId, nest } = await setup();
    await b.call("POST", "/v1/beta/join", { body: JOIN });
    const joinedAt = h.clock.now;
    h.advance(3600);
    const more = await b.call("POST", "/v1/beta/join", { body: { ...JOIN, channels: ["developer-beta"] } });
    expect(more.json).toMatchObject({ requested: ["public-beta", "developer-beta"], channels: ["public-beta", "developer-beta"] });
    expect(await member(h, accountId)).toMatchObject({ joined_at: joinedAt, public_requested_at: joinedAt, developer_requested_at: h.clock.now, rev: 2 });
    expect(nest.beta).toMatchObject({ channels: ["public-beta", "developer-beta"], rev: 2 });
  });

  it("joining tells the owner's nest at once, signed, and notes the rev it has", async () => {
    const { h, b, nest } = await setup();
    await b.call("POST", "/v1/beta/join", { body: JOIN });
    expect(nest.beta).toEqual({ member: true, termsVersion: BETA_TERMS_VERSION, channels: ["public-beta"], rev: 1 });
    expect(nest.refused).toEqual([]);
    expect((await h.query("SELECT beta_rev_pushed FROM nests"))[0]).toEqual({ beta_rev_pushed: 1 });
  });

  it("with BETA_AUTO_APPROVE off, a sign-up waits for approval and the page says so", async () => {
    const { h, b, accountId, nest } = await setup({ BETA_AUTO_APPROVE: "0" });
    const page0 = (await b.call("GET", "/account")).text;
    expect(page0).toContain("Sign-ups are looked at by hand. The maker of Pairnets can see who is in the beta and which of their computers use it.");
    const r = await b.call("POST", "/v1/beta/join", { body: BOTH });
    expect(r.json).toMatchObject({ member: true, channels: [], requested: ["public-beta", "developer-beta"], pending: ["public-beta", "developer-beta"], autoApprove: false });
    expect(await member(h, accountId)).toMatchObject({ public_allowed: 0, developer_allowed: 0 });
    expect(nest.beta).toMatchObject({ member: true, channels: [] });
    const page = (await b.call("GET", "/account")).text;
    expect(page).toContain("Public beta: waiting for approval.");
    expect(page).toContain("Developer beta: waiting for approval.");
    expect(page).toContain("Signed up on 09.10.2026 for Public beta and Developer beta. Beta terms of 26 October 2026.");
  });

  it("leaving: the nest is told, own computers go back to Off (friends' are left alone); joining again starts afresh", async () => {
    const { h, b, accountId, nest, deviceId } = await setup();
    await b.call("POST", "/v1/beta/join", { body: JOIN });
    expect((await b.call("PUT", `/v1/servers/${nest.nestId}/devices/${deviceId}/beta`, { body: { channel: "public-beta" } })).status).toBe(200);
    // A friend's computer on the same nest, on a beta of its own owner's choosing.
    nest.devices.set("devF", { ...nest.devices.get(deviceId)!, id: "devF", key: "pn_friend", access: "view", personId: "acc_testacct000000000000000009", beta: { channel: "public-beta", at: "x", by: "app" } });

    h.advance(60);
    const left = await b.call("POST", "/v1/beta/leave");
    expect(left.status).toBe(200);
    expect(left.json).toMatchObject({ member: false, channels: [] });
    expect(await member(h, accountId)).toMatchObject({ left_at: h.clock.now, rev: 2 });
    expect(nest.beta).toEqual({ member: false, termsVersion: BETA_TERMS_VERSION, channels: [], rev: 2 });
    expect(nest.devices.get(deviceId)!.beta).toMatchObject({ channel: "off", by: "web" });
    expect(nest.devices.get("devF")!.beta).toMatchObject({ channel: "public-beta", by: "app" });
    // Leaving twice changes nothing.
    expect((await b.call("POST", "/v1/beta/leave")).status).toBe(200);
    expect((await member(h, accountId)).rev).toBe(2);

    h.advance(3600);
    const again = await b.call("POST", "/v1/beta/join", { body: JOIN });
    expect(again.json.member).toBe(true);
    expect(await member(h, accountId)).toMatchObject({ left_at: null, joined_at: h.clock.now, rev: 3 });
    expect(nest.beta).toMatchObject({ member: true, rev: 3 });
  });

  it("new terms: a member on older ones gets no channels until agreeing again (and keeps the joined date)", async () => {
    const { h, b, accountId } = await setup();
    await b.call("POST", "/v1/beta/join", { body: JOIN });
    const joinedAt = (await member(h, accountId)).joined_at;
    await h.env.DB.prepare("UPDATE beta_members SET terms_version = '2026-01-01'").run();
    expect((await b.call("GET", "/v1/beta")).json).toMatchObject({ member: true, termsVersion: "2026-01-01", channels: [], needsNewTerms: true });
    const page = (await b.call("GET", "/account")).text;
    expect(page).toContain("Agree to the new beta terms");
    expect(page).not.toContain('id="beta-list"');
    h.advance(60);
    expect((await b.call("POST", "/v1/beta/join", { body: JOIN })).json).toMatchObject({ needsNewTerms: false, channels: ["public-beta"] });
    expect((await member(h, accountId)).joined_at).toBe(joinedAt);
  });

  it("deleting the account deletes its membership", async () => {
    const { h, b } = await setup();
    await b.call("POST", "/v1/beta/join", { body: JOIN });
    await b.signInByEmail("you@example.com"); // deleting needs a recent sign-in
    expect((await b.call("DELETE", "/v1/me")).status).toBe(204);
    expect(await h.query("SELECT * FROM beta_members")).toEqual([]);
  });
});

describe("a computer's beta choice from the account page", () => {
  const put = (b: Browser, nestId: string, deviceId: string, channel: unknown) => b.call("PUT", `/v1/servers/${nestId}/devices/${deviceId}/beta`, { body: { channel } });

  it("the nest sets it; GET /v1/servers then shows it with the computer's app version", async () => {
    const { b, nest, deviceId } = await setup();
    nest.devices.get(deviceId)!.app = "1.0.61.8 Public beta 3";
    await b.call("POST", "/v1/beta/join", { body: JOIN });
    const r = await put(b, nest.nestId, deviceId, "public-beta");
    expect(r.status, r.text).toBe(200);
    expect(r.json).toEqual({ id: deviceId, channel: "public-beta", at: expect.any(String), by: "web" });
    const listed = (await b.call("GET", "/v1/servers")).json[0].devices[0];
    expect(listed).toMatchObject({ id: deviceId, app: "1.0.61.8 Public beta 3", beta: { channel: "public-beta", by: "web" } });
    expect(betaCalls(nest)).toContain(`PUT /api/relay/devices/${deviceId}/beta {"channel":"public-beta"}`);
    expect((await put(b, nest.nestId, deviceId, "off")).json.channel).toBe("off");
  });

  it("only the betas open to the account, only the owner's own nest and computers", async () => {
    const { h, b, nest, deviceId } = await setup();
    // Not a member yet: only Off.
    expect((await put(b, nest.nestId, deviceId, "public-beta")).json.error).toBe("not_allowed");
    expect((await put(b, nest.nestId, deviceId, "off")).status).toBe(200);
    await b.call("POST", "/v1/beta/join", { body: JOIN });
    expect((await put(b, nest.nestId, deviceId, "developer-beta")).status).toBe(409);
    expect((await put(b, nest.nestId, deviceId, "stable")).status).toBe(400);
    expect((await put(b, nest.nestId, deviceId, null)).status).toBe(400);
    expect((await put(b, nest.nestId, "dev999", "off")).status).toBe(404);
    // A friend's computer is theirs to choose.
    nest.devices.set("devF", { ...nest.devices.get(deviceId)!, id: "devF", key: "pn_friend", access: "view", personId: "acc_testacct000000000000000009" });
    const friend = await put(b, nest.nestId, "devF", "public-beta");
    expect(friend.status).toBe(409);
    expect(friend.json.error).toBe("not_owner_device");
    // Another account's nest answers like an unknown one.
    const other = h.browser("203.0.113.70");
    await other.signInByEmail("other@example.com");
    await other.call("POST", "/v1/beta/join", { body: JOIN });
    expect((await put(other, nest.nestId, deviceId, "public-beta")).status).toBe(404);
    expect(nest.devices.get(deviceId)!.beta).toMatchObject({ channel: "off" });
  });

  it("an offline nest is 503 nest_offline; a nest from before betas is 409 nest_too_old", async () => {
    const { b, nest, deviceId } = await setup();
    await b.call("POST", "/v1/beta/join", { body: JOIN });
    nest.running = false;
    const off = await put(b, nest.nestId, deviceId, "public-beta");
    expect(off.status).toBe(503);
    expect(off.json.error).toBe("nest_offline");
    nest.running = true;
    nest.betaSupported = false;
    const before = await put(b, nest.nestId, deviceId, "public-beta");
    expect(before.status).toBe(409);
    expect(before.json.error).toBe("nest_too_old");
    nest.old = true;
    expect((await put(b, nest.nestId, deviceId, "public-beta")).json.error).toBe("nest_too_old");
  });

  it("a nest that missed the join is told it before the choice, so it does not refuse", async () => {
    const { b, nest, deviceId } = await setup();
    nest.running = false;
    await b.call("POST", "/v1/beta/join", { body: JOIN });
    expect(nest.beta).toBeNull();
    nest.running = true;
    expect((await put(b, nest.nestId, deviceId, "public-beta")).status).toBe(200);
    expect(nest.beta).toMatchObject({ member: true, rev: 1 });
  });
});

describe("the hourly cron tells nests that missed a change", () => {
  it("an offline nest is told once it is back, and not again after that", async () => {
    const { h, b, nest } = await setup();
    nest.running = false;
    await b.call("POST", "/v1/beta/join", { body: JOIN });
    expect((await h.query("SELECT beta_rev_pushed FROM nests"))[0]).toEqual({ beta_rev_pushed: 0 });
    nest.running = true;
    h.advance(3600);
    await h.scheduled();
    expect(nest.beta).toMatchObject({ member: true, rev: 1 });
    expect((await h.query("SELECT beta_rev_pushed FROM nests"))[0]).toEqual({ beta_rev_pushed: 1 });
    const told = betaCalls(nest).length;
    h.advance(3600);
    await h.scheduled();
    expect(betaCalls(nest)).toHaveLength(told);
  });

  it("a nest from before betas is asked at most once a day", async () => {
    const { h, b, nest } = await setup();
    nest.betaSupported = false;
    await b.call("POST", "/v1/beta/join", { body: JOIN });
    expect(betaCalls(nest)).toHaveLength(1);
    for (let i = 0; i < 3; i++) {
      h.advance(3600);
      await h.scheduled();
    }
    expect(betaCalls(nest)).toHaveLength(1);
    h.advance(DAY);
    nest.betaSupported = true; // updated meanwhile
    await h.scheduled();
    expect(nest.beta).toMatchObject({ member: true, rev: 1 });
  });
});

describe("the account page's Beta program", () => {
  it("not signed up: the sign-up form (Public beta ticked), the terms and Send sign-up; the page keeps its other hooks", async () => {
    const { b } = await setup();
    const page = (await b.call("GET", "/account")).text;
    const section = page.slice(page.indexOf('<section class="card" id="beta"'));
    expect(section).toContain("<h2>Beta program</h2>");
    expect(section).toContain("<legend>Sign me up for</legend>");
    expect(section).toContain('<input type="checkbox" data-field="beta-pick" value="public-beta" checked><span><b>Public beta</b><span class="muted">Versions that are nearly ready, a few days before everyone else.</span></span>');
    expect(section).toContain('<input type="checkbox" data-field="beta-pick" value="developer-beta"><span><b>Developer beta</b><span class="muted">The newest work, often the same day. Expect rough edges.</span></span>');
    expect(section).toContain('<a href="/beta-terms">Pairnets beta terms</a>');
    expect(section).toMatch(/<button type="button" class="join" data-action="beta-join" data-terms-version="2026-10-26" disabled>Send sign-up<\/button>/);
    expect(section).toContain("Sign-ups are approved at once. The maker of Pairnets can see who is in the beta and which of their computers use it.");
    expect(section).not.toContain("beta-leave");
    // After "Your nest", before the other sections; every older hook is still there.
    expect(page.indexOf('id="servers"')).toBeLessThan(page.indexOf('id="beta"'));
    expect(page.indexOf('id="beta"')).toBeLessThan(page.indexOf('id="sessions"'));
    for (const hook of ['id="username-form"', 'id="servers"', 'id="nests"', 'id="sessions"', 'id="delete-account"', 'data-action="rename-server"', 'data-action="remove-server"']) {
      expect(page).toContain(hook);
    }
    expect(page).not.toMatch(/<script>|style="/);
  });

  it("signed up: the signed-up line, Also sign up for the other beta, the list the script fills, and Leave", async () => {
    const { b } = await setup();
    await b.call("POST", "/v1/beta/join", { body: JOIN });
    const page = (await b.call("GET", "/account")).text;
    expect(page).toContain('<h2>Beta program <span class="pill online">signed up</span></h2>');
    expect(page).toContain('data-channels="public-beta"');
    expect(page).toContain(`data-min-app="${BETA_MIN_APP}"`); // the first app that knows beta updates, one constant
    expect(page).toContain(
      "Signed up on 09.10.2026 for Public beta, approved at once. Beta terms of 26 October 2026. Each computer chooses in its own Settings › Updates › Beta updates; you can change it here too.",
    );
    expect(page).toContain('data-action="beta-add" data-channel="developer-beta" data-terms-version="2026-10-26">Also sign up for Developer beta</button>');
    expect(page).toContain('<ul class="list beta-list" id="beta-list">');
    expect(page).toContain('<button type="button" class="danger" data-action="beta-leave">Leave the beta program</button>');
    expect(page).not.toContain('data-action="beta-join"');

    await b.call("POST", "/v1/beta/join", { body: { ...JOIN, channels: ["developer-beta"] } });
    const both = (await b.call("GET", "/account")).text;
    expect(both).toContain("for Public beta and Developer beta, approved at once.");
    expect(both).toContain('data-channels="public-beta developer-beta"');
    expect(both).not.toContain("beta-add");
  });

  it("account.js knows the hooks and the routes", async () => {
    const { b } = await setup();
    const page = (await b.call("GET", "/account")).text;
    const src = /<script src="([^"]*account\.js[^"]*)"/.exec(page)![1];
    const js = (await b.call("GET", src)).text;
    for (const s of ["beta-join", "beta-add", "beta-pick", "beta-leave", "data-field\", \"beta-channel", "/v1/beta/join", "/v1/beta/leave", "\"/beta\"", "channels: picks", "Can get beta updates after it updates to the newest Pairnets.", "Beta updates off. It keeps "]) {
      expect(js).toContain(s);
    }
    expect(() => new Function(js)).not.toThrow();
  });

  it("/beta-terms goes to the website", async () => {
    const h = await Harness.create();
    expect((await h.browser().call("GET", "/beta-terms")).location).toBe("https://pairnets.app/beta-terms/");
  });
});

/** An admin's browser, signed in. */
async function adminBrowser(h: Harness): Promise<Browser> {
  const admin = h.browser("203.0.113.80");
  await admin.signInByEmail(ADMIN);
  return admin;
}

describe("/admin/beta", () => {
  it("is for admins only: 404 for anyone else, the sign-in page for nobody, and a form post from elsewhere is refused", async () => {
    const { h, b, accountId } = await setup();
    await b.call("POST", "/v1/beta/join", { body: JOIN });
    expect((await b.call("GET", "/admin/beta")).status).toBe(404);
    for (const action of ["approve", "remove", "let-in"]) expect((await b.call("POST", `/admin/beta/${accountId}/${action}`)).status).toBe(404);
    expect((await h.browser("203.0.113.81").call("GET", "/admin/beta")).location).toBe("/login?next=/admin/beta");
    const admin = await adminBrowser(h);
    expect((await admin.call("POST", `/admin/beta/${accountId}/remove`, { origin: "https://evil.example" })).status).toBe(403);
    expect((await admin.call("POST", `/admin/beta/${accountId}/allow-developer`)).status).toBe(404);
    expect((await admin.call("POST", "/admin/beta/acc_bad/remove")).status).toBe(404);
    expect(await member(h, accountId)).toMatchObject({ blocked_at: null, public_allowed: 1 });
  });

  it("who signed up, and who is using it: read live from each member's nest, an offline one named", async () => {
    const { h, b, nest, deviceId } = await setup();
    await b.call("PATCH", "/v1/me", { body: { username: "alex" } });
    await b.call("POST", "/v1/beta/join", { body: BOTH });
    const pc = nest.devices.get(deviceId)!;
    pc.app = "1.0.61.9 Public beta 3";
    pc.beta = { channel: "public-beta", at: "2026-10-09T00:00:00Z", by: "app" };
    pc.lastSeen = "2026-10-09T00:05:00Z";
    // Another computer of the owner's on Off, and a friend's computer on a beta: neither is listed.
    nest.devices.set("devOff", { ...pc, id: "devOff", key: "pn_off", name: "LAPTOP", beta: { channel: "off", at: "x", by: "app" } });
    nest.devices.set("devF", { ...pc, id: "devF", key: "pn_friend", name: "Friend-PC", access: "view", personId: "acc_testacct000000000000000009" });

    const jo = h.browser("203.0.113.71");
    await jo.signInByEmail("jo@example.net");
    const joNest = await addServer(h, jo, { label: "jo nest", ip: "198.51.100.62" });
    await jo.call("GET", "/v1/servers");
    await jo.call("POST", "/v1/beta/join", { body: JOIN });
    joNest.running = false;

    const admin = await adminBrowser(h);
    const page = (await admin.call("GET", "/admin/beta")).text;
    expect(page).toContain("<div><b>2</b>signed up</div>");
    expect(page).toContain("<div><b>1</b>computer on a beta</div>");
    expect(page).toContain("<div><b>0</b>beta reports this week</div>");
    expect(page).toContain("Sign-ups are approved at once (BETA_AUTO_APPROVE is on). Beta terms of 26 October 2026.");
    expect(page).toContain('<div><strong>alex</strong> <span class="muted">you@example.com</span></div>');
    expect(page).toContain("<b>Public beta</b> and <b>Developer beta</b> · signed up today, 00.00 · approved at once · 1 computer on a beta");
    expect(page).toContain('<div><strong>jo</strong> <span class="muted">jo@example.net</span></div>');
    expect(page).toContain("<b>Public beta</b> · signed up today, 00.00 · approved at once · no computer on a beta yet");
    expect(page).toContain(
      '<tr><td>alex</td><td>StudyPC<div class="muted">Windows 11 · Home nest</div></td><td class="ch-public">Public beta</td><td>1.0.61.9 Public beta 3</td><td>today, 00.05</td></tr>',
    );
    expect(page).not.toContain("LAPTOP");
    expect(page).not.toContain("Friend-PC");
    expect(page).toContain("Read from each member's nest when this page opens; nothing about the computers is stored here. jo's nest is offline right now.");
    // Nothing about the computers was kept.
    expect(JSON.stringify(await h.query("SELECT * FROM beta_members"))).not.toContain("StudyPC");
  });

  it("remove: the account loses its betas, its nest is told and its computers go back to Off; let back in approves again", async () => {
    const { h, b, accountId, nest, deviceId } = await setup();
    await b.call("POST", "/v1/beta/join", { body: BOTH });
    expect((await b.call("PUT", `/v1/servers/${nest.nestId}/devices/${deviceId}/beta`, { body: { channel: "developer-beta" } })).status).toBe(200);
    const admin = await adminBrowser(h);

    h.advance(60);
    const removed = await admin.call("POST", `/admin/beta/${accountId}/remove`);
    expect(removed.status).toBe(303);
    expect(removed.location).toBe("/admin/beta");
    expect(await member(h, accountId)).toMatchObject({ blocked_at: h.clock.now, public_allowed: 0, developer_allowed: 0, rev: 2 });
    expect(nest.beta).toMatchObject({ member: false, channels: [], rev: 2 });
    expect(nest.devices.get(deviceId)!.beta).toMatchObject({ channel: "off", by: "web" });
    expect((await h.query("SELECT event FROM audit WHERE event LIKE 'beta_%' ORDER BY rowid")).map((r) => r.event)).toEqual(["beta_signup", "beta_removed"]);
    const list = (await admin.call("GET", "/admin/beta")).text;
    expect(list).toContain("Removed by you on 09.10.2026. A new sign-up from this account waits for you.");
    expect(list).toContain(`action="/admin/beta/${accountId}/let-in"`);
    expect(list).toContain("<div><b>0</b>signed up</div>");

    // The account sees it; its new sign-up waits, even with auto-approve on.
    const page = (await b.call("GET", "/account")).text;
    expect(page).toContain("You were removed from the beta program. A new sign-up waits for approval.");
    expect(page).toContain("Send sign-up");
    expect((await b.call("PUT", `/v1/servers/${nest.nestId}/devices/${deviceId}/beta`, { body: { channel: "public-beta" } })).json.error).toBe("not_allowed");
    h.advance(60);
    const again = await b.call("POST", "/v1/beta/join", { body: JOIN });
    expect(again.json).toMatchObject({ blocked: true, channels: [], blockedSignup: ["public-beta"] });
    expect(await member(h, accountId)).toMatchObject({ public_allowed: 0, developer_allowed: 0 });
    expect((await b.call("GET", "/account")).text).toContain("Your sign-up for Public beta is waiting for approval.");
    expect((await admin.call("GET", "/admin/beta")).text).toContain("Signed up again for <b>Public beta</b>; it waits for you.");

    const back = await admin.call("POST", `/admin/beta/${accountId}/let-in`);
    expect(back.status).toBe(303);
    expect(await member(h, accountId)).toMatchObject({ blocked_at: null, public_allowed: 1, developer_allowed: 1 });
    expect(nest.beta).toMatchObject({ member: true, channels: ["public-beta", "developer-beta"] });
    // Letting in twice changes nothing more.
    const rev = (await member(h, accountId)).rev;
    await admin.call("POST", `/admin/beta/${accountId}/let-in`);
    expect((await member(h, accountId)).rev).toBe(rev);
  });

  it("with auto-approve off: a waiting sign-up has Approve, which reaches the nest; let back in leaves it waiting", async () => {
    const { h, b, accountId, nest } = await setup({ BETA_AUTO_APPROVE: "0" });
    await b.call("POST", "/v1/beta/join", { body: JOIN });
    const admin = await adminBrowser(h);
    const list = (await admin.call("GET", "/admin/beta")).text;
    expect(list).toContain("Sign-ups wait for you to approve them (BETA_AUTO_APPROVE is off).");
    expect(list).toContain("<b>Public beta</b> · signed up today, 00.00 · waiting · no computer on a beta yet");
    expect(list).toContain(`action="/admin/beta/${accountId}/approve"`);

    expect((await admin.call("POST", `/admin/beta/${accountId}/approve`)).status).toBe(303);
    expect(await member(h, accountId)).toMatchObject({ public_allowed: 1, developer_allowed: 0, rev: 2 });
    expect(nest.beta).toMatchObject({ member: true, channels: ["public-beta"], rev: 2 });
    expect((await admin.call("GET", "/admin/beta")).text).toContain("· approved · ");
    // Nothing waiting any more: approving again changes nothing.
    await admin.call("POST", `/admin/beta/${accountId}/approve`);
    expect((await member(h, accountId)).rev).toBe(2);

    // A second beta asked for later waits on its own.
    await b.call("POST", "/v1/beta/join", { body: { ...JOIN, channels: ["developer-beta"] } });
    expect((await b.call("GET", "/v1/beta")).json).toMatchObject({ channels: ["public-beta"], pending: ["developer-beta"] });
    expect((await b.call("GET", "/account")).text).toContain("Developer beta: waiting for approval.");

    await admin.call("POST", `/admin/beta/${accountId}/remove`);
    await admin.call("POST", `/admin/beta/${accountId}/let-in`);
    expect(await member(h, accountId)).toMatchObject({ blocked_at: null, public_allowed: 0, developer_allowed: 0 });
    await admin.call("POST", `/admin/beta/${accountId}/approve`);
    expect(await member(h, accountId)).toMatchObject({ public_allowed: 1, developer_allowed: 1 });
  });

  it("counts beta bug reports of the last week", async () => {
    const h = await Harness.create({ ADMIN_EMAILS: ADMIN });
    const report = { app: "Windows app", version: "1.0.61.9", system: "Windows", report: "Pairnets bug report", note: null };
    await h.client().call("POST", "/v1/bugreport", { body: { ...report, channel: "developer-beta", build: 12 } });
    h.advance(8 * DAY);
    await h.client().call("POST", "/v1/bugreport", { body: { ...report, channel: "public-beta", build: 3 } });
    await h.client().call("POST", "/v1/bugreport", { body: { ...report, channel: "stable" } });
    const admin = await adminBrowser(h);
    expect((await admin.call("GET", "/admin/beta")).text).toContain("<div><b>1</b>beta report this week</div>");
  });
});

describe("bug reports from a beta", () => {
  it("keeps a known channel and the build, drops anything else, and the admin list marks beta reports", async () => {
    const h = await Harness.create({ ADMIN_EMAILS: ADMIN });
    const app = h.client();
    const report = { app: "Windows app", version: "1.0.61.8", system: "Windows", report: "Pairnets bug report", note: null };
    expect((await app.call("POST", "/v1/bugreport", { body: { ...report, channel: "public-beta", build: 3 } })).status).toBe(204);
    expect((await app.call("POST", "/v1/bugreport", { body: { ...report, channel: "nightly", build: "3" } })).status).toBe(204);
    expect((await app.call("POST", "/v1/bugreport", { body: report })).status).toBe(204);
    expect(await h.query("SELECT channel, build FROM bug_reports ORDER BY id")).toEqual([
      { channel: "public-beta", build: 3 },
      { channel: null, build: null },
      { channel: null, build: null },
    ]);
    const admin = h.browser();
    await admin.signInByEmail(ADMIN);
    const list = (await admin.call("GET", "/admin/reports")).text;
    expect(list).toContain('1.0.61.8 · <b class="beta">beta</b> public-beta build 3');
    expect(list.match(/class="beta"/g)).toHaveLength(1);
  });
});
