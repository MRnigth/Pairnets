using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pairnets.Core.Crypto;

/// <summary>One generation of a nest's folder key: a number and 32 random bytes.</summary>
public sealed record RingGeneration(int Number, byte[] Secret);

/// <summary>
/// A computer the ring trusts: its two public keys (SubjectPublicKeyInfo DER) and the generation it was added in.
/// <see cref="AgreementKey"/> receives sealed rings (ECDH P-256); <see cref="SigningKey"/> signs rings it hands on
/// (ECDSA P-256). The two are separate keys on purpose.
/// </summary>
public sealed record TrustedComputer(string Id, string Name, byte[] AgreementKey, byte[] SigningKey, int Since);

/// <summary>
/// A nest's folder key ring (docs/ENCRYPTION.md §2.1). It is made on the first computer when encryption is turned on,
/// never leaves the user's computers in plain form, and keeps every generation so old files stay readable after a
/// computer is removed. Sub-keys come from HKDF-SHA256 with fixed labels: <c>id</c> and <c>tag</c> from generation 0
/// (they never change, so a path keeps its id and an unchanged file keeps its tag across rotations), and
/// <c>content-wrap</c> and <c>meta</c> per generation (new uploads after a removal use keys the removed computer
/// never had).
/// </summary>
public sealed class KeyRing
{
    public const int SecretLength = 32;
    private const int FormatVersion = 1;

    private readonly SortedDictionary<int, RingGeneration> _generations;

    private KeyRing(string nestId, IEnumerable<RingGeneration> generations, IEnumerable<TrustedComputer> computers)
    {
        NestId = nestId;
        _generations = new SortedDictionary<int, RingGeneration>(generations.ToDictionary(g => g.Number));
        Computers = computers.ToList();
        if (_generations.Count == 0 || !_generations.ContainsKey(0))
            throw new CryptographicException("A key ring needs generation 0.");
        foreach (var g in _generations.Values)
        {
            if (g.Secret.Length != SecretLength)
                throw new CryptographicException("A key ring secret must be 32 bytes.");
        }
    }

    /// <summary>The nest this ring belongs to, so a ring can't be used for another nest by mistake.</summary>
    public string NestId { get; }

    /// <summary>The newest generation; new uploads use it.</summary>
    public int Current => _generations.Keys.Max();

    public IReadOnlyCollection<RingGeneration> Generations => _generations.Values;

    public IReadOnlyList<TrustedComputer> Computers { get; }

    /// <summary>Makes a new ring with one random generation 0 and the first computer.</summary>
    public static KeyRing Create(string nestId, TrustedComputer first) =>
        new(nestId, [new RingGeneration(0, RandomNumberGenerator.GetBytes(SecretLength))], [first with { Since = 0 }]);

    public bool HasGeneration(int number) => _generations.ContainsKey(number);

    public TrustedComputer? Computer(string id) => Computers.FirstOrDefault(c => c.Id == id);

    /// <summary>The same ring with one more trusted computer (unlocking a new computer).</summary>
    public KeyRing WithComputer(TrustedComputer computer) =>
        new(NestId, _generations.Values, Computers.Where(c => c.Id != computer.Id).Append(computer with { Since = Current }));

    /// <summary>
    /// A new generation without <paramref name="removedId"/> (a stolen or removed computer). Every old generation is
    /// kept, so files made before stay readable; new uploads use keys the removed computer never had.
    /// </summary>
    public KeyRing Rotate(string removedId) =>
        new(NestId,
            _generations.Values.Append(new RingGeneration(Current + 1, RandomNumberGenerator.GetBytes(SecretLength))),
            Computers.Where(c => c.Id != removedId));

    /// <summary>The key that makes path ids (generation 0, never changes).</summary>
    public byte[] IdKey() => Derive(0, "id");

    /// <summary>The key for the keyed content tag that replaces the plain SHA-256 (generation 0, never changes).</summary>
    public byte[] TagKey() => Derive(0, "tag");

    /// <summary>The key that wraps each file's own data key, for <paramref name="generation"/>.</summary>
    public byte[] ContentWrapKey(int generation) => Derive(generation, "content-wrap");

    /// <summary>The key that encrypts a file's meta blob (real path, size, times), for <paramref name="generation"/>.</summary>
    public byte[] MetaKey(int generation) => Derive(generation, "meta");

    private byte[] Derive(int generation, string label)
    {
        if (!_generations.TryGetValue(generation, out var g))
            throw new CryptographicException($"This computer doesn't have key generation {generation}.");
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, g.Secret, 32,
            salt: Encoding.UTF8.GetBytes("pairnets/ring/" + NestId), info: Encoding.UTF8.GetBytes("pairnets/v1/" + label));
    }

    /// <summary>
    /// Whether <paramref name="incoming"/> may replace <paramref name="known"/>: same nest, no older generation, every
    /// generation this computer has is still there with the same secret, and (for an update handed on by another
    /// computer) signed by a computer the known ring trusts. So neither the nest, the service nor a removed computer
    /// can hand back a ring that trusts it again (§2.8, point 5).
    /// </summary>
    public static RingCheck CheckUpdate(KeyRing known, KeyRing incoming, string? signerId)
    {
        if (incoming.NestId != known.NestId)
            return RingCheck.OtherNest;
        if (incoming.Current < known.Current)
            return RingCheck.Older;
        foreach (var g in known._generations.Values)
        {
            if (!incoming._generations.TryGetValue(g.Number, out var same)
                || !CryptographicOperations.FixedTimeEquals(same.Secret, g.Secret))
                return RingCheck.Replaced;
        }
        if (signerId is not null && known.Computer(signerId) is null)
            return RingCheck.UntrustedSigner;
        return RingCheck.Ok;
    }

    public byte[] ToBytes() => JsonSerializer.SerializeToUtf8Bytes(new RingFile(
        FormatVersion, NestId,
        _generations.Values.Select(g => new RingFileGeneration(g.Number, g.Secret)).ToList(),
        Computers.Select(c => new RingFileComputer(c.Id, c.Name, c.AgreementKey, c.SigningKey, c.Since)).ToList()), Json);

    public static KeyRing FromBytes(ReadOnlySpan<byte> bytes)
    {
        RingFile? file;
        try
        {
            file = JsonSerializer.Deserialize<RingFile>(bytes, Json);
        }
        catch (JsonException ex)
        {
            throw new CryptographicException("The key ring is damaged.", ex);
        }
        if (file is null || file.V != FormatVersion || file.Gens is null || file.Computers is null || file.Nest is null)
            throw new CryptographicException("The key ring is damaged or from a newer version.");
        return new KeyRing(file.Nest,
            file.Gens.Select(g => new RingGeneration(g.N, g.K ?? [])),
            file.Computers.Select(c => new TrustedComputer(c.Id ?? "", c.Name ?? "", c.Agree ?? [], c.Sign ?? [], c.Since)));
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record RingFile(int V, string? Nest, List<RingFileGeneration>? Gens, List<RingFileComputer>? Computers);

    private sealed record RingFileGeneration(int N, byte[]? K);

    private sealed record RingFileComputer(string? Id, string? Name, byte[]? Agree, byte[]? Sign, int Since);
}

public enum RingCheck
{
    Ok,
    OtherNest,
    Older,
    Replaced,
    UntrustedSigner,
}
