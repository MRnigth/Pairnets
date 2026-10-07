using Microsoft.Data.Sqlite;
using Pairnets.Server.Storage;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>Pair requests, setup links and sessions in auth.db, with the clock under control.</summary>
public sealed class PairRequestTests : IDisposable
{
    private readonly TempDir _dir = new("pairing");
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private AuthStore Store() => new(new ServerPaths(_dir.Path), _clock);

    [Fact]
    public void ARequestExpiresAfterTenMinutes()
    {
        var store = Store();
        var (request, _) = store.CreatePairRequest("LAPTOP", null, null, "192.0.2.9");

        _clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(PairRequest.Expired, store.FindPairRequestByCode(request.UserCode)!.StatusAt(_clock.Now));
        Assert.False(store.DecidePairRequest(request.Id, approve: true, "test"));
        Assert.Empty(store.ListPendingPairRequests());
    }

    [Fact]
    public void AnApprovedComputerHasFiveMinutesToCollectItsKey()
    {
        var store = Store();
        var (request, secret) = store.CreatePairRequest("LAPTOP", null, null, null);
        _clock.Advance(TimeSpan.FromMinutes(9));
        Assert.True(store.DecidePairRequest(request.Id, approve: true, "test"));

        _clock.Advance(TimeSpan.FromMinutes(4)); // past the original ten minutes, inside the pickup window
        Assert.NotNull(store.DeliverPairRequest(store.FindPairRequestBySecret(secret)!.Id));
        Assert.Null(store.DeliverPairRequest(request.Id));
    }

    [Fact]
    public void AnUncollectedApprovalAddsNoComputer()
    {
        var store = Store();
        var (request, _) = store.CreatePairRequest("LAPTOP", null, null, null);
        store.DecidePairRequest(request.Id, approve: true, "test");

        _clock.Advance(AuthStore.PairPickupWindow + TimeSpan.FromSeconds(1));

        Assert.Null(store.DeliverPairRequest(request.Id));
        Assert.Empty(store.ListDevices());
    }

    [Theory]
    [InlineData("kq7m-4pxd", "KQ7M4PXD")]
    [InlineData("KQ7M 4PXD", "KQ7M4PXD")]
    [InlineData("KQ7M4PXD", "KQ7M4PXD")]
    [InlineData("KQ7M-4PX", null)]
    [InlineData("KQ7M-4PXDD", null)]
    [InlineData("KQ0M-4PXD", null)] // no zero in codes
    [InlineData("AEIO-UUUU", null)] // no vowels either
    [InlineData(null, null)]
    public void CodesAreReadForgivingly(string? typed, string? expected) => Assert.Equal(expected, AuthStore.NormalizeCode(typed));

    [Fact]
    public void ASetupLinkWorksOnceAndOnlyForADay()
    {
        var store = Store();
        var used = store.CreateSetupCode();
        var late = store.CreateSetupCode();

        Assert.True(store.UseSetupCode(used));
        Assert.False(store.UseSetupCode(used));
        _clock.Advance(AuthStore.SetupLinkLifetime);
        Assert.False(store.UseSetupCode(late));
        Assert.False(store.UseSetupCode(string.Empty));
    }

    [Fact]
    public void AnEmailedLinkWorksOnceAndOnlyForFifteenMinutes()
    {
        var store = Store();
        var used = store.CreateEmailLink(EmailLinkPurpose.SignIn, "you@example.com");
        var late = store.CreateEmailLink(EmailLinkPurpose.Confirm, "new@example.com");

        Assert.Equal(new EmailLink(EmailLinkPurpose.SignIn, "you@example.com"), store.UseEmailLink(used));
        Assert.Null(store.UseEmailLink(used));
        _clock.Advance(AuthStore.EmailLinkLifetime);
        Assert.Null(store.UseEmailLink(late));
        Assert.Null(store.UseEmailLink(null));
        Assert.Null(store.UseEmailLink(new string('x', 200)));
    }

    [Fact]
    public void EmailAndGoogleCountAsWaysToSignIn()
    {
        var store = Store();
        Assert.False(store.HasSignInMethod);

        store.SetOwnerEmail("you@example.com");
        Assert.True(store.HasSignInMethod);
        store.SetOwnerEmail(null);
        Assert.False(store.HasSignInMethod);

        store.SetGoogleAccount("12345", "you@gmail.com");
        Assert.True(store.HasSignInMethod);
        Assert.Equal(("12345", "you@gmail.com"), store.GoogleAccount);
        store.SetGoogleAccount(null, null);
        Assert.Null(store.GoogleAccount);
        Assert.False(store.HasSignInMethod);
    }

    [Fact]
    public void SessionsLastThirtyDaysAfterTheLastVisit()
    {
        var store = Store();
        var (_, secret) = store.CreateSession("password", "test browser");

        _clock.Advance(TimeSpan.FromDays(20));
        Assert.NotNull(store.FindSession(secret)); // a visit extends it
        _clock.Advance(TimeSpan.FromDays(20));
        Assert.NotNull(store.FindSession(secret));
        _clock.Advance(AuthStore.SessionLifetime);
        Assert.Null(store.FindSession(secret));
        Assert.Empty(store.ListSessions());
    }
}
