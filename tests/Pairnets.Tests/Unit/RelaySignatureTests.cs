using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Pairnets.Core;
using Pairnets.Server;
using Pairnets.Server.Auth;
using Pairnets.Tests.Infrastructure;
using static Pairnets.Tests.Infrastructure.RelayFixtures;

namespace Pairnets.Tests.Unit;

/// <summary>
/// A nest linked to Pairnets (cloud/RELAY.md §3 and §4): its settings, strict base64url, and the "ra1" check of the
/// service's signed calls, every way it can fail.
/// </summary>
public class RelaySignatureTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_791_504_000);

    private const string Path = "/api/relay/devices";

    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"name":"Laptop","system":"Windows 11","approvedBy":"m***@gmail.com"}""");

    private static SyncOptions Linked() => new() { Token = new string('x', 32), RelayNestId = NestId, RelayKey = Key };

    private static RelaySignature Verifier(TimeProvider clock) => new(Linked(), clock);

    /// <summary>The headers the service sends for one call.</summary>
    private sealed record Call(string Nest, string Time, string Nonce, string Sig, string Method = "POST", string PathAndQuery = Path)
    {
        public byte[] Body { get; init; } = RelaySignatureTests.Body;

        public RelaySignature.Outcome CheckWith(RelaySignature verifier) => verifier.Check(Nest, Time, Nonce, Sig, Method, PathAndQuery, Body);
    }

    private static Call Sign(long? time = null, string? nonce = null, string method = "POST", string path = Path, byte[]? body = null,
        byte[]? key = null, string nest = NestId)
    {
        var ts = (time ?? Now.ToUnixTimeSeconds()).ToString(System.Globalization.CultureInfo.InvariantCulture);
        nonce ??= NewNonce();
        var sig = RelaySignature.Sign(key ?? KeyBytes, RelaySignature.Message(nest, ts, nonce, method, path, body ?? Body));
        return new Call(nest, ts, nonce, sig, method, path) { Body = body ?? Body };
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public void BothRelaySettingsOrNeither()
    {
        Assert.Null(new SyncOptions { Token = new string('x', 32) }.ValidateForServe());
        Assert.Null(Linked().ValidateForServe());
        Assert.True(Linked().RelayMode);

        var idOnly = new SyncOptions { Token = new string('x', 32), RelayNestId = NestId }.ValidateForServe();
        Assert.Contains("Sync:RelayNestId and Sync:RelayKey go together", idOnly);
        var keyOnly = new SyncOptions { Token = new string('x', 32), RelayKey = Key }.ValidateForServe();
        Assert.Contains("Sync:RelayNestId and Sync:RelayKey go together", keyOnly);
        Assert.DoesNotContain(Key, keyOnly);
    }

    [Theory]
    [InlineData("nst_testnest00000000000000001")] // 25 characters
    [InlineData("nst_testnest0000000000000000001")] // 27
    [InlineData("nst_TESTNEST000000000000000001")] // upper case
    [InlineData("nst_testnest00000000000000000i")] // i, l, o and u are not in the alphabet
    [InlineData("nst_testnest00000000000000000u")]
    [InlineData("acc_testnest000000000000000001")]
    [InlineData("nst-testnest000000000000000001")]
    public void ANestIdMustHaveTheServicesShape(string id)
    {
        var error = Linked().ValidateRelayWith(id, Key);
        Assert.Contains("Sync:RelayNestId must be nst_", error);
        Assert.Contains(id, error);
    }

    [Fact]
    public void TheKeyMustBeStrictBase64UrlOf32Bytes()
    {
        var almost = StrictBase64Url.Encode(new byte[31]);
        var tooLong = StrictBase64Url.Encode(new byte[33]);
        // The last character of 32 bytes carries 2 unused bits; any of them set is a different text for the same bytes.
        var unusedBits = Key[..42] + (char)(Key[42] + 1);
        string[] wrong = [almost, tooLong, Key + "=", "+" + Key[1..], Key[..42] + "/", unusedBits, Key[..20] + " " + Key[21..]];
        foreach (var key in wrong)
        {
            var error = new SyncOptions().ValidateRelayWith(NestId, key);
            Assert.True(error?.Contains("Sync:RelayKey must be the key Pairnets gave this server") == true, $"{key}: {error}");
            Assert.DoesNotContain(key, error); // a key, even a broken one, is never repeated
        }
        Assert.Null(new SyncOptions().ValidateRelayWith(NestId, Key));
    }

    [Fact]
    public void ALinkedNestHasNoPublicNameOfItsOwn()
    {
        var options = Linked();
        options.PublicUrl = "https://nest.example.com";
        Assert.Contains("must stay unset on a nest linked to Pairnets", options.ValidateForServe());
    }

    [Theory]
    [InlineData("https://sync.pairnets.app", true)]
    [InlineData("https://sync.example.com:8443", true)]
    [InlineData("http://127.0.0.1:5999", true)] // a test service on this machine
    [InlineData("http://sync.example.com", false)]
    [InlineData("https://sync.example.com/path", false)]
    [InlineData("https://user@sync.example.com", false)]
    [InlineData("sync.example.com", false)]
    public void TheServiceAddressIsHttpsWithoutAPath(string url, bool ok)
    {
        var options = Linked();
        options.RelayServiceUrl = url;
        var error = options.ValidateForServe();
        if (ok)
            Assert.Null(error);
        else
            Assert.Contains("Sync:RelayServiceUrl must look like https://sync.pairnets.app", error);
    }

    [Fact]
    public void SettingsAreReadFromTheEnvironmentNames()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sync:Token"] = new string('x', 32),
            ["Sync:RelayNestId"] = " " + NestId + " ",
            ["Sync:RelayKey"] = Key + "\n",
            ["Sync:RelayServiceUrl"] = "",
        }).Build();
        var options = SyncOptions.FromConfiguration(config);
        Assert.Equal((NestId, Key, SyncOptions.DefaultRelayServiceUrl), (options.RelayNestId, options.RelayKey, options.RelayServiceUrl));
        Assert.Null(options.ValidateForServe());
        Assert.Equal("sync.pairnets.app", options.RelayServiceHost);

        var blank = SyncOptions.FromConfiguration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Sync:RelayNestId"] = " ", ["Sync:RelayKey"] = "" }).Build());
        Assert.False(blank.RelayMode);
        Assert.Null(blank.RelayNestId);
    }

    [Fact]
    public void TheServerDoesNotStartWithHalfARelaySetting()
    {
        var error = Assert.Throws<InvalidOperationException>(() => PairnetsServerHost.Build([], b =>
            b.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sync:Token"] = new string('x', 32),
                ["Sync:DataDir"] = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pairnets-tests", "never-created"),
                ["Sync:RelayNestId"] = NestId,
            })));
        Assert.Contains("Sync:RelayNestId and Sync:RelayKey go together", error.Message);
    }

    // ------------------------------------------------------------------ strict base64url

    [Theory]
    [InlineData("", 0)]
    [InlineData("AA", 1)]
    [InlineData("AAA", 2)]
    [InlineData("AAAA", 3)]
    [InlineData("_-8", 2)]
    public void StrictBase64UrlTakesWhatEncodesBackToItself(string text, int length)
    {
        Assert.True(StrictBase64Url.TryDecode(text, out var bytes));
        Assert.Equal(length, bytes.Length);
        Assert.Equal(text, StrictBase64Url.Encode(bytes));
    }

    [Theory]
    [InlineData("A")] // length % 4 == 1
    [InlineData("AA==")] // padding
    [InlineData("AB")] // unused bits set: "AA" and "AB" would be the same byte
    [InlineData("+/8")]
    [InlineData("AA A")]
    [InlineData("AA\n")]
    [InlineData(null)]
    public void StrictBase64UrlRefusesTheRest(string? text)
    {
        Assert.False(StrictBase64Url.TryDecode(text, out var bytes));
        Assert.Empty(bytes);
    }

    // ------------------------------------------------------------------ the ra1 check

    [Fact]
    public void TheSignatureMatchesTheWorkedExamples()
    {
        // Worked examples made with openssl (cloud/RELAY.md §4). The key is bytes 00 01 … 1f, the public test key of
        // cloud/CONTRACT.md §5.6; the nonces are bytes 00 … 0f and 10 … 1f.
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var nonce1 = StrictBase64Url.Encode(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
        var nonce2 = StrictBase64Url.Encode(Enumerable.Range(16, 16).Select(i => (byte)i).ToArray());
        Assert.Equal("AAECAwQFBgcICQoLDA0ODw", nonce1);
        Assert.Equal(69, Body.Length);

        Assert.Equal("k40tvOTQMypQF3ThgisEKHCdjLJCkP495Wbrh1ZQP7M",
            RelaySignature.Sign(key, RelaySignature.Message(NestId, "1791504000", nonce1, "POST", "/api/relay/devices", Body)));
        Assert.Equal("GxyX-jrLv6v9uZpV5k0n_JnZSiH6I8He2oKOnVdRvQo",
            RelaySignature.Sign(key, RelaySignature.Message(NestId, "1791504001", nonce2, "GET", "/api/relay/status", [])));
        Assert.Equal(
            "ra1\nnst_testnest000000000000000001\n1791504001\n" + nonce2 + "\nGET /api/relay/status\n" +
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", // SHA-256 of nothing
            Encoding.UTF8.GetString(RelaySignature.Message(NestId, "1791504001", nonce2, "get", "/api/relay/status", [])));
    }

    [Fact]
    public void AGoodCallPasses()
    {
        var clock = new ManualClock(Now);
        var verifier = Verifier(clock);
        Assert.Equal(RelaySignature.Outcome.Ok, Sign().CheckWith(verifier));
        Assert.Equal(RelaySignature.Outcome.Ok, Sign(method: "GET", path: "/api/relay/status", body: []).CheckWith(verifier));
        Assert.Equal(RelaySignature.Outcome.Ok, Sign(method: "DELETE", path: "/api/relay/devices/abc", body: []).CheckWith(verifier));
    }

    [Fact]
    public void ACallForAnotherNestOrToANestThatIsNotLinkedFails()
    {
        var clock = new ManualClock(Now);
        Assert.Equal(RelaySignature.Outcome.WrongNest, Sign(nest: OtherNestId).CheckWith(Verifier(clock)));
        var call = Sign();
        Assert.Equal(RelaySignature.Outcome.WrongNest, (call with { Nest = OtherNestId }).CheckWith(Verifier(clock)));
        Assert.Equal(RelaySignature.Outcome.WrongNest, call.CheckWith(new RelaySignature(new SyncOptions(), clock)));
    }

    [Fact]
    public void ABadSignatureFails()
    {
        var verifier = Verifier(new ManualClock(Now));
        var otherKey = KeyBytes.Select(b => (byte)(b ^ 0x5a)).ToArray();
        Assert.Equal(RelaySignature.Outcome.BadSignature, Sign(key: otherKey).CheckWith(verifier));

        var call = Sign();
        var flipped = (call.Sig[0] == 'A' ? 'B' : 'A') + call.Sig[1..];
        string[] broken = [flipped, call.Sig + "=", call.Sig[..42], "", "+" + call.Sig[1..]];
        foreach (var sig in broken)
            Assert.Equal(RelaySignature.Outcome.BadSignature, (call with { Sig = sig }).CheckWith(verifier));
        Assert.Equal(RelaySignature.Outcome.Ok, call.CheckWith(verifier));
    }

    [Fact]
    public void ATamperedBodyPathQueryOrMethodFails()
    {
        var verifier = Verifier(new ManualClock(Now));
        var call = Sign();
        Assert.Equal(RelaySignature.Outcome.BadSignature, (call with { Body = Encoding.UTF8.GetBytes("""{"name":"Mallory"}""") }).CheckWith(verifier));
        Assert.Equal(RelaySignature.Outcome.BadSignature, (call with { PathAndQuery = "/api/relay/devices/other" }).CheckWith(verifier));
        Assert.Equal(RelaySignature.Outcome.BadSignature, (call with { PathAndQuery = Path + "?x=1" }).CheckWith(verifier));
        Assert.Equal(RelaySignature.Outcome.BadSignature, (call with { Method = "DELETE" }).CheckWith(verifier));
        Assert.Equal(RelaySignature.Outcome.BadSignature, (call with { Time = (Now.ToUnixTimeSeconds() + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) }).CheckWith(verifier));
        Assert.Equal(RelaySignature.Outcome.BadSignature, (call with { Nonce = NewNonce() }).CheckWith(verifier));
        // Nothing above was accepted, so the nonce was never used up: the real call still passes once.
        Assert.Equal(RelaySignature.Outcome.Ok, call.CheckWith(verifier));
    }

    [Theory]
    [InlineData("01791504000")] // leading zero
    [InlineData("+1791504000")]
    [InlineData(" 1791504000")]
    [InlineData("1791504000.0")]
    [InlineData("")]
    public void TheTimeIsPlainDecimalSeconds(string time)
    {
        var verifier = Verifier(new ManualClock(Now));
        var nonce = NewNonce();
        var sig = RelaySignature.Sign(KeyBytes, RelaySignature.Message(NestId, time, nonce, "POST", Path, Body));
        Assert.Equal(RelaySignature.Outcome.BadSignature, new Call(NestId, time, nonce, sig).CheckWith(verifier));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("AAECAwQFBgcICQoLDA0ODx")] // 22 characters, but unused bits set
    [InlineData("AAECAwQFBgcICQoLDA0ODw==")]
    public void TheNonceIs16BytesOfBase64Url(string nonce)
    {
        var verifier = Verifier(new ManualClock(Now));
        Assert.Equal(RelaySignature.Outcome.BadSignature, Sign(nonce: nonce).CheckWith(verifier));
    }

    [Fact]
    public void ACallOlderOrNewerThanTwoMinutesFails()
    {
        var clock = new ManualClock(Now);
        var verifier = Verifier(clock);
        var at = Now.ToUnixTimeSeconds();
        Assert.Equal(RelaySignature.Outcome.Stale, Sign(time: at - 121).CheckWith(verifier));
        Assert.Equal(RelaySignature.Outcome.Stale, Sign(time: at + 121).CheckWith(verifier));
        Assert.Equal(RelaySignature.Outcome.Ok, Sign(time: at - 120).CheckWith(verifier));
        Assert.Equal(RelaySignature.Outcome.Ok, Sign(time: at + 120).CheckWith(verifier));

        // A good call that waited too long on the way is refused, and its nonce is not used up by that.
        var late = Sign();
        clock.Advance(TimeSpan.FromSeconds(121));
        Assert.Equal(RelaySignature.Outcome.Stale, late.CheckWith(verifier));
    }

    [Fact]
    public void ACallCanBeUsedOnlyOnce()
    {
        var clock = new ManualClock(Now);
        var verifier = Verifier(clock);
        var call = Sign();
        Assert.Equal(RelaySignature.Outcome.Ok, call.CheckWith(verifier));
        Assert.Equal(RelaySignature.Outcome.Replayed, call.CheckWith(verifier));
        clock.Advance(TimeSpan.FromSeconds(100));
        Assert.Equal(RelaySignature.Outcome.Replayed, call.CheckWith(verifier));

        // The same nonce in a new call (new time, new signature) is refused too, for ten minutes.
        Assert.Equal(RelaySignature.Outcome.Replayed, Sign(time: clock.Now.ToUnixTimeSeconds(), nonce: call.Nonce).CheckWith(verifier));
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(RelaySignature.Outcome.Ok, Sign(time: clock.Now.ToUnixTimeSeconds(), nonce: call.Nonce).CheckWith(verifier));
    }

    [Fact]
    public void TheNonceMemoryKeepsAtMostItsCapacityOldestFirst()
    {
        var memory = new NonceMemory(TimeSpan.FromMinutes(10), capacity: 3);
        Assert.True(memory.TryRemember("a", Now));
        Assert.True(memory.TryRemember("b", Now.AddSeconds(1)));
        Assert.True(memory.TryRemember("c", Now.AddSeconds(2)));
        Assert.False(memory.TryRemember("b", Now.AddSeconds(3)));
        Assert.True(memory.TryRemember("d", Now.AddSeconds(4))); // "a" goes
        Assert.Equal(3, memory.Count);
        Assert.False(memory.TryRemember("c", Now.AddSeconds(5)));
        Assert.True(memory.TryRemember("a", Now.AddSeconds(6)));

        // Forgotten after the lifetime.
        Assert.True(memory.TryRemember("d", Now.AddMinutes(11)));
        Assert.Equal(1, memory.Count);
        Assert.Equal(10_000, RelaySignature.MaxNonces);
        Assert.Equal(TimeSpan.FromMinutes(10), RelaySignature.NonceLifetime);
    }

    // ------------------------------------------------------------------ hello

    [Fact]
    public void HelloCarriesTheRelayOnlyWhenThereIsOne()
    {
        var plain = JsonSerializer.Serialize(new ServerHello("Pairnets", 1, "1.0.48", "https://nest.example.com", true, true), PairnetsJson.Options);
        Assert.DoesNotContain("relay", plain);

        var linked = new ServerHello("Pairnets", 1, "1.0.48", null, true, false, new SignInMethods(false, false, false, false),
            new RelayHello(NestId, "https://sync.pairnets.app"));
        var json = JsonSerializer.Serialize(linked, PairnetsJson.Options);
        Assert.Contains("\"relay\":{\"nestId\":\"" + NestId + "\",\"serviceUrl\":\"https://sync.pairnets.app\"}", json);
        Assert.Contains("\"publicUrl\":null", json);
        Assert.Equal(linked, JsonSerializer.Deserialize<ServerHello>(json, PairnetsJson.Options));
        Assert.Null(JsonSerializer.Deserialize<ServerHello>(plain, PairnetsJson.Options)!.Relay);
    }
}

/// <summary>Checks one pair of relay settings without building a whole options object in every test.</summary>
internal static class RelayOptionsTestExtensions
{
    public static string? ValidateRelayWith(this SyncOptions options, string id, string key)
    {
        var copy = new SyncOptions { Token = new string('x', 32), RelayNestId = id, RelayKey = key, RelayServiceUrl = options.RelayServiceUrl };
        return copy.ValidateForServe();
    }
}
