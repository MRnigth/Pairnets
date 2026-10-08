using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.Mvc;
using Pairnets.Core;
using Pairnets.Server.Auth;
using Pairnets.Server.Storage;

namespace Pairnets.Server.Web;

/// <summary>
/// The other two ways into the nest's website: a link sent to your email address, and your Google account.
/// Both are off until the server is set up for them (SMTP; a Google OAuth client) and linked on the Security page.
/// </summary>
public static class LinkedSignInEndpoints
{
    public sealed record EmailBody(string? Email);

    public sealed record CodeBody(string? Code);

    /// <summary>How many sign-in emails the nest sends per hour at most: stops anyone filling your inbox.</summary>
    public const int MaxEmailsPerHour = 5;

    public static void Map(WebApplication app, RouteGroupBuilder open, RouteGroupBuilder signedIn)
    {
        var emails = new EmailThrottle();

        // ---- email links

        open.MapPost("/signin/email/request", async (OwnerAuth owner, IEmailSender sender, ILogger<OwnerAuth> log, CancellationToken ct) =>
        {
            // The answer never says whether an address is set up: anyone may ask, only the owner's inbox gets a link.
            if (owner.UsableMethods().Email && owner.Store.OwnerEmail is { } address)
            {
                if (!emails.TryTake(owner.Now))
                    return Error(StatusCodes.Status429TooManyRequests, ErrorCodes.BadRequest, "A few sign-in emails were sent a moment ago. Check your inbox, or wait a little.");
                await SendLinkAsync(owner, sender, address, EmailLinkPurpose.SignIn, log, ct);
            }
            return Results.NoContent();
        }).RequireRateLimiting(PairingEndpoints.RateLimitPolicy);

        open.MapPost("/signin/email/confirm", (HttpContext ctx, CodeBody? body, OwnerAuth owner, FailureThrottle throttle, ILogger<OwnerAuth> log) =>
        {
            var address = ctx.Connection.RemoteIpAddress ?? IPAddress.None;
            if (owner.Store.UseEmailLink(body?.Code) is not { } link)
            {
                var failures = throttle.RecordFailure(address);
                log.LogWarning("An email link was refused (used, expired or wrong) from {Address} ({Failures} recent failure(s))", address, failures);
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.Unauthorized, "This link was already used or has expired. Ask for a new one.");
            }
            if (link.Purpose == EmailLinkPurpose.Confirm)
                owner.Store.SetOwnerEmail(link.Email);
            owner.SignIn(ctx, "email link");
            log.LogInformation("Signed in to the nest's website with an email link from {Address}", address);
            return Results.NoContent();
        }).RequireRateLimiting(PairingEndpoints.RateLimitPolicy);

        signedIn.MapPost("/email", async (EmailBody? body, OwnerAuth owner, IEmailSender sender, ILogger<OwnerAuth> log, CancellationToken ct) =>
        {
            if (!owner.Options.EmailConfigured)
                return Error(StatusCodes.Status409Conflict, ErrorCodes.BadRequest, "Email is not set up on the server yet (see DEPLOY.md).");
            if (!TryAddress(body?.Email, out var address))
                return Error(StatusCodes.Status400BadRequest, ErrorCodes.BadRequest, "That does not look like an email address.");
            if (!emails.TryTake(owner.Now))
                return Error(StatusCodes.Status429TooManyRequests, ErrorCodes.BadRequest, "A few emails were sent a moment ago. Wait a little and try again.");
            try
            {
                await SendLinkAsync(owner, sender, address, EmailLinkPurpose.Confirm, log, ct);
            }
            catch (Exception ex) when (ex is SmtpException or InvalidOperationException or IOException)
            {
                return Error(StatusCodes.Status502BadGateway, ErrorCodes.BadRequest, "The email could not be sent. Check the mail settings on the server.");
            }
            return Results.NoContent();
        });

        signedIn.MapDelete("/email", (OwnerAuth owner) =>
        {
            if (!owner.HasOtherWayThan("email"))
                return Error(StatusCodes.Status409Conflict, ErrorCodes.BadRequest, "You can't remove the last way to sign in. Add another one first.");
            owner.Store.SetOwnerEmail(null);
            return Results.NoContent();
        });

        // ---- Google

        signedIn.MapDelete("/google", (OwnerAuth owner) =>
        {
            if (!owner.HasOtherWayThan("google"))
                return Error(StatusCodes.Status409Conflict, ErrorCodes.BadRequest, "You can't remove the last way to sign in. Add another one first.");
            owner.Store.SetGoogleAccount(null, null);
            return Results.NoContent();
        });

