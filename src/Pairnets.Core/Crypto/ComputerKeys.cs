using System.Security.Cryptography;

namespace Pairnets.Core.Crypto;

/// <summary>
/// This computer's own two P-256 key pairs for an encrypted nest: one to receive sealed rings (ECDH) and one to sign
/// rings it hands on (ECDSA). The private halves are kept beside the computer's nest key (DPAPI, the Keychain or the
/// keyring) as PKCS#8; they are never logged or sent.
/// </summary>
public sealed class ComputerKeys : IDisposable
{
    private ComputerKeys(string id, ECDiffieHellman agreement, ECDsa signing)
    {
        Id = id;
        Agreement = agreement;
        Signing = signing;
    }

    /// <summary>A random id for this computer in the ring (not its name, not its nest key).</summary>
    public string Id { get; }

    internal ECDiffieHellman Agreement { get; }

    internal ECDsa Signing { get; }

    public byte[] AgreementPublicKey => Agreement.ExportSubjectPublicKeyInfo();

    public byte[] SigningPublicKey => Signing.ExportSubjectPublicKeyInfo();

    public static ComputerKeys Create() =>
        new(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256), ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>This computer as the ring lists it.</summary>
    public TrustedComputer AsTrusted(string name) => new(Id, name, AgreementPublicKey, SigningPublicKey, 0);

    /// <summary>The private halves, for the protected key store only.</summary>
    public (byte[] Agreement, byte[] Signing) ExportPrivate() =>
        (Agreement.ExportPkcs8PrivateKey(), Signing.ExportPkcs8PrivateKey());

    public static ComputerKeys Import(string id, ReadOnlySpan<byte> agreementPkcs8, ReadOnlySpan<byte> signingPkcs8)
    {
        var agreement = ECDiffieHellman.Create();
        var signing = ECDsa.Create();
        try
        {
            agreement.ImportPkcs8PrivateKey(agreementPkcs8, out _);
            signing.ImportPkcs8PrivateKey(signingPkcs8, out _);
            return new ComputerKeys(id, agreement, signing);
        }
        catch
        {
            agreement.Dispose();
            signing.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Agreement.Dispose();
        Signing.Dispose();
    }
}
