using System.Security.Cryptography;
using System.Text;
using Pairnets.Core.Crypto;
using Pairnets.Core.Hashing;

namespace Pairnets.Tests.Unit;

/// <summary>
/// The encryption core (docs/ENCRYPTION.md): fixed vectors worked out independently (Python's hmac and hashlib),
/// round trips at every segment edge, and tamper tests that must fail closed.
/// </summary>
public class EncryptionTests
{
    private const int Seg = SealedSegments.SegmentSize;

    // generation 0 = bytes 0..31, nest "nest-test", no computers
    private static KeyRing FixedRing() => KeyRing.FromBytes(
        """{"v":1,"nest":"nest-test","gens":[{"n":0,"k":"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="}],"computers":[]}"""u8);

    private static KeyRing NewRing(out ComputerKeys keys, string name = "PC-1")
    {
        keys = ComputerKeys.Create();
        return KeyRing.Create("nest-a", keys.AsTrusted(name));
    }

    private static byte[] Bytes(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[] Encrypt(KeyRing ring, byte[] plain, out SealedHeader header)
    {
        var (h, key) = SealedHeader.Create(ring);
        header = h;
        using var stream = new SealedEncryptStream(new MemoryStream(plain), plain.Length, h, key);
        var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static async Task<byte[]> Decrypt(KeyRing ring, byte[] encrypted)
    {
        var output = new MemoryStream();
        await SealedFile.DecryptAsync(new MemoryStream(encrypted), output, ring);
        return output.ToArray();
    }

    [Fact]
    public void AesGcmIsSupportedHere() => Assert.True(SealedFile.IsSupported);

    [Fact]
    public void PathIdAndTagMatchFixedVectors()
    {
        var ring = FixedRing();
        var id = PathId.Of(ring.IdKey(), "Photos/Café/Ø.jpg");
        Assert.Equal("tidvio6ntxqrqpg4ttf4hvmgz2zymxaq", id);
        Assert.Equal("038738fa6514a67df2a7187ccc5b0b7f7ebae7c89d87f1fd07a2909e87b4734c", // check-secrets: allow
            ContentTag.Of(ring.TagKey(), id, ContentHash.Of("abc"u8)));
    }

    [Fact]
    public void PathIdsAreNfcStableFlatAndShaped()
    {
        var key = FixedRing().IdKey();
        var composed = PathId.Of(key, "Café.txt");
        Assert.Equal(composed, PathId.Of(key, "Café.txt"));
        Assert.NotEqual(composed, PathId.Of(key, "café.txt"));
        Assert.True(PathId.IsValid(composed));
        Assert.False(PathId.IsValid(composed.ToUpperInvariant()));
        Assert.Equal(composed[..2] + "/" + composed, PathId.StoragePath(composed));
        Assert.DoesNotContain("/", composed);
    }

    [Fact]
    public void TagsLookLikeHashesAndDifferByPlace()
    {
        var ring = FixedRing();
        var sha = ContentHash.Of("same content"u8);
        var a = ContentTag.Of(ring.TagKey(), PathId.Of(ring.IdKey(), "a.txt"), sha);
        var b = ContentTag.Of(ring.TagKey(), PathId.Of(ring.IdKey(), "b.txt"), sha);
        Assert.True(ContentHash.IsValid(a));
        Assert.NotEqual(a, b);
        Assert.NotEqual(sha, a);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Seg - 1)]
    [InlineData(Seg)]
    [InlineData(Seg + 1)]
    [InlineData(3 * Seg + 5)]
    public async Task RoundTripsAtEverySegmentEdge(int length)
    {
        var ring = NewRing(out var keys);
        using (keys)
        {
            var plain = Bytes(length);
            var encrypted = Encrypt(ring, plain, out _);
            Assert.Equal(SealedFile.EncryptedLength(length), encrypted.Length);
            Assert.Equal(length, SealedFile.PlainLength(encrypted.Length));
            Assert.Equal(plain, await Decrypt(ring, encrypted));
        }
    }

    [Fact]
    public void EveryUploadGetsNewKeys()
    {
        var ring = NewRing(out var keys);
        using (keys)
        {
            var plain = Bytes(1000);
            Assert.NotEqual(Encrypt(ring, plain, out _), Encrypt(ring, plain, out _));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(SealedHeader.Size)]
    [InlineData(SealedHeader.Size + 7)]
    [InlineData(SealedHeader.Size + Seg + 16)]
    [InlineData(SealedHeader.Size + 2 * (Seg + 16) + 3)]
    public void ResumedUploadMakesTheSameBytes(long offset)
    {
        var ring = NewRing(out var keys);
        using (keys)
        {
            var plain = Bytes(2 * Seg + 300);
            var (header, key) = SealedHeader.Create(ring);
            var full = new MemoryStream();
            using (var s = new SealedEncryptStream(new MemoryStream(plain), plain.Length, header, key))
                s.CopyTo(full);

            // after a cut the app rebuilds the header it kept and continues at the nest's offset
            var kept = SealedHeader.Parse(header.ToBytes());
            var resumed = new MemoryStream();
            using (var s = new SealedEncryptStream(new MemoryStream(plain), plain.Length, kept, kept.UnwrapDataKey(ring), offset))
                s.CopyTo(resumed);
            Assert.Equal(full.ToArray().AsSpan((int)offset).ToArray(), resumed.ToArray());
        }
    }

    [Fact]
    public async Task ResumedDownloadDecryptsFromASegment()
    {
        var ring = NewRing(out var keys);
        using (keys)
        {
            var plain = Bytes(3 * Seg + 11);
            var encrypted = Encrypt(ring, plain, out var header);
            var k = SealedFile.ResumeSegment(2 * Seg + 500);
            Assert.Equal(2, k);
            var output = new MemoryStream();
            await SealedFile.DecryptSegmentsAsync(header, ring,
                new MemoryStream(encrypted, (int)SealedFile.SegmentOffset(k), encrypted.Length - (int)SealedFile.SegmentOffset(k)),
                k, output);
            Assert.Equal(plain.AsSpan((int)(k * Seg)).ToArray(), output.ToArray());
        }
    }

    [Fact]
    public void ChangedFileWhileSendingThrows()
    {
        var ring = NewRing(out var keys);
        using (keys)
        {
            var (header, key) = SealedHeader.Create(ring);
            using var shorter = new SealedEncryptStream(new MemoryStream(Bytes(10)), 20, header, key);
            Assert.Throws<IOException>(() => shorter.CopyTo(Stream.Null));
            using var longer = new SealedEncryptStream(new MemoryStream(Bytes(30)), 20, header, key);
            Assert.Throws<IOException>(() => longer.CopyTo(Stream.Null));
        }
    }

    public static TheoryData<string> Tampers() =>
        ["flip-body", "flip-header", "flip-tag", "swap-segments", "cut-last-segment", "cut-mid", "graft", "extra-bytes"];

    [Theory]
    [MemberData(nameof(Tampers))]
    public async Task TamperingFailsClosed(string how)
    {
        var ring = NewRing(out var keys);
        using (keys)
        {
            var plain = Bytes(3 * Seg + 100);
            var e = Encrypt(ring, plain, out _);
            var other = Encrypt(ring, Bytes(3 * Seg + 100, seed: 2), out _);
            var seg = Seg + 16;
            var s0 = (int)SealedFile.SegmentOffset(0);
            byte[] bad = how switch
            {
                "flip-body" => Flip(e, s0 + 1234),
                "flip-header" => Flip(e, 12),
                "flip-tag" => Flip(e, s0 + seg - 1),
                "swap-segments" => Concat(e.AsSpan(0, s0), e.AsSpan(s0 + seg, seg), e.AsSpan(s0, seg), e.AsSpan(s0 + 2 * seg)),
                "cut-last-segment" => e.AsSpan(0, s0 + 3 * seg).ToArray(),
                "cut-mid" => e.AsSpan(0, e.Length - 50).ToArray(),
                "graft" => Concat(e.AsSpan(0, s0 + seg), other.AsSpan(s0 + seg)),
                "extra-bytes" => Concat(e, new byte[20]),
                _ => throw new ArgumentException(how),
            };
            await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(ring, bad));
        }
    }

    [Fact]
    public async Task AnotherNestsRingCannotOpenAFile()
    {
        var ring = NewRing(out var keys);
        var other = NewRing(out var otherKeys);
        using (keys)
        using (otherKeys)
            await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(other, Encrypt(ring, Bytes(10), out _)));
    }

