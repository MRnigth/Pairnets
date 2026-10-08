import { describe, expect, it } from "vitest";
import { b64urlDecodeStrict, b64urlEncode, isStrictB64url } from "../src/b64";
import { newId, randomBase32, B32_UPPER } from "../src/crypto";
import {
  ACCOUNT_ID_RE,
  checkPublicUrl,
  cleanLabel,
  maskEmail,
  NEST_ID_RE,
  normaliseClaimCode,
  normaliseEmail,
  normaliseUserCode,
  safeNext,
  iso,
} from "../src/formats";

describe("claim code normalisation (section 1.3)", () => {
  it("accepts every typed form of the contract's examples", () => {
    for (const typed of ["pn-abcd-efgh-jkmn", "ABCDEFGHJKMN", "PN ABCD EFGH JKMN", "PNABCDEFGHJKMN", "\tpn-ABCD efgh-JKMN "]) {
      expect(normaliseClaimCode(typed)).toBe("PN-ABCD-EFGH-JKMN");
    }
  });

  it("maps look-alikes and refuses U and short codes", () => {
    expect(normaliseClaimCode("PN-ABCD-EFGH-JKMO")).toBe("PN-ABCD-EFGH-JKM0");
    expect(normaliseClaimCode("PN-IBCD-EFGH-JKML")).toBe("PN-1BCD-EFGH-JKM1");
    expect(normaliseClaimCode("PN-ABCD-EFGH-JKMU")).toBeNull();
    expect(normaliseClaimCode("PN-ABCD-EFGH-JKM")).toBeNull();
    expect(normaliseClaimCode("PN-ABCD-EFGH-JKMN-")).toBe("PN-ABCD-EFGH-JKMN");
    expect(normaliseClaimCode("PN_ABCD_EFGH_JKMN")).toBeNull();
    expect(normaliseClaimCode("PN-ÀBCD-EFGH-JKMN")).toBeNull();
    expect(normaliseClaimCode(42)).toBeNull();
  });

  it("only drops PN when exactly 14 characters remain", () => {
    expect(normaliseClaimCode("PNAB-CDEF-GHJK")).toBe("PN-PNAB-CDEF-GHJK");
  });
});

describe("app user codes (section 1.4)", () => {
  it("normalises like claim codes without the PN rule", () => {
    expect(normaliseUserCode("abcd-efgh")).toBe("ABCDEFGH");
    expect(normaliseUserCode("ABCD EFGO")).toBe("ABCDEFG0");
    expect(normaliseUserCode("ABCD-EFGU")).toBeNull();
    expect(normaliseUserCode("ABCD-EFG")).toBeNull();
  });
});

describe("ids and random characters", () => {
  it("makes ids of the contract's shape", () => {
    for (let i = 0; i < 50; i++) {
      expect(newId("acc")).toMatch(ACCOUNT_ID_RE);
      expect(newId("nst")).toMatch(NEST_ID_RE);
    }
    expect(randomBase32(12, B32_UPPER)).toMatch(/^[0-9ABCDEFGHJKMNPQRSTVWXYZ]{12}$/);
  });
});

describe("strict base64url (section 1.7)", () => {
  it("round-trips and refuses padding, other alphabets and unused bits", () => {
    const bytes = new Uint8Array([251, 255, 0, 1, 2]);
    const s = b64urlEncode(bytes);
    expect(s).toBe("-_8AAQI");
    expect([...b64urlDecodeStrict(s)!]).toEqual([...bytes]);
    expect(b64urlDecodeStrict("-_8AAQI=")).toBeNull();
    expect(b64urlDecodeStrict("+/8AAQI")).toBeNull();
    expect(b64urlDecodeStrict("-_8AAQJ")).toBeNull(); // non-zero unused bits
    expect(b64urlDecodeStrict("A")).toBeNull(); // length % 4 == 1
    expect(b64urlDecodeStrict("AA A")).toBeNull();
    expect(b64urlDecodeStrict("")).toEqual(new Uint8Array(0));
    expect(isStrictB64url("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8", 32)).toBe(true);
  });
});

describe("public URL (section 1.8)", () => {
  it("accepts a normalised https origin", () => {
    expect(checkPublicUrl("https://nest.example.com")).toEqual({ url: "https://nest.example.com", host: "nest.example.com" });
    expect(checkPublicUrl("https://nest.example.com:8443")).toEqual({ url: "https://nest.example.com:8443", host: "nest.example.com:8443" });
    expect(checkPublicUrl("https://home.pairnets.app")?.url).toBe("https://home.pairnets.app");
    expect(checkPublicUrl("https://xn--bcher-kva.example")?.url).toBe("https://xn--bcher-kva.example");
  });

  it("refuses everything else", () => {
    for (const bad of [
      "http://nest.example.com",
      "https://nest.example.com/",
      "https://nest.example.com/path",
      "https://nest.example.com?x=1",
      "https://nest.example.com#x",
      "https://Nest.Example.com",
      "https://nest.example.com:443",
      "https://user@nest.example.com",
      "https://bücher.example",
      "https://192.0.2.1",
      "https://[2001:db8::1]",
      "https://nest",
      "https://localhost",
      "https://pairnets.app",
      "https://www.pairnets.app",
      "https://id.pairnets.app",
      "https://pairnets.app.",
      "https://a.localhost",
      `https://${"a".repeat(190)}.example.com`,
      "",
      "nest.example.com",
    ]) {
      expect(checkPublicUrl(bad), bad).toBeNull();
    }
    expect(checkPublicUrl(42)).toBeNull();
  });
});

describe("next allow-list (section 6.4)", () => {
  it("keeps allowed paths and turns the rest into /account", () => {
    expect(safeNext("/account")).toBe("/account");
    expect(safeNext("/app")).toBe("/app");
    expect(safeNext("/app?code=ABCD-EFGH")).toBe("/app?code=ABCD-EFGH");
    expect(safeNext("/nest-login?nest=x&nonce=y")).toBe("/nest-login?nest=x&nonce=y");
    for (const bad of ["//evil.example", "https://evil.example", "/account/../x", "/nest-login", "/nest-login?a b", "/app?code=<x>", "/login", null, 3]) {
      expect(safeNext(bad)).toBe("/account");
    }
  });
});

describe("small formats", () => {
  it("masks emails in ASCII", () => {
    expect(maskEmail("you@gmail.com")).toBe("y***@gmail.com");
    expect(maskEmail("élan@example.com")).toBe("****@example.com");
    expect(maskEmail("x@bücher.example")).toBe("x***@xn--bcher-kva.example");
  });

  it("cleans labels and emails", () => {
    expect(cleanLabel("  Home\u0000 nest \n")).toBe("Home nest");
    expect(cleanLabel("   ")).toBeNull();
    expect(cleanLabel("x".repeat(65))).toBe("invalid");
    expect(cleanLabel(5)).toBe("invalid");
    expect(normaliseEmail(" You@Example.COM ")).toBe("you@example.com");
    expect(normaliseEmail("not an email")).toBeNull();
    expect(normaliseEmail("a@b")).toBeNull();
  });

  it("writes ISO times with seconds", () => {
    expect(iso(1791504000)).toBe("2026-10-09T00:00:00Z");
  });
});
