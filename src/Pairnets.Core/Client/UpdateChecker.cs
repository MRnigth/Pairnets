using System.Globalization;
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
public sealed record UpdateInfo(string Version, string AssetName, Uri AssetUrl, string Sha256, Uri ReleasePage, bool Signed);

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
public sealed class UpdateChecker(HttpClient http, Uri? downloadBase = null, Uri? releasePage = null, string? signingKeyPem = null)
{
    public static readonly Uri DefaultDownloadBase = new("https://github.com/MRnigth/Pairnets/releases/latest/download/");
    public static readonly Uri DefaultReleasePage = new("https://github.com/MRnigth/Pairnets/releases/latest");

    private readonly Uri _base = downloadBase ?? DefaultDownloadBase;
    private readonly Uri _page = releasePage ?? DefaultReleasePage;
    private readonly string _signingKey = signingKeyPem ?? ReleaseSigning.PublicKeyPem;

    private sealed record VersionFile(string Version, string? Commit);

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

    private static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parts = text.Trim().TrimStart('v').Split('.');
        if (parts.Length is < 2 or > 4 || parts.Any(p => !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            return false;
        version = Version.Parse(string.Join('.', parts.Concat(Enumerable.Repeat("0", 4 - parts.Length))));
        return true;
    }

    /// <summary>
    /// The newer release for this platform, or null when this version is current, the platform has
    /// no download, or the release cannot be read (offline, private repository). A release whose
    /// checksums are not signed by Pairnets comes back with <see cref="UpdateInfo.Signed"/> false.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(string currentVersion, string? asset, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var versionJson = await http.GetByteArrayAsync(new Uri(_base, "version.json"), timeout.Token).ConfigureAwait(false);
            var file = JsonSerializer.Deserialize<VersionFile>(versionJson, PairnetsJson.Options);
            if (file is null || !IsNewer(file.Version, currentVersion) || asset is null)
                return null;
            var sums = await http.GetByteArrayAsync(new Uri(_base, ReleaseSigning.SumsFile), timeout.Token).ConfigureAwait(false);
            var signature = await GetIfPresentAsync(new Uri(_base, ReleaseSigning.SignatureFile), timeout.Token).ConfigureAwait(false);
            var text = Encoding.UTF8.GetString(sums);
            var sha = FindChecksum(text, asset);
            // The signature covers SHA256SUMS.txt, which lists version.json too: an old signed release cannot be
            // passed off as a newer version.
            var signed = ReleaseSigning.Verify(sums, signature, _signingKey)
                && string.Equals(FindChecksum(text, "version.json"), ContentHash.Of(versionJson), StringComparison.OrdinalIgnoreCase);
            if (!signed)
                return new UpdateInfo(file.Version.Trim(), asset, new Uri(_base, asset), sha ?? string.Empty, _page, Signed: false);
            return sha is null ? null : new UpdateInfo(file.Version.Trim(), asset, new Uri(_base, asset), sha, _page, Signed: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or NotSupportedException)
        {
            if (ct.IsCancellationRequested)
                throw;
            return null;
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
