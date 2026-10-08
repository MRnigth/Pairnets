import { describe, expect, it } from "vitest";
import { createWorker } from "../src/app";
import { ResendMailer, RESEND_URL } from "../src/mail";
import { FakeNet, Harness, jsonResponse, linkNest } from "./helpers";
import { createExecutionContext, waitOnExecutionContext } from "cloudflare:test";

describe("mail", () => {
  it("Resend: POST https://api.resend.com/emails with the Bearer key and MAIL_FROM", async () => {
    const net = new FakeNet();
    net.on(RESEND_URL, () => jsonResponse(200, { id: "x" }));
    const m = new ResendMailer("re_test_key", "Pairnets <noreply@pairnets.app>", net.fetch);
    await m.send({ to: "you@example.com", subject: "Hi", text: "Hello", html: "<p>Hello</p>" });
    expect(net.calls).toHaveLength(1);
    const c = net.calls[0];
    expect(c.method).toBe("POST");
    expect(c.headers.get("authorization")).toBe("Bearer re_test_key");
    expect(c.headers.get("content-type")).toBe("application/json");
    expect(JSON.parse(c.body)).toEqual({ from: "Pairnets <noreply@pairnets.app>", to: ["you@example.com"], subject: "Hi", text: "Hello", html: "<p>Hello</p>" });

    net.on(RESEND_URL, () => jsonResponse(429, { message: "slow" }));
    await expect(m.send({ to: "a@example.com", subject: "s", text: "t", html: "h" })).rejects.toThrow("Resend answered 429");
  });

  it("the Worker uses Resend when no mailer is given, and a failed send still answers 202", async () => {
    const h = await Harness.create();
    let status = 200;
    h.net.on(RESEND_URL, () => jsonResponse(status, {}));
    const worker = createWorker({ fetch: h.net.fetch, now: () => h.clock.now });
    const send = async () => {
      const ctx = createExecutionContext();
      const r = await worker.fetch!(
        new Request("https://id.pairnets.app/v1/login/email", {
          method: "POST",
          headers: { Origin: "https://id.pairnets.app", "Content-Type": "application/json", "CF-Connecting-IP": "203.0.113.9" },
          body: JSON.stringify({ email: "you@example.com", turnstile: "ok" }),
        }) as Request<unknown, IncomingRequestCfProperties>,
        h.env,
        ctx,
      );
      await waitOnExecutionContext(ctx);
      return r.status;
    };
    expect(await send()).toBe(202);
    const sent = h.net.callsTo(RESEND_URL);
    expect(sent).toHaveLength(1);
    expect(sent[0].headers.get("authorization")).toBe("Bearer test-resend-key");
    expect(JSON.parse(sent[0].body).subject).toBe("Your Pairnets sign-in link");
    status = 500;
    expect(await send()).toBe(202);
  });

  it("notices stop at 95 emails a day and are audited as skipped", async () => {
    const h = await Harness.create();
    const b = h.browser();
    await b.signInByEmail("you@example.com");
    // Use up the day's budget (95 in all, sign-in mails included).
    await h.env.DB.prepare("UPDATE rate_counters SET count = 95 WHERE bucket LIKE 'mail-all:%'").run();
    const before = h.mailer.sent.length;
    await linkNest(h, b);
    expect(h.mailer.sent.length).toBe(before);
    const skipped = await h.query<{ detail: string }>("SELECT detail FROM audit WHERE event = 'notice_skipped'");
    expect(skipped.map((s) => JSON.parse(s.detail).kind)).toEqual(["nest_linked"]);
  });
});
