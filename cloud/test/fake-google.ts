// A fake Google: a JWKS with an RSA key made at test time, and a token endpoint that returns an id_token whose
// claims each test controls.

import { b64urlEncode, b64urlEncodeText, utf8 } from "../src/b64";
import { type Browser, GOOGLE_AUTH, GOOGLE_CLIENT_ID, GOOGLE_JWKS, GOOGLE_TOKEN, type Harness, jsonResponse, type Res } from "./helpers";

export interface GoogleClaims {
  [k: string]: unknown;
}

export class FakeGoogle {
  kid = "google-test-key";
  private keys!: CryptoKeyPair;
  /** Changes applied to the next id_token's claims (undefined deletes a claim). */
  claims: GoogleClaims = {};
  header: Record<string, unknown> = {};
  /** Signs with a different key than the one published. */
  forgeSignature = false;
  tokenStatus = 200;
  lastTokenRequest: URLSearchParams | null = null;
  jwksFetches = 0;

  static async install(h: Harness): Promise<FakeGoogle> {
    const g = new FakeGoogle();
    g.keys = (await crypto.subtle.generateKey(
      { name: "RSASSA-PKCS1-v1_5", modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: "SHA-256" },
      true,
      ["sign", "verify"],
    )) as CryptoKeyPair;
    const pub = (await crypto.subtle.exportKey("jwk", g.keys.publicKey)) as JsonWebKey;
    h.net.on(GOOGLE_JWKS, () => {
      g.jwksFetches++;
      return jsonResponse(200, { keys: [{ kty: "RSA", kid: g.kid, n: pub.n, e: pub.e, alg: "RS256", use: "sig" }] });
    });
    h.net.on(GOOGLE_TOKEN, async (req) => {
      g.lastTokenRequest = new URLSearchParams(new TextDecoder().decode(await req.arrayBuffer()));
      if (g.tokenStatus !== 200) return jsonResponse(g.tokenStatus, { error: "invalid_grant" });
      return jsonResponse(200, { id_token: await g.idToken(h), access_token: "unused", token_type: "Bearer" });
    });
    return g;
  }

  nonce = "";

  async idToken(h: Harness): Promise<string> {
    const base: GoogleClaims = {
      iss: "https://accounts.google.com",
      azp: GOOGLE_CLIENT_ID,
      aud: GOOGLE_CLIENT_ID,
      sub: "109876543210987654321",
      email: "you@gmail.com",
      email_verified: true,
      nonce: this.nonce,
      iat: h.clock.now,
      exp: h.clock.now + 3600,
      auth_time: h.clock.now,
    };
    for (const [k, v] of Object.entries(this.claims)) {
      if (v === undefined) delete base[k];
      else base[k] = v;
    }
    const header = { alg: "RS256", kid: this.kid, typ: "JWT", ...this.header };
    const input = `${b64urlEncodeText(JSON.stringify(header))}.${b64urlEncodeText(JSON.stringify(base))}`;
    let key = this.keys.privateKey;
    if (this.forgeSignature) {
      const other = (await crypto.subtle.generateKey(
        { name: "RSASSA-PKCS1-v1_5", modulusLength: 2048, publicExponent: new Uint8Array([1, 0, 1]), hash: "SHA-256" },
        true,
        ["sign", "verify"],
      )) as CryptoKeyPair;
      key = other.privateKey;
    }
    const sig = await crypto.subtle.sign("RSASSA-PKCS1-v1_5", key, utf8(input) as Uint8Array<ArrayBuffer>);
    return `${input}.${b64urlEncode(new Uint8Array(sig))}`;
  }

  /** Runs the whole browser round trip. Returns the start redirect and the callback answer. */
  async signIn(b: Browser, query = ""): Promise<{ start: Res; callback: Res; auth: URL }> {
    const start = await b.call("GET", `/login/google${query}`);
    const auth = new URL(start.location!);
    if (!auth.href.startsWith(GOOGLE_AUTH)) throw new Error(`unexpected redirect ${start.location}`);
    this.nonce = auth.searchParams.get("nonce")!;
    const callback = await b.call("GET", `/login/google/callback?state=${auth.searchParams.get("state")}&code=test-auth-code`);
    return { start, callback, auth };
  }
}
