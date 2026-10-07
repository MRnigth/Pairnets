using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pairnets.Server.Auth.WebAuthn;

/// <summary>A passkey response that does not check out. The message is safe to show to the person.</summary>
public sealed class WebAuthnException(string message) : Exception(message);

/// <summary>A new passkey, read and checked from the browser's registration response.</summary>
public sealed record RegisteredPasskey(byte[] CredentialId, byte[] PublicKey, int Algorithm, uint SignCount);

/// <summary>
/// Checks passkey (WebAuthn) responses, following the W3C steps for registration and sign-in, for the one
/// owner of a nest. Deliberately small: no attestation (we ask for "none" and ignore any statement, since
/// nothing here trusts a device make), two algorithms (ES256 and RS256, which every platform and security
/// key offers), and the public key is kept as a standard X.509 SubjectPublicKeyInfo.
/// <list type="bullet">
/// <item>The challenge is one-time, random and short-lived (see <see cref="WebAuthnChallenges"/>).</item>
/// <item>clientDataJSON must say the right type, the same challenge, exactly the nest's origin, and not come from a cross-origin frame.</item>
/// <item>The authenticator data must be for this relying party (SHA-256 of the RP id) and prove the user was present.</item>
/// <item>A sign-in signature covers authenticatorData and the hash of clientDataJSON; a counter that does not
/// move forward (when either side uses one) means a cloned authenticator and is refused.</item>
/// </list>
/// </summary>
public static class WebAuthnVerifier
{
    public const int CoseEs256 = -7;
    public const int CoseRs256 = -257;

    private const byte FlagUserPresent = 0x01;
    private const byte FlagAttestedCredential = 0x40;
    private const int MaxCredentialIdLength = 1023;
    private const int MaxResponseBytes = 16 * 1024;

    // ------------------------------------------------------------------ base64url

