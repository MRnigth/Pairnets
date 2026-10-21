// Friends on a nest (RELAY.md section 10): invites by link or email, asking to join, the owner letting people in or
// declining, the friend's computers signing in with what was shared, changing and removing friends, leaving, the pages
// (/invite, the account page's People and "Shared with you", the app page's shared choices) and the cron's part.

import { describe, expect, it } from "vitest";
import { assetUrl } from "../src/assets";
import { cleanAccess, cleanFolders, dottedEmail, parseFolders, safeNext } from "../src/formats";
import { INVITE_MAIL_DAILY_LIMIT } from "../src/mail";
import type { FakeNestServer } from "./fake-relay";
import { addServer, appSignIn, type Browser, Harness, linkNest, type Res } from "./helpers";

const OWNER = "you@example.com";
const ANNA = "anna.berg@example.com";
const OSKAR = "oskar.nilsson@example.com";

async function setup() {
  const h = await Harness.create();
  const b = h.browser();
  const ownerId = await b.signInByEmail(OWNER);
  expect((await b.call("PATCH", "/v1/me", { body: { username: "Jonas Lund" } })).status).toBe(200);
  const nest = await addServer(h, b, { label: "Home nest" });
  const anna = h.browser("203.0.113.40");
  const annaId = await anna.signInByEmail(ANNA);
  expect((await anna.call("PATCH", "/v1/me", { body: { username: "Anna Berg" } })).status).toBe(200);
  return { h, b, ownerId, nest, anna, annaId };
}

function tokenOf(link: string): string {
  return new URL(link).searchParams.get("t")!;
}

async function invite(b: Browser, nest: FakeNestServer, body: Record<string, unknown> = { access: "view", folders: ["Photos", "School"] }): Promise<Res> {
  return b.call("POST", `/v1/servers/${nest.nestId}/invites`, { body });
}

/** The whole way in: an invite, the friend asks, the owner lets them in. */
async function letIn(b: Browser, nest: FakeNestServer, friend: Browser, body?: Record<string, unknown>): Promise<{ id: string; t: string }> {
  const inv = await invite(b, nest, body);
  expect(inv.status, inv.text).toBe(201);
  const t = tokenOf(inv.json.link);
  const asked = await friend.call("POST", "/v1/invite/request", { body: { t } });
  expect(asked.status, asked.text).toBe(200);
  const ok = await b.call("POST", `/v1/servers/${nest.nestId}/invites/${inv.json.id}/approve`);
  expect(ok.status, ok.text).toBe(200);
  return { id: inv.json.id, t };
}

describe("friend invites: the formats", () => {
  it("access is view (the default) or edit; folders are null (whole) or 1-20 top-level names", () => {
    expect(cleanAccess(undefined)).toBe("view");
    expect(cleanAccess(null)).toBe("view");
    expect(cleanAccess("edit")).toBe("edit");
    for (const bad of ["owner", "View", 1, true, ""]) expect(cleanAccess(bad)).toBeNull();

    expect(cleanFolders(undefined)).toBeNull();
    expect(cleanFolders(null)).toBeNull();
    expect(cleanFolders(["Photos", "School", "Photos"])).toEqual(["Photos", "School"]);
    expect(cleanFolders(["photos", "Photos"])).toEqual(["photos", "Photos"]); // exact case: two folders
    expect(cleanFolders(["Ünïcode mappe", "x".repeat(255)])).toEqual(["Ünïcode mappe", "x".repeat(255)]);
    for (const bad of [[], "Photos", [""], ["a/b"], ["a\\b"], ["."], [".."], [" Photos"], ["Photos "], ["a​b"], ["a\nb"], [7], ["x".repeat(256)]]) {
      expect(cleanFolders(bad), JSON.stringify(bad)).toBe("invalid");
    }
    expect(cleanFolders(Array.from({ length: 20 }, (_, i) => `F${i}`))).toHaveLength(20);
    expect(cleanFolders(Array.from({ length: 21 }, (_, i) => `F${i}`))).toBe("invalid");
    expect(parseFolders(null)).toBeNull();
    expect(parseFolders('["Photos"]')).toEqual(["Photos"]);
    expect(parseFolders("garbage")).toEqual([]);
    expect(dottedEmail("anna.berg@example.com")).toBe("a•••@example.com");
  });

  it("the sign-in page may come back to an invite, and to the apps' hand-over to the invite form", () => {
    const t = "pni_" + "a".repeat(32);
    expect(safeNext(`/invite?t=${t}`)).toBe(`/invite?t=${t}`);
    expect(safeNext("/invite?t=pni_short")).toBe("/account");
    expect(safeNext(`/invite?t=${t}&x=1`)).toBe("/account");
    const n = "nst_" + "a".repeat(26);
    for (const ok of [`/account?invite=${n}`, `/account?invite=${n}&folders=Photos%2CSchool&access=view`, `/account?invite=${n}&folders=My%20(old)%20files&access=edit`]) {
      expect(safeNext(ok)).toBe(ok);
    }
    for (const bad of ["/account?invite=nst_x", `/account?invite=${n}&next=//evil.example`, `/account?invite=${n}#x`, `/account?x=1`]) expect(safeNext(bad)).toBe("/account");
  });
});

