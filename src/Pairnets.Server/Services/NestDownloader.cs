using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Pairnets.Core;
using Pairnets.Core.Hashing;
using Pairnets.Core.Paths;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;

namespace Pairnets.Server.Services;

/// <summary>
/// Downloads the links the owner gives the nest (1.0.61.8, docs/DECISIONS.md "The nest downloads a link for you"), so the
/// owner's computers may be off meanwhile:
/// <list type="bullet">
/// <item><see cref="NestDownloadRules.AtOnce"/> run at once, the rest wait; <see cref="NestDownloadRules.MaxKept"/> are listed at most.</item>
/// <item>Only the web, never the nest's own network: <see cref="SafeHttp"/>. Redirects are followed by hand, at most
/// <see cref="MaxRedirects"/>, each through the same check.</item>
/// <item>The half-done file is tmp/dl-&lt;id&gt;.part (<see cref="SyncStore"/> keeps those over a restart) and the list is
/// downloads.json in the data folder, so a restart carries on where it stopped (Range + If-Range; from the start when the
/// website can't resume). A dropped connection is tried again <see cref="Tries"/> times, waiting longer each time.</item>
/// <item>The disk keeps <see cref="NestDownloadRules.KeepFree"/> free: a file announced too big is refused before it
/// starts, one of unknown size stops at that line.</item>
/// <item>Finished, the file goes into the nest like any computer's new file (history, the push channel, every app),
/// under a name not taken yet ("name (2).iso"). On an encrypted nest the nest can't make a file the apps can open, so it
/// keeps it in held/&lt;id&gt;/ outside the encrypted files instead, not end-to-end encrypted (<see cref="NestDownload.Plain"/>).</item>
/// <item>The log names only the website, never the link: links often carry private tokens.</item>
/// </list>
/// </summary>
public sealed class NestDownloader : BackgroundService
{
    public const int MaxRedirects = 10;
    public const int Tries = 5;
    public const string PartPrefix = "dl-";
    public const string PartSuffix = ".part";

    /// <summary>The device name the apps see on the "Changed" message of a finished download.</summary>
    public const string ChangedBy = "Nest download";

    private readonly SyncStore _store;
    private readonly IHubContext<SyncHub> _hub;
    private readonly FriendConnections _friends;
    private readonly ILogger<NestDownloader> _log;
    private readonly TimeProvider _clock;
    private readonly string _listFile;
    private readonly object _gate = new();
    private readonly List<Job> _jobs = [];
    private readonly Dictionary<string, Running> _running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _kick = new(0, 1);
    private readonly SemaphoreSlim _commit = new(1, 1);
    private HttpClient? _http;
    private bool _loaded;

    public NestDownloader(SyncStore store, IHubContext<SyncHub> hub, FriendConnections friends, ILogger<NestDownloader> log, TimeProvider? clock = null)
    {
        _store = store;
        _hub = hub;
        _friends = friends;
        _log = log;
        _clock = clock ?? TimeProvider.System;
        _listFile = Path.Combine(store.Paths.DataDir, "downloads.json");
    }

    /// <summary>
    /// Tests only: ports on this computer (127.0.0.1) that pass <see cref="SafeHttp"/>, for a local test website. Kept over
    /// a nest restart in the test, so a download that carries on after it reaches the same website. Empty in a real nest.
    /// </summary>
    internal static readonly ConcurrentDictionary<int, bool> TestPorts = new();

    private static bool TestAllows(IPAddress address, int port) => !TestPorts.IsEmpty && IPAddress.IsLoopback(address) && TestPorts.ContainsKey(port);

    /// <summary>Free space on the nest's disk (tests make it small).</summary>
    public Func<long?>? FreeSpace { get; set; }

    /// <summary>The wait before the first retry; it doubles each time.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A download that receives nothing for this long counts as a dropped connection.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Where held downloads of an encrypted nest are kept.</summary>
    public string HeldDir => Path.Combine(_store.Paths.DataDir, "held");

    /// <summary>The half-done file of a download.</summary>
    public string PartPath(string id) => Path.Combine(_store.Paths.Tmp, PartPrefix + id + PartSuffix);

