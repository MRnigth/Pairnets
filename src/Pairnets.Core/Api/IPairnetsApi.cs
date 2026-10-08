namespace Pairnets.Core.Api;

/// <summary>The server operations the sync engine needs.</summary>
public interface IPairnetsApi
{
    /// <summary>Which server this is (its address), so unfinished transfers are only continued with the server that has them.</summary>
    string ServerKey { get; }

    Task<ManifestResponse> GetManifestAsync(long? since, CancellationToken ct);

    /// <summary>
    /// Streams a file into <paramref name="destination"/>, verifying length and hash. With <paramref name="resume"/>
    /// the destination may already hold the first bytes of a version, and only the rest is asked for.
    /// </summary>
    Task<DownloadResult> DownloadAsync(string path, Stream destination, Action<long>? progress, CancellationToken ct, DownloadResume? resume = null);

    /// <summary>
    /// Uploads <paramref name="content"/>; <paramref name="baseHash"/> is the expected server hash or "none".
    /// With <paramref name="resume"/> a big file continues an upload an earlier attempt started (see <see cref="UploadResume"/>).
    /// </summary>
    Task<(ApiResult Result, string SentHash, long SentBytes)> UploadAsync(
        string path, string baseHash, long mtimeMs, Stream content, Action<long>? progress, CancellationToken ct, UploadResume? resume = null);

    /// <summary>Gives up an unfinished upload for good (the file changed or is no longer sent). Best effort; never throws.</summary>
    Task AbandonUploadAsync(string uploadId);

    Task<ApiResult> DeleteAsync(string path, string baseHash, CancellationToken ct);
}