describe("friend invites: making one", () => {
  it("by link: 201 with a link that lasts 7 days, listed as open; the nest was asked whether it can have friends", async () => {
    const { h, b, ownerId, nest } = await setup();
    const r = await invite(b, nest);
    expect(r.status, r.text).toBe(201);
    expect(r.json).toEqual({
      id: expect.stringMatching(/^inv_[0-9a-hjkmnp-tv-z]{26}$/),
      link: expect.stringMatching(/^https:\/\/sync\.pairnets\.app\/invite\?t=pni_[0-9a-hjkmnp-tv-z]{32}$/),
      expiresAt: "2026-10-16T00:00:03Z",
      emailed: false,
    });
    expect(nest.requests.at(-1)).toMatchObject({ method: "GET", path: "/api/relay/status" });
    expect(nest.refused).toEqual([]);
    const rows = await h.query("SELECT nest_id, email, access, folders, created_by, requested_by FROM nest_invites");
    expect(rows).toEqual([{ nest_id: nest.nestId, email: null, access: "view", folders: '["Photos","School"]', created_by: ownerId, requested_by: null }]);
    const people = await b.call("GET", `/v1/servers/${nest.nestId}/people`);
    expect(people.json).toEqual({
      members: [],
      invites: [
        {
          id: r.json.id,
          link: r.json.link,
          email: null,
          access: "view",
          folders: ["Photos", "School"],
          createdAt: "2026-10-09T00:00:03Z",
          expiresAt: "2026-10-16T00:00:03Z",
          status: "open",
          requester: null,
        },
      ],
      max: 10,
    });
    expect((await h.query<{ detail: string }>("SELECT detail FROM audit WHERE event = 'invite_created'"))[0].detail).toBe(
      '{"access":"view","folders":2,"emailed":false,"mailSkipped":false}',
    );
    // The defaults: the whole nest, view only.
    const plain = await invite(b, nest, {});
    expect(plain.status).toBe(201);
    expect((await h.query("SELECT access, folders FROM nest_invites WHERE id = ?1", plain.json.id))[0]).toEqual({ access: "view", folders: null });
  });

  it("by email: the friend gets the invite, from the invites' own small budget; the owner's own address is refused", async () => {
    const { h, b, nest } = await setup();
    const r = await invite(b, nest, { access: "edit", folders: ["Photos"], email: " Anna.Berg@Example.com " });
    expect(r.status, r.text).toBe(201);
    expect(r.json.emailed).toBe(true);
    expect((await h.query("SELECT email FROM nest_invites"))[0]).toEqual({ email: ANNA });
    const mail = h.mailer.to(ANNA).filter((m) => m.subject.includes("invited you"));
    expect(mail).toHaveLength(1);
    expect(mail[0].subject).toBe("Jonas Lund invited you to Home nest on Pairnets");
    expect(mail[0].text).toContain("Jonas Lund wants to share files with you");
    expect(mail[0].text).toContain("Jonas Lund invited you to their nest, Home nest. You'll get the folder Photos, and you can add, change and delete files in it.");
    expect(mail[0].text).toContain(`See the invite: ${r.json.link}`);
    expect(mail[0].text).toContain("The link works once and ends on 16 October.");
    expect(mail[0].text).toContain("If you don't know Jonas Lund, ignore this email and nothing happens.");
    expect(mail[0].text).toContain("Sent because Jonas Lund (y•••@example.com) typed your address in Pairnets. We don't keep it after the invite ends.");
    expect(mail[0].html).toContain(`<a href="${r.json.link}"`);
    expect(mail[0].html).toContain(">See the invite</a>");

    expect((await invite(b, nest, { email: "not an address" })).json).toMatchObject({ error: "bad_request" });
    expect((await invite(b, nest, { email: 7 })).status).toBe(400);
    const own = await invite(b, nest, { email: "YOU@example.com" });
    expect(own.status).toBe(409);
    expect(own.json.error).toBe("own_nest");

    // Five a day for the whole service; then the invite is still made (with no address kept) and the page says so.
    const allBefore = await h.query<{ count: number }>("SELECT count FROM rate_counters WHERE bucket LIKE 'mail-all:%'");
    for (let i = 1; i < INVITE_MAIL_DAILY_LIMIT; i++) {
      const more = await invite(b, nest, { email: `friend${i}@example.com` });
      expect(more.json.emailed, `invite ${i}`).toBe(true);
    }
    const sixth = await invite(b, nest, { email: "late@example.com" });
    expect(sixth.status).toBe(201);
    expect(sixth.json.emailed).toBe(false);
    expect(h.mailer.to("late@example.com")).toHaveLength(0);
    expect((await h.query("SELECT email FROM nest_invites WHERE id = ?1", sixth.json.id))[0]).toEqual({ email: null });
    expect((await h.query<{ detail: string }>("SELECT detail FROM audit WHERE event = 'invite_created' ORDER BY id DESC LIMIT 1"))[0].detail).toContain(
      '"mailSkipped":true',
    );
    // Invites never took from the day's total that sign-ins and notices share.
    expect(await h.query("SELECT count FROM rate_counters WHERE bucket LIKE 'mail-all:%'")).toEqual(allBefore);
    expect((await h.browser("203.0.113.99").call("POST", "/v1/login/email", { body: { email: "someone@example.com", turnstile: "ok" } })).status).toBe(202);
    expect(h.mailer.to("someone@example.com")).toHaveLength(1);
  });

  it("what is shared is checked: view or edit, the whole nest or 1-20 top-level folders", async () => {
    const { h, b, nest } = await setup();
    for (const body of [
      { access: "owner" },
      { access: "admin" },
      { folders: [] },
      { folders: "Photos" },
      { folders: ["a/b"] },
      { folders: [".."] },
      { folders: [7] },
      { folders: ["x".repeat(256)] },
      { folders: Array.from({ length: 21 }, (_, i) => `F${i}`) },
    ]) {
      const r = await invite(b, nest, body);
      expect(r.status, JSON.stringify(body)).toBe(400);
      expect(r.json).toEqual({ error: "bad_access", message: "Choose View only or Can change, and the whole nest or 1 to 20 of its top-level folders." });
    }
    expect((await b.call("POST", `/v1/servers/${nest.nestId}/invites`, { rawBody: "nope" })).json.error).toBe("bad_request");
    expect(await h.query("SELECT * FROM nest_invites")).toHaveLength(0);
    const dup = await invite(b, nest, { folders: ["Photos", "Photos", "School"] });
    expect((await h.query("SELECT folders FROM nest_invites WHERE id = ?1", dup.json.id))[0]).toEqual({ folders: '["Photos","School"]' });
  });

  it("only the owner, from the account page; other nests and accounts answer 404", async () => {
    const { h, b, nest, anna } = await setup();
    expect((await anna.call("POST", `/v1/servers/${nest.nestId}/invites`, { body: {} })).status).toBe(404);
    expect((await h.browser("203.0.113.77").call("POST", `/v1/servers/${nest.nestId}/invites`, { body: {} })).status).toBe(401);
    expect((await b.call("POST", `/v1/servers/${nest.nestId}/invites`, { body: {}, origin: null })).json.error).toBe("bad_origin");
    expect((await b.call("POST", `/v1/servers/${nest.nestId}/invites`, { body: {}, origin: "https://evil.example" })).status).toBe(403);
    expect((await b.call("POST", "/v1/servers/garbage/invites", { body: {} })).status).toBe(404);
    expect((await b.call("POST", "/v1/servers/nst_testnest000000000000000001/invites", { body: {} })).status).toBe(404);
    const own = await linkNest(h, b);
    expect((await b.call("POST", `/v1/servers/${own.nestId}/invites`, { body: {} })).status).toBe(404);
    expect((await anna.call("GET", `/v1/servers/${nest.nestId}/people`)).status).toBe(404);
    expect((await h.browser("203.0.113.78").call("GET", `/v1/servers/${nest.nestId}/people`)).status).toBe(401);
    expect(await h.query("SELECT * FROM nest_invites")).toHaveLength(0);
  });

  it("the nest must be on and new enough; an encrypted nest is shared whole only", async () => {
    const { h, b, nest } = await setup();
    nest.running = false;
    const offline = await invite(b, nest);
    expect(offline.status).toBe(409);
    expect(offline.json).toEqual({ error: "nest_offline", message: "Your nest is not connected right now. Inviting a friend needs it on." });
    nest.running = true;
    nest.friends = false;
    const old = await invite(b, nest);
    expect(old.status).toBe(409);
    expect(old.json).toEqual({ error: "nest_too_old", message: "Update your nest first. Friends need its newest version." });
    nest.old = true;
    expect((await invite(b, nest)).json.error).toBe("nest_too_old");
    nest.old = false;
    nest.friends = true;
    nest.sealed = true;
    const sealed = await invite(b, nest);
    expect(sealed.status).toBe(400);
    expect(sealed.json).toEqual({ error: "sealed_whole_only", message: "An encrypted nest can only be shared whole." });
    expect(await h.query("SELECT * FROM nest_invites")).toHaveLength(0);
    expect((await invite(b, nest, { access: "edit" })).status).toBe(201);
    // The account page learns it from the list: friends and sealed (unknown while the nest is off).
    expect((await b.call("GET", "/v1/servers")).json[0]).toMatchObject({ friends: true, sealed: true });
  });

  it("at most 10 friends per nest, members and open invites together; a cancelled or ended invite frees its place", async () => {
    const { h, b, nest, anna } = await setup();
    await letIn(b, nest, anna);
    const ids: string[] = [];
    for (let i = 0; i < 9; i++) {
      const r = await invite(b, nest, {});
      expect(r.status, `invite ${i}`).toBe(201);
      ids.push(r.json.id);
    }
    const full = await invite(b, nest, {});
    expect(full.status).toBe(409);
    expect(full.json).toEqual({
      error: "too_many_friends",
      message: "A nest can have at most 10 friends, open invites included. Remove someone or cancel an invite first.",
    });
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/invites/${ids[0]}`)).status).toBe(204);
    expect((await invite(b, nest, {})).status).toBe(201);
    expect((await invite(b, nest, {})).status).toBe(409);
    // A week on, the open links have ended: their places are free again (Anna's stays).
    h.advance(7 * 86400 + 1);
    await b.signInByEmail(OWNER);
    for (let i = 0; i < 9; i++) expect((await invite(b, nest, {})).status).toBe(201);
    expect((await invite(b, nest, {})).json.error).toBe("too_many_friends");
    // Another nest has its own ten.
    const other = await addServer(h, b, { label: "Work nest", ip: "198.51.100.41" });
    expect((await invite(b, other, {})).status).toBe(201);
  });

  it("an account makes at most 20 invites a day", async () => {
    const { h, b, nest } = await setup();
    for (let i = 0; i < 20; i++) {
      const r = await invite(b, nest, {});
      expect(r.status, `invite ${i}`).toBe(201);
      expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/invites/${r.json.id}`)).status).toBe(204);
    }
    const limited = await invite(b, nest, {});
    expect(limited.status).toBe(429);
    expect(limited.json.error).toBe("rate_limited");
    expect(limited.headers.get("Retry-After")).toBeTruthy();
    h.advance(86400);
    await b.signInByEmail(OWNER);
    expect((await invite(b, nest, {})).status).toBe(201);
  });
});

