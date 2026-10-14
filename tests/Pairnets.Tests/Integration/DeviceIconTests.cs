using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Server.Auth;
using Pairnets.Server.Storage;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>An icon per computer, saved on the nest: migration, endpoint, list, and an older nest.</summary>
public sealed class DeviceIconServerTests : IDisposable
{
    private readonly TempDir _dir = new("icons");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    [Fact]
    public void ADatabaseFromVersion4GetsTheIconColumnAndKeepsItsComputers()
    {
        var path = Path.Combine(_dir.Path, "auth.db");
        Directory.CreateDirectory(_dir.Path);
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE devices(id TEXT PRIMARY KEY NOT NULL, name TEXT NOT NULL, tokenHash BLOB NOT NULL UNIQUE, system TEXT NULL,
                    createdMs INTEGER NOT NULL, approvedBy TEXT NOT NULL, revokedMs INTEGER NULL);
                CREATE TABLE settings(key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL);
                CREATE TABLE pairRequests(codeHash BLOB PRIMARY KEY NOT NULL, userCode TEXT NOT NULL, name TEXT NOT NULL, system TEXT NULL,
                    createdMs INTEGER NOT NULL, expiresMs INTEGER NOT NULL, status TEXT NOT NULL, decidedMs INTEGER NULL, decidedBy TEXT NULL, deviceId TEXT NULL);
                CREATE TABLE passkeys(id TEXT PRIMARY KEY NOT NULL, credentialId BLOB NOT NULL UNIQUE, publicKey BLOB NOT NULL, algorithm INTEGER NOT NULL,
                    signCount INTEGER NOT NULL, name TEXT NOT NULL, createdMs INTEGER NOT NULL, lastUsedMs INTEGER NULL);
                CREATE TABLE emailLinks(codeHash BLOB PRIMARY KEY NOT NULL, purpose TEXT NOT NULL, email TEXT NOT NULL, createdMs INTEGER NOT NULL,
                    expiresMs INTEGER NOT NULL, usedMs INTEGER NULL);
                INSERT INTO devices VALUES('abc', 'LAPTOP', x'0102', 'Windows', 1700000000000, 'test', NULL);
                PRAGMA user_version=4;
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();

        var store = new AuthStore(new ServerPaths(_dir.Path));
        var laptop = Assert.Single(store.ListDevices());
        Assert.Equal(("abc", "LAPTOP"), (laptop.Id, laptop.Name));
        Assert.Null(laptop.Icon); // not chosen yet
        Assert.True(store.SetDeviceIcon("abc", "laptop"));
        Assert.Equal("laptop", store.GetDevice("abc")!.Icon);

        SqliteConnection.ClearAllPools();
        using var check = new SqliteConnection($"Data Source={path}");
        check.Open();
        using var version = check.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(AuthStore.SchemaVersion, Convert.ToInt32(version.ExecuteScalar()));
        Assert.Equal(5, AuthStore.SchemaVersion);
    }

