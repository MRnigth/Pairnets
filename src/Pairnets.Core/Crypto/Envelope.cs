using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Pairnets.Core.Crypto;

/// <summary>A ring taken out of an envelope, and whether it may replace the one this computer has.</summary>
public sealed record OpenedRing(KeyRing Ring, RingCheck Check);

/// <summary>
/// The sealed copies of a ring the nest keeps as opaque blobs (docs/ENCRYPTION.md §2.1): one per trusted computer and
/// one for the recovery key.
/// <list type="bullet">
/// <item>A computer envelope ("PNV1", kind 1) is the ring encrypted to the computer's agreement key (an ephemeral
/// P-256 key, ECDH, HKDF-SHA256, AES-256-GCM) and signed by the computer that made it. Anyone can encrypt to a public
/// key, so the signature is what counts: a computer only takes a ring signed by a computer its own ring trusts, with
/// no older generation and every generation it has unchanged (<see cref="KeyRing.CheckUpdate"/>). So neither the nest
/// nor the service can slip a computer in.</item>
/// <item>The recovery envelope ("PNV1", kind 2) is the ring encrypted with a key from the recovery key and the nest
/// id. Only the holder of the recovery key can make or open it.</item>
/// </list>
/// A computer with no ring yet never takes a computer envelope: it gets its first ring through pairing
/// (<see cref="Pairing"/>) or the recovery key.
/// </summary>
public static class Envelope
{
    private static ReadOnlySpan<byte> Magic => "PNV1"u8;
    private const byte ComputerKind = 1;
    private const byte RecoveryKind = 2;

    public static byte[] SealForComputer(KeyRing ring, byte[] recipientAgreementKey, ComputerKeys signer)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralPublic = ephemeral.ExportSubjectPublicKeyInfo();
        var signerId = Encoding.ASCII.GetBytes(signer.Id);
        var key = AgreedKey(ephemeral, recipientAgreementKey, ephemeralPublic, recipientAgreementKey);

        var head = new MemoryStream();
        head.Write(Magic);
        head.WriteByte(ComputerKind);
        head.WriteByte(checked((byte)signerId.Length));
        head.Write(signerId);
        WriteU16(head, ephemeralPublic.Length);
        head.Write(ephemeralPublic);
        var bound = head.ToArray();