describe("friend invites: the invite page and asking to join", () => {
  it("sign in first; then 'Join Home nest?' with what is shared, in the apps' quiet look", async () => {
    const { h, b, nest, anna } = await setup();
    const r = await invite(b, nest);
    const t = tokenOf(r.json.link);
    const out = await h.browser("203.0.113.80").call("GET", `/invite?t=${t}`);
    expect(out.status).toBe(302);
    expect(out.location).toBe(`/login?next=${encodeURIComponent(`/invite?t=${t}`)}`);
    const login = await h.browser("203.0.113.80").call("GET", out.location!);
    expect(login.text).toContain(`data-next="/invite?t=${t}"`);
    // A link that is not an invite's shape goes nowhere special.
    expect((await h.browser("203.0.113.81").call("GET", "/invite?t=<script>")).location).toBe(`/login?next=${encodeURIComponent("/account")}`);

    const page = await anna.call("GET", `/invite?t=${t}`);
    expect(page.status).toBe(200);
    expect(page.text).toContain("<title>Join Home nest? · Pairnets</title>");
    expect(page.text).toContain(`<link rel="stylesheet" href="${assetUrl("app.css")}">`);
    expect(page.text).toContain('<body class="q-page">');
    expect(page.text).toContain(`<script src="${assetUrl("invite.js")}" defer></script>`);
    expect(page.text).not.toMatch(/<script(?![^>]*\bsrc=)[^>]*>/);
    expect(page.text).not.toMatch(/\sstyle=/);
    expect(page.text).toContain(`<div id="invite-request" class="q-col" data-token="${t}">`);
    expect(page.text).toContain('<p class="q-from">An invite from Jonas Lund · y•••@example.com</p>');
    expect(page.text).toContain('<h1 id="screen-ask-title" tabindex="-1">Join Home nest?</h1>');
    expect(page.text).toContain("Jonas Lund shares these folders from their nest with you. Pairnets will keep them in sync on the computers you choose.");
    expect(page.text).toContain("<div><dt>Folders</dt><dd><b>Photos, School</b></dd></div>");
    expect(page.text).toContain('<b>View only</b><span class="q-fact-sub">Your own changes stay on your computer.</span>');
    expect(page.text).toContain(`<dd>${ANNA} · <button type="button" class="q-link" id="not-you">not you?</button></dd>`);
    expect(page.text).toContain('<button type="button" class="q-btn q-primary" id="ask">Ask to join</button>');
    expect(page.text).toContain('<button type="button" class="q-btn q-ghost" id="no-thanks">No thanks</button>');
    expect(page.text).toContain("Jonas Lund sees your name (Anna Berg) and your email, and lets you in. You can leave the nest any time.");
    // The other screens wait, hidden.
    expect(page.text).toContain('<section class="q-screen" id="screen-no" aria-labelledby="screen-no-title" hidden>');
    expect(page.text).toContain('<section class="q-screen" id="screen-waiting" aria-labelledby="screen-waiting-title" hidden>');

    // The whole nest, can change.
    const whole = await invite(b, nest, { access: "edit" });
    const wp = await anna.call("GET", `/invite?t=${tokenOf(whole.json.link)}`);
    expect(wp.text).toContain("Jonas Lund shares their whole nest with you.");
    expect(wp.text).toContain("<div><dt>You get</dt><dd><b>The whole nest</b></dd></div>");
    expect(wp.text).toContain("<b>Can change</b>");
  });

  it("asking: once, the owner is emailed and the nest hears who is waiting; asking again changes nothing", async () => {
    const { h, b, nest, anna, annaId } = await setup();
    const r = await invite(b, nest);
    const t = tokenOf(r.json.link);
    const asked = await anna.call("POST", "/v1/invite/request", { body: { t } });
    expect(asked.status, asked.text).toBe(200);
    expect(asked.json).toEqual({ status: "requested", nest: "Home nest", owner: "Jonas Lund" });
    const row = (await h.query<{ requested_by: string; requested_at: number }>("SELECT requested_by, requested_at FROM nest_invites"))[0];
    expect(row.requested_by).toBe(annaId);
    const mail = h.mailer.to(OWNER).filter((m) => m.subject.includes("wants to join"));
    expect(mail).toHaveLength(1);
    expect(mail[0].subject).toBe("Anna Berg wants to join Home nest");
    expect(mail[0].text).toContain(`Anna Berg (${ANNA}) used your invite link and asks to join your nest Home nest: Photos and School, view only.`);
    expect(mail[0].text).toContain("Nothing is shared until you let them in.");
    expect(mail[0].text).toContain(`https://sync.pairnets.app/account#people-${nest.nestId}`);
    expect(nest.waiting).toEqual([{ id: r.json.id, name: "Anna Berg", email: ANNA, access: "view", folders: ["Photos", "School"], at: expect.any(String) }]);
    expect(nest.refused).toEqual([]);

    // The owner sees who asked.
    const people = await b.call("GET", `/v1/servers/${nest.nestId}/people`);
    expect(people.json.invites[0]).toMatchObject({ status: "requested", requester: { accountId: annaId, name: "Anna Berg", email: ANNA } });

    // Asking again (another tab): the same answer, no second email.
    expect((await anna.call("POST", "/v1/invite/request", { body: { t } })).json).toEqual({ status: "requested", nest: "Home nest", owner: "Jonas Lund" });
    expect(h.mailer.to(OWNER).filter((m) => m.subject.includes("wants to join"))).toHaveLength(1);
    // The page now says it is waiting, and how to get ready.
    const page = await anna.call("GET", `/invite?t=${t}`);
    expect(page.text).toContain("<title>Asked to join Home nest · Pairnets</title>");
    expect(page.text).toContain("Waiting for Jonas Lund to let you in. We'll email you when that happens.");
    expect(page.text).toContain(`Sign in as <b>${ANNA}</b> and pick <b>Home nest · shared by Jonas Lund</b>.`);
    expect(page.text).toContain('href="pairnets://open"');
    expect(page.text).not.toContain("invite.js");

    // Someone else with the same link: it was used.
    const oskar = h.browser("203.0.113.41");
    await oskar.signInByEmail(OSKAR);
    const used = await oskar.call("POST", "/v1/invite/request", { body: { t } });
    expect(used.status).toBe(409);
    expect(used.json).toEqual({ error: "used", message: "Someone else already used this invite. Ask Jonas Lund for a new one." });
    expect((await oskar.call("GET", `/invite?t=${t}`)).text).toContain("This invite was already used");
    expect(nest.waiting).toHaveLength(1);
  });

  it("asking needs a session, the Origin and a token; 20 tries an hour", async () => {
    const { h, b, nest, anna } = await setup();
    const t = tokenOf((await invite(b, nest)).json.link);
    expect((await h.browser("203.0.113.82").call("POST", "/v1/invite/request", { body: { t } })).status).toBe(401);
    expect((await anna.call("POST", "/v1/invite/request", { body: { t }, origin: null })).json.error).toBe("bad_origin");
    expect((await anna.call("POST", "/v1/invite/request", { body: {} })).json.error).toBe("bad_request");
    const unknown = await anna.call("POST", "/v1/invite/request", { body: { t: "pni_" + "z".repeat(32) } });
    expect(unknown.status).toBe(404);
    expect(unknown.json.message).toBe("This invite link doesn't work. Check that the whole link was copied.");
    expect((await anna.call("POST", "/v1/invite/request", { body: { t: "nope" } })).status).toBe(404);
    for (let i = 0; i < 18; i++) expect((await anna.call("POST", "/v1/invite/request", { body: { t: "nope" } })).status).toBe(404);
    expect((await anna.call("POST", "/v1/invite/request", { body: { t } })).status).toBe(429);
    expect(await h.query("SELECT * FROM nest_invites WHERE requested_by IS NOT NULL")).toHaveLength(0);
  });

  it("every other state has its own page, and asking answers the same", async () => {
    const { h, b, nest, anna } = await setup();
    const ask = (who: Browser, t: string) => who.call("POST", "/v1/invite/request", { body: { t } });

    // The owner's own link.
    const mine = tokenOf((await invite(b, nest)).json.link);
    const ownPage = await b.call("GET", `/invite?t=${mine}`);
    expect(ownPage.text).toContain("This is your own invite");
    expect(ownPage.text).toContain(`href="/account#people-${nest.nestId}"`);
    expect((await ask(b, mine)).json).toMatchObject({ error: "own_nest", message: "This is your own nest. Send the link to the friend you want to invite." });

    // Unknown.
    const unknown = await anna.call("GET", `/invite?t=pni_${"z".repeat(32)}`);
    expect(unknown.status).toBe(404);
    expect(unknown.text).toContain("This invite link doesn't work");
    expect((await anna.call("GET", "/invite")).status).toBe(404);

    // Cancelled.
    const c = await invite(b, nest);
    await b.call("DELETE", `/v1/servers/${nest.nestId}/invites/${c.json.id}`);
    expect((await anna.call("GET", `/invite?t=${tokenOf(c.json.link)}`)).text).toContain("This invite was cancelled");
    expect((await ask(anna, tokenOf(c.json.link))).json).toEqual({ error: "cancelled", message: "This invite was cancelled." });

    // Declined.
    const d = await invite(b, nest);
    await ask(anna, tokenOf(d.json.link));
    expect((await b.call("POST", `/v1/servers/${nest.nestId}/invites/${d.json.id}/decline`)).json).toEqual({ status: "declined" });
    expect((await anna.call("GET", `/invite?t=${tokenOf(d.json.link)}`)).text).toContain("Jonas Lund didn't let you in");
    expect((await ask(anna, tokenOf(d.json.link))).json).toMatchObject({ error: "already_decided", message: "Jonas Lund said no to this invite." });

    // In, and then every other open link to this nest says Anna already has it.
    const open = await invite(b, nest);
    const { t: inT } = await letIn(b, nest, anna);
    const inPage = await anna.call("GET", `/invite?t=${inT}`);
    expect(inPage.text).toContain("<title>You&#39;re in Home nest · Pairnets</title>");
    expect(inPage.text).toContain("Jonas Lund let you in.");
    expect((await ask(anna, inT)).json).toEqual({ status: "in", nest: "Home nest", owner: "Jonas Lund" });
    expect((await anna.call("GET", `/invite?t=${tokenOf(open.json.link)}`)).text).toContain("You already have Home nest");
    expect((await ask(anna, tokenOf(open.json.link))).json).toEqual({ error: "already_member", message: "You already have this nest." });

    // Run out.
    h.advance(7 * 86400);
    const oskar = h.browser("203.0.113.41");
    await oskar.signInByEmail(OSKAR);
    expect((await oskar.call("GET", `/invite?t=${tokenOf(open.json.link)}`)).text).toContain("This invite has run out");
    expect((await ask(oskar, tokenOf(open.json.link))).json).toEqual({ error: "expired", message: "This invite has run out. Ask Jonas Lund for a new one." });

    // A nest removed from its account takes its invites with it.
    await b.signInByEmail(OWNER);
    const last = await invite(b, nest);
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}`)).status).toBe(204);
    expect((await oskar.call("GET", `/invite?t=${tokenOf(last.json.link)}`)).status).toBe(404);
  });
});

describe("friend invites: the owner's answer", () => {
  it("letting someone in: a recent sign-in, a member row, an email to the friend, and the nest's waiting list emptied", async () => {
    const { h, b, nest, anna, annaId } = await setup();
    const r = await invite(b, nest);
    await anna.call("POST", "/v1/invite/request", { body: { t: tokenOf(r.json.link) } });
    const path = `/v1/servers/${nest.nestId}/invites/${r.json.id}/approve`;

    // Not the owner, not from the page, not a recent sign-in.
    expect((await anna.call("POST", path)).status).toBe(404);
    expect((await b.call("POST", path, { origin: null })).status).toBe(403);
    h.advance(901);
    const old = await b.call("POST", path);
    expect(old.status).toBe(401);
    expect(old.json.error).toBe("reauth_required");
    await b.signInByEmail(OWNER);

    const ok = await b.call("POST", path);
    expect(ok.status, ok.text).toBe(200);
    expect(ok.json).toEqual({
      status: "in",
      member: { accountId: annaId, name: "Anna Berg", email: ANNA, access: "view", folders: ["Photos", "School"], addedAt: expect.any(String), synced: true },
    });
    expect(await h.query("SELECT nest_id, account_id, access, folders, synced, removed_at FROM nest_members")).toEqual([
      { nest_id: nest.nestId, account_id: annaId, access: "view", folders: '["Photos","School"]', synced: 1, removed_at: null },
    ]);
    const mail = h.mailer.to(ANNA).filter((m) => m.subject === "You're in Home nest");
    expect(mail).toHaveLength(1);
    expect(mail[0].text).toContain("Jonas Lund let you in to Home nest. You get the folders Photos and School, and you can view them.");
    expect(mail[0].text).toContain(`sign in as ${ANNA} and pick Home nest · shared by Jonas Lund.`);
    expect(nest.waiting).toEqual([]);
    expect((await b.call("GET", `/v1/servers/${nest.nestId}/people`)).json).toMatchObject({ members: [{ accountId: annaId, name: "Anna Berg" }], invites: [] });
    expect(await h.query("SELECT event FROM audit WHERE event = 'invite_approved'")).toHaveLength(1);

    // Answered: no second answer, either way.
    expect((await b.call("POST", path)).json).toMatchObject({ error: "already_decided" });
    expect((await b.call("POST", `/v1/servers/${nest.nestId}/invites/${r.json.id}/decline`)).status).toBe(409);
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/invites/${r.json.id}`)).status).toBe(409);
    // Nobody asked yet, cancelled, unknown.
    const fresh = await invite(b, nest);
    expect((await b.call("POST", `/v1/servers/${nest.nestId}/invites/${fresh.json.id}/approve`)).json).toEqual({
      error: "bad_request",
      message: "Nobody has asked to join with this invite yet.",
    });
    await b.call("DELETE", `/v1/servers/${nest.nestId}/invites/${fresh.json.id}`);
    expect((await b.call("POST", `/v1/servers/${nest.nestId}/invites/${fresh.json.id}/approve`)).json.error).toBe("cancelled");
    expect((await b.call("POST", `/v1/servers/${nest.nestId}/invites/inv_testinvite0000000000000001/approve`)).status).toBe(404);
    expect((await b.call("POST", `/v1/servers/${nest.nestId}/invites/garbage/approve`)).status).toBe(404);
  });

  it("someone asked just before the link ran out can still be let in, within 30 days", async () => {
    const { h, b, nest, anna } = await setup();
    const r = await invite(b, nest);
    h.advance(7 * 86400 - 10);
    await anna.call("POST", "/v1/invite/request", { body: { t: tokenOf(r.json.link) } });
    h.advance(3 * 86400);
    await b.signInByEmail(OWNER);
    expect((await b.call("GET", `/v1/servers/${nest.nestId}/people`)).json.invites).toHaveLength(1);
    expect((await b.call("POST", `/v1/servers/${nest.nestId}/invites/${r.json.id}/approve`)).status).toBe(200);
  });

  it("declining: answered, nobody let in, the nest's list emptied; cancelling stops the link", async () => {
    const { h, b, nest, anna } = await setup();
    const r = await invite(b, nest);
    await anna.call("POST", "/v1/invite/request", { body: { t: tokenOf(r.json.link) } });
    expect(nest.waiting).toHaveLength(1);
    h.advance(3600); // no recent sign-in needed to say no
    expect((await anna.call("POST", `/v1/servers/${nest.nestId}/invites/${r.json.id}/decline`)).status).toBe(404);
    expect((await b.call("POST", `/v1/servers/${nest.nestId}/invites/${r.json.id}/decline`)).status).toBe(200);
    expect(await h.query("SELECT * FROM nest_members")).toHaveLength(0);
    expect(nest.waiting).toEqual([]);
    expect((await h.query("SELECT decision FROM nest_invites"))[0]).toEqual({ decision: "declined" });

    // Cancelling an invite someone asked with also takes them off the nest's list; cancelling twice is fine.
    const c = await invite(b, nest);
    await anna.call("POST", "/v1/invite/request", { body: { t: tokenOf(c.json.link) } });
    expect(nest.waiting).toHaveLength(1);
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/invites/${c.json.id}`)).status).toBe(204);
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/invites/${c.json.id}`)).status).toBe(204);
    expect(nest.waiting).toEqual([]);
    expect((await anna.call("DELETE", `/v1/servers/${nest.nestId}/invites/${c.json.id}`)).status).toBe(404);
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/invites/${c.json.id}`, { origin: null })).status).toBe(403);
    expect(await h.query("SELECT event FROM audit WHERE event IN ('invite_declined', 'invite_cancelled') ORDER BY id")).toEqual([
      { event: "invite_declined" },
      { event: "invite_cancelled" },
    ]);
  });
});

describe("friend invites: a friend's computers", () => {
  it("the shared nest is a choice when signing in; the nest gets access, folders and person; the app is told whose it is", async () => {
    const { h, b, nest, anna, annaId } = await setup();
    await letIn(b, nest, anna);
    const app = h.client("198.51.100.90");
    const start = await app.call("POST", "/v1/app/start", { body: { name: "Anna-Laptop", system: "Windows 11", appVersion: "1.0.61" } });
    const code = start.json.userCode as string;

    // Anna has no nest of her own: the shared one is the only choice.
    const page = await anna.call("GET", `/app?code=${code}`);
    expect(page.text).toContain('It will sync with <b>Home nest</b> <span class="q-have-note">· shared by Jonas Lund</span>');
    expect(page.text).toContain(`data-nest="${nest.nestId}"`);
    expect(page.text).not.toContain("You don't have a nest yet");

    // With a nest of her own as well: a choice, the shared one under its own heading.
    const annas = await addServer(h, anna, { label: "Anna's nest", ip: "198.51.100.43" });
    const choice = await anna.call("GET", `/app?code=${code}&have=${annas.nestId}`);
    expect(choice.text).toContain('<p class="q-group">Shared with you</p>');
    expect(choice.text).toContain(
      `<label class="q-choice"><input type="radio" name="nest" value="${nest.nestId}" data-label="Home nest" checked> <span class="q-choice-text">Home nest<span class="q-choice-sub">Shared by Jonas Lund · Photos, School · view only</span></span></label>`,
    );
    expect(choice.text).toContain(`value="${annas.nestId}" data-label="Anna&#39;s nest" disabled>`);

    expect((await anna.call("POST", "/v1/app/approve", { body: { userCode: code, approve: true, nestId: nest.nestId } })).json).toEqual({ status: "approved" });
    h.advance(3);
    const poll = await app.call("POST", "/v1/app/poll", { body: { deviceCode: start.json.deviceCode } });
    expect(poll.status, poll.text).toBe(200);
    expect(poll.json).toMatchObject({
      status: "approved",
      email: ANNA,
      nest: {
        id: nest.nestId,
        label: "Home nest",
        serverUrl: `https://sync.pairnets.app/n/${nest.nestId}/`,
        sharedBy: "Jonas Lund",
        access: "view",
        folders: ["Photos", "School"],
      },
      device: { name: "Anna-Laptop" },
    });
    const sent = nest.requests.filter((q) => q.method === "POST" && q.path === "/api/relay/devices").at(-1)!;
    expect(JSON.parse(sent.body)).toEqual({
      name: "Anna-Laptop",
      system: "Windows 11",
      approvedBy: "a***@example.com",
      access: "view",
      folders: ["Photos", "School"],
      person: "Anna Berg · a•••@example.com",
      personId: annaId,
    });
    const device = nest.devices.get(poll.json.device.id)!;
    expect(device).toMatchObject({ access: "view", folders: ["Photos", "School"], personId: annaId });
    expect(nest.refused).toEqual([]);

    // The owner's list marks it as Anna's (the page counts it under People, not with the owner's computers).
    const listed = (await b.call("GET", "/v1/servers")).json[0].devices;
    expect(listed).toContainEqual(expect.objectContaining({ id: poll.json.device.id, access: "view", personId: annaId }));
    // The owner's own computers still ask with the old body.
    await appSignIn(h, b, nest.nestId, "Jonas-PC", "198.51.100.91");
    expect(JSON.parse(nest.requests.filter((q) => q.path === "/api/relay/devices" && q.method === "POST").at(-1)!.body)).toEqual({
      name: "Jonas-PC",
      system: "Windows 11",
      approvedBy: "y***@example.com",
    });
  });

  it("a nest that does not keep friends to what was shared is never asked for a friend's key: 409 nest_too_old", async () => {
    const { h, b, nest, anna } = await setup();
    await letIn(b, nest, anna);
    nest.friends = false;
    const before = nest.devices.size;
    const app = h.client("198.51.100.90");
    const start = await app.call("POST", "/v1/app/start", { body: { name: "Anna-Laptop" } });
    expect((await anna.call("POST", "/v1/app/approve", { body: { userCode: start.json.userCode, approve: true, nestId: nest.nestId } })).status).toBe(200);
    h.advance(3);
    const poll = await app.call("POST", "/v1/app/poll", { body: { deviceCode: start.json.deviceCode } });
    expect(poll.status).toBe(409);
    expect(poll.json).toEqual({ error: "nest_too_old", message: "This nest needs an update before friends can use it. Ask its owner to update it." });
    expect(nest.devices.size).toBe(before);
    expect(nest.requests.some((q) => q.method === "POST" && q.path === "/api/relay/devices")).toBe(false);
    // Once the nest is updated, the next poll goes through.
    nest.friends = true;
    h.advance(3);
    expect((await app.call("POST", "/v1/app/poll", { body: { deviceCode: start.json.deviceCode } })).json.status).toBe("approved");
  });

  it("only nests one was let in to; a friend removed before the key was collected gets none", async () => {
    const { h, b, nest, anna, annaId } = await setup();
    const app = h.client("198.51.100.90");
    const start = await app.call("POST", "/v1/app/start", { body: { name: "Anna-Laptop" } });
    const approve = () => anna.call("POST", "/v1/app/approve", { body: { userCode: start.json.userCode, approve: true, nestId: nest.nestId } });
    expect((await approve()).status).toBe(404);
    // An invite alone (asked, not answered) is not a way in either.
    const r = await invite(b, nest);
    await anna.call("POST", "/v1/invite/request", { body: { t: tokenOf(r.json.link) } });
    expect((await approve()).status).toBe(404);
    await b.call("POST", `/v1/servers/${nest.nestId}/invites/${r.json.id}/approve`);
    expect((await approve()).status).toBe(200);
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/people/${annaId}`)).status).toBe(204);
    h.advance(3);
    const poll = await app.call("POST", "/v1/app/poll", { body: { deviceCode: start.json.deviceCode } });
    expect(poll.status).toBe(400);
    expect(poll.json.error).toBe("expired");
    expect(nest.devices.size).toBe(0);
  });

  it("an encrypted nest that has a friend with folders refuses the key, and the app hears why", async () => {
    const { h, b, nest, anna } = await setup();
    await letIn(b, nest, anna);
    nest.sealed = true; // encrypted after the invite
    const app = h.client("198.51.100.90");
    const start = await app.call("POST", "/v1/app/start", { body: { name: "Anna-Laptop" } });
    await anna.call("POST", "/v1/app/approve", { body: { userCode: start.json.userCode, approve: true, nestId: nest.nestId } });
    h.advance(3);
    const poll = await app.call("POST", "/v1/app/poll", { body: { deviceCode: start.json.deviceCode } });
    expect(poll.status).toBe(409);
    expect(poll.json.error).toBe("sealed_whole_only");
    expect(nest.devices.size).toBe(0);
  });
});

describe("friend invites: changing, removing and leaving", () => {
  async function withFriendComputer() {
    const s = await setup();
    const { t } = await letIn(s.b, s.nest, s.anna);
    const { poll } = await appSignIn(s.h, s.anna, s.nest.nestId, "Anna-Laptop", "198.51.100.90");
    expect(poll.status, poll.text).toBe(200);
    return { ...s, t, deviceId: poll.json.device.id as string };
  }

  it("changing a friend: the nest changes their computers; a field left out keeps its value", async () => {
    const { h, b, nest, anna, annaId, deviceId } = await withFriendComputer();
    const path = `/v1/servers/${nest.nestId}/people/${annaId}`;
    const r = await b.call("PATCH", path, { body: { access: "edit", folders: null } });
    expect(r.status, r.text).toBe(200);
    expect(r.json).toMatchObject({ accountId: annaId, name: "Anna Berg", access: "edit", folders: null, synced: true });
    expect(nest.devices.get(deviceId)).toMatchObject({ access: "edit", folders: null });
    expect(nest.requests.at(-1)).toMatchObject({ method: "PATCH", path: `/api/relay/people/${annaId}`, body: '{"access":"edit","folders":null}' });
    const keep = await b.call("PATCH", path, { body: { folders: ["Music"] } });
    expect(keep.json).toMatchObject({ access: "edit", folders: ["Music"] });
    expect(nest.devices.get(deviceId)).toMatchObject({ access: "edit", folders: ["Music"] });
    expect((await h.query("SELECT access, folders, synced FROM nest_members"))[0]).toEqual({ access: "edit", folders: '["Music"]', synced: 1 });
    // What Anna's list says now.
    expect((await anna.call("GET", "/v1/shared")).json.nests[0]).toMatchObject({ access: "edit", folders: ["Music"] });

    expect((await b.call("PATCH", path, { body: { access: "owner" } })).json.error).toBe("bad_access");
    expect((await b.call("PATCH", path, { body: { folders: [] } })).json.error).toBe("bad_access");
    expect((await b.call("PATCH", path, { body: {}, origin: null })).status).toBe(403);
    expect((await anna.call("PATCH", path, { body: { access: "edit" } })).status).toBe(404);
    expect((await b.call("PATCH", `/v1/servers/${nest.nestId}/people/acc_testacct000000000000000009`, { body: { access: "edit" } })).status).toBe(404);
    expect((await b.call("PATCH", `/v1/servers/${nest.nestId}/people/garbage`, { body: { access: "edit" } })).status).toBe(404);
  });

  it("changing while the nest is off: kept, and the cron tells the nest later; an encrypted nest refuses folders", async () => {
    const { h, b, nest, annaId, deviceId } = await withFriendComputer();
    const path = `/v1/servers/${nest.nestId}/people/${annaId}`;
    nest.running = false;
    const r = await b.call("PATCH", path, { body: { access: "edit" } });
    expect(r.status).toBe(200);
    expect(r.json).toMatchObject({ access: "edit", synced: false });
    expect((await h.query("SELECT access, synced FROM nest_members"))[0]).toEqual({ access: "edit", synced: 0 });
    expect(nest.devices.get(deviceId)!.access).toBe("view");
    // Still off at the next sweep: nothing lost.
    await h.scheduled();
    expect((await h.query("SELECT synced FROM nest_members"))[0]).toEqual({ synced: 0 });
    nest.running = true;
    h.advance(3600);
    await h.scheduled();
    expect(nest.devices.get(deviceId)!.access).toBe("edit");
    expect((await h.query("SELECT synced FROM nest_members"))[0]).toEqual({ synced: 1 });

    await b.signInByEmail(OWNER);
    nest.sealed = true;
    const sealed = await b.call("PATCH", path, { body: { folders: ["Photos"] } });
    expect(sealed.status).toBe(400);
    expect(sealed.json.error).toBe("sealed_whole_only");
    expect((await h.query("SELECT folders FROM nest_members"))[0]).toEqual({ folders: '["Photos","School"]' });
    expect((await b.call("PATCH", path, { body: { folders: null } })).status).toBe(200);
    // A nest that no longer has the people endpoints.
    nest.friends = false;
    expect((await b.call("PATCH", path, { body: { access: "view" } })).json.error).toBe("nest_too_old");
  });

  it("removing a friend: their computers lose the nest at once, and it leaves their list", async () => {
    const { h, b, nest, anna, annaId, deviceId, t } = await withFriendComputer();
    expect((await anna.call("DELETE", `/v1/servers/${nest.nestId}/people/${annaId}`)).status).toBe(404);
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/people/${annaId}`, { origin: null })).status).toBe(403);
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/people/${annaId}`)).status).toBe(204);
    expect(nest.devices.has(deviceId)).toBe(false);
    expect(nest.requests.at(-1)).toMatchObject({ method: "DELETE", path: `/api/relay/people/${annaId}` });
    expect(await h.query("SELECT * FROM nest_members")).toHaveLength(0);
    expect((await anna.call("GET", "/v1/shared")).json).toEqual({ nests: [] });
    // The old invite no longer says "You're in".
    expect((await anna.call("GET", `/invite?t=${t}`)).text).toContain("This invite was cancelled");
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/people/${annaId}`)).status).toBe(404);
    expect(await h.query("SELECT event FROM audit WHERE event = 'friend_removed'")).toHaveLength(1);
  });

  it("removing while the nest is off: gone from the lists at once, and the cron removes the computers when it is back", async () => {
    const { h, b, nest, anna, annaId, deviceId } = await withFriendComputer();
    nest.running = false;
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/people/${annaId}`)).status).toBe(204);
    expect((await h.query("SELECT synced, removed_at FROM nest_members"))[0]).toEqual({ synced: 0, removed_at: expect.any(Number) });
    expect((await anna.call("GET", "/v1/shared")).json.nests).toEqual([]);
    expect((await b.call("GET", `/v1/servers/${nest.nestId}/people`)).json.members).toEqual([]);
    // Removing again while it is still pending is fine.
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}/people/${annaId}`)).status).toBe(204);
    // The friend can't sign a new computer in meanwhile.
    const app = h.client("198.51.100.92");
    const start = await app.call("POST", "/v1/app/start", { body: { name: "Anna-Desktop" } });
    expect((await anna.call("POST", "/v1/app/approve", { body: { userCode: start.json.userCode, approve: true, nestId: nest.nestId } })).status).toBe(404);
    nest.running = true;
    await h.scheduled();
    expect(nest.devices.has(deviceId)).toBe(false);
    expect(await h.query("SELECT * FROM nest_members")).toHaveLength(0);
  });

  it("let in again while a removal is still pending: the new access wins, on the nest too", async () => {
    const { h, b, nest, anna, annaId, deviceId } = await withFriendComputer();
    nest.running = false;
    await b.call("DELETE", `/v1/servers/${nest.nestId}/people/${annaId}`);
    nest.running = true;
    await letIn(b, nest, anna, { access: "edit" });
    expect((await h.query("SELECT access, folders, synced, removed_at FROM nest_members"))[0]).toEqual({ access: "edit", folders: null, synced: 1, removed_at: null });
    expect(nest.devices.get(deviceId)).toMatchObject({ access: "edit", folders: null });
    await h.scheduled();
    expect(nest.devices.has(deviceId)).toBe(true);
  });

  it("Shared with you, for the browser and the apps; leaving is like being removed", async () => {
    const { h, b, nest, anna, deviceId } = await withFriendComputer();
    const list = await anna.call("GET", "/v1/shared");
    expect(list.json).toEqual({
      nests: [
        {
          id: nest.nestId,
          label: "Home nest",
          sharedBy: "Jonas Lund",
          access: "view",
          folders: ["Photos", "School"],
          addedAt: expect.any(String),
          serverUrl: `https://sync.pairnets.app/n/${nest.nestId}/`,
        },
      ],
    });
    // An app token reads it too (it is how an app finds the nests shared with its account).
    const token = (await appSignIn(h, anna, nest.nestId, "Anna-Desktop", "198.51.100.93")).poll.json.appToken as string;
    expect((await h.client().call("GET", "/v1/shared", { headers: { Authorization: `Bearer ${token}` } })).json.nests).toHaveLength(1);
    // Leaving is for the account page only: an app's request has no Origin, and a token is no session.
    expect((await h.client().call("DELETE", `/v1/shared/${nest.nestId}`, { headers: { Authorization: `Bearer ${token}` } })).status).toBe(403);
    const withOrigin = await h.client().call("DELETE", `/v1/shared/${nest.nestId}`, { headers: { Authorization: `Bearer ${token}` }, origin: "https://sync.pairnets.app" });
    expect(withOrigin.status).toBe(401);
    expect((await h.browser("203.0.113.83").call("GET", "/v1/shared")).status).toBe(401);
    expect((await b.call("GET", "/v1/shared")).json).toEqual({ nests: [] });

    expect((await anna.call("DELETE", `/v1/shared/${nest.nestId}`, { origin: null })).status).toBe(403);
    expect((await b.call("DELETE", `/v1/shared/${nest.nestId}`)).status).toBe(404); // the owner is not a friend of their own nest
    expect((await anna.call("DELETE", `/v1/shared/${nest.nestId}`)).status).toBe(204);
    expect(nest.devices.has(deviceId)).toBe(false);
    expect([...nest.devices.values()].filter((d) => d.personId)).toEqual([]);
    expect((await anna.call("GET", "/v1/shared")).json.nests).toEqual([]);
    expect((await b.call("GET", `/v1/servers/${nest.nestId}/people`)).json.members).toEqual([]);
    expect((await anna.call("DELETE", `/v1/shared/${nest.nestId}`)).status).toBe(404);
    expect((await anna.call("DELETE", "/v1/shared/garbage")).status).toBe(404);
    expect(await h.query("SELECT event FROM audit WHERE event = 'friend_left'")).toHaveLength(1);
  });
});