    [Fact]
    public void TheStoreAcceptsOnlyTheFiveIcons_AndOnlyForActiveComputers()
    {
        var store = new AuthStore(new ServerPaths(_dir.Path));
        var (device, _) = store.AddDevice("DESKTOP", null, "test");
        Assert.Null(device.Icon);

        foreach (var icon in new[] { "desktop", "laptop", "office", "mini", "tv" })
        {
            Assert.True(store.SetDeviceIcon(device.Id, icon));
            Assert.Equal(icon, store.GetDevice(device.Id)!.Icon);
        }
        Assert.False(store.SetDeviceIcon(device.Id, "toaster"));
        Assert.False(store.SetDeviceIcon(device.Id, "LAPTOP"));
        Assert.False(store.SetDeviceIcon(device.Id, ""));
        Assert.Equal("tv", store.GetDevice(device.Id)!.Icon);
        Assert.False(store.SetDeviceIcon("nobody", "tv"));

        store.RemoveDevice(device.Id);
        Assert.False(store.SetDeviceIcon(device.Id, "mini"));
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task SettingAnIconShowsUpInTheDevicesList()
    {
        await using var server = await TestServer.StartAsync();
        var desktop = server.MintKey("DESKTOP");
        var laptop = server.MintKey("LAPTOP");
        using var api = server.Client("DESKTOP", desktop.Key);

        var before = await api.GetDevicesWithIconSupportAsync(CancellationToken.None);
        Assert.True(before!.IconsSupported); // the nest sends the field, even while it is empty
        Assert.All(before.Devices, d => Assert.Null(d.Icon));

        Assert.True(await api.SetDeviceIconAsync(laptop.Id, "laptop", CancellationToken.None)); // any computer may set another's
        Assert.True(await api.SetDeviceIconAsync(desktop.Id, "office", CancellationToken.None));

        var after = (await api.GetDevicesAsync(CancellationToken.None))!;
        Assert.Equal("laptop", after.Single(d => d.Id == laptop.Id).Icon);
        Assert.Equal("office", after.Single(d => d.Id == desktop.Id).Icon);

        // It is kept on the nest: a fresh store reads the same.
        Assert.Equal("laptop", new AuthStore(server.Paths).GetDevice(laptop.Id)!.Icon);
    }

    [Fact]
    public async Task AUnknownIconIs400_AnUnknownComputerIs404_AndSignInIsRequired()
    {
        await using var server = await TestServer.StartAsync();
        var desktop = server.MintKey("DESKTOP");
        using var http = server.RawHttp(desktop.Key);

        foreach (var body in new[] { """{"icon":"toaster"}""", """{"icon":""}""", """{"icon":null}""", "{}", """{"icon":"LAPTOP"}""" })
        {
            using var bad = await http.PatchAsync($"api/devices/{desktop.Id}/icon", Json(body));
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }
        using var empty = await http.PatchAsync($"api/devices/{desktop.Id}/icon", new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        using var missing = await http.PatchAsync("api/devices/no-such-computer/icon", Json("""{"icon":"tv"}"""));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Null(new AuthStore(server.Paths).GetDevice(desktop.Id)!.Icon);

        using var good = await http.PatchAsync($"api/devices/{desktop.Id}/icon", Json("""{"icon":"mini"}"""));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        var answer = await good.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("mini", answer!["icon"]);

        // No key, or a wrong one: refused, nothing changes.
        using var anonymous = server.RawHttp();
        using var noKey = await anonymous.PatchAsync($"api/devices/{desktop.Id}/icon", Json("""{"icon":"tv"}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);
        using var wrong = server.RawHttp("pn_wrong");
        using var wrongKey = await wrong.PatchAsync($"api/devices/{desktop.Id}/icon", Json("""{"icon":"tv"}"""));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
        Assert.Equal("mini", new AuthStore(server.Paths).GetDevice(desktop.Id)!.Icon);

        // A computer that was removed can no longer be given an icon (401 once its key is dropped, 404 while it is still cached).
        server.Services.GetRequiredService<DeviceKeys>().Store.RemoveDevice(desktop.Id);
        using var removed = await http.PatchAsync($"api/devices/{desktop.Id}/icon", Json("""{"icon":"tv"}"""));
        Assert.Contains(removed.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.NotFound });
        Assert.Equal("mini", new AuthStore(server.Paths).GetDevice(desktop.Id)!.Icon);
    }
}

/// <summary>What the app does with a nest that is too old to know icons.</summary>
public sealed class OldNestIconTests
{
    private sealed class OldNest(HttpStatusCode iconStatus) : HttpMessageHandler
    {
        public int IconCalls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/icon", StringComparison.Ordinal))
            {
                IconCalls++;
                return Task.FromResult(new HttpResponseMessage(iconStatus));
            }
            // A nest from before icons: no "icon" field on any computer.
            const string json = """[{"name":"DESKTOP","firstSeen":"2026-09-01T10:00:00+00:00","lastSeen":"2026-10-11T10:00:00+00:00","online":true,"id":"abc"}]""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task AListWithoutTheIconFieldMeansTheNestDoesNotKnowIcons()
    {
        using var api = new PairnetsApiClient(new Uri("http://nest.example.com/"), "token", "DESKTOP", new OldNest(HttpStatusCode.NotFound));
        var answer = await api.GetDevicesWithIconSupportAsync(CancellationToken.None);
        Assert.False(answer!.IconsSupported);
        Assert.Single(answer.Devices);
        Assert.Null(answer.Devices[0].Icon);
        Assert.Single((await api.GetDevicesAsync(CancellationToken.None))!); // the old call still works
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task SettingAnIconOnAnOldNestReturnsFalseInsteadOfThrowing(HttpStatusCode status)
    {
        using var api = new PairnetsApiClient(new Uri("http://nest.example.com/"), "token", "DESKTOP", new OldNest(status));
        Assert.False(await api.SetDeviceIconAsync("abc", "tv", CancellationToken.None));
    }
}
