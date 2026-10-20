using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pairnets.Core.Crypto;

/// <summary>
/// Unlocking a new computer with one that already has the ring (docs/ENCRYPTION.md §2.6). The nest only carries the
/// messages (its <c>/api/keys/requests</c> mailbox), so it must not be able to sit in the middle:
/// <list type="number">
/// <item>The new computer sends only a commitment: a hash of its ephemeral key, a nonce and its own two public keys.</item>
/// <item>The old computer answers with its ephemeral key and nonce (the offer).</item>
/// <item>The new computer reveals what it committed to; the old computer checks it against the commitment.</item>
/// <item>Both show the same 6-digit code, made from everything both sent. The user presses "Yes, same code" on the old
/// computer. A man in the middle has to pick his keys before he sees the other side's, so he matches with a chance of
/// one in a million, and gets one try.</item>
/// <item>The old computer adds the new one to the ring and sends it encrypted and authenticated with a key from the
/// ECDH of the two ephemeral keys.</item>
/// </list>
/// </summary>
public static class Pairing
{
    internal const int NonceLength = 16;

    internal static string Code(byte[] reveal, byte[] offer)
    {
        var hash = SHA256.HashData([.. "pairnets/v1/code"u8, .. Framed(reveal), .. Framed(offer)]);
        return (BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000).ToString("D6");
    }

    internal static byte[] Commitment(byte[] reveal) => SHA256.HashData([.. "pairnets/v1/commit"u8, .. reveal]);

    internal static byte[] SessionKey(ECDiffieHellman mine, byte[] otherEphemeral, byte[] reveal, byte[] offer)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(otherEphemeral, out _);
        var secret = mine.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256);
        var salt = SHA256.HashData([.. Framed(reveal), .. Framed(offer)]);
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, salt, "pairnets/v1/pair"u8.ToArray());
        CryptographicOperations.ZeroMemory(secret);
        return key;
    }

    internal static byte[] Framed(ReadOnlySpan<byte> part)
    {
        var framed = new byte[4 + part.Length];
        BinaryPrimitives.WriteInt32BigEndian(framed, part.Length);
        part.CopyTo(framed.AsSpan(4));
        return framed;
    }

    internal static byte[] Join(params byte[][] parts) => parts.SelectMany(p => Framed(p)).ToArray();

    internal static byte[][] Split(ReadOnlySpan<byte> data, int count)
    {
        var parts = new byte[count][];
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            if (offset + 4 > data.Length)
                throw new CryptographicException("A pairing message is damaged.");
            var length = BinaryPrimitives.ReadInt32BigEndian(data[offset..]);
            offset += 4;
            if (length < 0 || offset + length > data.Length)
                throw new CryptographicException("A pairing message is damaged.");
            parts[i] = data.Slice(offset, length).ToArray();
            offset += length;
        }
        if (offset != data.Length)
            throw new CryptographicException("A pairing message is damaged.");
        return parts;
    }

    internal static readonly byte[] RingLabel = "pairnets/v1/pair-ring"u8.ToArray();
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>The new computer's side of <see cref="Pairing"/>.</summary>
public sealed class PairingNewComputer : IDisposable
{
    private readonly ECDiffieHellman _ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly TrustedComputer _me;
    private readonly byte[] _reveal;
    private byte[]? _sessionKey;

    public PairingNewComputer(TrustedComputer me)
    {
        _me = me;
        _reveal = Pairing.Join(_ephemeral.ExportSubjectPublicKeyInfo(), RandomNumberGenerator.GetBytes(Pairing.NonceLength),
            JsonSerializer.SerializeToUtf8Bytes(me, Pairing.Json));
        Commitment = Pairing.Commitment(_reveal);
    }

    /// <summary>Step 1: what the new computer posts to the nest's mailbox first.</summary>
    public byte[] Commitment { get; }

    /// <summary>The 6 digits to show, once the offer has arrived.</summary>
    public string? Code { get; private set; }

    /// <summary>Step 3: takes the old computer's offer and returns the reveal to post.</summary>
    public byte[] Reveal(byte[] offer)
    {
        var parts = Pairing.Split(offer, 2);
        if (parts[1].Length != Pairing.NonceLength)
            throw new CryptographicException("A pairing message is damaged.");
        _sessionKey = Pairing.SessionKey(_ephemeral, parts[0], _reveal, offer);
        Code = Pairing.Code(_reveal, offer);
        return _reveal;
    }

