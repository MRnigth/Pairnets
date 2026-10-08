using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Pairnets.Core;
using Pairnets.Server;
using Pairnets.Server.Auth;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>
/// A setup link resets a forgotten password: for a short while after opening one, that browser may set a new
/// password without the old one. Every other browser still needs the old one. This runs the password endpoint's
/// own code against a real auth.db, with the clock under control.
/// </summary>
public sealed class SetupLinkPasswordResetTests : IDisposable
{
    private const string OldPassword = "forgotten password 1";
    private const string NewPassword = "brand new password 2";

    private readonly TempDir _dir = new("reset");
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));
    private readonly AuthStore _store;
    private readonly OwnerAuth _owner;
    private readonly FailureThrottle _throttle;

    public SetupLinkPasswordResetTests()
    {
        _store = new AuthStore(new ServerPaths(_dir.Path), _clock);
        _owner = new OwnerAuth(_store, new SyncOptions { PublicUrl = "https://nest.example.com" }, _clock);
        _throttle = new FailureThrottle(_clock);
        // The password was chosen long ago (and then forgotten).
        _owner.SetPassword(OldPassword);
        _clock.Advance(TimeSpan.FromDays(40));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    /// <summary>Signs a browser in with <paramref name="method"/>; returns its cookie secret.</summary>
    private string SignIn(string method) => _store.CreateSession(method, "test browser").Secret;

    /// <summary>POST /web/api/password from the browser with this cookie.</summary>
    private (int Status, string? Message) Post(string cookie, string password, string? current = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"{OwnerAuth.CookieName}={cookie}";
        Assert.NotNull(_owner.Current(ctx)); // the endpoint is only reached signed in
        var result = WebEndpoints.SetPassword(ctx, new WebEndpoints.PasswordBody(password, current), _owner, _throttle, NullLogger.Instance);
        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;
        var message = (result as IValueHttpResult)?.Value is ErrorBody error ? error.Message : null;
        return (status, message);
    }

    private OwnerSession Session(string cookie) => _store.FindSession(cookie)!;

    [Fact]
    public void ABrowserThatJustOpenedASetupLinkSetsANewPasswordWithoutTheOldOne()
    {
        var browser = SignIn(WebEndpoints.SetupLinkMethod);
        _clock.Advance(WebEndpoints.SetupLinkPasswordWindow - TimeSpan.FromMinutes(1));
        Assert.True(WebEndpoints.MayResetPassword(_owner, Session(browser)));

        // A current password that is given must still be right.
        Assert.Equal(StatusCodes.Status400BadRequest, Post(browser, NewPassword, current: "not the password").Status);
        Assert.True(_owner.CheckPassword(OldPassword));

        Assert.Equal(StatusCodes.Status204NoContent, Post(browser, NewPassword).Status);

        Assert.False(_owner.CheckPassword(OldPassword));
        Assert.True(_owner.CheckPassword(NewPassword));
    }

    [Fact]
    public void ResettingThePasswordSignsOutEveryOtherBrowser()
    {
        var stranger = SignIn("password");
        var browser = SignIn(WebEndpoints.SetupLinkMethod);

        Assert.Equal(StatusCodes.Status204NoContent, Post(browser, NewPassword).Status);

        Assert.Null(_store.FindSession(stranger));
        Assert.NotNull(_store.FindSession(browser));
    }

    [Fact]
    public void ChangingThePasswordWithTheOldOneKeepsOtherBrowsers()
    {
        var laptop = SignIn("password");
        var browser = SignIn("passkey");

        Assert.Equal(StatusCodes.Status204NoContent, Post(browser, NewPassword, current: OldPassword).Status);

        Assert.NotNull(_store.FindSession(laptop));
    }

    [Theory]
    [InlineData("password")]
    [InlineData("passkey")]
    [InlineData("email link")]
    [InlineData("google")]
    public void EveryOtherBrowserStillNeedsTheCurrentPassword(string method)
    {
        var browser = SignIn(method);
        Assert.False(WebEndpoints.MayResetPassword(_owner, Session(browser)));

        var (status, message) = Post(browser, NewPassword);
        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Equal("Type your current password.", message);
        Assert.Equal("Your current password is not right.", Post(browser, NewPassword, current: "not the password").Message);
        Assert.True(_owner.CheckPassword(OldPassword));
        Assert.False(_owner.CheckPassword(NewPassword));

        Assert.Equal(StatusCodes.Status204NoContent, Post(browser, NewPassword, current: OldPassword).Status);
        Assert.False(_owner.CheckPassword(OldPassword));
        Assert.True(_owner.CheckPassword(NewPassword));
    }

    [Fact]
    public void ASetupLinkOpenedTooLongAgoNoLongerResetsThePassword()
    {
        var browser = SignIn(WebEndpoints.SetupLinkMethod);
        _clock.Advance(WebEndpoints.SetupLinkPasswordWindow + TimeSpan.FromMinutes(1));
        Assert.False(WebEndpoints.MayResetPassword(_owner, Session(browser)));

        var (status, message) = Post(browser, NewPassword);

        Assert.Equal(StatusCodes.Status400BadRequest, status);
        Assert.Contains("owner-link", message); // how to get a fresh link
        Assert.True(_owner.CheckPassword(OldPassword));
        Assert.False(_owner.CheckPassword(NewPassword));
    }

    [Fact]
    public void OneSetupLinkResetsThePasswordOnce()
    {
        var browser = SignIn(WebEndpoints.SetupLinkMethod);
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(StatusCodes.Status204NoContent, Post(browser, NewPassword).Status);
        _clock.Advance(TimeSpan.FromMinutes(2));

        // The password it chose itself is known to it: changing that one again needs it, as usual.
        Assert.False(WebEndpoints.MayResetPassword(_owner, Session(browser)));
        Assert.Equal(StatusCodes.Status400BadRequest, Post(browser, "third password 3").Status);
        Assert.True(_owner.CheckPassword(NewPassword));
        Assert.Equal(StatusCodes.Status204NoContent, Post(browser, "third password 3", current: NewPassword).Status);
    }

    [Fact]
    public void WithoutAPasswordNoCurrentOneIsAsked()
    {
        _store.SetPasswordHash(null);
        var browser = SignIn("passkey");

        Assert.Equal(StatusCodes.Status204NoContent, Post(browser, NewPassword).Status);
        Assert.True(_owner.CheckPassword(NewPassword));
    }
}
