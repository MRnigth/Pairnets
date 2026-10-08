// The golden-vector cases of CONTRACT.md sections 8.2 and 8.3, shared by the generator and the check of the
// committed file (so a regenerated file cannot quietly change what a case expects).

export const VECTOR_FIXED = {
  T: 1791504000,
  NEST: "nst_testnest000000000000000001",
  NEST2: "nst_testnest000000000000000002",
  ACC: "acc_testacct000000000000000001",
  ACC2: "acc_testacct000000000000000002",
  N: "TestNonce000000000000A",
  N2: "OtherNonce00000000000A",
  J: "TestJti00000000000000A",
  H0: '{"alg":"ES256","typ":"pn-login+jwt","kid":"test-1"}',
  C0:
    '{"iss":"https://id.pairnets.app","aud":"nst_testnest000000000000000001","sub":"acc_testacct000000000000000001",' +
    '"iat":1791504000,"nbf":1791504000,"exp":1791504090,"nonce":"TestNonce000000000000A","jti":"TestJti00000000000000A",' +
    '"amr":["google"],"auth_time":1791503400,"ver":1}',
} as const;

export interface VectorCase {
  name: string;
  description: string;
  jws: string;
  now: number;
  nonce: string;
  expect: "ok" | "failed" | "clock";
  reason: string | null;
}

export interface VectorFile {
  format: "pairnets-login-assertion-vectors";
  version: 1;
  generator: string;
  issuer: string;
  nestId: string;
  sub: string;
  keys: { kty: "EC"; crv: "P-256"; kid: string; x: string; y: string }[];
  cases: VectorCase[];
}

const T = VECTOR_FIXED.T;
const N = VECTOR_FIXED.N;

type Row = [name: string, description: string, now: number, expect: VectorCase["expect"], reason: string | null, nonce?: string];

const ROWS: Row[] = [
  ["valid", "the reference assertion", T, "ok", null],
  ["valid-next-key", "kid test-2, signed with test-2 (the next key works)", T, "ok", null],
  ["skew-late-ok", "the reference assertion 149 s after iat (inside exp + 60 s)", T + 149, "ok", null],
  ["skew-early-ok", "the reference assertion with the nest clock 60 s behind", T - 60, "ok", null],
  ["wrong-kid", "kid test-9 (not pinned), signed with test-1", T, "failed", "kid"],
  ["alg-none", "alg none, empty signature segment", T, "failed", "alg"],
  ["alg-hs256", "alg HS256, HMAC keyed with test-1's public point 04||x||y", T, "failed", "alg"],
  ["typ-jwt", "typ JWT", T, "failed", "typ"],
  ["crit-header", "extra crit member in the header, validly signed", T, "failed", "header"],
  ["jwk-header", "header carries test-2's public JWK, signed with test-2", T, "failed", "header"],
  ["sig-der", "signature as DER instead of raw r||s", T, "failed", "sig-format"],
  ["sig-noncanonical", "last signature character with an unused bit set", T, "failed", "sig-format"],
  ["sig-std-alphabet", "signature in the standard base64 alphabet (+ and /)", T, "failed", "sig-format"],
  ["payload-padding", "payload segment with = padding", T, "failed", "b64"],
  ["payload-whitespace", "a space inside the payload segment", T, "failed", "b64"],
  ["tampered-payload", "sub changed after signing", T, "failed", "sig"],
  ["wrong-key-same-kid", "kid test-1, signed with test-2's key", T, "failed", "sig"],
  ["duplicate-claim", "sub appears twice in the payload", T, "failed", "claims"],
  ["aud-array", "aud is an array", T, "failed", "claims"],
  ["iat-string", "iat is a string", T, "failed", "claims"],
  ["ver-2", "ver 2", T, "failed", "ver"],
  ["iss-trailing-slash", "iss with a trailing slash", T, "failed", "iss"],
  ["bad-aud", "aud is another nest", T, "failed", "aud"],
  ["bad-sub", "sub is another account", T, "failed", "sub"],
  ["clock-behind", "nest clock 301 s ahead of iat", T + 301, "clock", "clock"],
  ["clock-ahead", "nest clock 301 s behind iat", T - 301, "clock", "clock"],
  ["iat-future", "iat 61 s in the nest's future", T - 61, "failed", "iat"],
  ["lifetime-too-long", "exp = iat + 301", T, "failed", "lifetime"],
  ["nbf-future", "nbf 61 s in the future", T, "failed", "nbf"],
  ["expired", "150 s after iat (exp + 60 s reached)", T + 150, "failed", "exp"],
  ["auth-time-future", "auth_time after iat", T, "failed", "auth_time"],
  ["nonce-mismatch", "the nest expected another nonce", T, "failed", "nonce", VECTOR_FIXED.N2],
  ["amr-empty", "amr is empty", T, "failed", "amr"],
  ["jti-short", "jti too short", T, "failed", "jti"],
];

export const CASES: Omit<VectorCase, "jws">[] = ROWS.map(([name, description, now, expect, reason, nonce]) => ({
  name,
  description,
  now,
  nonce: nonce ?? N,
  expect,
  reason,
}));
