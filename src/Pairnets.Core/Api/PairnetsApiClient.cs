using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Pairnets.Core.Hashing;
using Pairnets.Core.Legacy;

namespace Pairnets.Core.Api;

/// <summary>
/// HTTP client for the Pairnets server. Transfers are streamed (never buffered in memory) and guarded
/// by a stall watchdog instead of a total timeout, so multi-gigabyte files work on slow links.
/// The token is only ever placed in a request header; it is never logged.
/// </summary>
public sealed class PairnetsApiClient : IPairnetsApi, IDisposable
{
    private const int CopyBufferSize = 256 * 1024;

    /// <summary>
    /// The largest piece of an upload, and above this size a file always goes in pieces. Cloudflare's
    /// free plan refuses any request body over 100 MB, so a Cloudflare Tunnel needs pieces well below
    /// that. Smaller pieces are used on slow connections (see <see cref="PieceSizer"/>).
    /// </summary>
    public const long DefaultPieceSize = 50L * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _token;
    private readonly TimeSpan _stallTimeout;
    private readonly TimeSpan _metadataTimeout;
    private volatile bool _piecesUnsupported;

    /// <param name="serviceUrl">The Pairnets service whose relay addresses this client recognises (tests; by default sync.pairnets.app).</param>
    public PairnetsApiClient(Uri serverUrl, string token, string deviceId, HttpMessageHandler? handler = null,
        TimeSpan? stallTimeout = null, TimeSpan? metadataTimeout = null, long? pieceSize = null, long? minPieceSize = null,
        Uri? serviceUrl = null)
    {
        ArgumentNullException.ThrowIfNull(serverUrl);
        BaseAddress = NormalizeBase(serverUrl);
        IsRelay = Client.Relay.IsRelayAddress(BaseAddress, serviceUrl);
        _token = token;
        _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(60);
        _metadataTimeout = metadataTimeout ?? TimeSpan.FromMinutes(5);
        Pieces = new PieceSizer(minPieceSize ?? PieceSizer.DefaultMin, pieceSize is > 0 ? pieceSize.Value : DefaultPieceSize);
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
        _http.DefaultRequestHeaders.Add(PairnetsHeaders.DeviceId, Uri.EscapeDataString(deviceId));
        _http.DefaultRequestHeaders.Add(PairnetsHeaders.Client, PairnetsInfo.ClientDescription);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Pairnets", PairnetsInfo.ApiVersion.ToString(CultureInfo.InvariantCulture)));
    }

    public Uri BaseAddress { get; }

    /// <summary>
    /// True when the server is reached through the Pairnets service (<c>https://sync.pairnets.app/n/&lt;nest id&gt;/</c>).
    /// Every request still goes to a path relative to <see cref="BaseAddress"/>; only the words for Cloudflare's own
    /// error pages differ (there is no tunnel of the person's own to check).
    /// </summary>
    public bool IsRelay { get; }

    /// <summary>How big the pieces of uploads are, learned from how fast the last ones went.</summary>
    internal PieceSizer Pieces { get; }

    /// <summary>
    /// How long to wait before trying again after a piece (or the question how much the server has) failed, one
    /// entry per try in a row; about a minute in all. When they are used up the pass ends, and the next pass
    /// continues the upload from the server's count (nothing is thrown away on a broken connection).
    /// </summary>
    internal IReadOnlyList<TimeSpan> PieceRetryDelays { get; set; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];

    /// <summary>
    /// How long an upload waits while the server is still taking in an earlier piece of it (it gives up on a
    /// stalled one after 60 s) or checking a part it kept over a restart, asking every <see cref="BusyPollInterval"/>.
    /// </summary>
    internal TimeSpan BusyTimeout { get; set; } = TimeSpan.FromMinutes(3);

    internal TimeSpan BusyPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ensures the base URL ends with '/', so relative API paths resolve under it, path included: a relay address
    /// (https://sync.pairnets.app/n/&lt;nest id&gt;/) keeps its /n/&lt;nest id&gt;/ and "api/info" becomes /n/&lt;nest id&gt;/api/info.
    /// </summary>
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

    public async Task<bool> HealthAsync(CancellationToken ct) => (await CheckHealthAsync(ct).ConfigureAwait(false)).Ok;

    /// <summary>The answer of /api/health (sent without the token): whether it is OK, and whether Cloudflare relayed it.</summary>
    internal sealed record HealthCheck(bool Ok, bool ViaCloudflare, string? Problem);

