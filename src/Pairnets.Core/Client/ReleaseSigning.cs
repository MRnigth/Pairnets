using System.Security.Cryptography;

namespace Pairnets.Core.Client;

/// <summary>
/// Signed releases: every release carries SHA256SUMS.txt.sig, an ECDSA P-256 / SHA-256 signature of the exact bytes of
/// SHA256SUMS.txt, made by the release workflow with a private key only GitHub's secret store holds. The signature is
/// the raw DER (ASN.1) form that <c>openssl dgst -sha256 -sign</c> writes, so the same file is checked here, by the
/// server's updater and by get.sh (<c>openssl dgst -sha256 -verify</c>). The public key is deploy/release-signing.pub,
/// built into this assembly, so the apps only trust checksums Pairnets signed (docs/DECISIONS.md, "Signed releases").
/// </summary>
public static class ReleaseSigning
{
    public const string SumsFile = "SHA256SUMS.txt";
    public const string SignatureFile = "SHA256SUMS.txt.sig";

    /// <summary>What the apps say when a release is not signed with the Pairnets key.</summary>
    public const string NotSignedMessage = "This update isn't signed by Pairnets, so it wasn't installed.";

    private const string P256Oid = "1.2.840.10045.3.1.7";

    private static readonly Lazy<string> Embedded = new(() =>
    {
        using var stream = typeof(ReleaseSigning).Assembly.GetManifestResourceStream("Pairnets.release-signing.pub")
            ?? throw new InvalidOperationException("The release signing key is missing from Pairnets.Core.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>The Pairnets release public key (PEM, deploy/release-signing.pub).</summary>
    public static string PublicKeyPem => Embedded.Value;

    /// <summary>
    /// True only when <paramref name="signature"/> is a valid DER ECDSA P-256 signature of <paramref name="sums"/> by
    /// <paramref name="publicKeyPem"/>. Anything else (no signature, garbage, another key, another curve) is false;
    /// it never throws.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> sums, ReadOnlySpan<byte> signature, string publicKeyPem)
    {
        if (signature.IsEmpty || string.IsNullOrWhiteSpace(publicKeyPem))
            return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(publicKeyPem);
            var curve = key.ExportParameters(false).Curve.Oid; // (Windows names it, Linux and macOS give the OID)
            if (curve?.Value != P256Oid && curve?.FriendlyName is not ("nistP256" or "ECDSA_P256" or "prime256v1" or "secp256r1"))
                return false;
            return key.VerifyData(sums, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
