using System.Security.Cryptography;
using Pairnets.Server.Auth.WebAuthn;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>The passkey checks, against a software authenticator.</summary>
public sealed class WebAuthnTests
{
    private const string RpId = "nest.example.test";
    private const string Origin = "https://nest.example.test";

    private static byte[] Challenge() => RandomNumberGenerator.GetBytes(32);

    private static RegisteredPasskey Register(SoftwareAuthenticator key, byte[] challenge)
    {
        var made = key.MakeCredential(challenge);
        return WebAuthnVerifier.VerifyRegistration(RpId, Origin, challenge, made.ClientDataJson, made.AttestationObject, made.CredentialId);
    }

    private static uint SignIn(SoftwareAuthenticator key, RegisteredPasskey stored, uint storedCount, uint newCount, byte[]? challenge = null)
    {
        challenge ??= Challenge();
        var assertion = key.GetAssertion(challenge, newCount);
        return WebAuthnVerifier.VerifyAssertion(RpId, Origin, challenge, stored.PublicKey, stored.Algorithm, storedCount,
            assertion.ClientDataJson, assertion.AuthenticatorData, assertion.Signature);
    }

    [Theory]
    [InlineData(false, WebAuthnVerifier.CoseEs256)]
    [InlineData(true, WebAuthnVerifier.CoseRs256)]
    public void ARegisteredPasskeySignsInWithBothAlgorithms(bool rsa, int algorithm)
    {
        using var key = new SoftwareAuthenticator(RpId, Origin, rsa);

        var stored = Register(key, Challenge());

        Assert.Equal(algorithm, stored.Algorithm);
        Assert.Equal(key.CredentialId, stored.CredentialId);
        Assert.Equal(0u, SignIn(key, stored, 0, 0)); // synced passkeys report 0 forever
        Assert.Equal(5u, SignIn(key, stored, 4, 5)); // a counter that moves forward is fine
    }

