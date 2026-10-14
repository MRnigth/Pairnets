using Microsoft.Data.Sqlite;
using Pairnets.Server.Storage;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

public sealed class AuthStoreTests : IDisposable
{
    private readonly TempDir _dir = new("auth");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private AuthStore Store() => new(new ServerPaths(_dir.Path));

    [Fact]
    public void AKeyIsShownOnceAndOnlyItsHashIsKept()
    {
        var store = Store();
        var (device, key) = store.AddDevice("LAPTOP", "Windows", "test");

        Assert.StartsWith(AuthStore.KeyPrefix, key);
        Assert.True(key.Length >= 40);
        Assert.Equal(device.Id, store.FindByKey(key)?.Id);
        Assert.Null(store.FindByKey(key + "x"));
        Assert.Null(store.FindByKey("not-a-key"));
        SqliteConnection.ClearAllPools();
        Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(_dir.Path, "auth.db"), System.Text.Encoding.Latin1));
    }

    [Theory]
    [InlineData("approved by the owner (google)", "google")]
    [InlineData("approved by the owner (email link)", "email")]
    [InlineData("approved by the owner (passkey)", "passkey")]
    [InlineData("approved by the owner (Password)", "password")]
    [InlineData("approved by the owner (setup link)", "setup link")]
    [InlineData("approved by the owner (website)", null)]
    [InlineData("approved by the owner", null)]
    [InlineData("test", null)]
    public void TheWayTheOwnerSignedInIsReadFromTheApproval(string approvedBy, string? method) =>
        Assert.Equal(method, new PairedDevice("id", "LAPTOP", null, DateTimeOffset.UnixEpoch, approvedBy, null).ApprovalMethod);

    [Fact]
    public void NamesStayUniqueAmongActiveComputers()
    {
        var store = Store();
        var first = store.AddDevice("Laptop", null, "test").Device;
        var second = store.AddDevice("LAPTOP", null, "test").Device;
        var third = store.AddDevice("laptop", null, "test").Device;

        Assert.Equal(["Laptop", "LAPTOP (2)", "laptop (3)"], new[] { first.Name, second.Name, third.Name });

        store.RemoveDevice(first.Id);
        Assert.Equal("Laptop", store.AddDevice("Laptop", null, "test").Device.Name); // free again
        Assert.Equal("Desk", store.RenameDevice(second.Id, "  Desk\t")!.Name);
        Assert.Equal("desk (2)", store.RenameDevice(third.Id, "desk")!.Name);
    }

    [Fact]
    public void ARemovedComputerIsStillRecognisedButNotActive()
    {
        var store = Store();
        var (device, key) = store.AddDevice("LAPTOP", null, "test");

        Assert.True(store.RemoveDevice(device.Id));
        Assert.False(store.RemoveDevice(device.Id));
        Assert.False(store.FindByKey(key)!.IsActive);
        Assert.Empty(store.ListDevices());
        Assert.Single(store.ListDevices(includeRemoved: true));
        Assert.Null(store.RenameDevice(device.Id, "Other"));
        Assert.Null(store.ReplaceKey(device.Id));
    }

    [Fact]
    public void ReplacingAKeyLocksOutTheOldOne()
    {
        var store = Store();
        var (device, oldKey) = store.AddDevice("LAPTOP", null, "test");

        var newKey = store.ReplaceKey(device.Id)!;

        Assert.Null(store.FindByKey(oldKey));
        Assert.Equal(device.Id, store.FindByKey(newKey)?.Id);
    }

    [Theory]
    [InlineData("  LAPTOP  ", "LAPTOP")]
    [InlineData("a\u0000b\nc", "abc")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("unknown", null)]
    [InlineData("Another Computer", null)]
    public void NamesAreCleaned(string name, string? expected) => Assert.Equal(expected, AuthStore.CleanName(name));

    [Fact]
    public void LongNamesAreCut() => Assert.Equal(64, AuthStore.CleanName(new string('x', 100))!.Length);

    [Fact]
    public void TheSharedTokenIsAllowedUntilTurnedOff()
    {
        var store = Store();
        Assert.True(store.AllowSharedToken);
        store.AllowSharedToken = false;
        Assert.False(Store().AllowSharedToken);
    }

    [Fact]
    public void ADatabaseFromANewerServerIsRefused()
    {
        Store();
        SqliteConnection.ClearAllPools();
        using (var conn = new SqliteConnection($"Data Source={Path.Combine(_dir.Path, "auth.db")}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version=99;";
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        Assert.Throws<InvalidOperationException>(Store);
    }
}