    [Fact]
    public async Task RotationKeepsOldFilesAndLocksOutTheRemovedComputer()
    {
        var ring = NewRing(out var a);
        using var b = ComputerKeys.Create();
        using (a)
        {
            ring = ring.WithComputer(b.AsTrusted("Stolen laptop"));
            var stolenCopy = ring;
            var old = Encrypt(ring, Bytes(100), out var oldHeader);
            Assert.Equal(0, oldHeader.Generation);

            var rotated = ring.Rotate(b.Id);
            Assert.Equal(1, rotated.Current);
            Assert.Null(rotated.Computer(b.Id));
            Assert.Equal(Bytes(100), await Decrypt(rotated, old));

            var fresh = Encrypt(rotated, Bytes(200), out var newHeader);
            Assert.Equal(1, newHeader.Generation);
            await Assert.ThrowsAnyAsync<CryptographicException>(() => Decrypt(stolenCopy, fresh));

            // the path id and tag stay the same across generations
            Assert.Equal(PathId.Of(ring.IdKey(), "x"), PathId.Of(rotated.IdKey(), "x"));
            Assert.Equal(ring.TagKey(), rotated.TagKey());
            Assert.NotEqual(ring.MetaKey(0), rotated.MetaKey(1));
        }
    }

    [Fact]
    public void MetaRoundTripsPadsAndIsBoundToItsRow()
    {
        var ring = FixedRing();
        var id = PathId.Of(ring.IdKey(), "Docs/plan.txt");
        var meta = new FileMeta("Docs/plan.txt", 1234, 1_760_000_000_000, ContentHash.Of("x"u8), new string('a', 64));
        var blob = MetaBlob.Seal(ring, id, meta);
        Assert.Equal(meta, MetaBlob.Open(ring, id, blob));
        Assert.DoesNotContain("plan", blob);
        Assert.Equal(0, (Convert.FromBase64String(blob).Length - 36) % 64);

        var shortName = MetaBlob.Seal(ring, id, meta with { Path = "a" });
        var longerName = MetaBlob.Seal(ring, id, meta with { Path = "abcdefgh" });
        Assert.Equal(shortName.Length, longerName.Length);

        Assert.ThrowsAny<CryptographicException>(() => MetaBlob.Open(ring, PathId.Of(ring.IdKey(), "other.txt"), blob));
        var raw = Convert.FromBase64String(blob);
        raw[30] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => MetaBlob.Open(ring, id, Convert.ToBase64String(raw)));
        Assert.ThrowsAny<CryptographicException>(() => MetaBlob.Open(ring, id, "not base64!"));
    }

    [Fact]
    public void SwappedBlobsAreCaughtByTheMetaHeaderHash()
    {
        var ring = NewRing(out var keys);
        using (keys)
        {
            Encrypt(ring, Bytes(10), out var a);
            Encrypt(ring, Bytes(10), out var b);
            Assert.NotEqual(a.Hash, b.Hash);
            Assert.Equal(a.Hash, SealedHeader.Parse(a.ToBytes()).Hash);
        }
    }

    [Fact]
    public void RingSurvivesBytes()
    {
        var ring = NewRing(out var keys);
        using (keys)
        {
            var copy = KeyRing.FromBytes(ring.Rotate("nobody").ToBytes());
            Assert.Equal("nest-a", copy.NestId);
            Assert.Equal(1, copy.Current);
            Assert.Equal(keys.AgreementPublicKey, copy.Computer(keys.Id)!.AgreementKey);
            Assert.Equal(ring.IdKey(), copy.IdKey());
            Assert.ThrowsAny<CryptographicException>(() => KeyRing.FromBytes("{}"u8));
            Assert.ThrowsAny<CryptographicException>(() => KeyRing.FromBytes("nonsense"u8));
        }
    }

    [Fact]
    public void ComputerEnvelopeOpensOnlyForItsComputerAndASignerTheRingTrusts()
    {
        var ring = NewRing(out var a);
        using var b = ComputerKeys.Create();
        using var outsider = ComputerKeys.Create();
        using (a)
        {
            ring = ring.WithComputer(b.AsTrusted("PC-2"));
            var envelope = Envelope.SealForComputer(ring, b.AgreementPublicKey, a);

            var opened = Envelope.OpenForComputer(envelope, b, ring);
            Assert.Equal(RingCheck.Ok, opened.Check);
            Assert.Equal(ring.IdKey(), opened.Ring.IdKey());

            // the nest or the service can encrypt to b's public key, but can't sign as a trusted computer
            var forged = Envelope.SealForComputer(KeyRing.Create("nest-a", outsider.AsTrusted("Evil")), b.AgreementPublicKey, outsider);
            Assert.Equal(RingCheck.UntrustedSigner, Envelope.OpenForComputer(forged, b, ring).Check);

            Assert.ThrowsAny<CryptographicException>(() => Envelope.OpenForComputer(envelope, outsider, ring));
            Assert.ThrowsAny<CryptographicException>(() => Envelope.OpenForComputer(Flip(envelope, envelope.Length / 2), b, ring));
            Assert.ThrowsAny<CryptographicException>(() => Envelope.OpenForComputer(envelope.AsSpan(0, 40), b, ring));
        }
    }

    [Fact]
    public void AnOlderOrSwappedRingIsRefused()
    {
        var ring = NewRing(out var a);
        using var b = ComputerKeys.Create();
        using var stolen = ComputerKeys.Create();
        using (a)
        {
            var withStolen = ring.WithComputer(b.AsTrusted("PC-2")).WithComputer(stolen.AsTrusted("Laptop"));
            var replay = Envelope.SealForComputer(withStolen, b.AgreementPublicKey, a);
            var rotated = withStolen.Rotate(stolen.Id);

            // b already has generation 1: the nest hands back the old ring that still trusts the laptop
            Assert.Equal(RingCheck.Older, Envelope.OpenForComputer(replay, b, rotated).Check);

            // the stolen laptop signs a "newer" ring: b's ring no longer trusts it
            var fake = Envelope.SealForComputer(withStolen.Rotate("nobody").Rotate("nobody"), b.AgreementPublicKey, stolen);
            Assert.Equal(RingCheck.UntrustedSigner, Envelope.OpenForComputer(fake, b, rotated).Check);

            // a ring with the same generations but other secrets
            Assert.Equal(RingCheck.Replaced, KeyRing.CheckUpdate(rotated, KeyRing.Create("nest-a", a.AsTrusted("PC-1")).Rotate("x"), a.Id));
            Assert.Equal(RingCheck.OtherNest, KeyRing.CheckUpdate(rotated, KeyRing.Create("nest-b", a.AsTrusted("PC-1")), a.Id));
            Assert.Equal(RingCheck.Ok, KeyRing.CheckUpdate(withStolen, rotated, a.Id));
        }
    }

    [Fact]
    public void RecoveryKeyIsWrittenReadAndChecked()
    {
        var key = RecoveryKey.Create();
        var text = key.ToString();
        Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{4}){7}$", text);
        Assert.Equal(text, RecoveryKey.Parse(text)!.ToString());
        Assert.Equal(text, RecoveryKey.Parse(" " + text.ToLowerInvariant().Replace("-", " ") + " ")!.ToString());
        Assert.Equal(text.Substring(10, 4), key.Group(2));
        Assert.Equal(text, RecoveryKey.Parse(text.Replace('O', '0').Replace('I', '1').Replace('B', '8'))!.ToString());

        var typo = text[..5] + (text[5] == 'A' ? 'C' : 'A') + text[6..];
        Assert.Null(RecoveryKey.Parse(typo));
        Assert.Null(RecoveryKey.Parse(text[..^1]));
        Assert.Null(RecoveryKey.Parse(null));
        Assert.Null(RecoveryKey.Parse(text[..^1] + "9"));
        Assert.NotEqual(text, RecoveryKey.Create().ToString());
    }

    [Fact]
    public void RecoveryEnvelopeOpensOnlyWithItsKeyForItsNest()
    {
        var ring = NewRing(out var a);
        using (a)
        {
            var key = RecoveryKey.Create();
            var envelope = Envelope.SealForRecovery(ring, key);
            Assert.Equal(ring.IdKey(), Envelope.OpenWithRecovery(envelope, RecoveryKey.Parse(key.ToString())!, "nest-a").IdKey());
            Assert.ThrowsAny<CryptographicException>(() => Envelope.OpenWithRecovery(envelope, RecoveryKey.Create(), "nest-a"));
            Assert.ThrowsAny<CryptographicException>(() => Envelope.OpenWithRecovery(envelope, key, "nest-b"));
            Assert.ThrowsAny<CryptographicException>(() => Envelope.OpenWithRecovery(Flip(envelope, 30), key, "nest-a"));
        }
    }

    [Fact]
    public void PairingShowsTheSameCodeAndHandsOverTheRing()
    {
        var ring = NewRing(out var oldKeys);
        using var newKeys = ComputerKeys.Create();
        using (oldKeys)
        {
            using var fresh = new PairingNewComputer(newKeys.AsTrusted("PC-2"));
            using var old = new PairingOldComputer(fresh.Commitment);
            var reveal = fresh.Reveal(old.Offer);
            old.Accept(reveal);
            Assert.Matches("^[0-9]{6}$", fresh.Code!);
            Assert.Equal(fresh.Code, old.Code);
            Assert.Equal("PC-2", old.NewComputer!.Name);

            var sealedRing = old.SealRing(ring.WithComputer(old.NewComputer));
            var got = fresh.OpenRing(sealedRing);
            Assert.Equal(ring.IdKey(), got.IdKey());
            Assert.NotNull(got.Computer(newKeys.Id));

            Assert.ThrowsAny<CryptographicException>(() => fresh.OpenRing(Flip(sealedRing, 20)));
            Assert.Throws<InvalidOperationException>(() => old.SealRing(ring));
        }
    }

    [Fact]
    public void AManInTheMiddleIsSeen()
    {
        using var newKeys = ComputerKeys.Create();
        using var evilKeys = ComputerKeys.Create();

        // the nest swaps the reveal for its own: it doesn't match the commitment
        using (var fresh = new PairingNewComputer(newKeys.AsTrusted("PC-2")))
        using (var old = new PairingOldComputer(fresh.Commitment))
        using (var evil = new PairingNewComputer(evilKeys.AsTrusted("PC-2")))
        {
            fresh.Reveal(old.Offer);
            Assert.ThrowsAny<CryptographicException>(() => old.Accept(evil.Reveal(old.Offer)));
        }

        // the nest runs two separate pairings (its own commitment to the old computer, its own offer to the new one):
        // the two screens show different codes
        var mismatches = 0;
        for (var i = 0; i < 20; i++)
        {
            using var fresh = new PairingNewComputer(newKeys.AsTrusted("PC-2"));
            using var evilOld = new PairingOldComputer(fresh.Commitment);
            using var evilNew = new PairingNewComputer(evilKeys.AsTrusted("PC-2"));
            using var old = new PairingOldComputer(evilNew.Commitment);
            fresh.Reveal(evilOld.Offer);
            old.Accept(evilNew.Reveal(old.Offer));
            if (fresh.Code != old.Code)
                mismatches++;
        }
        Assert.True(mismatches >= 19);
    }

    [Fact]
    public void ARingSentToSomeoneElseIsRefused()
    {
        var ring = NewRing(out var oldKeys);
        using var newKeys = ComputerKeys.Create();
        using var otherKeys = ComputerKeys.Create();
        using (oldKeys)
        {
            using var fresh = new PairingNewComputer(newKeys.AsTrusted("PC-2"));
            using var old = new PairingOldComputer(fresh.Commitment);
            old.Accept(fresh.Reveal(old.Offer));
            // a ring that lists the new computer under another key must not be taken
            var wrong = ring.WithComputer(otherKeys.AsTrusted("PC-2") with { Id = newKeys.Id });
            Assert.ThrowsAny<CryptographicException>(() => fresh.OpenRing(old.SealRing(wrong)));
        }
    }

    private static byte[] Flip(byte[] data, int index)
    {
        var copy = (byte[])data.Clone();
        copy[index] ^= 0x40;
        return copy;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) => [.. a, .. b];

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c, ReadOnlySpan<byte> d) =>
        [.. a, .. b, .. c, .. d];
}