    /// <summary>Whether a tmp/ file name is a download's half-done file (kept over a restart for 7 days).</summary>
    public static bool IsPartFile(string name) =>
        name.StartsWith(PartPrefix, StringComparison.Ordinal) && name.EndsWith(PartSuffix, StringComparison.Ordinal);

    /// <summary>One download as the nest keeps it; the link itself never leaves the nest.</summary>
    public sealed class Job
    {
        public string Id { get; set; } = "";
        public string Url { get; set; } = "";
        public string Name { get; set; } = "";
        public string Folder { get; set; } = "";
        public string State { get; set; } = NestDownload.Queued;
        public long Bytes { get; set; }
        public long? Total { get; set; }
        public DateTimeOffset Added { get; set; }
        public DateTimeOffset? Finished { get; set; }
        public string? Path { get; set; }
        public string? Error { get; set; }
        public bool Plain { get; set; }
        public int Failures { get; set; }
        public DateTimeOffset? NextTry { get; set; }

        /// <summary>The website's ETag or Last-Modified of the half-done file, for If-Range.</summary>
        public string? Validator { get; set; }

        /// <summary>The link the website sent the nest to last (after redirects), for a resume.</summary>
        public string? FinalUrl { get; set; }

        public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : "";
    }

    private sealed class Running(CancellationTokenSource cancel, long startBytes)
    {
        public CancellationTokenSource Cancel { get; } = cancel;
        public long StartBytes { get; } = startBytes;
        public double BytesPerSecond { get; set; }
        public bool Forget { get; set; }
        public Task Work { get; set; } = Task.CompletedTask;
    }

    // ------------------------------------------------------------------ what the endpoints call

