namespace Pairnets.Core.Api;

/// <summary>The server operations the sync engine needs.</summary>
public interface IPairnetsApi
{
    Task<ManifestResponse> GetManifestAsync(long? since, CancellationToken ct);

    /// <summary>Streams a file into <paramref name="destination"/>, verifying length and hash.</summary>
    Task<DownloadResult> DownloadAsync(string path, Stream destination, Action<long>? progress, CancellationToken ct);

    /// <summary>Uploads <paramref name="content"/>; <paramref name="baseHash"/> is the expected server hash or "none".</summary>
    Task<(ApiResult Result, string SentHash, long SentBytes)> UploadAsync(
        string path, string baseHash, long mtimeMs, Stream content, Action<long>? progress, CancellationToken ct);

    Task<ApiResult> DeleteAsync(string path, string baseHash, CancellationToken ct);
}