    internal async Task<HealthCheck> CheckHealthAsync(CancellationToken ct)
    {
        using var timeout = Linked(ct, TimeSpan.FromSeconds(15));
        try
        {
            using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, HealthPath), HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
            return new HealthCheck(resp.IsSuccessStatusCode, IsFromCloudflare(resp), DescribeCloudflareError(resp, IsRelay));
        }
        catch (Exception ex) when (ex is PairnetsNetworkException { Code: not null } or PairnetsAuthException { Code: ServiceErrors.NestUnknown })
        {
            return new HealthCheck(false, false, ex.Message); // the Pairnets service answered for the server: "not connected", "not linked"
        }
    }

    public async Task<ServerInfo> GetInfoAsync(CancellationToken ct)
    {
        using var timeout = Linked(ct, TimeSpan.FromSeconds(30));
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "api/info"), HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        await ThrowForStatusAsync(resp).ConfigureAwait(false);
        var info = await ReadJsonAsync<ServerInfo>(resp, timeout, ct).ConfigureAwait(false);
        PiecesInFlight = info.PiecesInFlight ?? 1;
        return info;
    }

    private volatile int _piecesInFlight = 1;

    /// <summary>
    /// How many pieces of one upload are sent at once: 2 once the server said it takes that (/api/info), else 1. The
    /// second piece goes out as soon as the first one's bytes are sent; the server keeps it waiting until the first has
    /// arrived. Through Cloudflare, which takes in a whole piece before it passes it on, this keeps both lines busy.
    /// </summary>
    internal int PiecesInFlight
    {
        get => _piecesInFlight;
        set => _piecesInFlight = Math.Clamp(value, 1, 2);
    }

    /// <summary>The computers that use this server, or null for a server from before the Devices list.</summary>
    public async Task<IReadOnlyList<DeviceInfo>?> GetDevicesAsync(CancellationToken ct)
    {
        using var timeout = Linked(ct, TimeSpan.FromSeconds(30));
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "api/devices"), HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        if ((int)resp.StatusCode is 404 or 405)
            return null;
        await ThrowForStatusAsync(resp).ConfigureAwait(false);
        return await ReadJsonAsync<List<DeviceInfo>>(resp, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>The server's updater report (Debug mode), or null for a server from before it existed.</summary>
    public async Task<UpdaterDiagnostics?> GetUpdateDiagnosticsAsync(CancellationToken ct)
    {
        using var timeout = Linked(ct, TimeSpan.FromSeconds(30));
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "api/update/diagnostics"), HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        if ((int)resp.StatusCode is 404 or 405)
            return null;
        await ThrowForStatusAsync(resp).ConfigureAwait(false);
        return await ReadJsonAsync<UpdaterDiagnostics>(resp, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>"Is this a Pairnets server?" (no sign-in needed), or null for a server from before per-computer keys.</summary>
    public async Task<ServerHello?> GetHelloAsync(CancellationToken ct)
    {
        using var timeout = Linked(ct, TimeSpan.FromSeconds(15));
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "api/hello"), HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        if ((int)resp.StatusCode is 401 or 404 or 405)
            return null; // older servers have no such endpoint (and ask for the token first)
        await ThrowForStatusAsync(resp).ConfigureAwait(false);
        return await ReadJsonAsync<ServerHello>(resp, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>Who the server thinks this computer is, or null for a server from before per-computer keys.</summary>
    public async Task<DeviceMe?> GetMeAsync(CancellationToken ct)
    {
        using var timeout = Linked(ct, TimeSpan.FromSeconds(30));
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, "api/me"), HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        if ((int)resp.StatusCode is 404 or 405)
            return null;
        await ThrowForStatusAsync(resp).ConfigureAwait(false);
        return await ReadJsonAsync<DeviceMe>(resp, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>Removes a computer from the nest (this one too: "Sign out of this computer"). False when it was already gone.</summary>
    public async Task<bool> RemoveDeviceAsync(string id, CancellationToken ct)
    {
        using var timeout = Linked(ct, TimeSpan.FromSeconds(30));
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Delete, "api/devices/" + Uri.EscapeDataString(id)), HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return false;
        await ThrowForStatusAsync(resp).ConfigureAwait(false);
        return true;
    }

    /// <summary>Renames this computer; returns the name the server settled on (it may add " (2)").</summary>
    public async Task<DeviceMe> RenameThisDeviceAsync(string name, CancellationToken ct)
    {
        using var timeout = Linked(ct, TimeSpan.FromSeconds(30));
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Patch, "api/devices/me") { Content = JsonContent.Create(new DeviceNameRequest(name), options: PairnetsJson.Options) },
            HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        await ThrowForStatusAsync(resp).ConfigureAwait(false);
        return await ReadJsonAsync<DeviceMe>(resp, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>Asks the nest to let this computer join (no key needed). Null for a server without sign-in.</summary>
    public async Task<PairStartResponse?> StartPairingAsync(PairStartRequest request, CancellationToken ct)
    {
        using var timeout = Linked(ct, TimeSpan.FromSeconds(30));
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, "api/pair/start") { Content = JsonContent.Create(request, options: PairnetsJson.Options) },
            HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        if ((int)resp.StatusCode is 401 or 404 or 405)
            return null;
        if ((int)resp.StatusCode is 400 or 409 or 429)
        {
            // The nest's own words ("too many waiting", "no website yet") are meant for people.
            var refusal = await TryReadErrorAsync(resp, ct).ConfigureAwait(false);
            throw new PairnetsProtocolException(refusal?.Message ?? (resp.StatusCode == HttpStatusCode.TooManyRequests
                ? "Too many sign-ins are waiting on your nest. Approve them or let them expire, then try again."
                : $"Your nest refused the request ({(int)resp.StatusCode})."));
        }
        await ThrowForStatusAsync(resp).ConfigureAwait(false);
        return await ReadJsonAsync<PairStartResponse>(resp, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>Asks whether the request was approved; on approval the answer carries this computer's own key (once).</summary>
    public async Task<PairPollResponse> PollPairingAsync(string pollToken, CancellationToken ct)
    {
        using var timeout = Linked(ct, TimeSpan.FromSeconds(30));
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, "api/pair/poll") { Content = JsonContent.Create(new PairPollRequest(pollToken), options: PairnetsJson.Options) },
            HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new PairPollResponse(PairPollResponse.Expired);
        await ThrowForStatusAsync(resp).ConfigureAwait(false);
        return await ReadJsonAsync<PairPollResponse>(resp, timeout, ct).ConfigureAwait(false);
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
            return new(ConnectionTestStatus.InvalidUrl, "The server URL must look like https://sync.example.com/.");
        if (string.IsNullOrWhiteSpace(token))
            return new(ConnectionTestStatus.BadToken, "Enter the token printed by install.sh on the server.");

        using var client = new PairnetsApiClient(url, token.Trim(), deviceId, handler);
        try
        {
            var health = await client.CheckHealthAsync(ct).ConfigureAwait(false);
            // The health check carries no token, so nothing secret went out before this check.
            if (health.ViaCloudflare && url.Scheme == Uri.UriSchemeHttp)
                return new(ConnectionTestStatus.InvalidUrl, "This server is reached through Cloudflare. Use https:// in the server URL, so the token is never sent unencrypted.");
            if (health.Problem is not null)
                return new(ConnectionTestStatus.Unreachable, health.Problem);
            if (!health.Ok)
                return new(ConnectionTestStatus.ServerError, "The server answered, but /api/health did not return OK.");
        }
        catch (PairnetsNetworkException ex)
        {
            return new(ConnectionTestStatus.Unreachable, client.IsRelay
                ? $"Cannot reach Pairnets: {ex.Message}. Check that this computer is online."
                : $"Cannot reach the server: {ex.Message}. Is the server running, and its Cloudflare Tunnel connected?");
        }

        try
        {
            var info = await client.GetInfoAsync(ct).ConfigureAwait(false);
            if (info.ApiVersion != PairnetsInfo.ApiVersion)
                return new(ConnectionTestStatus.ServerError, $"Server API version {info.ApiVersion} does not match this client ({PairnetsInfo.ApiVersion}). Update both to the same release.", info);
            return new(ConnectionTestStatus.Ok, "Connected. Server and token are OK.", info);
        }
        catch (PairnetsAuthException ex) when (ex.NeedsSignIn)
        {
            return new(ConnectionTestStatus.BadToken, ex.Message);
        }
        catch (PairnetsAuthException)
        {
            return new(ConnectionTestStatus.BadToken, "The server is reachable but rejected the token.");
        }
        catch (PairnetsNetworkException ex)
        {
            return new(ConnectionTestStatus.Unreachable, $"Health is OK but the API failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is PairnetsProtocolException or JsonException)
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
        var serverId = HeaderValue(resp, PairnetsHeaders.ServerId, TetherNames.ServerIdHeader) ?? throw new PairnetsProtocolException("Manifest response has no server id.");
        if (!long.TryParse(HeaderValue(resp, PairnetsHeaders.Version, TetherNames.VersionHeader), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            throw new PairnetsProtocolException("Manifest response has no version.");
        var entries = await ReadJsonAsync<List<ManifestEntry>>(resp, timeout, ct).ConfigureAwait(false);
        return new ManifestResponse(entries, serverId, version);
    }

    // ------------------------------------------------------------------ download

    /// <summary>Speed limit shared by all uploads of this client (no limit by default).</summary>
    public Throttle UploadLimit { get; } = new();

    /// <summary>Speed limit shared by all downloads of this client (no limit by default).</summary>
    public Throttle DownloadLimit { get; } = new();

    public string ServerKey => BaseAddress.ToString();

    /// <summary>
    /// Downloads a file, verifying its length and content hash. With <paramref name="resume"/> holding the first
    /// bytes of a version, only the rest is asked for (a Range request that the server answers only for that same
    /// version, If-Range); the bytes already there are read again so the whole file is still verified.
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(string path, Stream destination, Action<long>? progress, CancellationToken ct, DownloadResume? resume = null)
    {
        var offset = resume is { Offset: > 0, Hash: not null } ? resume.Offset : 0;
        if (resume is { Offset: > 0 } && !(destination.CanSeek && destination.CanRead))
            throw new ArgumentException("Continuing a download needs a readable, seekable destination.", nameof(destination));
        using var stall = Linked(ct, _stallTimeout);
        var req = new HttpRequestMessage(HttpMethod.Get, "api/file?path=" + Uri.EscapeDataString(path));
        if (offset > 0)
        {
            req.Headers.Range = new RangeHeaderValue(offset, null);
            req.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue("\"" + resume!.Hash + "\""));
        }
        using var resp = await SendAsync(req, HttpCompletionOption.ResponseHeadersRead, stall, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new DownloadResult(false, string.Empty, 0, 0);
        await ThrowForStatusAsync(resp).ConfigureAwait(false);

        var expectedHash = HeaderValue(resp, PairnetsHeaders.Hash, TetherNames.HashHeader);
        if (!ContentHash.IsValid(expectedHash))
            throw new PairnetsProtocolException("Download response has no valid content hash.");
        _ = long.TryParse(HeaderValue(resp, PairnetsHeaders.ModifiedMs, TetherNames.ModifiedMsHeader), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var modifiedMs);
        var expectedLength = resp.Content.Headers.ContentLength;
        long start = 0;
        if (resp.StatusCode == HttpStatusCode.PartialContent)
        {
            // Only ever the rest of exactly the version whose start is already here.
            if (offset == 0 || resp.Content.Headers.ContentRange is not { From: { } from, Length: { } total } || from != offset
                || !string.Equals(expectedHash, resume!.Hash, StringComparison.Ordinal))
                throw new PairnetsProtocolException($"The server sent an unexpected part of '{path}'.");
            start = offset;
            expectedLength = total;
        }
        else if (resp.StatusCode != HttpStatusCode.OK)
        {
            throw new PairnetsProtocolException($"Unexpected status {(int)resp.StatusCode} for a download.");
        }
        if (resume is not null)
        {
            resume.ServedHash = expectedHash;
            resume.ServedSize = expectedLength;
            resume.ContinuedFrom = start;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[CopyBufferSize];
        if (start > 0)
        {
            await HashExistingAsync(destination, start, hash, buffer, ct).ConfigureAwait(false);
            progress?.Invoke(start);
        }
        else if (offset > 0)
        {
            // The version changed since the partial file was started: the server sent the whole new one.
            destination.SetLength(0);
            destination.Position = 0;
        }

        var received = start;
        try
        {
            await using var body = await resp.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            while (true)
            {
                int n;
                try
                {
                    n = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    throw new PairnetsNetworkException($"Download of '{path}' was interrupted: {ex.Message}", ex);
                }
                if (n == 0)
                    break;
                await DownloadLimit.WaitAsync(n, ct).ConfigureAwait(false);
                stall.CancelAfter(_stallTimeout);
                // Local write errors (disk full, ...) propagate as IOException: a per-file failure.
                await destination.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                hash.AppendData(buffer, 0, n);
                received += n;
                progress?.Invoke(received);
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PairnetsNetworkException($"Download of '{path}' stalled.", ex);
        }

        var actual = ContentHash.ToHex(hash.GetHashAndReset());
        if (expectedLength is { } length && received != length)
            throw new PairnetsNetworkException($"Download of '{path}' was truncated ({received} of {length} bytes).");
        if (!string.Equals(actual, expectedHash, StringComparison.Ordinal))
            throw new DownloadMismatchException(start > 0
                ? $"'{path}' does not match the server hash after continuing its download; it will be downloaded again."
                : $"Download of '{path}' does not match the server hash.");
        return new DownloadResult(true, actual, received, modifiedMs);
    }

    /// <summary>Hashes the first <paramref name="length"/> bytes the destination already holds and leaves it positioned after them.</summary>
    private static async Task HashExistingAsync(Stream destination, long length, IncrementalHash hash, byte[] buffer, CancellationToken ct)
    {
        destination.Position = 0;
        var left = length;
        while (left > 0)
        {
            var n = await destination.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct).ConfigureAwait(false);
            if (n == 0)
                throw new IOException("The partial download got shorter.");
            hash.AppendData(buffer, 0, n);
            left -= n;
        }
        destination.Position = length;
    }

    // ------------------------------------------------------------------ upload

    public async Task<(ApiResult Result, string SentHash, long SentBytes)> UploadAsync(
        string path, string baseHash, long mtimeMs, Stream content, Action<long>? progress, CancellationToken ct, UploadResume? resume = null)
    {
        // An upload an earlier attempt started goes on in pieces, whatever the speed says now.
        if (!_piecesUnsupported && content.CanSeek && (resume?.Id is not null || Pieces.ShouldSplit(content.Length - content.Position)))
        {
            var inPieces = await UploadInPiecesAsync(path, baseHash, mtimeMs, content, progress, resume ?? new UploadResume(), ct).ConfigureAwait(false);
            if (inPieces is { } done)
                return done;
            // A server from before uploads in pieces: send the file in one request, as it always did.
        }
        if (resume?.Id is { } unused)
        {
            // Sent in one request after all: nothing of it can be continued.
            await AbandonUploadAsync(unused).ConfigureAwait(false);
            resume.Forget();
        }

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

    /// <summary>
    /// Uploads a big file in pieces (POST /api/upload, PUT /api/upload/{id}?offset=, POST .../commit), so it fits
    /// through proxies that cap one request (Cloudflare: 100 MB), and a broken connection costs only the piece in
    /// flight. The network never makes it throw the session away: when the connection stays away, or the pass is
    /// cancelled (Pause), the session stays on the server and <paramref name="resume"/> keeps its id, so a later
    /// attempt continues from the server's count. Null when the server is too old to take uploads in pieces.
    /// </summary>
    private async Task<(ApiResult Result, string SentHash, long SentBytes)?> UploadInPiecesAsync(
        string path, string baseHash, long mtimeMs, Stream content, Action<long>? progress, UploadResume resume, CancellationToken ct)
    {
        using var reader = new PieceReader(content, progress);
        long offset = 0;
        if (resume.Id is { } earlier)
        {
            UploadStatus? status;
            try
            {
                status = await UploadStatusAsync(earlier, ct).ConfigureAwait(false);
            }
            catch (PairnetsProtocolException)
            {
                // An answer this upload cannot go on from: give it up (the next pass starts it again).
                await AbandonUploadAsync(earlier).ConfigureAwait(false);
                resume.Forget();
                throw;
            }
            if (status is { Received: var have } && have <= reader.Size)
            {
                // The server kept what an earlier attempt sent. Those bytes are read again here (not sent), so the hash
                // covers the file as it is now: if that is not what was sent then, the server refuses the commit.
                await reader.SkipAsync(have, ct).ConfigureAwait(false);
                offset = have;
                resume.ResumedFrom = have;
            }
            else
            {
                if (status is not null)
                    await AbandonUploadAsync(earlier).ConfigureAwait(false); // more than this file has: not this upload
                resume.Forget();
            }
        }

        var startedAgain = false;
        while (true)
        {
            if (resume.Id is null)
            {
                var (id, rejected) = await StartUploadAsync(path, baseHash, mtimeMs, reader.Size, ct).ConfigureAwait(false);
                if (id is null)
                    return rejected is null ? null : (rejected, string.Empty, 0); // conflict, collision, bad name
                resume.Started(id);
                offset = 0;
            }
            try
            {
                var (result, hash) = await FinishUploadAsync(path, resume, reader, offset, ct).ConfigureAwait(false);
                return (result, result.Outcome == ApiOutcome.Ok ? hash : string.Empty, reader.Size);
            }
            catch (UploadGoneException) when (!startedAgain)
            {
                // The server dropped it (an hour without data, or a restart it could not keep it over): send it again.
                resume.Forget();
                startedAgain = true;
            }
            catch (UploadGoneException)
            {
                resume.Forget();
                throw new PairnetsNetworkException($"The server dropped the upload of {path} twice; it will be sent again.");
            }
            catch (PairnetsProtocolException) when (resume.Id is { } broken)
            {
                // An answer this upload cannot go on from: give it up (the next pass starts it again).
                await AbandonUploadAsync(broken).ConfigureAwait(false);
                resume.Forget();
                throw;
            }
        }
    }

    /// <summary>POST /api/upload: the new session's id, or the server's refusal (conflict, ...), or neither from a server without uploads in pieces.</summary>
    private async Task<(string? Id, ApiResult? Rejected)> StartUploadAsync(string path, string baseHash, long mtimeMs, long size, CancellationToken ct)
    {
        using var timeout = Linked(ct, _metadataTimeout);
        var start = $"api/upload?path={Uri.EscapeDataString(path)}&base={Uri.EscapeDataString(baseHash)}" +
            $"&size={size.ToString(CultureInfo.InvariantCulture)}&mtime={mtimeMs.ToString(CultureInfo.InvariantCulture)}";
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, start), HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        if ((int)resp.StatusCode is 404 or 405 && !IsJson(resp))
        {
            _piecesUnsupported = true;
            return (null, null);
        }
        if (resp.StatusCode != HttpStatusCode.Created)
            return (null, await ReadChangeResultAsync(resp, timeout, ct).ConfigureAwait(false));
        return ((await ReadJsonAsync<UploadStatus>(resp, timeout, ct).ConfigureAwait(false)).Id, null);
    }

    /// <summary>
    /// Sends what the server does not have yet, then commits. A broken connection is retried a few times (see
    /// <see cref="PieceRetryDelays"/>) and a server still busy with an earlier piece is waited for (see
    /// <see cref="BusyTimeout"/>); after that the pass ends and the session stays for the next one. Once this
    /// returns, the session is finished on the server (stored, or refused like a PUT would be).
    /// </summary>
    private async Task<(ApiResult Result, string Hash)> FinishUploadAsync(string path, UploadResume resume, PieceReader reader, long offset, CancellationToken ct)
    {
        var id = resume.Id!;
        var patience = new Patience(this);
        var ask = false;
        while (true)
        {
            var sending = false;
            try
            {
                if (ask)
                {
                    var status = await UploadStatusAsync(id, ct).ConfigureAwait(false) ?? throw new UploadGoneException();
                    if (status.Busy)
                        throw new UploadBusyException();
                    offset = status.Received;
                    ask = false;
                }
                if (offset < reader.Size)
                {
                    sending = true;
                    offset = PiecesInFlight > 1
                        ? await SendPiecesTwoAtOnceAsync(id, reader, offset, patience.Progressed, ct).ConfigureAwait(false)
                        : await SendPieceAsync(id, reader, offset, Math.Min(Pieces.Next, reader.Size - offset), ct).ConfigureAwait(false);
                    patience.Progressed();
                    continue;
                }

                if (offset > reader.Size)
                    throw new PairnetsProtocolException($"The server has more of {path} than the file holds.");
                await reader.SkipAsync(reader.Size, ct).ConfigureAwait(false); // the bytes an earlier attempt sent, if not read yet
                var hash = reader.GetHash();
                using var timeout = Linked(ct, _metadataTimeout);
                using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"api/upload/{id}/commit?hash={hash}"),
                    HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode && await TryReadErrorAsync(resp, ct).ConfigureAwait(false) is { } error)
                {
                    switch (error.Code)
                    {
                        case ErrorCodes.UploadMismatch:
                            resume.Forget(); // the server already dropped it
                            throw new PairnetsProtocolException($"{path} changed while it was being uploaded; it will be sent again.");
                        case ErrorCodes.UploadBusy or ErrorCodes.UploadOffset:
                            // A piece is still arriving, or bytes are missing: ask how much is there and go on from it.
                            throw new UploadBusyException();
                        case ErrorCodes.NotFound when resp.StatusCode == HttpStatusCode.NotFound:
                            throw new UploadGoneException();
                    }
                }
                var result = await ReadChangeResultAsync(resp, timeout, ct).ConfigureAwait(false);
                resume.Forget(); // stored, or refused (conflict, ...): either way the server is done with it
                return (result, hash);
            }
            catch (UploadBusyException)
            {
                await patience.BusyAsync(ct).ConfigureAwait(false); // throws when cancelled (Pause) or waited too long
                ask = true;
            }
            catch (PairnetsSlowDownException ex) when (!ct.IsCancellationRequested)
            {
                // Too many requests: the next one waits by itself (SendAsync). Not the piece's fault, so its size stays.
                patience.SlowedDown(ex);
                ask = true;
            }
            catch (PairnetsNetworkException ex) when (!ct.IsCancellationRequested)
            {
                if (sending)
                    Pieces.Failed();
                await patience.FailedAsync(ex, ct).ConfigureAwait(false);
                ask = true;
            }
        }
    }

    /// <summary>
    /// Sends the rest of the upload from <paramref name="offset"/> with two pieces on the way at once: the next piece
    /// starts as soon as every byte of the one before was handed to the connection, and the server keeps it waiting
    /// until that one has arrived. Returns how many bytes the server has: the whole file, or less when a piece arrived
    /// short (the caller asks and goes on). When a piece fails, the other one is stopped and waited for before the
    /// exception goes on, so nothing reads the file meanwhile.
    /// </summary>
    private async Task<long> SendPiecesTwoAtOnceAsync(string id, PieceReader reader, long offset, Action progressed, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var flying = new Queue<(Task<long> Received, long End)>();
        var next = offset;
        var received = offset;
        try
        {
            while (true)
            {
                // A new piece only while none has failed: a failed one may have stopped part-way through the file.
                if (next < reader.Size && flying.Count < 2 && flying.All(f => !f.Received.IsCompleted || f.Received.IsCompletedSuccessfully))
                {
                    var length = Math.Min(Pieces.Next, reader.Size - next);
                    var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var piece = SendPieceAsync(id, reader, next, length, stop.Token, waitTurn: flying.Count > 0, bodySent: () => sent.TrySetResult());
                    flying.Enqueue((piece, next + length));
                    next += length;
                    // The reader is this piece's until all of it is out (or it ended): only then may the next one start.
                    await Task.WhenAny(sent.Task, piece).ConfigureAwait(false);
                    if (piece.IsCompleted && !piece.IsCompletedSuccessfully)
                        await piece.ConfigureAwait(false); // throws: the others are stopped below
                    continue;
                }
                if (flying.Count == 0)
                    return received;
                var (oldest, end) = flying.Peek();
                received = await oldest.ConfigureAwait(false);
                flying.Dequeue();
                progressed();
                if (received != end)
                {
                    // It arrived short; the piece after it is refused (wrong offset). Wait for it, then go on from what arrived.
                    await DrainAsync(flying, stop).ConfigureAwait(false);
                    return received;
                }
            }
        }
        catch
        {
            await DrainAsync(flying, stop).ConfigureAwait(false);
            throw;
        }

        static async Task DrainAsync(Queue<(Task<long> Received, long End)> flying, CancellationTokenSource stop)
        {
            if (flying.Count == 0)
                return;
            await stop.CancelAsync().ConfigureAwait(false);
            foreach (var (piece, _) in flying)
            {
                try
                {
                    await piece.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Stopped on purpose; the server's count says what arrived.
                }
            }
            flying.Clear();
        }
    }

    /// <summary>
    /// Sends one piece; returns how many bytes the server has afterwards. With <paramref name="waitTurn"/> the piece
    /// before it may still be arriving, and the server keeps this one waiting until it has. <paramref name="bodySent"/>
    /// is told when every byte of the piece was handed to the connection, so the next piece may read the file.
    /// </summary>
    private async Task<long> SendPieceAsync(string id, PieceReader reader, long offset, long length, CancellationToken ct,
        bool waitTurn = false, Action? bodySent = null)
    {
        // The server can have more than this attempt read: the rest of a piece an earlier attempt sent.
        if (offset > reader.HashedUpTo && offset <= reader.Size)
            await reader.SkipAsync(offset, ct).ConfigureAwait(false);
        reader.StartPiece(offset, length);
        using var stall = Linked(ct, _stallTimeout);
        var uri = $"api/upload/{id}?offset={offset.ToString(CultureInfo.InvariantCulture)}" + (waitTurn ? "&turn=wait" : string.Empty);
        using var req = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new StreamingUploadContent(reader, stall, _stallTimeout, UploadLimit, length, bodySent),
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var started = Stopwatch.GetTimestamp();
        using var resp = await SendAsync(req, HttpCompletionOption.ResponseContentRead, stall, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.OK)
        {
            var received = (await ReadJsonAsync<UploadStatus>(resp, stall, ct).ConfigureAwait(false)).Received;
            Pieces.Succeeded(received - offset, Stopwatch.GetElapsedTime(started));
            return received;
        }
        if (resp.StatusCode == HttpStatusCode.Conflict)
            throw new UploadBusyException(); // an earlier piece is still arriving, or the count moved on: ask and go on
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new UploadGoneException();
        await ThrowForStatusAsync(resp).ConfigureAwait(false);
        throw new PairnetsProtocolException($"Unexpected status {(int)resp.StatusCode} for a piece of an upload.");
    }

    /// <summary>How many bytes of the upload the server has (where the next piece starts), or null when it has no such upload.</summary>
    private async Task<UploadStatus?> UploadStatusAsync(string id, CancellationToken ct)
    {
        using var timeout = Linked(ct, TimeSpan.FromSeconds(30));
        using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"api/upload/{id}"), HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        await ThrowForStatusAsync(resp).ConfigureAwait(false);
        return await ReadJsonAsync<UploadStatus>(resp, timeout, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells the server to drop an unfinished upload for good: only when it will not be continued (the file changed,
    /// was deleted or is no longer sent), never because of the network. Best effort: it expires on its own anyway.
    /// </summary>
    public async Task AbandonUploadAsync(string uploadId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"api/upload/{Uri.EscapeDataString(uploadId)}"), HttpCompletionOption.ResponseContentRead, timeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or LocalFileReadException)
        {
            // The server drops it after an hour without data.
        }
    }

    /// <summary>The server no longer has the upload (404): it was finished, dropped after an hour without data, or lost in a restart.</summary>
    private sealed class UploadGoneException() : Exception("The server no longer has this upload.");

    /// <summary>The server is still taking in an earlier piece of the upload, or its count moved on (409): ask and go on.</summary>
    private sealed class UploadBusyException() : Exception("The server is still busy with an earlier piece of this upload.");

    /// <summary>How long one upload keeps trying within one attempt (see <see cref="PieceRetryDelays"/> and <see cref="BusyTimeout"/>).</summary>
    private sealed class Patience(PairnetsApiClient owner)
    {
        private int _failures;
        private int _slowDowns;
        private long? _busySince;

        /// <summary>A 429: goes on (the wait happens before the next request), or rethrows after many in a row.</summary>
        public void SlowedDown(PairnetsSlowDownException ex)
        {
            if (++_slowDowns > MaxSlowDownsInARow)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex);
        }

        /// <summary>A request failed: waits before the next try, or rethrows once the tries are used up.</summary>
        public async Task FailedAsync(PairnetsNetworkException ex, CancellationToken ct)
        {
            if (_failures >= owner.PieceRetryDelays.Count)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex);
            await Task.Delay(owner.PieceRetryDelays[_failures++], ct).ConfigureAwait(false);
        }

        /// <summary>The server is busy with this upload: waits a moment, or gives up for this attempt after <see cref="BusyTimeout"/>.</summary>
        public async Task BusyAsync(CancellationToken ct)
        {
            _busySince ??= Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(_busySince.Value) >= owner.BusyTimeout)
                throw new PairnetsNetworkException("The server is still busy with an earlier piece of this upload; it goes on at the next try.");
            await Task.Delay(owner.BusyPollInterval, ct).ConfigureAwait(false);
        }

        /// <summary>A piece arrived: the counts start again.</summary>
        public void Progressed()
        {
            _failures = 0;
            _slowDowns = 0;
            _busySince = null;
        }
    }

    /// <summary>How many 429s in a row one upload in pieces (or the engine, for one file) takes before it gives up for this pass.</summary>
    internal const int MaxSlowDownsInARow = 30;

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

    /// <summary>How often to ask again while the server is still restoring a big version (it answers 202 within a minute).</summary>
    internal TimeSpan RestorePollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Restores a history version. A big one can take the server longer than a proxy waits for an answer, so it
    /// answers 202 meanwhile and this asks again with the same ticket (which joins that restore) until it is done.
    /// </summary>
    public async Task<ApiResult> RestoreAsync(string path, string id, CancellationToken ct)
    {
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var uri = $"api/history/restore?path={Uri.EscapeDataString(path)}&id={Uri.EscapeDataString(id)}&ticket={ticket}";
        var giveUp = Stopwatch.GetTimestamp();
        while (true)
        {
            using var timeout = Linked(ct, _metadataTimeout);
            using var resp = await SendAsync(new HttpRequestMessage(HttpMethod.Post, uri), HttpCompletionOption.ResponseContentRead, timeout, ct).ConfigureAwait(false);
            if (resp.StatusCode != HttpStatusCode.Accepted)
                return await ReadChangeResultAsync(resp, timeout, ct).ConfigureAwait(false);
            if (Stopwatch.GetElapsedTime(giveUp) > TimeSpan.FromHours(1))
                throw new PairnetsNetworkException("The server is still restoring that version; look at History again later.");
            await Task.Delay(RestorePollInterval, ct).ConfigureAwait(false);
        }
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
                    _ => throw new PairnetsProtocolException($"Server answered {(int)resp.StatusCode}: {error?.Code ?? "no error code"} {error?.Message}"),
                };
            default:
                await ThrowForStatusAsync(resp).ConfigureAwait(false);
                throw new PairnetsProtocolException($"Unexpected status {(int)resp.StatusCode}.");
        }
    }

    private async Task ThrowForStatusAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode)
            return;
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            var reason = await TryReadErrorAsync(resp, CancellationToken.None).ConfigureAwait(false);
            throw new PairnetsAuthException(reason?.Code is ErrorCodes.DeviceRemoved or ErrorCodes.SharedTokenOff && reason.Message is { } m
                ? m
                : "The server rejected the token (401).", reason?.Code);
        }
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new PairnetsSlowDownException(RetryAfterOf(resp));
        if (DescribeCloudflareError(resp, IsRelay) is { } cloudflare)
            throw new PairnetsNetworkException(cloudflare);
        if (resp.StatusCode == HttpStatusCode.RequestEntityTooLarge)
            throw new PairnetsProtocolException("The server, or a proxy in front of it such as Cloudflare, refused a request that was too large (413).");
        if ((int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.RequestTimeout)
            throw new PairnetsNetworkException($"Server error {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        var error = await TryReadErrorAsync(resp, CancellationToken.None).ConfigureAwait(false);
        throw new PairnetsProtocolException($"Server answered {(int)resp.StatusCode}: {error?.Code} {error?.Message}".TrimEnd());
    }

    /// <summary>
    /// A plain-words explanation when Cloudflare itself (not the Pairnets server behind it) answered with
    /// an error page: the tunnel is down, or a bot check blocked the app. Null otherwise. With <paramref name="relay"/>
    /// the page came from in front of the Pairnets service, so the advice about the person's own tunnel does not apply.
    /// </summary>
    internal static string? DescribeCloudflareError(HttpResponseMessage resp, bool relay = false)
    {
        if (resp.IsSuccessStatusCode || !IsFromCloudflare(resp) || IsJson(resp))
            return null; // Pairnets's own errors are JSON, also when Cloudflare relays them
        var status = (int)resp.StatusCode;
        if (relay)
        {
            if (status == 403 && resp.Headers.Contains("cf-mitigated"))
                return "Pairnets turned this app away with a browser check. Try again in a few minutes.";
            if (status is 502 or 503 or 504 or 530 or (>= 520 and <= 527))
                return $"Pairnets is having trouble right now (error {status}). Syncing goes on by itself when it is back.";
            return null;
        }
        if (status == 403 && resp.Headers.Contains("cf-mitigated"))
            return "Cloudflare blocked Pairnets with a browser check. In the Cloudflare dashboard turn off Bot Fight Mode for this domain (Security > Bots), or add a rule that skips it for the Pairnets address.";
        if (status == 524)
            return "Cloudflare gave up waiting for your Pairnets server (error 524). Check that the server is running: sudo systemctl status pairnets-server";
        if (status is 502 or 503 or 504 or 530 or (>= 520 and <= 527))
            return $"Cloudflare cannot reach your Pairnets server (error {status}). On the server, check that the tunnel runs (sudo systemctl status pairnets-tunnel) and that its public hostname points at http://localhost:5075.";
        return null;
    }

    private static bool IsFromCloudflare(HttpResponseMessage resp) =>
        resp.Headers.Contains("CF-RAY")
        || resp.Headers.Server.Any(p => string.Equals(p.Product?.Name, "cloudflare", StringComparison.OrdinalIgnoreCase));

    private static bool IsJson(HttpResponseMessage resp) =>
        resp.Content.Headers.ContentType?.MediaType is { } type && type.EndsWith("json", StringComparison.OrdinalIgnoreCase);

    private static async Task<ErrorBody?> TryReadErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadFromJsonAsync<ErrorBody>(PairnetsJson.Options, ct).ConfigureAwait(false);
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
            var value = await resp.Content.ReadFromJsonAsync<T>(PairnetsJson.Options, timeout.Token).ConfigureAwait(false);
            return value ?? throw new PairnetsProtocolException("Empty JSON response.");
        }
        catch (JsonException ex)
        {
            throw new PairnetsProtocolException("Invalid JSON from server: " + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new PairnetsNetworkException("Connection lost while reading the response: " + ex.Message, ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new PairnetsNetworkException("Timed out reading the response.", ex);
        }
    }

    private const string HealthPath = "api/health";

    /// <summary>The longest a 429 makes this client wait, whatever Retry-After says.</summary>
    internal static readonly TimeSpan MaxSlowDown = TimeSpan.FromMinutes(2);

    /// <summary>How long to wait after a 429 without a usable Retry-After.</summary>
    internal static readonly TimeSpan DefaultSlowDown = TimeSpan.FromSeconds(10);

    /// <summary><see cref="Environment.TickCount64"/> until which no request is sent (after a 429), or 0.</summary>
    private long _slowUntilMs;

    /// <summary>
    /// Raised when a 429 makes every request wait (the length of the wait). The limit counts all requests from this
    /// address, so all transfers wait together rather than each running into it again.
    /// </summary>
    public event Action<TimeSpan>? SlowingDown;

    private void SlowDown(TimeSpan wait)
    {
        var until = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        long seen;
        do
        {
            seen = Volatile.Read(ref _slowUntilMs);
            if (seen >= until)
                return; // another transfer already set the same or a later time
        }
        while (Interlocked.CompareExchange(ref _slowUntilMs, until, seen) != seen);
        SlowingDown?.Invoke(wait);
    }

    /// <summary>Retry-After of a 429 (seconds or a date), within 1 s and <see cref="MaxSlowDown"/>.</summary>
    internal static TimeSpan RetryAfterOf(HttpResponseMessage resp)
    {
        var after = resp.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - DateTimeOffset.UtcNow,
            _ => DefaultSlowDown,
        };
        return after < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : after > MaxSlowDown ? MaxSlowDown : after;
    }

    /// <summary>
    /// Holds a request back while a 429 asked this client to wait. The request's own timeout does not run meanwhile:
    /// it starts again, at its full length, when the wait is over.
    /// </summary>
    private async Task WaitWhileSlowedDownAsync(CancellationTokenSource timeout, CancellationToken callerCt)
    {
        var wait = Volatile.Read(ref _slowUntilMs) - Environment.TickCount64;
        if (wait <= 0)
            return;
        timeout.CancelAfter(Timeout.InfiniteTimeSpan);
        await Task.Delay(TimeSpan.FromMilliseconds(wait), callerCt).ConfigureAwait(false);
        if (TimeoutSpans.TryGetValue(timeout, out var span))
            timeout.CancelAfter((TimeSpan)span);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, HttpCompletionOption completion,
        CancellationTokenSource timeout, CancellationToken callerCt)
    {
        // Every request carries the token except the health check, which needs none (so a URL that
        // would expose it, plain http through Cloudflare, can be caught before it is ever sent).
        if (req.RequestUri?.OriginalString != HealthPath)
            req.Headers.TryAddWithoutValidation(PairnetsHeaders.Token, _token);
        try
        {
            await WaitWhileSlowedDownAsync(timeout, callerCt).ConfigureAwait(false);
            var resp = await _http.SendAsync(req, completion, timeout.Token).ConfigureAwait(false);
            // The relay's limit says how long to wait; a nest's own 429s (sign-in, update) do not, and mean something else.
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && resp.Headers.RetryAfter is not null)
                SlowDown(RetryAfterOf(resp));
            if (await ServiceErrorAsync(resp).ConfigureAwait(false) is { } serviceError)
            {
                resp.Dispose();
                throw serviceError;
            }
            return resp;
        }
        catch (Exception ex) when (FindLocalReadFailure(ex) is { } local)
        {
            throw local;
        }
        catch (HttpRequestException ex)
        {
            throw new PairnetsNetworkException(Describe(ex), ex);
        }
        catch (IOException ex)
        {
            throw new PairnetsNetworkException(ex.Message, ex);
        }
        catch (OperationCanceledException ex) when (!callerCt.IsCancellationRequested)
        {
            throw new PairnetsNetworkException("The request timed out or stalled.", ex);
        }
        finally
        {
            if (completion == HttpCompletionOption.ResponseContentRead)
                req.Dispose();
        }
    }

    /// <summary>
    /// The Pairnets service's own answer for a server it cannot pass the request to ("not connected", "not linked any
    /// more"), as the exception to throw; null for anything else, the server's own answers included. Checked on every
    /// answer before anything reads a 404 as "a server too old for this" or "no such file".
    /// </summary>
    private static async Task<Exception?> ServiceErrorAsync(HttpResponseMessage resp)
    {
        if ((int)resp.StatusCode is not (404 or 502 or 503) || !IsJson(resp) || resp.Content.Headers.ContentLength > ServiceErrorMaxBytes)
            return null;
        try
        {
            // Kept in memory and read as bytes: a stream handed out here would be the one every later reader gets, closed.
            // This way whoever reads this answer next (the nest's own error) still finds the whole body.
            await resp.Content.LoadIntoBufferAsync(ServiceErrorMaxBytes).ConfigureAwait(false);
            var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return ServiceErrors.ToException(JsonSerializer.Deserialize<ServiceErrorBody>(bytes, PairnetsJson.Options)?.Error);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or HttpRequestException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A service error is a short JSON line; anything bigger is not one.</summary>
    private const int ServiceErrorMaxBytes = 16 * 1024;

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

    /// <summary>A response header, or its old Tether name from a server that is not updated yet.</summary>
    private static string? HeaderValue(HttpResponseMessage resp, string name, string legacyName) =>
        HeaderValue(resp, name) ?? HeaderValue(resp, legacyName);

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
        TimeoutSpans.AddOrUpdate(cts, after);
        return cts;
    }

    /// <summary>Each request timeout's length, so a wait for a 429 can start it again afterwards (see <see cref="WaitWhileSlowedDownAsync"/>).</summary>
    private static readonly ConditionalWeakTable<CancellationTokenSource, object> TimeoutSpans = new();

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Request body that streams from the local file, resets the stall watchdog on every chunk,
    /// and tags local read failures so they are not mistaken for network failures.
    /// </summary>
    /// <param name="sent">Told once every byte of the body was handed to the connection (the source is not read again).</param>
    private sealed class StreamingUploadContent(Stream source, CancellationTokenSource stall, TimeSpan stallTimeout, Throttle limit, long? length = null,
        Action? sent = null) : HttpContent
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
            sent?.Invoke();
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long computed)
        {
            computed = length ?? 0;
            return length is not null;
        }
    }
}
