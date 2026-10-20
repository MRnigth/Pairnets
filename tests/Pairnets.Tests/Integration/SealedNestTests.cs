using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Data.Sqlite;
using Pairnets.Core;
using Pairnets.Core.Crypto;
using Pairnets.Core.Hashing;
using Pairnets.Server.Storage;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// An encrypted nest (docs/ENCRYPTION.md): it stores only blobs under ids with the app's tag and meta, compares the
/// base with the tag, refuses apps that don't encrypt with 426, and passes key envelopes and unlock messages on
/// without being able to read them.
/// </summary>
public class SealedNestTests : IAsyncLifetime
{
    private const string SecretName = "Tax return Ørsted 2026.pdf";
    private static readonly byte[] SecretContent = Encoding.UTF8.GetBytes("CONFIDENTIAL-CONTENT-" + new string('z', 5000));

    private TestServer _server = null!;
    private ComputerKeys _keys = null!;
    private KeyRing _ring = null!;

    public async Task InitializeAsync()
    {
        _server = await TestServer.StartAsync();
        _keys = ComputerKeys.Create();
        _ring = KeyRing.Create(_server.Store.ServerId, _keys.AsTrusted("PC-1"));
    }

    public async Task DisposeAsync()
    {
        _keys.Dispose();
        await _server.DisposeAsync();
    }

    private HttpClient Http(bool encrypts = true)
    {
        var http = _server.RawHttp(_server.Token);
        if (encrypts)
            http.DefaultRequestHeaders.Add(PairnetsHeaders.Features, PairnetsHeaders.SealedFeature);
        return http;
    }

    private static async Task<string?> Code(HttpResponseMessage resp) =>
        (await resp.Content.ReadFromJsonAsync<ErrorBody>(PairnetsJson.Options))?.Code;

    private async Task SealAsync()
    {
        using var http = Http();
        Assert.Equal(HttpStatusCode.NoContent, (await http.PutAsync("api/keys/recovery",
            new ByteArrayContent(Envelope.SealForRecovery(_ring, RecoveryKey.Create())))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PutAsync($"api/keys/envelopes/{_keys.Id}",
            new ByteArrayContent(Envelope.SealForComputer(_ring, _keys.AgreementPublicKey, _keys)))).StatusCode);
        var sealedResp = await http.PostAsync("api/keys/seal", null);
        Assert.Equal(HttpStatusCode.OK, sealedResp.StatusCode);
        Assert.True((await sealedResp.Content.ReadFromJsonAsync<KeysInfo>(PairnetsJson.Options))!.Sealed);
    }

    /// <summary>What an app does for one upload: id, encrypted bytes, tag and meta.</summary>
    private (string Path, byte[] Blob, string Tag, string Meta) Prepare(string realPath, byte[] content)
    {
        var id = PathId.Of(_ring.IdKey(), realPath);
        var (header, key) = SealedHeader.Create(_ring);
        using var stream = new SealedEncryptStream(new MemoryStream(content), content.Length, header, key);
        var blob = new MemoryStream();
        stream.CopyTo(blob);
        var sha = ContentHash.Of(content);
        var meta = MetaBlob.Seal(_ring, id, new FileMeta(realPath, content.Length, 1_760_000_000_000, sha, header.Hash));
        return (PathId.StoragePath(id), blob.ToArray(), ContentTag.Of(_ring.TagKey(), id, sha), meta);
    }

    private static string Query(string tag, string meta) => $"tag={tag}&meta={Uri.EscapeDataString(meta)}";

    [Fact]
    public async Task APlainNestSaysNothingAndRefusesTagsAndMeta()
    {
        using var http = Http(encrypts: false);
        var hello = await http.GetStringAsync("api/hello");
        Assert.DoesNotContain("sealed", hello, StringComparison.OrdinalIgnoreCase);
        Assert.Null((await http.GetFromJsonAsync<ServerInfo>("api/info", PairnetsJson.Options))!.Sealed);

        var (path, blob, tag, meta) = Prepare("a.txt", "x"u8.ToArray());
        var resp = await http.PutAsync($"api/file?path={path}&base=none&{Query(tag, meta)}", new ByteArrayContent(blob));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // a plain database stays on schema 1, so an older server still opens it
        Assert.Equal(1L, UserVersion());
    }