        var plain = ring.ToBytes();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plain, cipher, tag, bound);
        CryptographicOperations.ZeroMemory(plain);

        var body = new MemoryStream();
        body.Write(bound);
        body.Write(nonce);
        WriteU32(body, cipher.Length);
        body.Write(cipher);
        body.Write(tag);
        var signature = signer.Signing.SignData(body.ToArray(), HashAlgorithmName.SHA256);
        WriteU16(body, signature.Length);
        body.Write(signature);
        return body.ToArray();
    }

    /// <summary>
    /// Opens a computer envelope addressed to <paramref name="me"/> and checks it against <paramref name="known"/>,
    /// the ring this computer already trusts. Throws <see cref="CryptographicException"/> if it is damaged, not for
    /// this computer, or the signature is wrong; the result's <see cref="OpenedRing.Check"/> says whether the ring may
    /// be used.
    /// </summary>
    public static OpenedRing OpenForComputer(ReadOnlySpan<byte> envelope, ComputerKeys me, KeyRing known)
    {
        var reader = new Reader(envelope);
        if (!reader.Take(4).SequenceEqual(Magic) || reader.Byte() != ComputerKind)
            throw new CryptographicException("This isn't a computer's key envelope.");
        var signerId = Encoding.ASCII.GetString(reader.Take(reader.Byte()));
        var ephemeralPublic = reader.Take(reader.U16()).ToArray();
        var bound = envelope[..reader.Offset].ToArray();
        var nonce = reader.Take(12).ToArray();
        var cipher = reader.Take(reader.U32()).ToArray();
        var tag = reader.Take(16).ToArray();
        var signed = envelope[..reader.Offset];
        var signature = reader.Take(reader.U16());
        if (!reader.AtEnd)
            throw new CryptographicException("A key envelope is damaged.");

        var signer = known.Computer(signerId);
        if (signer is null)
            return new OpenedRing(known, RingCheck.UntrustedSigner);
        using (var verify = ECDsa.Create())
        {
            verify.ImportSubjectPublicKeyInfo(signer.SigningKey, out _);
            if (!verify.VerifyData(signed, signature, HashAlgorithmName.SHA256))
                throw new CryptographicException("A key envelope's signature is wrong.");
        }

        using var sender = ECDiffieHellman.Create();
        sender.ImportSubjectPublicKeyInfo(ephemeralPublic, out _);
        var myPublic = me.AgreementPublicKey;
        var key = DeriveShared(me.Agreement, sender.PublicKey, ephemeralPublic, myPublic);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(key, 16))
            aes.Decrypt(nonce, cipher, tag, plain, bound);
        var ring = KeyRing.FromBytes(plain);
        CryptographicOperations.ZeroMemory(plain);
        return new OpenedRing(ring, KeyRing.CheckUpdate(known, ring, signerId));
    }

    public static byte[] SealForRecovery(KeyRing ring, RecoveryKey recovery)
    {
        var bound = RecoveryBound(ring.NestId);
        var plain = ring.ToBytes();
        var blob = new byte[5 + 12 + plain.Length + 16];
        Magic.CopyTo(blob);
        blob[4] = RecoveryKind;
        RandomNumberGenerator.Fill(blob.AsSpan(5, 12));
        using (var aes = new AesGcm(recovery.EnvelopeKey(ring.NestId), 16))
            aes.Encrypt(blob.AsSpan(5, 12), plain, blob.AsSpan(17, plain.Length), blob.AsSpan(17 + plain.Length, 16), bound);
        CryptographicOperations.ZeroMemory(plain);
        return blob;
    }

    /// <summary>Opens the recovery envelope; throws <see cref="CryptographicException"/> for a wrong key or a changed envelope.</summary>
    public static KeyRing OpenWithRecovery(ReadOnlySpan<byte> envelope, RecoveryKey recovery, string nestId)
    {
        if (envelope.Length < 5 + 12 + 16 || !envelope[..4].SequenceEqual(Magic) || envelope[4] != RecoveryKind)
            throw new CryptographicException("This isn't a recovery envelope.");
        var plain = new byte[envelope.Length - 5 - 12 - 16];
        using (var aes = new AesGcm(recovery.EnvelopeKey(nestId), 16))
            aes.Decrypt(envelope.Slice(5, 12), envelope.Slice(17, plain.Length), envelope.Slice(17 + plain.Length, 16),
                plain, RecoveryBound(nestId));
        var ring = KeyRing.FromBytes(plain);
        CryptographicOperations.ZeroMemory(plain);
        if (ring.NestId != nestId)
            throw new CryptographicException("This recovery key belongs to another nest.");
        return ring;
    }

    private static byte[] RecoveryBound(string nestId) =>
        [.. Magic, RecoveryKind, .. Encoding.UTF8.GetBytes(nestId)];

    private static byte[] AgreedKey(ECDiffieHellman mine, byte[] otherPublic, byte[] senderPublic, byte[] recipientPublic)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(otherPublic, out _);
        return DeriveShared(mine, other.PublicKey, senderPublic, recipientPublic);
    }

    internal static byte[] DeriveShared(ECDiffieHellman mine, ECDiffieHellmanPublicKey other, byte[] senderPublic,
        byte[] recipientPublic)
    {
        var secret = mine.DeriveKeyFromHash(other, HashAlgorithmName.SHA256);
        var salt = SHA256.HashData([.. senderPublic, .. recipientPublic]);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, "pairnets/v1/envelope"u8.ToArray());
        CryptographicOperations.ZeroMemory(secret);
        return key;
    }

    private static void WriteU16(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, checked((ushort)value));
        s.Write(b);
    }

    private static void WriteU32(Stream s, int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        s.Write(b);
    }

    /// <summary>Bounds-checked reading of a length-prefixed blob; any overrun is a damaged envelope.</summary>
    internal ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Offset { get; private set; }

        public readonly bool AtEnd => Offset == _data.Length;

        public ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || Offset + count > _data.Length)
                throw new CryptographicException("A key envelope is damaged.");
            var span = _data.Slice(Offset, count);
            Offset += count;
            return span;
        }

        public byte Byte() => Take(1)[0];

        public int U16() => BinaryPrimitives.ReadUInt16BigEndian(Take(2));

        public int U32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));
    }
}
