using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pairnets.Server.Auth.WebAuthn;

namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// A passkey in software, for tests: makes the same bytes a browser hands the nest (clientDataJSON,
/// attestationObject, authenticatorData, signature), so the real verifier can be exercised without a browser.
/// </summary>
public sealed class SoftwareAuthenticator : IDisposable
{
    private readonly ECDsa? _ec;
    private readonly RSA? _rsa;

    public SoftwareAuthenticator(string rpId, string origin, bool rsa = false)
    {
        RpId = rpId;
        Origin = origin;
        CredentialId = RandomNumberGenerator.GetBytes(32);
        if (rsa)
            _rsa = RSA.Create(2048);
        else
            _ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    }

    public string RpId { get; set; }

    public string Origin { get; set; }

    public byte[] CredentialId { get; }

    /// <summary>The authenticator flags: user present (1), user verified (4), attested credential (64).</summary>
    public byte Flags { get; set; } = 0x01 | 0x04;

    public void Dispose()
    {
        _ec?.Dispose();
        _rsa?.Dispose();
    }

    public sealed record Registration(byte[] ClientDataJson, byte[] AttestationObject, byte[] CredentialId);

    public sealed record Assertion(byte[] ClientDataJson, byte[] AuthenticatorData, byte[] Signature, byte[] CredentialId);

    public Registration MakeCredential(byte[] challenge, string? type = null, bool omitAttested = false, byte[]? credentialIdInData = null)
    {
        var clientData = ClientData(type ?? "webauthn.create", challenge);
        var authData = new List<byte>();
        authData.AddRange(SHA256.HashData(Encoding.UTF8.GetBytes(RpId)));
        authData.Add((byte)(Flags | (omitAttested ? 0 : 0x40)));
        authData.AddRange(new byte[4]);
        if (!omitAttested)
        {
            var id = credentialIdInData ?? CredentialId;
            authData.AddRange(new byte[16]);
            var length = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)id.Length);
            authData.AddRange(length);
            authData.AddRange(id);
            authData.AddRange(CoseKey());
        }

        var writer = new CborWriter(CborConformanceMode.Lax);
        writer.WriteStartMap(3);
        writer.WriteTextString("fmt");
        writer.WriteTextString("none");
        writer.WriteTextString("attStmt");
        writer.WriteStartMap(0);
        writer.WriteEndMap();
        writer.WriteTextString("authData");
        writer.WriteByteString(authData.ToArray());
        writer.WriteEndMap();
        return new Registration(clientData, writer.Encode(), CredentialId);
    }

    public Assertion GetAssertion(byte[] challenge, uint signCount = 0, string? type = null, Action<byte[]>? tamperSigned = null)
    {
        var clientData = ClientData(type ?? "webauthn.get", challenge);
        var authData = new byte[37];
        SHA256.HashData(Encoding.UTF8.GetBytes(RpId)).CopyTo(authData, 0);
        authData[32] = Flags;
        BinaryPrimitives.WriteUInt32BigEndian(authData.AsSpan(33), signCount);
        var signed = authData.Concat(SHA256.HashData(clientData)).ToArray();
        var signature = _ec is not null
            ? _ec.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
            : _rsa!.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        tamperSigned?.Invoke(signature);
        return new Assertion(clientData, authData, signature, CredentialId);
    }

    private byte[] ClientData(string type, byte[] challenge) =>
        JsonSerializer.SerializeToUtf8Bytes(new { type, challenge = WebAuthnVerifier.ToBase64Url(challenge), origin = Origin, crossOrigin = false });

    private byte[] CoseKey()
    {
        var writer = new CborWriter(CborConformanceMode.Lax);
        if (_ec is not null)
        {
            var q = _ec.ExportParameters(false).Q;
            writer.WriteStartMap(5);
            writer.WriteInt32(1); writer.WriteInt32(2);
            writer.WriteInt32(3); writer.WriteInt32(-7);
            writer.WriteInt32(-1); writer.WriteInt32(1);
            writer.WriteInt32(-2); writer.WriteByteString(q.X!);
            writer.WriteInt32(-3); writer.WriteByteString(q.Y!);
        }
        else
        {
            var p = _rsa!.ExportParameters(false);
            writer.WriteStartMap(4);
            writer.WriteInt32(1); writer.WriteInt32(3);
            writer.WriteInt32(3); writer.WriteInt32(-257);
            writer.WriteInt32(-1); writer.WriteByteString(p.Modulus!);
            writer.WriteInt32(-2); writer.WriteByteString(p.Exponent!);
        }
        writer.WriteEndMap();
        return writer.Encode();
    }
}
