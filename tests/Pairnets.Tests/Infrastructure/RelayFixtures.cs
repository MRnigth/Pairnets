using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Pairnets.Server.Auth;

namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// A nest linked to Pairnets, as the tests use it: a test nest id (the shape cloud/CONTRACT.md uses in its examples), a
/// nest key made here from fixed bytes (no key-shaped text sits in the repo), and requests signed like the service
/// signs them (cloud/RELAY.md §4).
/// </summary>
public static class RelayFixtures
{
    public const string NestId = "nst_testnest000000000000000001";

    public const string OtherNestId = "nst_testnest000000000000000002";

    public static readonly byte[] KeyBytes = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray();

    public static readonly string Key = StrictBase64Url.Encode(KeyBytes);

    /// <summary>The settings install.sh writes when it links a server.</summary>
    public static Dictionary<string, string?> Config(string serviceUrl = "https://sync.example.com") => new()
    {
        ["Sync:RelayNestId"] = NestId,
        ["Sync:RelayKey"] = Key,
        ["Sync:RelayServiceUrl"] = serviceUrl,
        ["Sync:TrustProxyHeaders"] = "true",
    };

    public static string NewNonce() => StrictBase64Url.Encode(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// A call to the nest signed with <paramref name="key"/> (the nest's own by default). <paramref name="pathAndQuery"/>
    /// starts with "/" and is also what is signed, unless <paramref name="signedPath"/> says otherwise.
    /// </summary>
    public static HttpRequestMessage Signed(HttpMethod method, string pathAndQuery, string? json = null, byte[]? key = null,
        string nest = NestId, long? time = null, string? nonce = null, string? signedPath = null, string? signedMethod = null, string? signedJson = null)
    {
        var body = json is null ? [] : Encoding.UTF8.GetBytes(json);
        var signedBody = signedJson is null ? body : Encoding.UTF8.GetBytes(signedJson);
        var ts = (time ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString(System.Globalization.CultureInfo.InvariantCulture);
        nonce ??= NewNonce();
        var sig = RelaySignature.Sign(key ?? KeyBytes,
            RelaySignature.Message(nest, ts, nonce, signedMethod ?? method.Method, signedPath ?? pathAndQuery, signedBody));
        var request = new HttpRequestMessage(method, pathAndQuery.TrimStart('/'));
        request.Headers.Add(RelaySignature.NestHeader, nest);
        request.Headers.Add(RelaySignature.TimeHeader, ts);
        request.Headers.Add(RelaySignature.NonceHeader, nonce);
        request.Headers.Add(RelaySignature.SignatureHeader, sig);
        if (json is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        return request;
    }
}