describe("friend invites: the account page", () => {
  it("People under each nest: friends, people waiting, open links, and Shared with you for the friend", async () => {
    const { h, b, nest, anna, annaId } = await setup();
    let page = (await b.call("GET", "/account")).text;
    expect(page).toContain(`<section class="people" id="people-${nest.nestId}" aria-labelledby="people-${nest.nestId}-title">`);
    expect(page).toContain(`<h3 id="people-${nest.nestId}-title">People</h3> <span class="muted">Only you</span>`);
    expect(page).toContain('<button type="button" class="secondary small" data-action="invite-open">Invite a friend…</button>');
    expect(page).toContain('<form class="invite-form" data-field="invite-form" hidden>');
    expect(page).toContain("<h3>Invite a friend to Home nest</h3>");
    expect(page).toContain(`<input type="radio" name="what-${nest.nestId}" value="whole" checked> The whole nest`);
    expect(page).toContain(`<input type="radio" name="access-${nest.nestId}" value="view" checked> <span><strong>View only</strong>`);
    expect(page).toContain("Whoever uses the link still waits for you: you let each person in before they get any files. It ends after 7 days.");
    expect(page).not.toContain('id="shared"');

    await letIn(b, nest, anna);
    const waiting = await invite(b, nest, { access: "edit" });
    const oskar = h.browser("203.0.113.41");
    await oskar.signInByEmail(OSKAR);
    await oskar.call("POST", "/v1/invite/request", { body: { t: tokenOf(waiting.json.link) } });
    const link = await invite(b, nest, { folders: ["Music"], email: "jonas.friend@example.com" });

    page = (await b.call("GET", "/account")).text;
    expect(page).toContain('<span class="muted">1 friend · 1 waiting</span>');
    expect(page).toContain(`<li class="person waiting" data-invite="${waiting.json.id}"><span class="letter" aria-hidden="true">O</span>`);
    expect(page).toContain(`<strong>oskar.nilsson wants to join</strong> <span class="muted">· ${OSKAR}</span>`);
    expect(page).toContain("Used your invite link · Whole nest · can change");
    expect(page).toContain('<button type="button" class="secondary" data-action="invite-decline">Decline</button> <button type="button" data-action="invite-approve">Let oskar.nilsson in</button>');
    expect(page).toContain(`<li class="person" data-person="${annaId}" data-access="view" data-folders="Photos, School"><span class="letter" aria-hidden="true">A</span>`);
    expect(page).toContain(`<strong data-field="name">Anna Berg</strong> <span class="muted">· ${ANNA}</span>`);
    expect(page).toContain('<span data-field="share">Photos, School · view only</span><span data-field="computers"></span>');
    expect(page).toContain('data-action="person-change">Change…</button> <button type="button" class="danger" data-action="person-remove">Remove</button>');
    expect(page).toContain(`<li class="person link" data-invite="${link.json.id}" data-link="${link.json.link}">`);
    expect(page).toContain("<strong>Invite sent to jonas.friend@example.com</strong>");
    expect(page).toContain("Music · view only · ends 16 October");
    expect(page).toContain('data-action="invite-copy">Copy link</button> <button type="button" class="danger" data-action="invite-cancel">Cancel</button>');

    const theirs = (await anna.call("GET", "/account")).text;
    expect(theirs).toContain('<section class="card" id="shared">');
    expect(theirs).toContain(`<li data-shared="${nest.nestId}">`);
    expect(theirs).toContain('<strong>Home nest</strong> <span class="muted">· shared by Jonas Lund</span>');
    expect(theirs).toContain("Photos, School · view only");
    expect(theirs).toContain('<button type="button" class="danger" data-action="leave">Leave</button>');

    // The page's script knows every one of these buttons and the routes behind them.
    const script = (await b.call("GET", "/assets/account.js")).text;
    for (const piece of [
      '"/v1/servers/" + id + "/invites"',
      '"/v1/servers/" + id + "/invites/" + inv',
      '"/v1/servers/" + id + "/people/"',
      '"/v1/shared/"',
      '"invite-open"',
      '"invite-close"',
      '"invite-link"',
      '"invite-copy"',
      '"invite-copy-new"',
      '"invite-finish"',
      '"invite-approve"',
      '"invite-cancel"',
      '"person-change"',
      '"person-remove"',
      "button[data-action=leave]",
      "Update your nest first to invite friends.",
      "!d.personId",
    ]) {
      expect(script, piece).toContain(piece);
    }
    expect(script).not.toContain("innerHTML");
  });

  it("/account?invite=…&folders=…&access=… opens that nest's invite form filled in (the apps hand over to it)", async () => {
    const { h, b, nest, anna } = await setup();
    const other = await addServer(h, b, { label: "Work nest", ip: "198.51.100.41" });
    const path = `/account?invite=${nest.nestId}&folders=Photos,%20School&access=edit`;
    const page = (await b.call("GET", path)).text;
    expect(page).toContain('<form class="invite-form" data-field="invite-form" data-open>');
    expect(page).toContain(`<input type="radio" name="what-${nest.nestId}" value="folders" data-field="chosen" checked> Chosen folders`);
    expect(page).toContain(`value="Photos, School">`);
    expect(page).toContain(`<input type="radio" name="access-${nest.nestId}" value="edit" checked>`);
    // Only that nest's form.
    expect(page.match(/data-field="invite-form" data-open>/g)).toHaveLength(1);
    expect(page).toContain(`<input type="radio" name="what-${other.nestId}" value="whole" checked>`);
    // The whole nest and view only when nothing (or nothing usable) is said.
    const plain = (await b.call("GET", `/account?invite=${nest.nestId}&folders=a/b`)).text;
    expect(plain).toContain(`<input type="radio" name="what-${nest.nestId}" value="whole" checked>`);
    expect(plain).toContain(`<input type="radio" name="access-${nest.nestId}" value="view" checked>`);
    expect(plain).toContain("data-open>");
    // Someone else's nest, or nonsense: nothing opens.
    expect((await anna.call("GET", path)).text).not.toContain("data-open");
    expect((await b.call("GET", "/account?invite=garbage")).text).not.toContain("data-open");

    // Not signed in: the sign-in comes back here, with what was asked.
    const out = await h.browser("203.0.113.84").call("GET", path);
    const next = `/account?invite=${nest.nestId}&folders=${encodeURIComponent("Photos,School")}&access=edit`;
    expect(out.location).toBe(`/login?next=${encodeURIComponent(next)}`);
    expect(safeNext(next)).toBe(next);
    expect((await h.browser("203.0.113.84").call("GET", out.location!)).text).toContain(`data-next="${next.replace(/&/g, "&amp;")}"`);
  });
});