        app.MapGet("/auth/google/start", (HttpContext ctx, string? purpose, OwnerAuth owner, GoogleSignIn google) =>
        {
            if (!owner.IsNestRequest(ctx))
                return Results.NotFound();
            if (!owner.Options.GoogleConfigured)
                return Results.Redirect("/signin?error=google-none");
            var session = owner.Current(ctx);
            if (purpose == GoogleSignIn.Connect)
                return session is null ? Results.Redirect("/signin") : Results.Redirect(google.Start(GoogleSignIn.Connect, session.Id).ToString());
            if (owner.Store.GoogleAccount is null)
                return Results.Redirect("/signin?error=google-none");
            return Results.Redirect(google.Start(GoogleSignIn.SignIn, null).ToString());
        }).RequireRateLimiting(PairingEndpoints.RateLimitPolicy);

        app.MapGet("/auth/google/callback", async (HttpContext ctx, [FromQuery] string? state, [FromQuery] string? code, OwnerAuth owner, GoogleSignIn google,
            FailureThrottle throttle, ILogger<GoogleSignIn> log, CancellationToken ct) =>
        {
            if (!owner.IsNestRequest(ctx) || !owner.Options.GoogleConfigured)
                return Results.NotFound();
            var address = ctx.Connection.RemoteIpAddress ?? IPAddress.None;
            try
            {
                var (purpose, sessionId, identity) = await google.FinishAsync(state, code, ct);
                if (purpose == GoogleSignIn.Connect)
                {
                    if (owner.Current(ctx) is not { } session || session.Id != sessionId)
                        return Results.Redirect("/signin?error=google-expired");
                    owner.Store.SetGoogleAccount(identity.Sub, identity.Email);
                    log.LogInformation("Google account {Email} was connected to the nest", identity.Email);
                    return Results.Redirect("/security?connected=google");
                }
                if (owner.Store.GoogleAccount is not { } linked || linked.Sub != identity.Sub)
                {
                    var failures = throttle.RecordFailure(address);
                    log.LogWarning("A Google account that is not the nest's tried to sign in from {Address} ({Failures} recent failure(s))", address, failures);
                    return Results.Redirect("/signin?error=google-mismatch");
                }
                owner.SignIn(ctx, "google");
                return Results.Redirect("/devices");
            }
            catch (GoogleSignInException ex)
            {
                log.LogWarning("A Google sign-in did not work ({Code}): {Message}", ex.Code, ex.Message);
                return Results.Redirect($"/signin?error=google-{ex.Code}");
            }
        }).RequireRateLimiting(PairingEndpoints.RateLimitPolicy);
    }

    private static async Task SendLinkAsync(OwnerAuth owner, IEmailSender sender, string address, string purpose, ILogger log, CancellationToken ct)
    {
        var code = owner.Store.CreateEmailLink(purpose, address);
        var url = $"{owner.PublicUrl}/email-link#code={code}";
        var host = owner.Options.PublicHost ?? "your nest";
        var (subject, intro) = purpose == EmailLinkPurpose.Confirm
            ? ("Confirm your email for Pairnets", $"Someone (hopefully you) asked to use this address for sign-in links to {host}.")
            : ("Your Pairnets sign-in link", $"Someone (hopefully you) asked to sign in to {host}.");
        var text = $"{intro}\n\nOpen this link to continue. It works once, for 15 minutes:\n{url}\n\nDidn't ask? Ignore this email: nothing happens unless the link is opened.";
        var html = $"<p>{WebUtility.HtmlEncode(intro)}</p><p><a href=\"{url}\" style=\"display:inline-block;padding:10px 18px;background:#2563EB;color:#fff;border-radius:8px;text-decoration:none;font-weight:600\">Continue</a></p>" +
                   "<p style=\"color:#6B7280\">It works once, for 15 minutes. Didn't ask? Ignore this email: nothing happens unless the link is opened.</p>";
        await sender.SendAsync(address, subject, text, html, ct);
        log.LogInformation("A {Purpose} email link was sent", purpose);
    }

    private static bool TryAddress(string? text, out string address)
    {
        address = string.Empty;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 200 || trimmed.Any(char.IsControl))
            return false;
        try
        {
            var parsed = new MailAddress(trimmed);
            if (parsed.Address != trimmed || !parsed.Host.Contains('.', StringComparison.Ordinal))
                return false;
            address = parsed.Address;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>"you@example.com" → "y•••@example.com" for showing on a page.</summary>
    public static string Mask(string address)
    {
        var at = address.IndexOf('@');
        return at <= 0 ? "•••" : address[0] + "•••" + address[at..];
    }

    private static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorBody(code, message), PairnetsJson.Options, statusCode: status);

    /// <summary>At most <see cref="MaxEmailsPerHour"/> emails an hour, whoever asks.</summary>
    private sealed class EmailThrottle
    {
        private readonly Queue<DateTimeOffset> _sent = new();

        public bool TryTake(DateTimeOffset now)
        {
            lock (_sent)
            {
                while (_sent.Count > 0 && now - _sent.Peek() > TimeSpan.FromHours(1))
                    _sent.Dequeue();
                if (_sent.Count >= MaxEmailsPerHour)
                    return false;
                _sent.Enqueue(now);
                return true;
            }
        }
    }
}
