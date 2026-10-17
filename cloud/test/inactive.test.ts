import { describe, expect, it } from "vitest";
import { deletionDay, INACTIVE_DAYS, WARNING_DAYS, WARNINGS_PER_DAY } from "../src/inactive";
import { inactiveWarningEmail } from "../src/mail";
import { addServer, Harness } from "./helpers";

const DAY = 86400;
const EMAIL = "you@example.com";

async function signedIn() {
  const h = await Harness.create();
  const b = h.browser();
  const accountId = await b.signInByEmail(EMAIL);
  return { h, b, accountId };
}

const accounts = (h: Harness) => h.query<{ id: string; inactive_warned_at: number | null }>("SELECT id, inactive_warned_at FROM accounts");
const warnings = (h: Harness) => h.mailer.to(EMAIL).filter((m) => m.subject.startsWith("Your Pairnets account will be deleted on"));
const deletedMails = (h: Harness) => h.mailer.to(EMAIL).filter((m) => m.subject === "Your Pairnets account was deleted");

describe("accounts nobody uses", () => {
  it("are warned after 11 months, once, and deleted 30 days later with everything stored about them", async () => {
    const { h, accountId } = await signedIn();

    h.advance((INACTIVE_DAYS - WARNING_DAYS - 1) * DAY);
    await h.scheduled();
    expect(warnings(h)).toHaveLength(0);

    h.advance(2 * DAY);
    await h.scheduled();
    const [warning] = warnings(h);
    expect(warning.subject).toBe(`Your Pairnets account will be deleted on ${deletionDay(h.clock.now)}`);
    expect(warning.text).toContain("sign in once at https://sync.pairnets.app/account");
    expect(warning.text).toContain("Your nests and computers keep their files.");
    expect((await accounts(h))[0].inactive_warned_at).toBe(h.clock.now);

    h.advance(3600);
    await h.scheduled();
    expect(warnings(h)).toHaveLength(1); // once

    h.advance((WARNING_DAYS - 1) * DAY);
    await h.scheduled();
    expect(await accounts(h)).toHaveLength(1); // not yet

    h.advance(DAY + 3600);
    await h.scheduled();
    expect(await accounts(h)).toHaveLength(0);
    for (const t of ["identities", "sessions", "nests", "audit"]) expect(await h.query(`SELECT * FROM ${t} WHERE account_id = ?1`, accountId)).toHaveLength(0);
    expect(deletedMails(h)).toHaveLength(1);
    expect(deletedMails(h)[0].text).toContain("wasn't used for a year");
  });

  it("signing in once after the warning keeps the account", async () => {
    const { h, b } = await signedIn();
    h.advance((INACTIVE_DAYS - WARNING_DAYS + 1) * DAY);
    await h.scheduled();
    expect(warnings(h)).toHaveLength(1);

    h.advance(10 * DAY);
    await b.signInByEmail(EMAIL);
    h.advance(3600);
    await h.scheduled();
    expect((await accounts(h))[0].inactive_warned_at).toBeNull();

    h.advance(25 * DAY);
    await h.scheduled();
    expect(await accounts(h)).toHaveLength(1);
    expect(deletedMails(h)).toHaveLength(0);
    expect(await h.query("SELECT * FROM audit WHERE event = 'inactive_kept'")).toHaveLength(1);
  });

  it("an account whose nest is online is in use, without anyone signing in", async () => {
    const { h, b } = await signedIn();
    const server = await addServer(h, b);
    for (let month = 0; month < 14; month++) {
      h.advance(30 * DAY);
      expect((await h.client().call("GET", `/n/${server.nestId}/api/health`)).text).toBe("ok"); // its computers sync through it
      await h.scheduled();
    }
    expect(warnings(h)).toHaveLength(0);
    expect(await accounts(h)).toHaveLength(1);
    expect(h.cf.tunnels.has(server.tunnelId)).toBe(true);
  });

  it("a deleted account's relayed servers lose their tunnels", async () => {
    const { h, b } = await signedIn();
    const server = await addServer(h, b);
    expect((await h.client().call("GET", `/n/${server.nestId}/api/health`)).text).toBe("ok"); // reached once, then never again
    h.advance(2 * 3600);
    await h.scheduled();
    expect(h.cf.tunnels.size).toBe(1);
    h.advance((INACTIVE_DAYS - WARNING_DAYS + 1) * DAY);
    await h.scheduled();
    h.advance((WARNING_DAYS + 1) * DAY);
    await h.scheduled();
    expect(await accounts(h)).toHaveLength(0);
    expect(h.cf.tunnels.size).toBe(0);
  });

  it("no warning sent, no delete: a failed email is tried again the next hour", async () => {
    const { h } = await signedIn();
    const send = h.mailer.send.bind(h.mailer);
    h.mailer.send = async () => {
      throw new Error("mail is down");
    };
    h.advance((INACTIVE_DAYS + 40) * DAY);
    await h.scheduled();
    expect((await accounts(h))[0].inactive_warned_at).toBeNull();

    h.mailer.send = send;
    h.advance(3600);
    await h.scheduled();
    expect(warnings(h)).toHaveLength(1);
    h.advance((WARNING_DAYS - 1) * DAY);
    await h.scheduled();
    expect(await accounts(h)).toHaveLength(1); // the 30 days count from the warning that went out
  });

  it(`sends at most ${WARNINGS_PER_DAY} warnings a day; the rest follow the next days`, async () => {
    const h = await Harness.create();
    const old = h.clock.now;
    for (let i = 0; i < WARNINGS_PER_DAY + 5; i++) {
      const id = `acc_unused${String(i).padStart(20, "0")}`;
      await h.env.DB.prepare("INSERT INTO accounts (id, email, created_at, updated_at) VALUES (?1, ?2, ?3, ?3)").bind(id, `u${i}@example.com`, old).run();
    }
    h.advance(INACTIVE_DAYS * DAY);
    await h.scheduled();
    const warned = async () => (await accounts(h)).filter((a) => a.inactive_warned_at !== null).length;
    expect(await warned()).toBe(WARNINGS_PER_DAY);
    h.advance(3600);
    await h.scheduled();
    expect(await warned()).toBe(WARNINGS_PER_DAY);
    h.advance(DAY);
    await h.scheduled();
    expect(await warned()).toBe(WARNINGS_PER_DAY + 5);
  });

  it("the warning names the day and keeps to plain words", () => {
    expect(deletionDay(Date.UTC(2027, 9, 18) / 1000)).toBe("17 November 2027");
    const m = inactiveWarningEmail(EMAIL, "17 November 2027");
    expect(m.subject).toBe("Your Pairnets account will be deleted on 17 November 2027");
    expect(m.html).toContain('<a href="https://sync.pairnets.app/account"');
    expect(m.text).toContain("Privacy: https://pairnets.app/privacy/");
  });
});