describe("friend invites: the cron, and deleting accounts", () => {
  it("ended invites go (with the address they were sent to); a request waits 30 days for an answer", async () => {
    const { h, b, nest, anna } = await setup();
    await invite(b, nest, { email: ANNA });
    await invite(b, nest, {});
    const asked = await invite(b, nest, {});
    await anna.call("POST", "/v1/invite/request", { body: { t: tokenOf(asked.json.link) } });
    const cancelled = await invite(b, nest, {});
    await b.call("DELETE", `/v1/servers/${nest.nestId}/invites/${cancelled.json.id}`);
    h.advance(3600);
    await h.scheduled();
    expect(await h.query("SELECT * FROM nest_invites")).toHaveLength(4); // nothing has ended yet
    h.advance(7 * 86400);
    await h.scheduled();
    expect((await h.query<{ id: string }>("SELECT id FROM nest_invites")).map((r) => r.id)).toEqual([asked.json.id]);
    expect(await h.query("SELECT * FROM nest_invites WHERE email IS NOT NULL")).toHaveLength(0);
    h.advance(30 * 86400);
    await h.scheduled();
    expect(await h.query("SELECT * FROM nest_invites")).toHaveLength(0);
  });

  it("a removed nest takes its friends and invites with it", async () => {
    const { h, b, nest, anna } = await setup();
    await letIn(b, nest, anna);
    await invite(b, nest, {});
    expect((await b.call("DELETE", `/v1/servers/${nest.nestId}`)).status).toBe(204);
    expect(await h.query("SELECT * FROM nest_members")).toHaveLength(0);
    expect(await h.query("SELECT * FROM nest_invites")).toHaveLength(0);
    expect((await anna.call("GET", "/v1/shared")).json.nests).toEqual([]);
    // Rows left behind any other way (a nest the sweep gave up) are swept too.
    await h.env.DB.prepare("INSERT INTO nest_members (nest_id, account_id, access, added_at) VALUES ('nst_testnest000000000000000001', ?1, 'view', 1)")
      .bind((await h.query<{ id: string }>("SELECT id FROM accounts WHERE email = ?1", ANNA))[0].id)
      .run();
    await h.scheduled();
    expect(await h.query("SELECT * FROM nest_members")).toHaveLength(0);
  });

  it("a friend who deletes their account is removed from the nest (by the cron); an owner's account takes its people along", async () => {
    const { h, b, nest, anna } = await setup();
    await letIn(b, nest, anna);
    const { poll } = await appSignIn(h, anna, nest.nestId, "Anna-Laptop", "198.51.100.90");
    const deviceId = poll.json.device.id as string;
    const r = await invite(b, nest, {});
    const oskar = h.browser("203.0.113.41");
    await oskar.signInByEmail(OSKAR);
    await oskar.call("POST", "/v1/invite/request", { body: { t: tokenOf(r.json.link) } });

    expect((await anna.call("DELETE", "/v1/me")).status).toBe(204);
    expect((await h.query("SELECT synced, removed_at FROM nest_members"))[0]).toEqual({ synced: 0, removed_at: expect.any(Number) });
    await h.scheduled();
    expect(nest.devices.has(deviceId)).toBe(false);
    expect(await h.query("SELECT * FROM nest_members")).toHaveLength(0);

    // Oskar's request goes when Oskar's account does.
    expect((await oskar.call("DELETE", "/v1/me")).status).toBe(204);
    expect(await h.query("SELECT * FROM nest_invites WHERE requested_by IS NOT NULL")).toHaveLength(0);

    // The owner's account: its nests' people and invites go with it.
    const jonas = h.browser("203.0.113.42");
    await jonas.signInByEmail("jonas.lund@example.com");
    await letIn(b, nest, jonas);
    await invite(b, nest, {});
    await b.signInByEmail(OWNER);
    expect((await b.call("DELETE", "/v1/me")).status).toBe(204);
    expect(await h.query("SELECT * FROM nest_members")).toHaveLength(0);
    expect(await h.query("SELECT * FROM nest_invites")).toHaveLength(0);
    expect((await jonas.call("GET", "/v1/shared")).json.nests).toEqual([]);
  });
});