    [Fact]
    public void AnswersToAnotherChallengeAreRefused()
    {
        using var key = new SoftwareAuthenticator(RpId, Origin);
        var made = key.MakeCredential(Challenge());

        var ex = Assert.Throws<WebAuthnException>(() => WebAuthnVerifier.VerifyRegistration(RpId, Origin, Challenge(), made.ClientDataJson, made.AttestationObject, made.CredentialId));
        Assert.Contains("does not answer this request", ex.Message);
    }

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("https://nest.example.test:8443")]
    [InlineData("http://nest.example.test")]
    public void OnlyTheNestsExactOriginIsAccepted(string origin)
    {
        using var key = new SoftwareAuthenticator(RpId, origin);
        var challenge = Challenge();
        var made = key.MakeCredential(challenge);

        var ex = Assert.Throws<WebAuthnException>(() => WebAuthnVerifier.VerifyRegistration(RpId, Origin, challenge, made.ClientDataJson, made.AttestationObject, made.CredentialId));
        Assert.Contains("different website address", ex.Message);
    }

    [Fact]
    public void APasskeyForAnotherRelyingPartyIsRefused()
    {
        using var key = new SoftwareAuthenticator("evil.example", Origin);
        var challenge = Challenge();
        var made = key.MakeCredential(challenge);

        var ex = Assert.Throws<WebAuthnException>(() => WebAuthnVerifier.VerifyRegistration(RpId, Origin, challenge, made.ClientDataJson, made.AttestationObject, made.CredentialId));
        Assert.Contains("different website", ex.Message);
    }

    [Fact]
    public void TheUserMustHaveBeenPresent()
    {
        using var key = new SoftwareAuthenticator(RpId, Origin) { Flags = 0x04 }; // verified but "not present": not a real state, still refused
        var challenge = Challenge();
        var made = key.MakeCredential(challenge);

        Assert.Throws<WebAuthnException>(() => WebAuthnVerifier.VerifyRegistration(RpId, Origin, challenge, made.ClientDataJson, made.AttestationObject, made.CredentialId));
    }

    [Fact]
    public void ResponsesForTheWrongKindOfRequestAreRefused()
    {
        using var key = new SoftwareAuthenticator(RpId, Origin);
        var challenge = Challenge();
        var made = key.MakeCredential(challenge, type: "webauthn.get");
        Assert.Throws<WebAuthnException>(() => WebAuthnVerifier.VerifyRegistration(RpId, Origin, challenge, made.ClientDataJson, made.AttestationObject, made.CredentialId));

        var stored = Register(key, Challenge());
        var assertion = key.GetAssertion(challenge, type: "webauthn.create");
        Assert.Throws<WebAuthnException>(() => WebAuthnVerifier.VerifyAssertion(RpId, Origin, challenge, stored.PublicKey, stored.Algorithm, 0,
            assertion.ClientDataJson, assertion.AuthenticatorData, assertion.Signature));
    }

    [Fact]
    public void ARegistrationWithoutANewCredentialOrWithAMismatchedIdIsRefused()
    {
        using var key = new SoftwareAuthenticator(RpId, Origin);
        var challenge = Challenge();
        var none = key.MakeCredential(challenge, omitAttested: true);
        Assert.Throws<WebAuthnException>(() => WebAuthnVerifier.VerifyRegistration(RpId, Origin, challenge, none.ClientDataJson, none.AttestationObject, none.CredentialId));

        var wrongId = key.MakeCredential(challenge, credentialIdInData: RandomNumberGenerator.GetBytes(32));
        var ex = Assert.Throws<WebAuthnException>(() => WebAuthnVerifier.VerifyRegistration(RpId, Origin, challenge, wrongId.ClientDataJson, wrongId.AttestationObject, wrongId.CredentialId));
        Assert.Contains("does not match", ex.Message);
    }

    [Fact]
    public void ATamperedOrForeignSignatureIsRefused()
    {
        using var key = new SoftwareAuthenticator(RpId, Origin);
        using var other = new SoftwareAuthenticator(RpId, Origin);
        var stored = Register(key, Challenge());
        var challenge = Challenge();

        var tampered = key.GetAssertion(challenge);
        tampered.Signature[^1] ^= 0xFF;
        Assert.Throws<WebAuthnException>(() => WebAuthnVerifier.VerifyAssertion(RpId, Origin, challenge, stored.PublicKey, stored.Algorithm, 0,
            tampered.ClientDataJson, tampered.AuthenticatorData, tampered.Signature));

        var foreign = other.GetAssertion(challenge);
        var ex = Assert.Throws<WebAuthnException>(() => WebAuthnVerifier.VerifyAssertion(RpId, Origin, challenge, stored.PublicKey, stored.Algorithm, 0,
            foreign.ClientDataJson, foreign.AuthenticatorData, foreign.Signature));
        Assert.Contains("signature", ex.Message);

        // The signature covers the authenticator data: a changed counter breaks it.
        var changed = key.GetAssertion(challenge, 7);
        changed.AuthenticatorData[36] ^= 0x01;
        Assert.Throws<WebAuthnException>(() => WebAuthnVerifier.VerifyAssertion(RpId, Origin, challenge, stored.PublicKey, stored.Algorithm, 0,
            changed.ClientDataJson, changed.AuthenticatorData, changed.Signature));
    }

    [Theory]
    [InlineData(5u, 5u)] // same counter twice
    [InlineData(5u, 3u)] // goes backwards
    [InlineData(5u, 0u)] // drops to zero after counting
    [InlineData(0u, 0u)] // (control: both zero is allowed, see below)
    public void AClonedAuthenticatorIsCaughtByItsCounter(uint stored, uint now)
    {
        using var key = new SoftwareAuthenticator(RpId, Origin);
        var passkey = Register(key, Challenge());

        if (stored == 0 && now == 0)
        {
            Assert.Equal(0u, SignIn(key, passkey, stored, now));
            return;
        }
        var ex = Assert.Throws<WebAuthnException>(() => SignIn(key, passkey, stored, now));
        Assert.Contains("cloned", ex.Message);
    }

    [Fact]
    public void CrossOriginFramesAreRefused()
    {
        using var key = new SoftwareAuthenticator(RpId, Origin);
        var challenge = Challenge();
        var made = key.MakeCredential(challenge);
        var json = System.Text.Encoding.UTF8.GetString(made.ClientDataJson).Replace("\"crossOrigin\":false", "\"crossOrigin\":true");

        Assert.Throws<WebAuthnException>(() => WebAuthnVerifier.VerifyRegistration(RpId, Origin, challenge, System.Text.Encoding.UTF8.GetBytes(json), made.AttestationObject, made.CredentialId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void GarbageClientDataIsRefusedNotCrashed(string clientData)
    {
        using var key = new SoftwareAuthenticator(RpId, Origin);
        var made = key.MakeCredential(Challenge());

        Assert.Throws<WebAuthnException>(() => WebAuthnVerifier.VerifyRegistration(RpId, Origin, Challenge(), System.Text.Encoding.UTF8.GetBytes(clientData), made.AttestationObject, made.CredentialId));
        Assert.Throws<WebAuthnException>(() => WebAuthnVerifier.VerifyRegistration(RpId, Origin, Challenge(), made.ClientDataJson, [1, 2, 3], made.CredentialId));
    }

    [Fact]
    public void ChallengesAreOneTimeAndForOnePurpose()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var challenges = new WebAuthnChallenges(clock);

        var (id, bytes) = challenges.Create(WebAuthnChallenges.SignIn);
        Assert.Null(challenges.Consume(id, WebAuthnChallenges.Register)); // wrong purpose uses it up
        Assert.Null(challenges.Consume(id, WebAuthnChallenges.SignIn));

        var (second, secondBytes) = challenges.Create(WebAuthnChallenges.SignIn);
        Assert.Equal(secondBytes, challenges.Consume(second, WebAuthnChallenges.SignIn));
        Assert.Null(challenges.Consume(second, WebAuthnChallenges.SignIn)); // never twice
        Assert.Equal(32, bytes.Length);

        var (late, _) = challenges.Create(WebAuthnChallenges.Register);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(challenges.Consume(late, WebAuthnChallenges.Register));
        Assert.Null(challenges.Consume(null, WebAuthnChallenges.Register));
    }

    [Theory]
    [InlineData("AAEC", new byte[] { 0, 1, 2 })]
    [InlineData("-_8", new byte[] { 0xFB, 0xFF })]
    public void Base64UrlRoundTrips(string text, byte[] bytes)
    {
        Assert.Equal(bytes, WebAuthnVerifier.FromBase64Url(text));
        Assert.Equal(text.TrimEnd('='), WebAuthnVerifier.ToBase64Url(bytes));
        Assert.Null(WebAuthnVerifier.FromBase64Url("not base64 !"));
        Assert.Null(WebAuthnVerifier.FromBase64Url(null));
        Assert.Null(WebAuthnVerifier.FromBase64Url(new string('A', 100), maxBytes: 10));
    }
}
