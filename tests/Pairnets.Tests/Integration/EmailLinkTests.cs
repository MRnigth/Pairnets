using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Server;
using Pairnets.Server.Auth;
using Pairnets.Server.Mail;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>Sign-in links sent by email: confirming an address, signing in with a link, and what protects it.</summary>
public sealed class EmailLinkTests : IAsyncLifetime
{
    private readonly FakeEmailSender _mail = new();
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await StartAsync(configured: true);

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private Task<TestServer> StartAsync(bool configured) => TestServer.StartWithWebsiteAsync(
        configured ? new() { ["Sync:SmtpHost"] = "smtp.example.test", ["Sync:SmtpFrom"] = "Pairnets nest <nest@example.test>" } : null,
        builder => builder.Services.AddSingleton<IEmailSender>(_mail));

    private AuthStore Auth => _server.Services.GetRequiredService<AuthStore>();

    private async Task<HttpClient> SignedIn()
    {
        var web = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.NoContent, (await web.PostAsJsonAsync("web/api/setup", new { code = Auth.CreateSetupCode() })).StatusCode);
        return web;
    }

    private async Task<WebEndpoints.StateView> State(HttpClient web) =>
        (await web.GetFromJsonAsync<WebEndpoints.StateView>("web/api/state", PairnetsJson.Options))!;

    /// <summary>Adds and confirms an address the way a person does: ask on Security, open the link in a fresh browser.</summary>
    private async Task ConfirmAddress(HttpClient owner, string address)
    {
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync("web/api/email", new { email = address })).StatusCode);
        using var fromInbox = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.NoContent, (await fromInbox.PostAsJsonAsync("web/api/signin/email/confirm", new { code = _mail.Messages[^1].Code })).StatusCode);
    }

    [Fact]
    public async Task AConfirmedAddressGetsSignInLinks()
    {
        using var owner = await SignedIn();
        await ConfirmAddress(owner, "you@example.com");
        var confirm = _mail.Messages[0]; // the welcome comes after it, once the address is confirmed
        Assert.Equal("you@example.com", confirm.To);
        Assert.Equal("Confirm your email for Pairnets", confirm.Subject);
        Assert.StartsWith("https://localhost/email-link#code=", confirm.Link); // the code is in the fragment: no server or mail scanner log sees it
        Assert.Equal("you@example.com", Auth.OwnerEmail);
        Assert.True((await State(owner)).Methods.Email);

        using var phone = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsync("web/api/signin/email/request", null)).StatusCode);
        var link = _mail.Messages[^1];
        Assert.Equal(("you@example.com", "Your Pairnets sign-in link"), (link.To, link.Subject));
        Assert.Contains("works once, for 15 minutes", link.Text);
        Assert.Contains(link.Link, link.Html);
        Assert.False((await State(phone)).SignedIn); // asking is not signing in

        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsJsonAsync("web/api/signin/email/confirm", new { code = link.Code })).StatusCode);
        var state = await State(phone);
        Assert.True(state.SignedIn);
        Assert.Equal("email link", state.SessionMethod);

        using var again = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.BadRequest, (await again.PostAsJsonAsync("web/api/signin/email/confirm", new { code = link.Code })).StatusCode); // once only
        Assert.False((await State(again)).SignedIn);
    }

    [Fact]
    public async Task AWelcomeComesOnceWhenAnAddressIsFirstConfirmed()
    {
        using var owner = await SignedIn();
        await ConfirmAddress(owner, "you@example.com");

        Assert.Equal(new[] { EmailTemplates.ConfirmSubject, EmailTemplates.WelcomeSubject }, _mail.Messages.Select(m => m.Subject));
        var welcome = _mail.Messages[1];
        Assert.Equal("you@example.com", welcome.To);
        Assert.Contains("https://localhost/devices", welcome.Text);
        Assert.Contains("https://localhost/security", welcome.Text);
        Assert.Contains("https://localhost/devices", welcome.Html);
        Assert.Equal(string.Empty, welcome.Link); // a welcome carries no sign-in link

        // Signing in with a link, or confirming the same address again (however it is cased), welcomes nobody twice.
        using var phone = _server.WebBrowser();
        await phone.PostAsync("web/api/signin/email/request", null);
        await phone.PostAsJsonAsync("web/api/signin/email/confirm", new { code = _mail.Messages[^1].Code });
        await ConfirmAddress(owner, "You@Example.com");
        Assert.Single(_mail.Messages, m => m.Subject == EmailTemplates.WelcomeSubject);

        // A different address is new to the nest: it is welcomed.
        await ConfirmAddress(owner, "other@example.com");
        Assert.Equal(new[] { "you@example.com", "other@example.com" },
            _mail.Messages.Where(m => m.Subject == EmailTemplates.WelcomeSubject).Select(m => m.To));
    }

    [Fact]
    public async Task SigningInStillWorksWhenTheWelcomeCannotBeSent()
    {
        await using var server = await TestServer.StartWithWebsiteAsync(
            new() { ["Sync:SmtpHost"] = "smtp.example.test", ["Sync:SmtpFrom"] = "Pairnets nest <nest@example.test>" },
            builder => builder.Services.AddSingleton<IEmailSender>(new RefusesWelcomes(_mail)));
        using var owner = server.WebBrowser();
        var setup = server.Services.GetRequiredService<AuthStore>().CreateSetupCode();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync("web/api/setup", new { code = setup })).StatusCode);
        await owner.PostAsJsonAsync("web/api/email", new { email = "you@example.com" });
        using var fromInbox = server.WebBrowser();

        var confirmed = await fromInbox.PostAsJsonAsync("web/api/signin/email/confirm", new { code = _mail.Messages[^1].Code });

        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);
        Assert.Equal("you@example.com", server.Services.GetRequiredService<AuthStore>().OwnerEmail);
        Assert.True((await State(fromInbox)).SignedIn);
        Assert.DoesNotContain(_mail.Messages, m => m.Subject == EmailTemplates.WelcomeSubject);
    }

    /// <summary>A mail server that takes everything except the welcome.</summary>
    private sealed class RefusesWelcomes(IEmailSender inner) : IEmailSender
    {
        public Task SendAsync(string to, string subject, string textBody, string htmlBody, CancellationToken ct = default) =>
            subject == EmailTemplates.WelcomeSubject
                ? throw new SmtpException("The mail server said no.")
                : inner.SendAsync(to, subject, textBody, htmlBody, ct);
    }

    [Fact]
    public async Task AskingForALinkNeverRevealsWhetherAnAddressIsSetUp()
    {
        using var stranger = _server.WebBrowser();

        Assert.Equal(HttpStatusCode.NoContent, (await stranger.PostAsync("web/api/signin/email/request", null)).StatusCode);

        Assert.Empty(_mail.Messages);
    }

    [Fact]
    public async Task TheAddressTypedInTheAppMustBeTheOwnersAndTheLinkCarriesTheWayBack()
    {
        using var owner = await SignedIn();
        await ConfirmAddress(owner, "you@example.com");
        using var app = _server.WebBrowser();

        // Someone else's address (or a typo) sends nothing anywhere, and the answer does not say so.
        Assert.Equal(HttpStatusCode.NoContent,
            (await app.PostAsJsonAsync("web/api/signin/email/request", new { email = "wrong@example.com", next = "/link?code=KQ7M-4PXD" })).StatusCode);
        Assert.Equal(2, _mail.Messages.Count); // still only the confirmation and welcome mails from the setup above

        // The owner's own address (however it is cased) gets the link; the way back rides in the fragment, like the code.
        Assert.Equal(HttpStatusCode.NoContent,
            (await app.PostAsJsonAsync("web/api/signin/email/request", new { email = "You@Example.COM", next = "/link?code=KQ7M-4PXD" })).StatusCode);
        var link = _mail.Messages[^1];
        Assert.Equal(("you@example.com", "Your Pairnets sign-in link"), (link.To, link.Subject));
        Assert.EndsWith("&next=%2Flink%3Fcode%3DKQ7M-4PXD", link.Link);
        Assert.NotEqual(string.Empty, link.Code);
        Assert.DoesNotContain("&", link.Code, StringComparison.Ordinal);

        // Only a path on this site may ride along; anything else is dropped.
        Assert.Equal(HttpStatusCode.NoContent,
            (await app.PostAsJsonAsync("web/api/signin/email/request", new { email = "you@example.com", next = "https://evil.example/" })).StatusCode);
        Assert.DoesNotContain("next=", _mail.Messages[^1].Link, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyAFewEmailsAreSentPerHour()
    {
        using var owner = await SignedIn();
        await ConfirmAddress(owner, "you@example.com"); // one
        using var browser = _server.WebBrowser();
        for (var i = 0; i < LinkedSignInEndpoints.MaxEmailsPerHour - 1; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("web/api/signin/email/request", null)).StatusCode);

        var refused = await browser.PostAsync("web/api/signin/email/request", null);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(LinkedSignInEndpoints.MaxEmailsPerHour, _mail.Messages.Count(m => m.Subject != EmailTemplates.WelcomeSubject)); // the welcome is not a link anyone can ask for
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("a@b")]
    [InlineData("a@b.c\nBcc: evil@example.com")]
    [InlineData("Name <a@b.example>")]
    public async Task OnlyPlainAddressesAreAccepted(string address)
    {
        using var owner = await SignedIn();

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("web/api/email", new { email = address })).StatusCode);
        Assert.Empty(_mail.Messages);
    }

    [Fact]
    public async Task AnAddressCanOnlyBeSetWhileSignedInAndNeedsItsLink()
    {
        using var stranger = _server.WebBrowser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.PostAsJsonAsync("web/api/email", new { email = "evil@example.com" })).StatusCode);

        using var owner = await SignedIn();
        await owner.PostAsJsonAsync("web/api/email", new { email = "you@example.com" });
        Assert.Null(Auth.OwnerEmail); // nothing changes until the link in that inbox is opened
    }

    [Fact]
    public async Task TheLastWayInCannotBeTurnedOff()
    {
        using var owner = await SignedIn();
        await ConfirmAddress(owner, "you@example.com");
        // Only the email link remains (no password, passkey or Google): it must stay.
        using var viaLink = _server.WebBrowser();
        await viaLink.PostAsync("web/api/signin/email/request", null);
        await viaLink.PostAsJsonAsync("web/api/signin/email/confirm", new { code = _mail.Messages[^1].Code });

        Assert.Equal(HttpStatusCode.Conflict, (await viaLink.DeleteAsync("web/api/email")).StatusCode);

        await viaLink.PostAsJsonAsync("web/api/password", new { password = "some long password" });
        Assert.Equal(HttpStatusCode.NoContent, (await viaLink.DeleteAsync("web/api/email")).StatusCode);
        Assert.Null(Auth.OwnerEmail);
    }

    [Fact]
    public async Task WithoutMailSettingsEmailIsOffAndSaysSo()
    {
        await using var plain = await StartAsync(configured: false);
        using var web = plain.WebBrowser();
        var code = plain.Services.GetRequiredService<AuthStore>().CreateSetupCode();
        await web.PostAsJsonAsync("web/api/setup", new { code });

        var security = (await web.GetFromJsonAsync<WebEndpoints.SecurityView>("web/api/security", PairnetsJson.Options))!;
        Assert.False(security.EmailAvailable);
        Assert.Equal(HttpStatusCode.Conflict, (await web.PostAsJsonAsync("web/api/email", new { email = "you@example.com" })).StatusCode);
    }

    [Fact]
    public async Task SecurityShowsTheAddressMasked()
    {
        using var owner = await SignedIn();
        await ConfirmAddress(owner, "you@example.com");

        var security = (await owner.GetFromJsonAsync<WebEndpoints.SecurityView>("web/api/security", PairnetsJson.Options))!;

        Assert.True(security.EmailAvailable);
        Assert.Equal("y•••@example.com", security.EmailAddress);
    }

    [Fact]
    public async Task LinksAndTheirCodesNeverReachTheLog()
    {
        using var owner = await SignedIn();
        await ConfirmAddress(owner, "you@example.com");
        using var phone = _server.WebBrowser();
        await phone.PostAsync("web/api/signin/email/request", null);
        var code = _mail.Messages[^1].Code;
        await phone.PostAsJsonAsync("web/api/signin/email/confirm", new { code });

        Assert.DoesNotContain(_server.Logs.Lines, l => _mail.Messages.Any(m => m.Code.Length > 0 && l.Contains(m.Code, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheRealSenderTalksSmtpToAMailServer()
    {
        await using var smtp = new FakeSmtpServer();
        var options = new SyncOptions { SmtpHost = "127.0.0.1", SmtpPort = smtp.Port, SmtpFrom = "Pairnets nest <nest@example.test>", SmtpUseTls = false };
        var sender = new SmtpEmailSender(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<SmtpEmailSender>.Instance);

        await sender.SendAsync("you@example.com", "Your Pairnets sign-in link", "Open https://nest.example.test/email-link#code=abc", "<p>Open it</p>");

        var mail = Assert.Single(smtp.Mails);
        Assert.Equal(("nest@example.test", "you@example.com"), (mail.From, mail.To));
        Assert.Contains("Subject: Your Pairnets sign-in link", mail.Data);
        // The body is quoted-printable ("=" is sent as "=3D"); mail programs decode it.
        Assert.Contains("email-link#code=3Dabc", mail.Data);
        Assert.Contains("text/html", mail.Data);
    }

    [Fact]
    public async Task ASenderThatCannotConnectFailsWithoutLeakingTheCredentials()
    {
        var options = new SyncOptions { SmtpHost = "127.0.0.1", SmtpPort = 9, SmtpFrom = "nest@example.test", SmtpUser = "user", SmtpPassword = "hunter2-secret", SmtpUseTls = false };
        var sender = new SmtpEmailSender(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<SmtpEmailSender>.Instance);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync("you@example.com", "s", "t", "<p>t</p>"));

        Assert.DoesNotContain("hunter2-secret", ex.ToString());
    }
}
