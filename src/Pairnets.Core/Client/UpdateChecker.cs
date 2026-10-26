using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Pairnets.Core.Hashing;

namespace Pairnets.Core.Client;

/// <summary>A newer Pairnets release for this computer.</summary>
/// <param name="Version">"1.0.58".</param>
/// <param name="AssetName">The file for this platform, e.g. "PairnetsSetup.exe".</param>
/// <param name="Sha256">Its checksum from SHA256SUMS.txt, verified after downloading.</param>
/// <param name="Signed">
/// True when SHA256SUMS.txt carries a valid Pairnets signature (and lists this version.json). False: the apps show the
/// release as "could not be installed" and <see cref="UpdateChecker.DownloadVerifiedAsync"/> refuses it.
/// </param>
/// <param name="Channel">The feed it came from (<see cref="ReleaseChannel"/>); <see cref="Build"/> is its release run.</param>
public sealed record UpdateInfo(string Version, string AssetName, Uri AssetUrl, string Sha256, Uri ReleasePage, bool Signed,
    string Channel = ReleaseChannel.Stable, int Build = 0)
{
    public ReleaseVersion Release => new(ReleaseVersion.TryParseNumber(Version, out var n) ? n : new Version(0, 0, 0, 0), Channel, Build);

    /// <summary>"1.0.61.8" or "1.0.61.8 Public beta 3".</summary>
    public string Display => Release.Display;
}

/// <summary>What one feed offered (<see cref="UpdateChecker.Judge"/>).</summary>
public enum FeedVerdict
{
    /// <summary>A newer build this computer may install.</summary>
    Newer,
    NotNewer,

    /// <summary>The signed version.json names another channel than the feed's (or none, past 1.0.61): never installed.</summary>
    WrongChannel,

    /// <summary>Newer, but it could not read data this build may have written (see <see cref="DataVersions"/>).</summary>
    LowersData,
    Unreadable,
}

/// <summary>The newest build found over the feeds read, and whether a newer one was held back because of its data versions.</summary>
public sealed record UpdateCheckResult(UpdateInfo? Found, bool HeldBackForData);

/// <summary>
/// The update was not installed: the download did not match its checksum (it was deleted), or the release is not
/// signed by Pairnets (<see cref="NotSigned"/>; nothing was downloaded).
/// </summary>
public sealed class UpdateVerificationException(string message, bool notSigned = false) : Exception(message)
{
    public bool NotSigned { get; } = notSigned;
}

