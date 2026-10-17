using System.Security.Cryptography;

namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// TEST ONLY: a release signing key made fresh for each test run (never saved, never the Pairnets key), so the fake
/// release feeds can sign their SHA256SUMS.txt the way the release workflow does: ECDSA P-256 / SHA-256, DER signature.
/// </summary>
public static class TestReleaseKey
{
    private static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>The public half, PEM, for <c>UpdateChecker</c> / <c>ClientEnvironment.UpdateSigningKey</c>.</summary>
    public static string PublicKeyPem { get; } = Key.ExportSubjectPublicKeyInfoPem();

    /// <summary>SHA256SUMS.txt.sig for <paramref name="sums"/>: what <c>openssl dgst -sha256 -sign</c> writes.</summary>
    public static byte[] Sign(byte[] sums)
    {
        lock (Key)
            return Key.SignData(sums, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }
}