    [Fact]
    public async Task SealingNeedsTheEnvelopesAndAnEmptyNest()
    {
        using var http = Http();
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync("api/keys/seal", null)).StatusCode);

        // a nest that already has plain files must be moved over first
        using (var plain = Http(encrypts: false))
            Assert.Equal(HttpStatusCode.OK, (await plain.PutAsync("api/file?path=old.txt&base=none", new StringContent("plain"))).StatusCode);
        await http.PutAsync("api/keys/recovery", new ByteArrayContent(Envelope.SealForRecovery(_ring, RecoveryKey.Create())));
        await http.PutAsync($"api/keys/envelopes/{_keys.Id}", new ByteArrayContent(Envelope.SealForComputer(_ring, _keys.AgreementPublicKey, _keys)));
        var resp = await http.PostAsync("api/keys/seal", null);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.False(_server.Store.Sealed);
    }

    [Fact]
    public async Task OldAppsGetUpdateRequiredButCanStillUpdate()
    {
        await SealAsync();
        Assert.Equal(2L, UserVersion());
        using var old = Http(encrypts: false);
        foreach (var url in new[] { "api/manifest", "api/file?path=ab/x", "api/history?path=ab/x", "hub/negotiate?negotiateVersion=1" })
        {
            var resp = url.StartsWith("hub", StringComparison.Ordinal) ? await old.PostAsync(url, null) : await old.GetAsync(url);
            Assert.Equal(HttpStatusCode.UpgradeRequired, resp.StatusCode);
            Assert.Equal(ErrorCodes.UpdateRequired, await Code(resp));
        }
        Assert.Equal(HttpStatusCode.OK, (await old.GetAsync("api/info")).StatusCode);
        Assert.True((await old.GetFromJsonAsync<ServerInfo>("api/info", PairnetsJson.Options))!.Sealed);
        Assert.Contains("\"sealed\":true", await old.GetStringAsync("api/hello"));
        Assert.Equal(HttpStatusCode.OK, (await old.GetAsync("api/devices")).StatusCode);

        using var fresh = Http();
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("api/manifest")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fresh.PostAsync("hub/negotiate?negotiateVersion=1", null)).StatusCode);
    }

    [Fact]
    public async Task StoresOnlyBlobsUnderIdsAndLeaksNoNameOrContent()
    {
        await SealAsync();
        using var http = Http();

        // plain names and missing tags are refused
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsync("api/file?path=a.txt&base=none", new StringContent("x"))).StatusCode);
        var (path, blob, tag, meta) = Prepare("Documents/" + SecretName, SecretContent);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsync($"api/file?path={path}&base=none", new ByteArrayContent(blob))).StatusCode);

        var put = await http.PutAsync($"api/file?path={path}&base=none&{Query(tag, meta)}", new ByteArrayContent(blob));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var entry = Assert.Single((await http.GetFromJsonAsync<List<ManifestEntry>>("api/manifest", PairnetsJson.Options))!);
        Assert.Equal(path, entry.Path);
        Assert.Equal(tag, entry.Tag);
        Assert.Equal(meta, entry.Meta);
        Assert.Equal(ContentHash.Of(blob), entry.Hash);

        // another computer reads the real name from the meta and the content from the blob
        var id = path[3..];
        Assert.Equal("Documents/" + SecretName, MetaBlob.Open(_ring, id, entry.Meta!).Path);
        var download = await http.GetAsync($"api/file?path={path}");
        Assert.Equal(tag, download.Headers.GetValues(PairnetsHeaders.Tag).Single());
        Assert.Equal(meta, download.Headers.GetValues(PairnetsHeaders.Meta).Single());
        var plain = new MemoryStream();
        var header = await SealedFile.DecryptAsync(await download.Content.ReadAsStreamAsync(), plain, _ring);
        Assert.Equal(SecretContent, plain.ToArray());
        Assert.Equal(header.Hash, MetaBlob.Open(_ring, id, entry.Meta!).HeaderHash);

        // nothing readable on the nest's disk or in its log
        foreach (var file in Directory.EnumerateFiles(_server.DataDir, "*", SearchOption.AllDirectories).Where(f => f != _server.Paths.LockFile))
        {
            var bytes = await ReadSharedAsync(file);
            Assert.False(Contains(bytes, Encoding.UTF8.GetBytes("Tax return")), file);
            Assert.False(Contains(bytes, Encoding.UTF8.GetBytes("CONFIDENTIAL-CONTENT")), file);
        }
        Assert.DoesNotContain(_server.Logs.Lines, l => l.Contains("Tax return", StringComparison.Ordinal) || l.Contains("Documents", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheBaseIsTheTag()
    {
        await SealAsync();
        using var http = Http();
        var first = Prepare("notes.txt", "one"u8.ToArray());
        await http.PutAsync($"api/file?path={first.Path}&base=none&{Query(first.Tag, first.Meta)}", new ByteArrayContent(first.Blob));

        var second = Prepare("notes.txt", "two"u8.ToArray());
        var byHash = await http.PutAsync($"api/file?path={second.Path}&base={ContentHash.Of(first.Blob)}&{Query(second.Tag, second.Meta)}",
            new ByteArrayContent(second.Blob));
        Assert.Equal(HttpStatusCode.Conflict, byHash.StatusCode);
        var byTag = await http.PutAsync($"api/file?path={second.Path}&base={first.Tag}&{Query(second.Tag, second.Meta)}",
            new ByteArrayContent(second.Blob));
        Assert.Equal(HttpStatusCode.OK, byTag.StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await http.DeleteAsync($"api/file?path={second.Path}&base={first.Tag}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.DeleteAsync($"api/file?path={second.Path}&base={second.Tag}")).StatusCode);
        var tombstone = Assert.Single((await http.GetFromJsonAsync<List<ManifestEntry>>("api/manifest", PairnetsJson.Options))!);
        Assert.True(tombstone.Deleted);
        Assert.Null(tombstone.Tag);

        // the nest can't make an old version's tag and meta, so the app restores it itself
        var history = await http.GetFromJsonAsync<List<HistoryVersion>>($"api/history?path={second.Path}", PairnetsJson.Options);
        Assert.NotEmpty(history!);
        var restore = await http.PostAsync($"api/history/restore?path={second.Path}&id={history![0].Id}", null);
        Assert.Equal(HttpStatusCode.BadRequest, restore.StatusCode);
    }

    [Fact]
    public async Task UploadsInPiecesCarryTheTagAndMeta()
    {
        await SealAsync();
        using var http = Http();
        var (path, blob, tag, meta) = Prepare("big.bin", new byte[3 * 1024 * 1024 + 7]);
        var start = await http.PostAsync($"api/upload?path={path}&base=none&size={blob.Length}", null);
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        var id = (await start.Content.ReadFromJsonAsync<UploadStatus>(PairnetsJson.Options))!.Id;
        var half = blob.Length / 2;
        await http.PutAsync($"api/upload/{id}?offset=0", new ByteArrayContent(blob, 0, half));
        await http.PutAsync($"api/upload/{id}?offset={half}", new ByteArrayContent(blob, half, blob.Length - half));
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync($"api/upload/{id}/commit?hash={ContentHash.Of(blob)}", null)).StatusCode);

        start = await http.PostAsync($"api/upload?path={path}&base=none&size={blob.Length}", null);
        id = (await start.Content.ReadFromJsonAsync<UploadStatus>(PairnetsJson.Options))!.Id;
        await http.PutAsync($"api/upload/{id}?offset=0", new ByteArrayContent(blob));
        var commit = await http.PostAsync($"api/upload/{id}/commit?hash={ContentHash.Of(blob)}&{Query(tag, meta)}", null);
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
        Assert.Equal(tag, _server.Store.Manifest.Get(path)!.Tag);
    }

    [Fact]
    public async Task EnvelopesAreStoredAsSent()
    {
        using var http = Http();
        var envelope = Envelope.SealForComputer(_ring, _keys.AgreementPublicKey, _keys);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PutAsync("api/keys/envelopes/not-an-id", new ByteArrayContent(envelope))).StatusCode);
        await http.PutAsync($"api/keys/envelopes/{_keys.Id}", new ByteArrayContent(envelope));
        var back = await http.GetByteArrayAsync($"api/keys/envelopes/{_keys.Id}");
        Assert.Equal(RingCheck.Ok, Envelope.OpenForComputer(back, _keys, _ring).Check);
        var info = await http.GetFromJsonAsync<KeysInfo>("api/keys", PairnetsJson.Options);
        Assert.Equal([_keys.Id], info!.Computers);
        Assert.False(info.Recovery);
        await http.DeleteAsync($"api/keys/envelopes/{_keys.Id}");
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"api/keys/envelopes/{_keys.Id}")).StatusCode);
    }

    [Fact]
    public async Task TwoComputersPairThroughTheMailbox()
    {
        await SealAsync();
        using var http = Http();
        using var newKeys = ComputerKeys.Create();
        using var fresh = new PairingNewComputer(newKeys.AsTrusted("PC-2"));

        var started = await http.PostAsJsonAsync("api/keys/requests", new KeyRequestStart("PC-2", fresh.Commitment), PairnetsJson.Options);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var id = (await started.Content.ReadFromJsonAsync<KeyRequestView>(PairnetsJson.Options))!.Id;

        // the old computer sees the request and answers
        var pending = Assert.Single((await http.GetFromJsonAsync<List<KeyRequestView>>("api/keys/requests", PairnetsJson.Options))!);
        Assert.Equal("PC-2", pending.Name);
        using var old = new PairingOldComputer(pending.Commitment);
        await Post(http, id, "offer", old.Offer, HttpStatusCode.OK);
        await Post(http, id, "offer", old.Offer, HttpStatusCode.Conflict); // written once

        var view = await http.GetFromJsonAsync<KeyRequestView>($"api/keys/requests/{id}", PairnetsJson.Options);
        await Post(http, id, "reveal", fresh.Reveal(view!.Offer!), HttpStatusCode.OK);

        view = await http.GetFromJsonAsync<KeyRequestView>($"api/keys/requests/{id}", PairnetsJson.Options);
        old.Accept(view!.Reveal!);
        Assert.Equal(fresh.Code, old.Code);
        await Post(http, id, "ring", old.SealRing(_ring.WithComputer(old.NewComputer!)), HttpStatusCode.OK);

        view = await http.GetFromJsonAsync<KeyRequestView>($"api/keys/requests/{id}", PairnetsJson.Options);
        Assert.Equal(_ring.IdKey(), fresh.OpenRing(view!.Ring!).IdKey());
        Assert.Empty((await http.GetFromJsonAsync<List<KeyRequestView>>("api/keys/requests", PairnetsJson.Options))!);
        Assert.Equal(HttpStatusCode.NoContent, (await http.DeleteAsync($"api/keys/requests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"api/keys/requests/{id}")).StatusCode);
    }

    [Fact]
    public async Task ASealedNestStaysSealedWhenOpenedAgain()
    {
        await SealAsync();
        Assert.True(new ManifestStore(_server.Paths.Database).Sealed);
    }

    private static async Task Post(HttpClient http, string id, string what, byte[] data, HttpStatusCode expected) =>
        Assert.Equal(expected, (await http.PostAsJsonAsync($"api/keys/requests/{id}/{what}", new KeyRequestMessage(data), PairnetsJson.Options)).StatusCode);

    private long UserVersion()
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _server.Paths.Database, Pooling = false }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return (long)cmd.ExecuteScalar()!;
    }

    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new MemoryStream();
        await fs.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
