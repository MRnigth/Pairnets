using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Tether.Core.Hashing;

namespace Tether.Core.Api;

/// <summary>
/// HTTP client for the Tether server. Transfers are streamed (never buffered in memory) and guarded
/// by a stall watchdog instead of a total timeout, so multi-gigabyte files work on slow links.
/// The token is only ever placed in a request header; it is never logged.
/// </summary>
public sealed class TetherApiClient : ITetherApi, IDisposable
{
    private const int CopyBufferSize = 256 * 1024;

    private readonly HttpClient _http;
    private readonly TimeSpan _stallTimeout;
    private readonly TimeSpan _metadataTimeout;

    public TetherApiClient(Uri serverUrl, string token, string deviceId, HttpMessageHandler? handler = null,
        TimeSpan? stallTimeout = null, TimeSpan? metadataTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        BaseAddress = NormalizeBase(serverUrl);
        _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(60);
        _metadataTimeout = metadataTimeout ?? TimeSpan.FromMinutes(5);
        handler ??= new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = false,
        };
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = BaseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.Add(TetherHeaders.Token, token);
        _http.DefaultRequestHeaders.Add(TetherHeaders.DeviceId, Uri.EscapeDataString(deviceId));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Tether", TetherInfo.ApiVersion.ToString(CultureInfo.InvariantCulture)));
    }

    public Uri BaseAddress { get; }

    /// <summary>Ensures the base URL ends with '/', so relative API paths resolve under it.</summary>
    public static Uri NormalizeBase(Uri url)
    {
        var s = url.ToString();
        return s.EndsWith('/') ? url : new Uri(s + "/");
    }

    /// <summary>Validates a user-entered server URL (http or https, absolute).</summary>
    public static bool TryParseServerUrl(string? text, out Uri? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed))
            return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            return false;
        if (!string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
            return false;
        url = NormalizeBase(parsed);
        return true;
    }

    // ------------------------------------------------------------------ health / info

    public async Task<bool> HealthAsync(CancellationToken ct)
    {
        using var timeout = Linked(ct, TimeSpan.FromSeconds(15));
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "api/health"), HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }

    public async Task<ServerInfo> GetInfoAsync(CancellationToken ct)
    {
        using var timeout = Linked(ct, TimeSpan.FromSeconds(30));
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "api/info"), HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        await ThrowForStatusAsync(resp).ConfigureAwait(false);
        return await ReadJsonAsync<ServerInfo>(resp, timeout, ct).ConfigureAwait(false);
    }

    public enum ServerUpdateRequest { Requested, UpdaterMissing, TooSoon, NotSupported }

    /// <summary>Asks the server to update itself to the newest release (POST /api/update).</summary>
    public async Task<ServerUpdateRequest> RequestServerUpdateAsync(CancellationToken ct)
    {
        using var timeout = Linked(ct, TimeSpan.FromSeconds(30));
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, "api/update"), HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        switch ((int)resp.StatusCode)
        {
            case 202:
                return ServerUpdateRequest.Requested;
            case 409:
                return ServerUpdateRequest.UpdaterMissing;
            case 429:
                return ServerUpdateRequest.TooSoon;
            case 404 or 405:
                return ServerUpdateRequest.NotSupported; // a server from before self-update
            default:
                await ThrowForStatusAsync(resp).ConfigureAwait(false);
                return ServerUpdateRequest.NotSupported;
        }
    }

    /// <summary>Checks reachability (health, no auth) and then the token (info). Never throws.</summary>
    public static async Task<ConnectionTestResult> TestConnectionAsync(string? serverUrl, string? token, string deviceId,
        HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        if (!TryParseServerUrl(serverUrl, out var url) || url is null)
            return new(ConnectionTestStatus.InvalidUrl, "The server URL must look like http://<tailscale-ip>:5075/ (http or https).");
        if (string.IsNullOrWhiteSpace(token))
            return new(ConnectionTestStatus.BadToken, "Enter the token printed by install.sh on the server.");

        using var client = new TetherApiClient(url, token.Trim(), deviceId, handler);
        try
        {
            if (!await client.HealthAsync(ct).ConfigureAwait(false))
                return new(ConnectionTestStatus.ServerError, "The server answered, but /api/health did not return OK.");
        }
        catch (TetherNetworkException ex)
        {
            return new(ConnectionTestStatus.Unreachable, $"Cannot reach the server: {ex.Message}. Is Tailscale connected and the service running?");
        }

        try
        {
            var info = await client.GetInfoAsync(ct).ConfigureAwait(false);
            if (info.ApiVersion != TetherInfo.ApiVersion)
                return new(ConnectionTestStatus.ServerError, $"Server API version {info.ApiVersion} does not match this client ({TetherInfo.ApiVersion}). Update both to the same release.", info);
            return new(ConnectionTestStatus.Ok, "Connected. Server and token are OK.", info);
        }
        catch (TetherAuthException)
        {
            return new(ConnectionTestStatus.BadToken, "The server is reachable but rejected the token.");
        }
        catch (TetherNetworkException ex)
        {
            return new(ConnectionTestStatus.Unreachable, $"Health is OK but the API failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is TetherProtocolException or JsonException)
        {
            return new(ConnectionTestStatus.ServerError, $"Unexpected answer from the server: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ manifest

    public async Task<ManifestResponse> GetManifestAsync(long? since, CancellationToken ct)
    {
        var uri = since is null ? "api/manifest" : $"api/manifest?since={since.Value.ToString(CultureInfo.InvariantCulture)}";
        using var timeout = Linked(ct, _metadataTimeout);
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), HttpCompletionOption.ResponseHeadersRead, timeout, ct).ConfigureAwait(false);
        await ThrowForStatusAsync(resp).ConfigureAwait(false);
        var serverId = HeaderValue(resp, TetherHeaders.ServerId) ?? throw new TetherProtocolException("Manifest response has no server id.");
        if (!long.TryParse(HeaderValue(resp, TetherHeaders.Version), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            throw new TetherProtocolException("Manifest response has no version.");
        var entries = await ReadJsonAsync<List<ManifestEntry>>(resp, timeout, ct).ConfigureAwait(false);
        return new ManifestResponse(entries, serverId, version);
    }

    // ------------------------------------------------------------------ download

    /// <summary>Speed limit shared by all uploads of this client (no limit by default).</summary>
    public Throttle UploadLimit { get; } = new();

    /// <summary>Speed limit shared by all downloads of this client (no limit by default).</summary>
    public Throttle DownloadLimit { get; } = new();

    public async Task<DownloadResult> DownloadAsync(string path, Stream destination, Action<long>? progress, CancellationToken ct)
    {
        using var stall = Linked(ct, _stallTimeout);
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "api/file?path=" + Uri.EscapeDataString(path)),
            HttpCompletionOption.ResponseHeadersRead, stall, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new DownloadResult(false, string.Empty, 0, 0);
        await ThrowForStatusAsync(resp).ConfigureAwait(false);

        var expectedHash = HeaderValue(resp, TetherHeaders.Hash);
        if (!ContentHash.IsValid(expectedHash))
            throw new TetherProtocolException("Download response has no valid content hash.");
        _ = long.TryParse(HeaderValue(resp, "X-Tether-Modified-Ms"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var modifiedMs);
        var expectedLength = resp.Content.Headers.ContentLength;

        using var hashing = new HashingStream(destination, leaveOpen: true, progress);
        try
        {
            await using var body = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            var buffer = new byte[CopyBufferSize];
            while (true)
            {
                int n;
                try
                {
                    n = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    throw new TetherNetworkException($"Download of '{path}' was interrupted: {ex.Message}", ex);
                }
                if (n == 0)
                    break;
                await DownloadLimit.WaitAsync(n, ct).ConfigureAwait(false);
                stall.CancelAfter(_stallTimeout);
                // Local write errors (disk full, ...) propagate as IOException: a per-file failure.
                await hashing.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TetherNetworkException($"Download of '{path}' stalled.", ex);
        }

        var hash = hashing.GetHash();
        if (expectedLength is not null && hashing.BytesTransferred != expectedLength)
            throw new TetherNetworkException($"Download of '{path}' was truncated ({hashing.BytesTransferred} of {expectedLength} bytes).");
        if (!string.Equals(hash, expectedHash, StringComparison.Ordinal))
            throw new TetherNetworkException($"Download of '{path}' does not match the server hash.");
        return new DownloadResult(true, hash, hashing.BytesTransferred, modifiedMs);
    }

    // ------------------------------------------------------------------ upload

    public async Task<(ApiResult Result, string SentHash, long SentBytes)> UploadAsync(
        string path, string baseHash, long mtimeMs, Stream content, Action<long>? progress, CancellationToken ct)
    {
        using var stall = Linked(ct, _stallTimeout);
        var hashing = new HashingStream(content, leaveOpen: true, progress);
        var uri = $"api/file?path={Uri.EscapeDataString(path)}&base={Uri.EscapeDataString(baseHash)}&mtime={mtimeMs.ToString(CultureInfo.InvariantCulture)}";
        using var req = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new StreamingUploadContent(hashing, stall, _stallTimeout, UploadLimit),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // The server answers 400/401/409 before we send a possibly huge body.
        req.Headers.ExpectContinue = true;

        using var resp = await SendAsync(req, HttpCompletionOption.ResponseContentRead, stall, ct).ConfigureAwait(false);
        var result = await ReadChangeResultAsync(resp, stall, ct).ConfigureAwait(false);
        var sent = result.Outcome == ApiOutcome.Ok ? hashing.GetHash() : string.Empty;
        return (result, sent, hashing.BytesTransferred);
    }

    // ------------------------------------------------------------------ delete

    public async Task<ApiResult> DeleteAsync(string path, string baseHash, CancellationToken ct)
    {
        using var timeout = Linked(ct, _metadataTimeout);
        var uri = $"api/file?path={Uri.EscapeDataString(path)}&base={Uri.EscapeDataString(baseHash)}";
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Delete, uri), HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        return await ReadChangeResultAsync(resp, timeout, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ history

    public async Task<IReadOnlyList<HistoryVersion>> GetHistoryAsync(string path, CancellationToken ct)
    {
        using var timeout = Linked(ct, _metadataTimeout);
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "api/history?path=" + Uri.EscapeDataString(path)),
            HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        await ThrowForStatusAsync(resp).ConfigureAwait(false);
        return await ReadJsonAsync<List<HistoryVersion>>(resp, timeout, ct).ConfigureAwait(false);
    }

    public async Task<ApiResult> RestoreAsync(string path, string id, CancellationToken ct)
    {
        using var timeout = Linked(ct, _metadataTimeout);
        var uri = $"api/history/restore?path={Uri.EscapeDataString(path)}&id={Uri.EscapeDataString(id)}";
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, uri), HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        return await ReadChangeResultAsync(resp, timeout, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ plumbing

    private async Task<ApiResult> ReadChangeResultAsync(HttpResponseMessage resp, CancellationTokenSource timeout, CancellationToken ct)
    {
        switch (resp.StatusCode)
        {
            case HttpStatusCode.OK:
                return ApiResult.Ok(await ReadJsonAsync<ManifestEntry>(resp, timeout, ct).ConfigureAwait(false));
            case HttpStatusCode.Conflict:
            case HttpStatusCode.BadRequest:
            case HttpStatusCode.NotFound:
                var error = await TryReadErrorAsync(resp, ct).ConfigureAwait(false);
                return error?.Code switch
                {
                    ErrorCodes.Conflict => new ApiResult(ApiOutcome.Conflict, null, error.Message),
                    ErrorCodes.CaseCollision => new ApiResult(ApiOutcome.CaseCollision, null, error.Message),
                    ErrorCodes.InvalidName => new ApiResult(ApiOutcome.InvalidName, null, error.Message),
                    ErrorCodes.NotFound => new ApiResult(ApiOutcome.NotFound, null, error.Message),
                    _ => throw new TetherProtocolException($"Server answered {(int)resp.StatusCode}: {error?.Code ?? "no error code"} {error?.Message}"),
                };
            default:
                await ThrowForStatusAsync(resp).ConfigureAwait(false);
                throw new TetherProtocolException($"Unexpected status {(int)resp.StatusCode}.");
        }
    }

    private static async Task ThrowForStatusAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode)
            return;
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new TetherAuthException("The server rejected the token (401).");
        if ((int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.RequestTimeout)
            throw new TetherNetworkException($"Server error {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        var error = await TryReadErrorAsync(resp, CancellationToken.None).ConfigureAwait(false);
        throw new TetherProtocolException($"Server answered {(int)resp.StatusCode}: {error?.Code} {error?.Message}".TrimEnd());
    }

    private static async Task<ErrorBody?> TryReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadFromJsonAsync<ErrorBody>(TetherJson.Options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or HttpRequestException)
        {
            return null;
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage resp, CancellationTokenSource timeout, CancellationToken ct)
    {
        try
        {
            var value = await resp.Content.ReadFromJsonAsync<T>(TetherJson.Options, timeout.Token).ConfigureAwait(false);
            return value ?? throw new TetherProtocolException("Empty JSON response.");
        }
        catch (JsonException ex)
        {
            throw new TetherProtocolException("Invalid JSON from server: " + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new TetherNetworkException("Connection lost while reading the response: " + ex.Message, ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TetherNetworkException("Timed out reading the response.", ex);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, HttpCompletionOption completion,
        CancellationTokenSource timeout, CancellationToken callerCt)
    {
        try
        {
            return await _http.SendAsync(req, completion, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (FindLocalReadFailure(ex) is { } local)
        {
            throw local;
        }
        catch (HttpRequestException ex)
        {
            throw new TetherNetworkException(Describe(ex), ex);
        }
        catch (IOException ex)
        {
            throw new TetherNetworkException(ex.Message, ex);
        }
        catch (OperationCanceledException ex) when (!callerCt.IsCancellationRequested)
        {
            throw new TetherNetworkException("The request timed out or stalled.", ex);
        }
        finally
        {
            if (completion == HttpCompletionOption.ResponseContentRead)
                req.Dispose();
        }
    }

    private static string Describe(HttpRequestException ex)
    {
        var inner = ex.InnerException?.Message;
        return inner is null ? ex.Message : $"{ex.Message} ({inner})";
    }

    private static LocalFileReadException? FindLocalReadFailure(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is LocalFileReadException local)
                return local;
        }
        return null;
    }

    private static string? HeaderValue(HttpResponseMessage resp, string name)
    {
        if (resp.Headers.TryGetValues(name, out var values))
            return values.FirstOrDefault();
        if (resp.Content.Headers.TryGetValues(name, out values))
            return values.FirstOrDefault();
        return null;
    }

    private static CancellationTokenSource Linked(CancellationToken ct, TimeSpan after)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(after);
        return cts;
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Request body that streams from the local file, resets the stall watchdog on every chunk,
    /// and tags local read failures so they are not mistaken for network failures.
    /// </summary>
    private sealed class StreamingUploadContent(Stream source, CancellationTokenSource stall, TimeSpan stallTimeout, Throttle limit) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var buffer = new byte[CopyBufferSize];
            while (true)
            {
                int n;
                try
                {
                    n = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new LocalFileReadException("Reading the local file failed: " + ex.Message, ex);
                }
                if (n == 0)
                    break;
                await limit.WaitAsync(n, cancellationToken).ConfigureAwait(false);
                stall.CancelAfter(stallTimeout);
                await stream.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                stall.CancelAfter(stallTimeout);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
