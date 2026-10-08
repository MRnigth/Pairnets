import { describe, expect, it } from "vitest";
import { ORIGIN_EXEMPT } from "../src/app";
import { Harness } from "./helpers";

const MUTATIONS: [string, string][] = [
  ["POST", "/v1/login/email"],
  ["POST", "/v1/login/email/confirm"],
  ["DELETE", "/v1/me"],
  ["POST", "/v1/nests/claim-codes"],
  ["DELETE", "/v1/nests/claim-codes"],
  ["PATCH", "/v1/nests/nst_testnest000000000000000001"],
  ["DELETE", "/v1/nests/nst_testnest000000000000000001"],
  ["DELETE", "/v1/sessions/ses_testsess000000000000000001"],
  ["POST", "/v1/logout"],
  ["POST", "/v1/app/approve"],
  ["PUT", "/v1/me"],
  ["POST", "/account"],
  ["POST", "/login"],
];

describe("csrf", () => {
  it("every mutation needs the exact Origin", async () => {
    const h = await Harness.create();
    const b = h.browser();
    await b.signInByEmail("you@example.com");
    for (const [method, path] of MUTATIONS) {
      for (const origin of [null, "https://nest.pairnets.app", "http://id.pairnets.app", "https://id.pairnets.app.evil.example", "null", "https://ID.pairnets.app"]) {
        const r = await b.call(method, path, { origin, body: {} });
        expect(r.status, `${method} ${path} from ${origin}`).toBe(403);
        if (path.startsWith("/v1/")) expect(r.json).toEqual({ error: "bad_origin", message: expect.any(String) });
      }
    }
    // The session is untouched by all of that.
    expect((await b.call("GET", "/v1/me")).status).toBe(200);
  });

  it("only the server and app endpoints are exempt, and they work without Origin", async () => {
    expect([...ORIGIN_EXEMPT].sort()).toEqual(
      ["/v1/app/logout", "/v1/app/poll", "/v1/app/start", "/v1/claim", "/v1/heartbeat", "/v1/nest/confirm", "/v1/nest/unlink"].sort(),
    );
    const h = await Harness.create();
    const c = h.client();
    expect((await c.call("POST", "/v1/claim", { body: {} })).status).toBe(400);
    expect((await c.call("POST", "/v1/app/start", { body: { name: "Laptop" } })).status).toBe(200);
    expect((await c.call("POST", "/v1/app/poll", { body: { deviceCode: "x" } })).status).toBe(400);
    expect((await c.call("POST", "/v1/app/logout")).status).toBe(204);
    expect((await c.call("POST", "/v1/heartbeat", { body: {} })).status).toBe(410);
    expect((await c.call("POST", "/v1/nest/confirm", { body: {} })).status).toBe(410);
    expect((await c.call("POST", "/v1/nest/unlink", { body: {} })).status).toBe(410);
  });

  it("exempt endpoints never act on the browser's cookie", async () => {
    const h = await Harness.create();
    const b = h.browser();
    await b.signInByEmail("you@example.com");
    // POST /v1/app/logout with only a browser cookie (no Bearer token): nothing happens to the browser session.
    expect((await b.call("POST", "/v1/app/logout")).status).toBe(204);
    expect((await b.call("GET", "/v1/me")).status).toBe(200);
  });
});
