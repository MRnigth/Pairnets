import { describe, expect, it } from "vitest";
import { limitAddress } from "../src/ratelimit";

describe("limitAddress", () => {
  it("leaves IPv4 and unknown callers as they are", () => {
    expect(limitAddress("203.0.113.7")).toBe("203.0.113.7");
    expect(limitAddress("unknown")).toBe("unknown");
    expect(limitAddress("not an address")).toBe("not an address");
  });

  it("counts every address of an IPv6 /64 as the same caller, whatever its spelling", () => {
    const same = [
      "2001:db8:1:2:3:4:5:6",
      "2001:0db8:0001:0002:ffff:ffff:ffff:ffff",
      "2001:DB8:1:2::",
      "2001:db8:1:2::1",
      "[2001:db8:1:2::abcd]",
      "2001:db8:1:2:aaaa::1%eth0",
      "2001:db8:1:2:0:0:0:0",
    ];
    const key = limitAddress(same[0]);
    expect(key).toBe("2001:db8:1:2::/64");
    for (const a of same) expect(limitAddress(a), a).toBe(key);
  });

  it("tells other /64s apart, and handles compression inside the prefix", () => {
    expect(limitAddress("2001:db8:1:3::1")).toBe("2001:db8:1:3::/64");
    expect(limitAddress("2001:db8::1")).toBe("2001:db8:0:0::/64");
    expect(limitAddress("2001:db8:0:0:9::1")).toBe(limitAddress("2001:db8::7"));
    expect(limitAddress("::1")).toBe("0:0:0:0::/64");
    expect(limitAddress("::")).toBe("0:0:0:0::/64");
    expect(limitAddress("fe80::1")).toBe("fe80:0:0:0::/64");
    expect(limitAddress("1::")).toBe("1:0:0:0::/64");
  });

  it("an IPv4-mapped IPv6 address is the IPv4 address", () => {
    expect(limitAddress("::ffff:203.0.113.7")).toBe("203.0.113.7");
    expect(limitAddress("::FFFF:cb00:7107")).toBe("203.0.113.7");
    expect(limitAddress("0:0:0:0:0:ffff:203.0.113.7")).toBe("203.0.113.7");
    // Other embedded IPv4 (NAT64) is an ordinary IPv6 address.
    expect(limitAddress("64:ff9b::203.0.113.7")).toBe("64:ff9b:0:0::/64");
  });

  it("does not guess at malformed addresses", () => {
    for (const bad of ["2001:db8:::1", "2001:db8:1:2:3:4:5:6:7", "2001:db8:1", "12345::1", "::ffff:300.1.1.1", "g::1", "1::2::3"]) {
      expect(limitAddress(bad), bad).toBe(bad);
    }
  });
});