    public static string ToBase64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes base64url (padding optional); null when it is not valid or too long.</summary>
    public static byte[]? FromBase64Url(string? text, int maxBytes = MaxResponseBytes)
    {
        if (string.IsNullOrEmpty(text) || text.Length > maxBytes * 2)
            return null;
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try
        {
            var bytes = Convert.FromBase64String(s);
            return bytes.Length <= maxBytes ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ registration

    /// <summary>
    /// Checks the response of <c>navigator.credentials.create()</c> and returns the new passkey's id and public key.
    /// </summary>
    public static RegisteredPasskey VerifyRegistration(string rpId, string origin, byte[] expectedChallenge,
        byte[] clientDataJson, byte[] attestationObject, byte[] credentialId)
    {
        CheckClientData(clientDataJson, "webauthn.create", origin, expectedChallenge);
        if (credentialId.Length is 0 or > MaxCredentialIdLength)
            throw new WebAuthnException("The passkey has an unusable id.");

        byte[] authData;
        try
        {
            var reader = new CborReader(attestationObject, CborConformanceMode.Lax);
            authData = ReadAuthData(reader);
        }
        catch (Exception ex) when (ex is CborContentException or InvalidOperationException)
        {
            throw new WebAuthnException("The passkey's registration data is not readable.");
        }

        var (flags, signCount, rest) = ReadAuthenticatorData(authData, rpId);
        if ((flags & FlagAttestedCredential) == 0)
            throw new WebAuthnException("The response holds no new passkey.");
        // aaguid (16) + credential id length (2) + credential id + public key (CBOR)
        if (rest.Length < 18)
            throw new WebAuthnException("The passkey's registration data is cut short.");
        var idLength = BinaryPrimitives.ReadUInt16BigEndian(rest.AsSpan(16, 2));
        if (idLength is 0 or > MaxCredentialIdLength || rest.Length < 18 + idLength)
            throw new WebAuthnException("The passkey's registration data is cut short.");
        var embeddedId = rest.AsSpan(18, idLength);
        if (!embeddedId.SequenceEqual(credentialId))
            throw new WebAuthnException("The passkey's id does not match its registration data.");

        var (publicKey, algorithm) = ReadCoseKey(rest.AsMemory(18 + idLength));
        return new RegisteredPasskey(credentialId, publicKey, algorithm, signCount);
    }

    private static byte[] ReadAuthData(CborReader reader)
    {
        byte[]? authData = null;
        var entries = reader.ReadStartMap();
        for (var i = 0; i < entries; i++)
        {
            var key = reader.ReadTextString();
            if (key == "authData")
                authData = reader.ReadByteString();
            else
                reader.SkipValue(); // "fmt" and "attStmt": attestation is not used (see the class summary)
        }
        reader.ReadEndMap();
        return authData ?? throw new WebAuthnException("The passkey's registration data has no authenticator data.");
    }

    // ------------------------------------------------------------------ sign-in

    /// <summary>
    /// Checks the response of <c>navigator.credentials.get()</c> against the stored passkey. Returns the passkey's new
    /// sign count to store. Throws <see cref="WebAuthnException"/> for anything that does not check out.
    /// </summary>
    public static uint VerifyAssertion(string rpId, string origin, byte[] expectedChallenge, byte[] publicKey, int algorithm,
        uint storedSignCount, byte[] clientDataJson, byte[] authenticatorData, byte[] signature)
    {
        CheckClientData(clientDataJson, "webauthn.get", origin, expectedChallenge);
        var (_, signCount, _) = ReadAuthenticatorData(authenticatorData, rpId);

        var signed = new byte[authenticatorData.Length + 32];
        authenticatorData.CopyTo(signed, 0);
        SHA256.HashData(clientDataJson).CopyTo(signed, authenticatorData.Length);
        if (!SignatureIsValid(publicKey, algorithm, signed, signature))
            throw new WebAuthnException("The passkey's signature is not valid.");

        // Many authenticators (and synced passkeys) always report 0. Only when a counter is in use must it move forward.
        if ((signCount != 0 || storedSignCount != 0) && signCount <= storedSignCount)
            throw new WebAuthnException("This passkey looks cloned (its use counter went backwards), so it was refused.");
        return signCount;
    }

    private static bool SignatureIsValid(byte[] publicKey, int algorithm, byte[] data, byte[] signature)
    {
        try
        {
            switch (algorithm)
            {
                case CoseEs256:
                    using (var ecdsa = ECDsa.Create())
                    {
                        ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
                        return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
                    }
                case CoseRs256:
                    using (var rsa = RSA.Create())
                    {
                        rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
                        return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    }
                default:
                    return false;
            }
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ shared pieces

    /// <summary>clientDataJSON: the right type, our challenge, exactly the nest's origin, and not from a cross-origin frame.</summary>
    private static void CheckClientData(byte[] clientDataJson, string type, string origin, byte[] expectedChallenge)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(clientDataJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new WebAuthnException("The browser's passkey data is not readable.");
        }
        if (root.ValueKind != JsonValueKind.Object
            || Text(root, "type") != type)
            throw new WebAuthnException("The passkey response is for something else.");
        var challenge = FromBase64Url(Text(root, "challenge"), 128);
        if (challenge is null || !CryptographicOperations.FixedTimeEquals(challenge, expectedChallenge))
            throw new WebAuthnException("The passkey response does not answer this request (it may have expired). Try again.");
        if (!string.Equals(Text(root, "origin"), origin, StringComparison.Ordinal))
            throw new WebAuthnException("The passkey was made for a different website address.");
        if (root.TryGetProperty("crossOrigin", out var cross) && cross.ValueKind == JsonValueKind.True)
            throw new WebAuthnException("Passkeys cannot be used from inside another website.");
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>rpIdHash (32) · flags (1) · signCount (4) · the rest. The hash must be this nest's, and the user present.</summary>
    private static (byte Flags, uint SignCount, byte[] Remainder) ReadAuthenticatorData(byte[] authData, string rpId)
    {
        if (authData.Length < 37)
            throw new WebAuthnException("The passkey's authenticator data is cut short.");
        if (!CryptographicOperations.FixedTimeEquals(authData.AsSpan(0, 32), SHA256.HashData(Encoding.UTF8.GetBytes(rpId))))
            throw new WebAuthnException("The passkey belongs to a different website.");
        var flags = authData[32];
        if ((flags & FlagUserPresent) == 0)
            throw new WebAuthnException("The passkey was not confirmed by you (no touch or approval).");
        return (flags, BinaryPrimitives.ReadUInt32BigEndian(authData.AsSpan(33, 4)), authData[37..]);
    }

    /// <summary>
    /// Reads the COSE public key that follows the credential id (EC2 P-256 for ES256, RSA for RS256) and returns it as an
    /// X.509 SubjectPublicKeyInfo with its algorithm. Anything else is refused.
    /// </summary>
    private static (byte[] PublicKey, int Algorithm) ReadCoseKey(ReadOnlyMemory<byte> cbor)
    {
        try
        {
            var reader = new CborReader(cbor, CborConformanceMode.Lax);
            // COSE labels: 1 kty, 3 alg; for EC2 keys -1 is the curve, -2 x, -3 y; for RSA keys -1 is n and -2 is e.
            int? kty = null, alg = null, curve = null;
            byte[]? first = null, second = null, third = null;
            var entries = reader.ReadStartMap();
            for (var i = 0; i < entries; i++)
            {
                var label = reader.PeekState() is CborReaderState.UnsignedInteger or CborReaderState.NegativeInteger ? reader.ReadInt32() : int.MinValue;
                if (label == int.MinValue)
                {
                    reader.SkipValue();
                    reader.SkipValue();
                    continue;
                }
                switch (label)
                {
                    case 1:
                        kty = reader.ReadInt32();
                        break;
                    case 3:
                        alg = reader.ReadInt32();
                        break;
                    case -1:
                        if (reader.PeekState() == CborReaderState.ByteString)
                            first = reader.ReadByteString();
                        else
                            curve = reader.ReadInt32();
                        break;
                    case -2:
                        second = reader.ReadByteString();
                        break;
                    case -3:
                        third = reader.ReadByteString();
                        break;
                    default:
                        reader.SkipValue();
                        break;
                }
            }
            reader.ReadEndMap();

            if (kty == 2 && alg == CoseEs256 && curve == 1 && second is { Length: 32 } && third is { Length: 32 })
            {
                using var ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = second, Y = third } });
                return (ecdsa.ExportSubjectPublicKeyInfo(), CoseEs256);
            }
            if (kty == 3 && alg == CoseRs256 && first is { Length: >= 256 } && second is { Length: > 0 and <= 8 })
            {
                using var rsa = RSA.Create(new RSAParameters { Modulus = first, Exponent = second });
                return (rsa.ExportSubjectPublicKeyInfo(), CoseRs256);
            }
        }
        catch (Exception ex) when (ex is CborContentException or InvalidOperationException or CryptographicException or ArgumentException)
        {
            throw new WebAuthnException("The passkey's public key is not readable.");
        }
        throw new WebAuthnException("This passkey uses a type of key Pairnets does not support (it needs ES256 or RS256).");
    }
}
