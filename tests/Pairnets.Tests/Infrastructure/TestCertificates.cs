using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pairnets.Tests.Infrastructure;

/// <summary>Throwaway certificates shaped like Let's Encrypt's: a leaf plus one intermediate in fullchain.pem.</summary>
public static class TestCertificates
{
    public sealed record Issued(X509Certificate2 Leaf, X509Certificate2 Intermediate, string FullChainPem, string KeyPem)
    {
        /// <summary>Writes privkey.pem and fullchain.pem the way the certbot deploy hook does.</summary>
        public void WriteTo(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "privkey.pem"), KeyPem);
            File.WriteAllText(Path.Combine(dir, "fullchain.pem"), FullChainPem);
        }

        /// <summary>
        /// Writes the pair with the leaf alone in fullchain.pem. Some Windows machines refuse to build a chain through an
        /// intermediate they cannot trust ("An unknown chain building error occurred"), and then the server shows no
        /// certificate at all; the end-to-end test servers only need one the client accepts, so they serve the leaf alone.
        /// </summary>
        public void WriteLeafOnlyTo(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "privkey.pem"), KeyPem);
            File.WriteAllText(Path.Combine(dir, "fullchain.pem"), Leaf.ExportCertificatePem() + "\n");
        }
    }

    public static Issued Create(string dnsName)
    {
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(30);

        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Pairnets Test Root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var root = rootRequest.CreateSelfSigned(notBefore, notAfter);

        using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = new CertificateRequest("CN=Pairnets Test Intermediate", intermediateKey, HashAlgorithmName.SHA256);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        intermediateRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var intermediatePublic = intermediateRequest.Create(root, notBefore, notAfter, Serial());
        using var intermediate = intermediatePublic.CopyWithPrivateKey(intermediateKey);

        var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafRequest = new CertificateRequest($"CN={dnsName}", leafKey, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(dnsName);
        names.AddIpAddress(IPAddress.Loopback);
        leafRequest.CertificateExtensions.Add(names.Build());
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var leaf = leafRequest.Create(intermediate, notBefore, notAfter.AddDays(-1), Serial());

        var fullChain = leaf.ExportCertificatePem() + "\n" + intermediatePublic.ExportCertificatePem() + "\n";
        return new Issued(leaf, new X509Certificate2(intermediatePublic.RawData), fullChain, leafKey.ExportPkcs8PrivateKeyPem());
    }

    private static byte[] Serial()
    {
        var serial = RandomNumberGenerator.GetBytes(12);
        serial[0] = (byte)((serial[0] & 0x7F) | 0x01); // positive, no leading zero
        return serial;
    }
}