/// <summary>
/// Looks for a newer release on GitHub (version.json of the latest release, no sign-in, so the
/// repository must be public) and downloads it with its checksum verified. The checksums are only
/// trusted when SHA256SUMS.txt.sig is a valid signature by the Pairnets release key
/// (<see cref="ReleaseSigning"/>). Also tells whether the server runs an older version than this app.
/// </summary>
/// <param name="signingKeyPem">The release public key (PEM); null is the Pairnets key. Tests pass a key of their own.</param>
/// <param name="betaFeed">
/// Where a beta channel's files are (its download folder and release page); null is GitHub's rolling pre-release of that
/// name, or a folder beside <paramref name="downloadBase"/> when that is set (test feeds).
/// </param>
public sealed class UpdateChecker(HttpClient http, Uri? downloadBase = null, Uri? releasePage = null, string? signingKeyPem = null,
    Func<string, (Uri DownloadBase, Uri ReleasePage)>? betaFeed = null)
{
    public static readonly Uri DefaultDownloadBase = new("https://github.com/MRnigth/Pairnets/releases/latest/download/");
    public static readonly Uri DefaultReleasePage = new("https://github.com/MRnigth/Pairnets/releases/latest");

    /// <summary>The highest version a release without a "channel" in its version.json may have: the last ones before channels.</summary>
    public static readonly Version LastWithoutChannel = new(1, 0, 61, ushort.MaxValue);

    private readonly Uri _base = downloadBase ?? DefaultDownloadBase;
    private readonly Uri _page = releasePage ?? DefaultReleasePage;
    private readonly string _signingKey = signingKeyPem ?? ReleaseSigning.PublicKeyPem;

    /// <summary>
    /// version.json. <paramref name="Version"/> has at most four numbers, as apps up to 1.0.61.9 need (they read only it);
    /// <paramref name="Release"/> is the full number ("1.0.61.10.1"), absent in releases before five-number versions.
    /// </summary>
    private sealed record VersionFile(string Version, string? Commit, string? Channel = null, int? Build = null, DataVersions? Data = null,
        string? Release = null)
    {
        public string Newest => (string.IsNullOrWhiteSpace(Release) ? Version : Release)?.Trim() ?? string.Empty;
    }

    /// <summary>The download folder and release page of <paramref name="channel"/>'s feed.</summary>
    public (Uri DownloadBase, Uri ReleasePage) FeedOf(string channel)
    {
        if (ReleaseChannel.FeedTag(channel) is not { } tag)
            return (_base, _page);
        if (betaFeed is not null)
            return betaFeed(channel);
        return downloadBase is null
            ? (new Uri($"https://github.com/MRnigth/Pairnets/releases/download/{tag}/"), new Uri($"https://github.com/MRnigth/Pairnets/releases/tag/{tag}"))
            : (new Uri(_base, $"../{tag}/"), _page);
    }

    /// <summary>
    /// Whether a feed's version.json offers something <paramref name="current"/> may install: newer (number, then stable
    /// before a beta of the same number, then build), from the channel the feed is for, and able to read this computer's
    /// data. A version.json without a channel counts as stable only up to <see cref="LastWithoutChannel"/>, so main's
    /// "Latest build" (1.0.&lt;run&gt;) is never taken for a release.
    /// </summary>
    public static FeedVerdict Judge(string? version, string? channel, int? build, DataVersions? data, string feedChannel,
        ReleaseVersion current, DataVersions here)
    {
        if (!ReleaseVersion.TryParseNumber(version, out var number))
            return FeedVerdict.Unreadable;
        var said = channel is null ? (number <= LastWithoutChannel ? ReleaseChannel.Stable : null) : channel.Trim();
        if (said is null || !string.Equals(said, ReleaseChannel.Normalize(feedChannel), StringComparison.Ordinal))
            return FeedVerdict.WrongChannel;
        if (new ReleaseVersion(number, said, build ?? 0) <= current)
            return FeedVerdict.NotNewer;
        return (data ?? DataVersions.Before).CanRead(here) ? FeedVerdict.Newer : FeedVerdict.LowersData;
    }

    /// <summary>The release file for this computer, or null where there is none (32-bit, ARM Linux).</summary>
    public static string? AssetForThisPlatform()
    {
        if (OperatingSystem.IsWindows())
            return "PairnetsSetup.exe";
        if (OperatingSystem.IsMacOS())
            return RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "Pairnets-macos-arm64.dmg" : "Pairnets-macos-x64.dmg";
        if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64)
            return "pairnets-desktop-linux-x64.tar.gz";
        return null;
    }

    /// <summary>True when <paramref name="candidate"/> is a higher version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? candidate, string? current) =>
        TryParse(candidate, out var c) && TryParse(current, out var cur) && c > cur;

    /// <summary>True when the server reports an older release than this app (old servers report none).</summary>
    public static bool ServerIsOlder(string? serverVersion, string appVersion) =>
        serverVersion is not null && IsNewer(appVersion, serverVersion);

    private static bool TryParse(string? text, out VersionNumber version) => ReleaseVersion.TryParseNumber(text, out version);

    /// <summary>
    /// The newer stable release for this platform, or null when this version is current, the platform has no download,
    /// or the release cannot be read (offline, private repository). A release whose checksums are not signed by Pairnets
    /// comes back with <see cref="UpdateInfo.Signed"/> false.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(string currentVersion, string? asset, CancellationToken ct)
    {
        var current = new ReleaseVersion(ReleaseVersion.TryParseNumber(currentVersion, out var n) ? n : new Version(0, 0, 0, 0), ReleaseChannel.Stable, 0);
        return (await CheckAsync(current, ReleaseChannel.Stable, asset, DataVersions.Current, ct).ConfigureAwait(false)).Found;
    }

    /// <summary>
    /// The newest build <paramref name="current"/> may move to: from the stable feed, and on a beta channel also from that
    /// channel's feed (a newer stable wins over an old beta). Moving to a lower channel finds nothing until that channel
    /// has something newer than the build running now.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(ReleaseVersion current, string channel, string? asset, DataVersions here, CancellationToken ct)
    {
        string[] feeds = ReleaseChannel.IsBeta(channel) ? [ReleaseChannel.Stable, channel] : [ReleaseChannel.Stable];
        UpdateInfo? best = null;
        var heldBack = false;
        foreach (var feed in feeds)
        {
            var (found, verdict) = await CheckFeedAsync(feed, current, asset, here, ct).ConfigureAwait(false);
            heldBack |= verdict == FeedVerdict.LowersData;
            if (found is not null && (best is null || found.Release > best.Release))
                best = found;
        }
        return new UpdateCheckResult(best, heldBack);
    }

    private async Task<(UpdateInfo? Found, FeedVerdict Verdict)> CheckFeedAsync(string feed, ReleaseVersion current, string? asset, DataVersions here,
        CancellationToken ct)
    {
        var (root, page) = FeedOf(feed);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var versionJson = await http.GetByteArrayAsync(new Uri(root, "version.json"), timeout.Token).ConfigureAwait(false);
            var file = JsonSerializer.Deserialize<VersionFile>(versionJson, PairnetsJson.Options);
            if (file is null)
                return (null, FeedVerdict.Unreadable);
            var verdict = Judge(file.Newest, file.Channel, file.Build, file.Data, feed, current, here);
            if (verdict != FeedVerdict.Newer || asset is null)
                return (null, verdict);
            var sums = await http.GetByteArrayAsync(new Uri(root, ReleaseSigning.SumsFile), timeout.Token).ConfigureAwait(false);
            var signature = await GetIfPresentAsync(new Uri(root, ReleaseSigning.SignatureFile), timeout.Token).ConfigureAwait(false);
            var text = Encoding.UTF8.GetString(sums);
            var sha = FindChecksum(text, asset);
            // The signature covers SHA256SUMS.txt, which lists version.json too: an old signed release cannot be passed off
            // as a newer version, nor one channel's build as another's.
            var signed = ReleaseSigning.Verify(sums, signature, _signingKey)
                && string.Equals(FindChecksum(text, "version.json"), ContentHash.Of(versionJson), StringComparison.OrdinalIgnoreCase);
            var channel = ReleaseChannel.Normalize(feed);
            var build = file.Build ?? 0;
            if (!signed)
                return (new UpdateInfo(file.Newest, asset, new Uri(root, asset), sha ?? string.Empty, page, Signed: false, channel, build), verdict);
            return (sha is null ? null : new UpdateInfo(file.Newest, asset, new Uri(root, asset), sha, page, Signed: true, channel, build), verdict);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
        {
            if (ct.IsCancellationRequested)
                throw;
            return (null, FeedVerdict.Unreadable);
        }
    }

    /// <summary>The file's bytes, or none when the release does not have it (404).</summary>
    private async Task<byte[]> GetIfPresentAsync(Uri url, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return [];
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The checksum listed for <paramref name="asset"/> in a sha256sum file, lower case.</summary>
    public static string? FindChecksum(string sums, string asset)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == asset && ContentHash.IsValid(parts[0].ToLowerInvariant()))
                return parts[0].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>Downloads the update to <paramref name="destination"/> and verifies its checksum.</summary>
    /// <exception cref="UpdateVerificationException">
    /// The release is not signed by Pairnets (nothing is downloaded), or the file did not match; nothing is left behind.
    /// </exception>
    public async Task DownloadVerifiedAsync(UpdateInfo update, string destination, IProgress<(long Done, long? Total)>? progress, CancellationToken ct)
    {
        if (!update.Signed || !ContentHash.IsValid(update.Sha256.ToLowerInvariant()))
            throw new UpdateVerificationException(ReleaseSigning.NotSignedMessage, notSigned: true);
        var tmp = destination + ".part";
        try
        {
            using (var resp = await http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength;
                await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                long done = 0;
                int n;
                while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    progress?.Report((done, total));
                }
            }
            var (hash, _) = await ContentHash.OfFileAsync(tmp, ct).ConfigureAwait(false);
            if (!string.Equals(hash, update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateVerificationException("The download did not match its checksum. Nothing was installed.");
            File.Move(tmp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }
}
