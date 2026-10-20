using System.Net;
using System.Net.Http.Json;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Tests.Infrastructure;
using Pairnets.Tests.Unit;

namespace Pairnets.Tests.Integration;

/// <summary>GET /api/storage and POST /api/storage/move, as the apps' "Where your nest keeps files" uses them.</summary>
public sealed class StorageApiTests
{
    private static async Task<string?> Code(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<ErrorBody>(PairnetsJson.Options))?.Code;

    /// <summary>A server with the stand-ins for the root helper (place.sh next to update.sh, and its path unit).</summary>
    private static async Task<(TestServer Server, TempDir Dir)> StartWithHelperAsync()
    {
        var dir = new TempDir("storage-api");
        var script = dir.Combine("update.sh");
        File.WriteAllText(script, "#!/bin/sh\n");
        File.WriteAllText(dir.Combine("place.sh"), "#!/bin/sh\n");
        File.WriteAllText(dir.Combine("pairnets-place.path"), "[Path]\n");
        var server = await TestServer.StartAsync(config: new()
        {
            ["Sync:UpdaterScript"] = script,
            ["Sync:PlacePathUnit"] = dir.Combine("pairnets-place.path"),
        });
        return (server, dir);
    }

    [Fact]
    public async Task ANestWithoutTheHelperSaysSoAndRefusesAMove()
    {
        await using var server = await TestServer.StartAsync();
        using var api = server.Client();
        Assert.False((await api.GetInfoAsync(default)).CanMoveStorage);
        var storage = await api.GetStorageAsync(default);
        Assert.NotNull(storage);
        Assert.False(storage.CanMove);
        Assert.Equal(server.Paths.DataDir, storage.Current);
        Assert.NotNull(storage.Free);
        Assert.False(File.Exists(Path.Combine(server.Paths.DataDir, "update", "place-request")));

        using var http = server.RawHttp(server.Token);
        var resp = await http.PostAsJsonAsync("api/storage/move", new StorageMoveRequest("/mnt/data/pairnets"), PairnetsJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal(ErrorCodes.UpdaterMissing, await Code(resp));
        var answer = await api.RequestStorageMoveAsync("/mnt/data/pairnets", default);
        Assert.Equal(PairnetsApiClient.StorageMoveOutcome.Refused, answer.Outcome);
        Assert.Contains("install command", answer.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDrivesAreAskedForAndAMoveLeavesARequestForTheHelper()
    {
        var (server, dir) = await StartWithHelperAsync();
        await using var _ = server;
        using var __ = dir;
        using var api = server.Client();
        var updateDir = Path.Combine(server.Paths.DataDir, "update");
        var request = Path.Combine(updateDir, "place-request");

        Assert.Equal(HttpStatusCode.Unauthorized, (await server.RawHttp().GetAsync("api/storage")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.RawHttp().PostAsJsonAsync("api/storage/move", new StorageMoveRequest("/x"), PairnetsJson.Options)).StatusCode);
        Assert.True((await api.GetInfoAsync(default)).CanMoveStorage);

        // No list yet: the nest asks its helper for one.
        var first = (await api.GetStorageAsync(default))!;
        Assert.True(first.CanMove);
        Assert.Empty(first.Drives);
        Assert.Equal("action=list\n", File.ReadAllText(request));

        // The helper answers (here: the test, as the helper would).
        File.Delete(request);
        File.WriteAllText(Path.Combine(updateDir, "drives.json"), StorageMoverTests.SampleDrives.Replace("2026-10-20T12:00:00Z",
            DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        var listed = (await api.GetStorageAsync(default))!;
        Assert.Equal(3, listed.Drives.Count);
        Assert.Equal("/var/lib/pairnets", listed.Current);
        Assert.False(File.Exists(request));

        using var http = server.RawHttp(server.Token);
        var bad = await http.PostAsJsonAsync("api/storage/move", new StorageMoveRequest("/media/stick/pairnets"), PairnetsJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync("api/storage/move", new { }, PairnetsJson.Options)).StatusCode);
        var same = await api.RequestStorageMoveAsync("/var/lib/pairnets", default);
        Assert.Equal(PairnetsApiClient.StorageMoveOutcome.AlreadyThere, same.Outcome);
        Assert.False(File.Exists(request));

        var moved = await api.RequestStorageMoveAsync("/mnt/data/pairnets", default);
        Assert.Equal(PairnetsApiClient.StorageMoveOutcome.Requested, moved.Outcome);
        Assert.Equal(StorageMoveStatus.Requested, moved.Storage!.Status!.State);
        Assert.Equal("action=move\ndata_dir=/mnt/data/pairnets\n", File.ReadAllText(request));

        // A second ask while that one waits: busy (409).
        var busy = await http.PostAsJsonAsync("api/storage/move", new StorageMoveRequest("/mnt/data/pairnets"), PairnetsJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, busy.StatusCode);
        Assert.Equal(ErrorCodes.Busy, await Code(busy));

        // The helper takes it and reports; asking again at once is too soon (429).
        File.Delete(request);
        File.WriteAllText(Path.Combine(updateDir, "place-status.json"),
            """{"state":"running","percent":40,"message":"Copying your files to the new drive","oldCopy":"","at":"2026-10-20T12:00:05Z"}""");
        var running = (await api.GetStorageAsync(default))!;
        Assert.Equal(40, running.Status!.Percent);
        File.WriteAllText(Path.Combine(updateDir, "place-status.json"),
            """{"state":"failed","percent":0,"message":"That drive can't be used: it is read-only. Nothing was changed.","oldCopy":"","at":"2026-10-20T12:00:09Z"}""");
        var tooSoon = await http.PostAsJsonAsync("api/storage/move", new StorageMoveRequest("/mnt/data/pairnets"), PairnetsJson.Options);
        Assert.Equal((HttpStatusCode)429, tooSoon.StatusCode);
    }

    [Fact]
    public async Task AMountMadeByHandCantBeChangedFromTheApp()
    {
        var (server, dir) = await StartWithHelperAsync();
        await using var _ = server;
        using var __ = dir;
        var updateDir = Path.Combine(server.Paths.DataDir, "update");
        Directory.CreateDirectory(updateDir);
        File.WriteAllText(Path.Combine(updateDir, "drives.json"), StorageMoverTests.SampleDrives.Replace("\"byHand\":false", "\"byHand\":true", StringComparison.Ordinal));
        using var http = server.RawHttp(server.Token);
        var resp = await http.PostAsJsonAsync("api/storage/move", new StorageMoveRequest("/mnt/data/pairnets"), PairnetsJson.Options);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal(ErrorCodes.Conflict, await Code(resp));
    }
}