// ------------------------------------------------------------------ invite.js in a small fake document

class El {
  hidden: boolean;
  disabled = false;
  textContent = "";
  focused = false;
  private listeners: (() => void)[] = [];
  constructor(public attrs: Record<string, string>) {
    this.hidden = "hidden" in attrs;
  }
  getAttribute(n: string): string | null {
    return n in this.attrs ? this.attrs[n] : null;
  }
  addEventListener(_t: string, f: () => void) {
    this.listeners.push(f);
  }
  click() {
    if (!this.disabled) for (const f of this.listeners) f();
  }
  focus() {
    this.focused = true;
  }
}

type Answer = { status: number; body: unknown };

async function runInvitePage(b: Browser, path: string, answers: Answer[]) {
  const html = (await b.call("GET", path)).text;
  const script = (await b.call("GET", "/assets/invite.js")).text;
  const byId = new Map<string, El>();
  for (const m of html.matchAll(/<([a-z0-9]+)\s([^>]*)>/g)) {
    const attrs: Record<string, string> = {};
    for (const a of m[2].matchAll(/([a-z-]+)(?:="([^"]*)")?/g)) attrs[a[1]] = a[2] ?? "";
    if (attrs.id) byId.set(attrs.id, new El(attrs));
  }
  const sent: { url: string; method: string; body: unknown }[] = [];
  const location = { href: `https://sync.pairnets.app${path}`, reloaded: false, reload() { this.reloaded = true; } };
  const fetch = async (url: string, init: { method: string; body?: string }) => {
    sent.push({ url, method: init.method, body: init.body ? JSON.parse(init.body) : undefined });
    const a = answers.shift();
    if (!a) throw new Error("no answer queued");
    return new Response(a.status === 204 ? null : JSON.stringify(a.body), { status: a.status });
  };
  new Function("document", "location", "fetch", script)({ getElementById: (id: string) => byId.get(id) ?? null }, location, fetch);
  const el = (id: string) => byId.get(id)!;
  const shown = () => ["ask", "no", "waiting"].filter((s) => byId.get(`screen-${s}`) && !byId.get(`screen-${s}`)!.hidden);
  const settle = async () => {
    for (let i = 0; i < 5; i++) await new Promise((r) => globalThis.setTimeout(r, 0));
  };
  return { el, shown, settle, sent, location };
}

describe("the invite page's script", () => {
  type Setup = Awaited<ReturnType<typeof setup>>;
  async function page(answers: Answer[], s?: Setup) {
    const { b, nest, anna } = s ?? (await setup());
    const t = tokenOf((await invite(b, nest)).json.link);
    return { t, p: await runInvitePage(anna, `/invite?t=${t}`, answers) };
  }

  it("Ask to join: sends the token and shows the waiting screen", async () => {
    const { t, p } = await page([{ status: 200, body: { status: "requested" } }]);
    expect(p.shown()).toEqual(["ask"]);
    p.el("ask").click();
    expect(p.el("ask").disabled).toBe(true);
    p.el("ask").disabled = false;
    p.el("ask").click(); // a second press while the answer is on its way does nothing
    await p.settle();
    expect(p.sent).toEqual([{ url: "/v1/invite/request", method: "POST", body: { t } }]);
    expect(p.shown()).toEqual(["waiting"]);
    expect(p.el("screen-waiting-title").focused).toBe(true);
  });

  it("an error says so and the buttons work again; an invite that changed meanwhile reloads", async () => {
    const { p } = await page([
      { status: 429, body: { error: "rate_limited", message: "Too many requests." } },
      { status: 500, body: { error: "server_error", message: "Something went wrong on our side." } },
      { status: 409, body: { error: "used", message: "Someone else already used this invite." } },
    ]);
    p.el("ask").click();
    await p.settle();
    expect(p.el("invite-error").hidden).toBe(false);
    expect(p.el("invite-error").textContent).toBe("That was a lot of tries in a short time. Wait a minute, then try again.");
    expect(p.el("ask").disabled).toBe(false);
    p.el("ask").click();
    await p.settle();
    expect(p.el("invite-error").textContent).toBe("Something went wrong on our side.");
    p.el("ask").click();
    await p.settle();
    expect(p.location.reloaded).toBe(true);
    expect(p.shown()).toEqual(["ask"]);
  });

  it("No thanks shows that nothing happened; not you signs out and back in to this invite; let in already reloads", async () => {
    const s = await setup();
    const { t, p } = await page([{ status: 204, body: null }], s);
    p.el("no-thanks").click();
    expect(p.shown()).toEqual(["no"]);
    expect(p.sent).toEqual([]);
    p.el("not-you").click();
    await p.settle();
    expect(p.sent).toEqual([{ url: "/v1/logout", method: "POST", body: undefined }]);
    expect(p.location.href).toBe(`/login?next=${encodeURIComponent(`/invite?t=${t}`)}`);

    const again = await page([{ status: 200, body: { status: "in" } }], s);
    again.p.el("ask").click();
    await again.p.settle();
    expect(again.p.location.reloaded).toBe(true);

    const out = await page([{ status: 401, body: { error: "unauthorized" } }], s);
    out.p.el("ask").click();
    await out.p.settle();
    expect(out.p.location.href).toBe(`/login?next=${encodeURIComponent(`/invite?t=${out.t}`)}`);
  });
});
