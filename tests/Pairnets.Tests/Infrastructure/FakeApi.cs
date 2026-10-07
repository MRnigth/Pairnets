using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Hashing;

namespace Pairnets.Tests.Infrastructure;

/// <summary>In-memory stand-in for the server, used by fast engine unit tests.</summary>
public sealed class FakeApi : IPairnetsApi
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (ManifestEntry Entry, byte[] Data)> _files = new(StringComparer.Ordinal);
    private long _version;

    public string ServerId { get; set; } = "fake-server";

    public bool Offline { get; set; }

    public long Version
    {
        get { lock (_gate) return _version; }
        set { lock (_gate) _version = value; }
    }

    public void Put(string path, string content, long mtimeMs = 1_700_000_000_000)
    {
        lock (_gate)
        {
            var data = System.Text.Encoding.UTF8.GetBytes(content);
            _files[path] = (new ManifestEntry(path, ContentHash.Of(data), data.Length, mtimeMs, false, ++_version), data);
        }
    }

    public void Delete(string path)
    {
        lock (_gate)
            _files[path] = (new ManifestEntry(path, null, 0, 0, true, ++_version), []);
    }

    public string? Content(string path)
    {
        lock (_gate)
            return _files.TryGetValue(path, out var f) && !f.Entry.Deleted ? System.Text.Encoding.UTF8.GetString(f.Data) : null;
    }

    public IReadOnlyList<string> LivePaths()
    {
        lock (_gate)
            return _files.Values.Where(f => !f.Entry.Deleted).Select(f => f.Entry.Path).Order(StringComparer.Ordinal).ToList();
    }

    private void ThrowIfOffline()
    {
        if (Offline)
            throw new PairnetsNetworkException("fake offline");
    }

    public Task<ManifestResponse> GetManifestAsync(long? since, CancellationToken ct)
    {
        ThrowIfOffline();
        lock (_gate)
        {
            var entries = _files.Values.Select(f => f.Entry).Where(e => since is null || e.Version > since).OrderBy(e => e.Version).ToList();
            return Task.FromResult(new ManifestResponse(entries, ServerId, _version));
        }
    }

    public async Task<DownloadResult> DownloadAsync(string path, Stream destination, Action<long>? progress, CancellationToken ct)
    {
        ThrowIfOffline();
        byte[] data;
        ManifestEntry entry;
        lock (_gate)
        {
            if (!_files.TryGetValue(path, out var f) || f.Entry.Deleted)
                return new DownloadResult(false, string.Empty, 0, 0);
            (entry, data) = f;
        }
        await destination.WriteAsync(data, ct);
        return new DownloadResult(true, entry.Hash!, data.Length, entry.ModifiedMs);
    }

    public async Task<(ApiResult Result, string SentHash, long SentBytes)> UploadAsync(string path, string baseHash, long mtimeMs, Stream content, Action<long>? progress, CancellationToken ct)
    {
        ThrowIfOffline();
        var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        var data = ms.ToArray();
        var hash = ContentHash.Of(data);
        lock (_gate)
        {
            _files.TryGetValue(path, out var current);
            var currentHash = current.Entry is { Deleted: false } ? current.Entry.Hash : null;
            if (currentHash == hash)
                return (ApiResult.Ok(current.Entry!), hash, data.Length);
            var expected = baseHash == ContentHash.NoneBase ? null : baseHash;
            if (currentHash != expected)
                return (new ApiResult(ApiOutcome.Conflict, null), string.Empty, data.Length);
            var entry = new ManifestEntry(path, hash, data.Length, mtimeMs, false, ++_version);
            _files[path] = (entry, data);
            return (ApiResult.Ok(entry), hash, data.Length);
        }
    }

    public Task<ApiResult> DeleteAsync(string path, string baseHash, CancellationToken ct)
    {
        ThrowIfOffline();
        lock (_gate)
        {
            if (!_files.TryGetValue(path, out var current) || current.Entry.Deleted)
                return Task.FromResult(new ApiResult(ApiOutcome.Ok, current.Entry));
            if (current.Entry.Hash != baseHash)
                return Task.FromResult(new ApiResult(ApiOutcome.Conflict, null));
            var entry = new ManifestEntry(path, null, 0, 0, true, ++_version);
            _files[path] = (entry, []);
            return Task.FromResult(ApiResult.Ok(entry));
        }
    }
}
