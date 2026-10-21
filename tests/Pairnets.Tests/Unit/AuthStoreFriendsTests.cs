using Microsoft.Data.Sqlite;
using Pairnets.Server.Storage;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>auth.db and friends' computers (schema 6): what each computer may do, kept with its key; nothing changes for the owner's.</summary>
public sealed class AuthStoreFriendsTests : IDisposable
{
    private readonly TempDir _dir = new("auth-friends");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private string DbPath => Path.Combine(_dir.Path, "auth.db");

    private AuthStore Store() => new(new ServerPaths(_dir.Path));

    private static readonly DeviceAccess PhotosViewOnly = new(DeviceAccess.View, ["Photos"]);

    private void Sql(string sql)
    {
        using (var conn = new SqliteConnection($"Data Source={DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
    }

    private long UserVersion()
    {
        using var conn = new SqliteConnection($"Data Source={DbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var version = (long)cmd.ExecuteScalar()!;
        conn.Close();
        SqliteConnection.ClearAllPools();
        return version;
    }

    [Fact]
    public void AnUpgradeFromSchema5KeepsEveryComputerAsTheOwners()
    {
        // auth.db as the server before friends left it (schema 5): one active computer with an icon, one removed.
        var key = AuthStore.NewKey();
        var hash = Convert.ToHexString(AuthStore.HashKey(key));
        Sql($"""
            CREATE TABLE devices(
                id TEXT PRIMARY KEY NOT NULL,
                name TEXT NOT NULL,
                tokenHash BLOB NOT NULL UNIQUE,
                system TEXT NULL,
                createdMs INTEGER NOT NULL,
                approvedBy TEXT NOT NULL,
                revokedMs INTEGER NULL,
                icon TEXT NULL);
            CREATE UNIQUE INDEX ix_devices_active_name ON devices(name COLLATE NOCASE) WHERE revokedMs IS NULL;
            CREATE TABLE settings(key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL);
            INSERT INTO devices VALUES('dev-desk', 'DESK', X'{hash}', 'Windows 11', 1760000000000, 'approved by the owner (google)', NULL, 'desktop');
            INSERT INTO devices VALUES('dev-old', 'OLD', X'00', NULL, 1750000000000, 'test', 1755000000000, NULL);
            PRAGMA user_version=5;
            """);

        var store = Store();

        Assert.Equal(6, UserVersion());
        var desk = store.FindByKey(key)!;
        Assert.Equal(("dev-desk", "DESK", "desktop", "google"), (desk.Id, desk.Name, desk.Icon, desk.ApprovalMethod));
        Assert.True(desk.AccessOrOwner.IsOwner);
        Assert.Null(desk.AccessOrOwner.Folders);
        Assert.Null(desk.Person);
        Assert.Null(desk.PersonId);
        Assert.All(store.ListDevices(includeRemoved: true), d => Assert.Equal(DeviceAccess.Owner, d.AccessOrOwner.Level));

        // Opening it again changes nothing more.
        SqliteConnection.ClearAllPools();
        Assert.True(Store().GetDevice("dev-desk")!.AccessOrOwner.IsOwner);
        Assert.Equal(6, UserVersion());
    }

    [Fact]
    public void ANewComputerIsTheOwnersUnlessItIsAFriends()
    {
        var store = Store();
        var (owner, _) = store.AddDevice("DESK", "Windows 11", "test");
        var (friend, friendKey) = store.AddDevice("ANNA-PC", "Windows 11", "test", PhotosViewOnly, "Anna Berg · a•••@example.com", "acc_annaberg");

        Assert.True(owner.AccessOrOwner.IsOwner);
        Assert.True(store.GetDevice(owner.Id)!.AccessOrOwner.IsOwner);
        var read = store.FindByKey(friendKey)!;
        Assert.Equal((DeviceAccess.View, "Anna Berg · a•••@example.com", "acc_annaberg"), (read.AccessOrOwner.Level, read.Person, read.PersonId));
        Assert.Equal(new[] { "Photos" }, read.AccessOrOwner.Folders!);
        Assert.Equal(friend.AccessOrOwner.Level, read.AccessOrOwner.Level);
    }

    [Fact]
    public void AFriendsShareChangesForAllTheirComputersAndNeverForTheOwners()
    {
        var store = Store();
        var (owner, _) = store.AddDevice("DESK", null, "test", null, null, "acc_annaberg"); // the owner's, whatever it was sent with
        var (laptop, _) = store.AddDevice("ANNA-LAPTOP", null, "test", PhotosViewOnly, "Anna Berg", "acc_annaberg");
        var (phone, _) = store.AddDevice("ANNA-PC", null, "test", PhotosViewOnly, "Anna Berg", "acc_annaberg");
        var (other, _) = store.AddDevice("JONAS-PC", null, "test", PhotosViewOnly, "Jonas", "acc_jonas");
        var (gone, _) = store.AddDevice("ANNA-OLD", null, "test", PhotosViewOnly, "Anna Berg", "acc_annaberg");
        store.RemoveDevice(gone.Id);

        var changed = store.SetFriendAccess("acc_annaberg", new DeviceAccess(DeviceAccess.Edit));

        Assert.Equal(new[] { laptop.Id, phone.Id }, changed.Select(d => d.Id));
        Assert.All(changed, d => Assert.Equal((DeviceAccess.Edit, (IReadOnlyList<string>?)null), (d.AccessOrOwner.Level, d.AccessOrOwner.Folders)));
        Assert.True(store.GetDevice(owner.Id)!.AccessOrOwner.IsOwner);
        Assert.Equal(DeviceAccess.View, store.GetDevice(other.Id)!.AccessOrOwner.Level);
        Assert.Equal(DeviceAccess.View, store.GetDevice(gone.Id)!.AccessOrOwner.Level);
        Assert.Equal(new[] { laptop.Id, phone.Id }, store.ListFriendDevices("acc_annaberg").Select(d => d.Id));
        Assert.Empty(store.ListFriendDevices("acc_nobody"));
        Assert.Empty(store.SetFriendAccess("acc_nobody", PhotosViewOnly));
        Assert.Throws<ArgumentException>(() => store.SetFriendAccess("acc_annaberg", DeviceAccess.Full));
    }

    [Fact]
    public void ARowWithAnAccessThisServerDoesNotKnowSharesNothing()
    {
        var store = Store();
        var (friend, _) = store.AddDevice("ANNA-PC", null, "test", PhotosViewOnly, "Anna Berg", "acc_annaberg");
        var (damaged, _) = store.AddDevice("JONAS-PC", null, "test", PhotosViewOnly, "Jonas", "acc_jonas");
        Sql($"UPDATE devices SET access='superuser' WHERE id='{friend.Id}'; UPDATE devices SET folders='not json' WHERE id='{damaged.Id}';");

        var unknown = Store().GetDevice(friend.Id)!.AccessOrOwner;
        Assert.Equal(DeviceAccess.View, unknown.Level);
        Assert.False(unknown.Allows("Photos/a.jpg"));
        var broken = Store().GetDevice(damaged.Id)!.AccessOrOwner;
        Assert.Equal(DeviceAccess.View, broken.Level);
        Assert.False(broken.Allows("Photos/a.jpg"));
    }

    [Fact]
    public void ThePersonLineIsCut()
    {
        var (friend, _) = Store().AddDevice("ANNA-PC", null, "test", PhotosViewOnly, new string('x', 500), "acc_annaberg");
        Assert.Equal(AuthStore.MaxPersonLength, Store().GetDevice(friend.Id)!.Person!.Length);
    }
}