    /// <summary>
    /// Step 5: opens the ring the old computer sent. Throws <see cref="CryptographicException"/> if it didn't come
    /// from the computer this one paired with, or if the ring doesn't list this computer with its own keys.
    /// </summary>
    public KeyRing OpenRing(byte[] sealedRing)
    {
        if (_sessionKey is null)
            throw new InvalidOperationException("The offer hasn't arrived yet.");
        var ring = KeyRing.FromBytes(PairingSeal.Open(_sessionKey, sealedRing));
        var listed = ring.Computer(_me.Id);
        if (listed is null || !listed.AgreementKey.AsSpan().SequenceEqual(_me.AgreementKey)
            || !listed.SigningKey.AsSpan().SequenceEqual(_me.SigningKey))
            throw new CryptographicException("The ring that came back doesn't list this computer.");
        return ring;
    }

    public void Dispose()
    {
        _ephemeral.Dispose();
        if (_sessionKey is not null)
            CryptographicOperations.ZeroMemory(_sessionKey);
    }
}

/// <summary>The old (already unlocked) computer's side of <see cref="Pairing"/>.</summary>
public sealed class PairingOldComputer : IDisposable
{
    private readonly ECDiffieHellman _ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _commitment;
    private byte[]? _sessionKey;

    /// <summary>Step 2: starts from the new computer's commitment and makes the offer to post.</summary>
    public PairingOldComputer(byte[] commitment)
    {
        if (commitment.Length != 32)
            throw new CryptographicException("A pairing message is damaged.");
        _commitment = commitment;
        Offer = Pairing.Join(_ephemeral.ExportSubjectPublicKeyInfo(), RandomNumberGenerator.GetBytes(Pairing.NonceLength));
    }

    public byte[] Offer { get; }

    public string? Code { get; private set; }

    /// <summary>The computer asking to be let in, as it described itself (its name is shown with the code).</summary>
    public TrustedComputer? NewComputer { get; private set; }

    /// <summary>Step 4: checks the reveal against the commitment; throws if it doesn't match (someone in the middle).</summary>
    public void Accept(byte[] reveal)
    {
        if (!CryptographicOperations.FixedTimeEquals(Pairing.Commitment(reveal), _commitment))
            throw new CryptographicException("The other computer's answer doesn't match what it promised.");
        var parts = Pairing.Split(reveal, 3);
        if (parts[1].Length != Pairing.NonceLength)
            throw new CryptographicException("A pairing message is damaged.");
        TrustedComputer? computer;
        try
        {
            computer = JsonSerializer.Deserialize<TrustedComputer>(parts[2], Pairing.Json);
        }
        catch (JsonException ex)
        {
            throw new CryptographicException("A pairing message is damaged.", ex);
        }
        if (computer is null || string.IsNullOrEmpty(computer.Id) || computer.AgreementKey is null || computer.SigningKey is null)
            throw new CryptographicException("A pairing message is damaged.");
        _sessionKey = Pairing.SessionKey(_ephemeral, parts[0], reveal, Offer);
        Code = Pairing.Code(reveal, Offer);
        NewComputer = computer;
    }

    /// <summary>Step 5, only after the user said the codes match: the ring with the new computer in it, sealed for it.</summary>
    public byte[] SealRing(KeyRing ringWithNewComputer)
    {
        if (_sessionKey is null || NewComputer is null)
            throw new InvalidOperationException("The other computer hasn't answered yet.");
        if (ringWithNewComputer.Computer(NewComputer.Id) is null)
            throw new InvalidOperationException("Add the new computer to the ring first.");
        return PairingSeal.Seal(_sessionKey, ringWithNewComputer.ToBytes());
    }

    public void Dispose()
    {
        _ephemeral.Dispose();
        if (_sessionKey is not null)
            CryptographicOperations.ZeroMemory(_sessionKey);
    }
}

internal static class PairingSeal
{
    public static byte[] Seal(byte[] key, byte[] plain)
    {
        var blob = new byte[12 + plain.Length + 16];
        RandomNumberGenerator.Fill(blob.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(blob.AsSpan(0, 12), plain, blob.AsSpan(12, plain.Length), blob.AsSpan(12 + plain.Length), Pairing.RingLabel);
        CryptographicOperations.ZeroMemory(plain);
        return blob;
    }

    public static byte[] Open(byte[] key, byte[] blob)
    {
        if (blob.Length < 12 + 16)
            throw new CryptographicException("A pairing message is damaged.");
        var plain = new byte[blob.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(12, plain.Length), blob.AsSpan(12 + plain.Length), plain, Pairing.RingLabel);
        return plain;
    }
}