    /// <summary>The list as the apps see it, newest first.</summary>
    public NestDownloadList List()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var list = _jobs.OrderByDescending(j => j.Added).Select(j => new NestDownload(j.Id, j.Host, j.Name, j.Folder, j.State, j.Bytes, j.Total,
                _running.TryGetValue(j.Id, out var run) ? run.BytesPerSecond : 0, j.Added, j.Finished, j.Path, j.Error, j.Plain)).ToList();
            return new NestDownloadList(list, NestDownloadRules.AtOnce, _store.Sealed);
        }
    }

    /// <summary>Adds a download; returns it, or the error code of <see cref="NestDownloadErrors"/> it was refused with.</summary>
    public (NestDownload? Download, string? Error) Add(string? url, string? folder)
    {
        if (NestDownloadRules.WebLink(url) is not { } uri)
            return (null, NestDownloadErrors.NotWeb);
        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && SafeHttp.IsBlocked(ip) && !TestAllows(ip, uri.Port)
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return (null, NestDownloadErrors.Blocked);
        var cleanFolder = CleanFolder(folder);
        if (cleanFolder is null || !FolderTakesFiles(cleanFolder))
            return (null, NestDownloadErrors.BadFolder);
        Job job;
        lock (_gate)
        {
            EnsureLoaded();
            Tidy();
            if (_jobs.Count >= NestDownloadRules.MaxKept)
            {
                // A finished or stopped one makes room; a busy one never does.
                var oldest = _jobs.Where(j => j.State is NestDownload.Done or NestDownload.Failed).OrderBy(j => j.Finished ?? j.Added).FirstOrDefault();
                if (oldest is null)
                    return (null, NestDownloadErrors.Full);
                Drop(oldest);
            }
            job = new Job
            {
                Id = Guid.NewGuid().ToString("N")[..16],
                Url = uri.AbsoluteUri,
                Name = NameFromUrl(uri),
                Folder = cleanFolder,
                Added = _clock.GetUtcNow(),
            };
            _jobs.Add(job);
            Save();
        }
        _log.LogInformation("Download {Id} added from {Host}", job.Id, job.Host);
        Kick();
        return (List().Downloads.First(d => d.Id == job.Id), null);
    }

    /// <summary>Stops a download and deletes its half-done file, or takes a finished or stopped one off the list. False when unknown.</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_jobs.FirstOrDefault(j => j.Id == id) is not { } job)
                return false;
            Drop(job);
            Save();
        }
        _log.LogInformation("Download {Id} removed", id);
        Kick();
        return true;
    }

    /// <summary>Puts a stopped download back in the queue ("Try again"), keeping what it had. False when unknown or not stopped.</summary>
    public bool Retry(string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_jobs.FirstOrDefault(j => j.Id == id) is not { State: NestDownload.Failed } job)
                return false;
            job.State = NestDownload.Queued;
            job.Error = null;
            job.Failures = 0;
            job.NextTry = null;
            job.Finished = null;
            Save();
        }
        Kick();
        return true;
    }

    /// <summary>The file of a held download (encrypted nest), or null.</summary>
    public string? HeldFile(string id)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_jobs.FirstOrDefault(j => j.Id == id) is not { State: NestDownload.Done, Plain: true } job)
                return null;
            var file = Path.Combine(HeldDir, job.Id, job.Name);
            return File.Exists(file) ? file : null;
        }
    }

    /// <summary>Runs the queue soon.</summary>
    public void Kick()
    {
        try
        {
            _kick.Release();
        }
        catch (SemaphoreFullException)
        {
            // A look is due already.
        }
    }

    // ------------------------------------------------------------------ the queue

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        FreeSpace ??= () => ServerUpdater.DiskSpace(_store.Paths.DataDir).Free;
        _http = SafeHttp.Create(TestAllows);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                StartDue(stoppingToken);
                await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(1), stoppingToken), _kick.WaitAsync(stoppingToken)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: the running ones stop and stay queued for the next start.
        }
        finally
        {
            Running[] left;
            lock (_gate)
                left = _running.Values.ToArray();
            foreach (var run in left)
                run.Cancel.Cancel();
            try
            {
                // Each one saves where it stopped before the nest closes, so the next start carries on from there.
                await Task.WhenAll(left.Select(r => r.Work)).WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.LogWarning("Downloads didn't stop in time");
            }
        }
    }

    private void StartDue(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            EnsureLoaded();
            Tidy();
            var now = _clock.GetUtcNow();
            foreach (var job in _jobs.Where(j => j.State == NestDownload.Queued && (j.NextTry is null || j.NextTry <= now)).OrderBy(j => j.Added).ToList())
            {
                if (_running.Count >= NestDownloadRules.AtOnce)
                    break;
                var run = new Running(CancellationTokenSource.CreateLinkedTokenSource(stoppingToken), job.Bytes);
                _running[job.Id] = run;
                job.State = NestDownload.Running;
                Save();
                run.Work = Task.Run(() => RunAsync(job, run, stoppingToken), CancellationToken.None);
            }
        }
    }

    private async Task RunAsync(Job job, Running run, CancellationToken stoppingToken)
    {
        string? error = null;
        var retry = false;
        try
        {
            await DownloadAsync(job, run, run.Cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.Cancel.IsCancellationRequested)
        {
            // Removed, or the nest is stopping.
        }
        catch (DownloadException ex)
        {
            error = ex.Code;
            retry = ex.Code == NestDownloadErrors.Lost;
        }
        catch (Exception ex) when (Blocked(ex))
        {
            error = NestDownloadErrors.Blocked;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            error = NestDownloadErrors.Lost;
            retry = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Download {Id} from {Host} failed", job.Id, job.Host);
            error = NestDownloadErrors.Lost;
        }
        finally
        {
            Finish(job, run, error, retry, stoppingToken.IsCancellationRequested);
        }
    }

    private void Finish(Job job, Running run, string? error, bool retry, bool stopping)
    {
        lock (_gate)
        {
            _running.Remove(job.Id);
            run.Cancel.Dispose();
            if (run.Forget || !_jobs.Contains(job))
            {
                TryDelete(PartPath(job.Id));
            }
            else if (job.State == NestDownload.Done)
            {
                // Finished: Done set it.
            }
            else if (stopping && error is null)
            {
                job.State = NestDownload.Queued; // carries on at the next start
            }
            else if (error is not null && retry && (job.Bytes > run.StartBytes + 1024 * 1024 ? job.Failures = 1 : ++job.Failures) < Tries)
            {
                job.State = NestDownload.Queued;
                job.Error = error;
                job.NextTry = _clock.GetUtcNow() + RetryDelay * Math.Pow(2, job.Failures - 1);
                _log.LogInformation("Download {Id} from {Host}: connection lost, try {Try} of {Tries} soon", job.Id, job.Host, job.Failures + 1, Tries);
            }
            else if (error is not null)
            {
                job.State = NestDownload.Failed;
                job.Error = error;
                job.Finished = _clock.GetUtcNow();
                _log.LogInformation("Download {Id} from {Host} stopped: {Error}", job.Id, job.Host, error);
                if (error is NestDownloadErrors.NoSpace)
                    TryDelete(PartPath(job.Id)); // the space is what's missing
            }
            else
            {
                job.State = NestDownload.Queued;
            }
            Save();
        }
        Kick();
    }

    private sealed class DownloadException(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }

    private static bool Blocked(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is BlockedAddressException)
                return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ one download

    private async Task DownloadAsync(Job job, Running run, CancellationToken ct)
    {
        var part = PartPath(job.Id);
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have == 0)
            job.Validator = null;
        using var response = await SendAsync(job, have > 0 ? have : null, ct).ConfigureAwait(false);
        var resumed = response.StatusCode == HttpStatusCode.PartialContent && have > 0
            && response.Content.Headers.ContentRange is { From: { } from, Unit: "bytes" } && from == have;
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
        {
            // The website's file changed under the half-done one: from the start next try.
            TryDelete(part);
            throw new DownloadException(NestDownloadErrors.Lost);
        }
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            throw new DownloadException(NestDownloadErrors.NotFound);
        if (!response.IsSuccessStatusCode || (response.StatusCode == HttpStatusCode.PartialContent && !resumed))
            throw new DownloadException(response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ? NestDownloadErrors.Lost : NestDownloadErrors.Refused);

        long? total = resumed ? response.Content.Headers.ContentRange!.Length : response.Content.Headers.ContentLength;
        var start = resumed ? have : 0;
        var free = FreeSpace!();
        if (total is { } size && free is { } room && size - start > room - NestDownloadRules.KeepFree)
            throw new DownloadException(NestDownloadErrors.NoSpace);

        lock (_gate)
        {
            if (NameFromResponse(response) is { } named)
                job.Name = named;
            job.Total = total;
            job.Bytes = start;
            job.Validator = response.Headers.ETag?.ToString() ?? response.Content.Headers.LastModified?.ToString("R");
            Save();
        }

        await using (var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous))
        {
            await CopyAsync(job, run, body, file, ct).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
        }
        var got = new FileInfo(part).Length;
        if (total is { } expected && got != expected)
            throw new DownloadException(NestDownloadErrors.Lost);
        await CommitAsync(job, part, got, ct).ConfigureAwait(false);
    }

    /// <summary>Sends the request, following redirects by hand: each one has to be a web link and passes the address check again.</summary>
    private async Task<HttpResponseMessage> SendAsync(Job job, long? from, CancellationToken ct)
    {
        var uri = new Uri(from is not null && job.FinalUrl is not null ? job.FinalUrl : job.Url);
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (from is { } offset)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                if (job.Validator is { } validator)
                    request.Headers.TryAddWithoutValidation("If-Range", validator);
            }
            var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (hop >= MaxRedirects || NestDownloadRules.WebLink(next.AbsoluteUri) is null)
                    throw new DownloadException(NestDownloadErrors.Redirects);
                uri = next;
                continue;
            }
            job.FinalUrl = uri.AbsoluteUri;
            return response;
        }
    }

    private async Task CopyAsync(Job job, Running run, Stream body, Stream file, CancellationToken ct)
    {
        var buffer = new byte[1024 * 1024];
        var clock = Stopwatch.StartNew();
        long windowBytes = 0, sinceSpaceCheck = 0;
        var windowStart = clock.Elapsed;
        var lastSave = clock.Elapsed;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            stall.CancelAfter(StallTimeout);
            int n;
            try
            {
                n = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new DownloadException(NestDownloadErrors.Lost);
            }
            if (n == 0)
                break;
            await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            windowBytes += n;
            sinceSpaceCheck += n;
            if (sinceSpaceCheck >= 16 * 1024 * 1024)
            {
                sinceSpaceCheck = 0;
                if (FreeSpace!() is { } room && room < NestDownloadRules.KeepFree)
                    throw new DownloadException(NestDownloadErrors.NoSpace);
            }
            var now = clock.Elapsed;
            lock (_gate)
            {
                job.Bytes += n;
                if (now - windowStart >= TimeSpan.FromSeconds(1))
                {
                    run.BytesPerSecond = windowBytes / (now - windowStart).TotalSeconds;
                    windowBytes = 0;
                    windowStart = now;
                }
                if (now - lastSave >= TimeSpan.FromSeconds(10))
                {
                    lastSave = now;
                    Save();
                }
            }
        }
        if (FreeSpace!() is { } left && left < NestDownloadRules.KeepFree)
            throw new DownloadException(NestDownloadErrors.NoSpace);
    }

    /// <summary>Puts the finished file in the nest under a free name (or, encrypted, in held/), then tells the apps.</summary>
    private async Task CommitAsync(Job job, string part, long size, CancellationToken ct)
    {
        if (_store.Sealed)
        {
            var dir = Path.Combine(HeldDir, job.Id);
            Directory.CreateDirectory(dir);
            File.Move(part, Path.Combine(dir, job.Name), overwrite: true);
            lock (_gate)
                MarkDone(job, null, size, plain: true);
            return;
        }
        var (hash, _) = await ContentHash.OfFileAsync(part, ct).ConfigureAwait(false);
        var mtime = _clock.GetUtcNow().ToUnixTimeMilliseconds(); // when it arrived, so it sorts as new in the folder
        // One at a time, so two that finish together never pick the same free name.
        await _commit.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await CommitFreeNameAsync(job, part, hash, size, mtime, ct).ConfigureAwait(false);
        }
        finally
        {
            _commit.Release();
        }
    }

    private async Task CommitFreeNameAsync(Job job, string part, string hash, long size, long mtime, CancellationToken ct)
    {
        for (var n = 1; n <= 100; n++)
        {
            var path = Combine(job.Folder, n == 1 ? job.Name : Numbered(job.Name, n));
            if (await _store.PrecheckAsync(path, null, ct).ConfigureAwait(false) is not null)
                continue; // taken, or the same name in other letters
            if (!File.Exists(part))
                throw new DownloadException(NestDownloadErrors.Lost);
            // The store moves the file in, or deletes it when it can't: never the part file a retry could resume.
            var commit = Path.Combine(_store.Paths.Tmp, "dlc-" + job.Id + ".upload");
            File.Move(part, commit, overwrite: true);
            var result = await _store.CommitUploadAsync(path, null, commit, hash, size, mtime).ConfigureAwait(false);
            if (result.Status is ChangeStatus.Ok or ChangeStatus.Unchanged)
            {
                lock (_gate)
                    MarkDone(job, path, size, plain: false);
                try
                {
                    await _friends.SendChangedAsync(_hub, ChangedBy, path).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The apps also see it at their next pass.
                }
                return;
            }
            if (result.Status is ChangeStatus.InvalidName or ChangeStatus.BadRequest)
                throw new DownloadException(NestDownloadErrors.BadFolder);
            // A computer took the name meanwhile: the store deleted the file, so it starts over.
            throw new DownloadException(NestDownloadErrors.Lost);
        }
        throw new DownloadException(NestDownloadErrors.BadFolder);
    }

    private void MarkDone(Job job, string? path, long size, bool plain)
    {
        job.State = NestDownload.Done;
        job.Path = path;
        job.Plain = plain;
        job.Bytes = size;
        job.Total = size;
        job.Error = null;
        job.Finished = _clock.GetUtcNow();
        Save();
        _log.LogInformation("Download {Id} from {Host} finished ({Size} bytes)", job.Id, job.Host, size);
    }

    // ------------------------------------------------------------------ names and folders

    /// <summary>The folder as the nest keeps it: "" for the top, "Downloads" when none was given; null when it can't be a folder.</summary>
    public static string? CleanFolder(string? folder)
    {
        if (folder is null)
            return NestDownloadRules.DefaultFolder;
        var clean = folder.Trim().Trim('/');
        return clean.Length == 0 ? "" : PathRules.IsValid(clean) ? clean : null;
    }

    /// <summary>No file has the folder's name (or a parent's), and an encrypted nest's paths are ids, where any folder is fine.</summary>
    private bool FolderTakesFiles(string folder)
    {
        if (folder.Length == 0 || _store.Sealed)
            return true;
        var at = folder;
        while (true)
        {
            if (_store.Manifest.Get(at) is { Deleted: false })
                return false;
            if (PathRules.Parent(at) is not { Length: > 0 } parent)
                return true;
            at = parent;
        }
    }

    private static string Combine(string folder, string name) => folder.Length == 0 ? name : folder + "/" + name;

    /// <summary>"ubuntu.iso" → "ubuntu (2).iso".</summary>
    public static string Numbered(string name, int n)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? $"{name[..dot]} ({n}){name[dot..]}" : $"{name} ({n})";
    }

    /// <summary>The last part of the link, or "download".</summary>
    public static string NameFromUrl(Uri uri) =>
        CleanName(Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/').Split('/')[^1])) ?? "download";

    private static string? NameFromResponse(HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var named = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"');
        if (CleanName(named) is { } fromWebsite)
            return fromWebsite;
        return response.RequestMessage?.RequestUri is { } final ? CleanName(Uri.UnescapeDataString(final.AbsolutePath.TrimEnd('/').Split('/')[^1])) : null;
    }

    /// <summary>A file name the nest and every computer can take: odd characters become "_", at most 200 characters; null when nothing is left.</summary>
    public static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var chars = name.Select(c => c < 0x20 || c == 0x7F || c is '<' or '>' or ':' or '"' or '|' or '?' or '*' or '/' or '\\' ? '_' : c).ToArray();
        var clean = new string(chars).Trim().TrimEnd('.').Trim();
        if (clean.Length > 200)
        {
            var dot = clean.LastIndexOf('.');
            var ext = dot > 0 && clean.Length - dot <= 16 ? clean[dot..] : "";
            clean = clean[..(200 - ext.Length)].TrimEnd(' ', '.') + ext;
        }
        if (clean.Length == 0 || clean is "." or "..")
            return null;
        if (!PathRules.IsValid(clean))
            clean = "_" + clean;
        return PathRules.IsValid(clean) ? clean : null;
    }

    // ------------------------------------------------------------------ the saved list

    private void EnsureLoaded()
    {
        if (_loaded)
            return;
        _loaded = true;
        try
        {
            if (File.Exists(_listFile) && JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(_listFile), PairnetsJson.Options) is { } saved)
                _jobs.AddRange(saved.Where(j => j.Id.Length > 0 && j.Url.Length > 0));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning("Couldn't read the download list: {Error}", ex.Message);
        }
        foreach (var job in _jobs.Where(j => j.State == NestDownload.Running))
            job.State = NestDownload.Queued; // stopped by a restart: carries on
    }

    /// <summary>Finished and stopped ones leave after <see cref="NestDownloadRules.KeepFor"/>, a stopped one's half-done file too.</summary>
    private void Tidy()
    {
        var cutoff = _clock.GetUtcNow() - NestDownloadRules.KeepFor;
        // A held one (encrypted nest) stays until the owner removes it: its file is nowhere else.
        var old = _jobs.Where(j => j.State is NestDownload.Done or NestDownload.Failed && !j.Plain && (j.Finished ?? j.Added) < cutoff).ToList();
        foreach (var job in old)
            Drop(job);
        if (old.Count > 0)
            Save();
    }

    private void Drop(Job job)
    {
        _jobs.Remove(job);
        if (_running.TryGetValue(job.Id, out var run))
        {
            run.Forget = true;
            run.Cancel.Cancel(); // its end deletes the part file
        }
        else
        {
            TryDelete(PartPath(job.Id));
        }
        if (job.Plain)
        {
            try
            {
                Directory.Delete(Path.Combine(HeldDir, job.Id), recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Gone already.
            }
        }
    }

    private void Save()
    {
        var tmp = _listFile + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(_jobs, PairnetsJson.Options));
            File.Move(tmp, _listFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Couldn't save the download list: {Error}", ex.Message);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // tmp/ drops it after 7 days.
        }
    }

    public override void Dispose()
    {
        _http?.Dispose();
        base.Dispose();
    }
}
