using System.Net;
using Pairnets.Core;
using Pairnets.Server.Auth;
using Pairnets.Server.Auth.WebAuthn;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Web;

/// <summary>
/// Passkeys for the nest's website (/web/api/passkeys, /web/api/signin/passkey): Windows Hello, Face ID, Touch ID,
/// a phone or a security key instead of a password. The browser does the asking; <see cref="WebAuthnVerifier"/>
/// checks what comes back. Registration needs a signed-in browser; signing in is open but rate-limited.
/// </summary>
public static class PasskeyEndpoints
{
    public sealed record RegisterBody(string? ChallengeId, string? Name, string? Id, string? ClientDataJson, string? AttestationObject);

    public sealed record SignInBody(string? ChallengeId, string? Id, string? ClientDataJson, string? AuthenticatorData, string? Signature);

    public sealed record PasskeyView(string Id, string Name, DateTimeOffset Created, DateTimeOffset? LastUsed);

    private sealed record Credential(string Type, string Id);

    public static void Map(RouteGroupBuilder open, RouteGroupBuilder signedIn)
    {
        // ---- registration (signed in)

        signedIn.MapPost("/passkeys/register/options", (OwnerAuth owner, SyncOptions options, WebAuthnChallenges challenges) =>
        {
            if (options.EffectiveRpId is not { } rpId)
                return Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Passkeys need the nest's own name.");
            var (id, challenge) = challenges.Create(WebAuthnChallenges.Register);
            return Json(new
            {
                challengeId = id,
                publicKey = new
                {
                    rp = new { id = rpId, name = "Pairnets nest" },
                    user = new { id = WebAuthnVerifier.ToBase64Url(owner.Store.UserHandle()), name = "owner", displayName = "Nest owner" },
                    challenge = WebAuthnVerifier.ToBase64Url(challenge),
                    pubKeyCredParams = new[] { new { type = "public-key", alg = WebAuthnVerifier.CoseEs256 }, new { type = "public-key", alg = WebAuthnVerifier.CoseRs256 } },
                    timeout = 120_000,
                    attestation = "none",
                    authenticatorSelection = new { residentKey = "preferred", userVerification = "preferred" },
                    excludeCredentials = owner.Store.ListPasskeys().Select(k => new Credential("public-key", WebAuthnVerifier.ToBase64Url(k.CredentialId))).ToArray(),
                },
            });
        });

        signedIn.MapPost("/passkeys/register", (RegisterBody? body, OwnerAuth owner, SyncOptions options, WebAuthnChallenges challenges, ILogger<OwnerAuth> log) =>
        {
            if (options.EffectiveRpId is not { } rpId || options.PublicUrl is not { } origin)
                return Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Passkeys need the nest's own name.");
            var challenge = challenges.Consume(body?.ChallengeId, WebAuthnChallenges.Register);
            if (challenge is null)
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "This request expired. Try adding the passkey again.");
            try
            {
                var clientData = Decode(body?.ClientDataJson);
                var attestation = Decode(body?.AttestationObject);
                var credentialId = Decode(body?.Id, 1023);
                var passkey = WebAuthnVerifier.VerifyRegistration(rpId, origin, challenge, clientData, attestation, credentialId);
                if (!owner.Store.AddPasskey(passkey.CredentialId, passkey.PublicKey, passkey.Algorithm, passkey.SignCount, body?.Name))
                    return Error(StatusCodes.Status409Conflict, ErrorCodes.Conflict, "This passkey is already added.");
                log.LogInformation("A passkey was added ({Name})", AuthStore.CleanLabel(body?.Name) ?? "Passkey");
                return Results.NoContent();
            }
            catch (WebAuthnException ex)
            {
                log.LogWarning("A passkey was refused: {Reason}", ex.Message);
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, ex.Message);
            }
        });

        signedIn.MapGet("/passkeys", (OwnerAuth owner) => Json(owner.Store.ListPasskeys().Select(k => new PasskeyView(k.Id, k.Name, k.Created, k.LastUsed)).ToList()));

        signedIn.MapDelete("/passkeys/{id}", (string id, OwnerAuth owner) =>
        {
            var store = owner.Store;
            var (password, passkeys, email, google) = owner.UsableMethods();
            if (!password && !email && !google && passkeys <= 1)
                return Error(StatusCodes.Status409Conflict, ErrorCodes.BadRequest, "You can't remove the last way to sign in. Add another one first.");
            return store.RemovePasskey(id) ? Results.NoContent() : Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "No such passkey.");
        });

        // ---- sign in (open, rate-limited)

        open.MapPost("/signin/passkey/options", (OwnerAuth owner, SyncOptions options, WebAuthnChallenges challenges) =>
        {
            if (options.EffectiveRpId is not { } rpId || owner.Store.CountPasskeys() == 0)
                return Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "No passkey is set up on this nest.");
            var (id, challenge) = challenges.Create(WebAuthnChallenges.SignIn);
            return Json(new
            {
                challengeId = id,
                publicKey = new
                {
                    challenge = WebAuthnVerifier.ToBase64Url(challenge),
                    rpId,
                    timeout = 120_000,
                    userVerification = "preferred",
                    allowCredentials = owner.Store.ListPasskeys().Select(k => new Credential("public-key", WebAuthnVerifier.ToBase64Url(k.CredentialId))).ToArray(),
                },
            });
        }).RequireRateLimiting(PairingEndpoints.RateLimitPolicy);

        open.MapPost("/signin/passkey", async (HttpContext ctx, SignInBody? body, OwnerAuth owner, SyncOptions options, WebAuthnChallenges challenges,
            FailureThrottle throttle, ILogger<OwnerAuth> log) =>
        {
            var address = ctx.Connection.RemoteIpAddress ?? IPAddress.None;
            var delay = throttle.CurrentDelay(address);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ctx.RequestAborted);
            if (options.EffectiveRpId is not { } rpId || options.PublicUrl is not { } origin)
                return Error(StatusCodes.Status404NotFound, ErrorCodes.NotFound, "Passkeys need the nest's own name.");

            IResult Refuse(string message)
            {
                var failures = throttle.RecordFailure(address);
                log.LogWarning("A passkey sign-in was refused from {Address}: {Reason} ({Failures} recent failure(s))", address, message, failures);
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.Unauthorized, message);
            }

            var challenge = challenges.Consume(body?.ChallengeId, WebAuthnChallenges.SignIn);
            if (challenge is null)
                return Refuse("This sign-in expired. Try again.");
            try
            {
                var credentialId = Decode(body?.Id, 1023);
                var passkey = owner.Store.FindPasskey(credentialId);
                if (passkey is null)
                    return Refuse("This nest does not know that passkey.");
                var newCount = WebAuthnVerifier.VerifyAssertion(rpId, origin, challenge, passkey.PublicKey, passkey.Algorithm, passkey.SignCount,
                    Decode(body?.ClientDataJson), Decode(body?.AuthenticatorData), Decode(body?.Signature));
                owner.Store.TouchPasskey(passkey.Id, newCount);
                throttle.RecordSuccess(address);
                owner.SignIn(ctx, "passkey");
                return Results.NoContent();
            }
            catch (WebAuthnException ex)
            {
                return Refuse(ex.Message);
            }
        }).RequireRateLimiting(PairingEndpoints.RateLimitPolicy);
    }

    /// <summary>Base64url from the browser, or a refusal when it is missing or not valid.</summary>
    private static byte[] Decode(string? text, int maxBytes = 16 * 1024) =>
        WebAuthnVerifier.FromBase64Url(text, maxBytes) ?? throw new WebAuthnException("The browser sent incomplete passkey data.");

    private static IResult Json<T>(T value) => Results.Json(value, PairnetsJson.Options);

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorBody(code, message), PairnetsJson.Options, statusCode: status);
}
