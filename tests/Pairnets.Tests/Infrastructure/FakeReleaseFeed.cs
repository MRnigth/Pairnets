using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pairnets.Core.Client;

namespace Pairnets.Tests.Infrastructure;

/// <summary>
/// The "latest release" download folder on a local port, as the apps read it from GitHub: version.json,
/// SHA256SUMS.txt (signed with <see cref="TestReleaseKey"/>) and the installer for this computer. Point a
/// <see cref="ClientEnvironment"/> at <see cref="DownloadBase"/> (with <see cref="SigningKey"/>) and the app finds
/// whatever <see cref="Version"/> says.
/// </summary>
public sealed class FakeReleaseFeed : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _versionChecks;

    private FakeReleaseFeed(WebApplication app, string version, string? asset)
    {
        _app = app;
        Version = version;
        Asset = asset;
        AssetBytes = Encoding.UTF8.GetBytes("Pairnets test installer " + version);
        Sha256 = Convert.ToHexString(SHA256.HashData(AssetBytes)).ToLowerInvariant();
    }

    /// <summary>The version the feed announces (change it to make an update appear).</summary>
    public string Version { get; set; }

    /// <summary>False: the release has no SHA256SUMS.txt.sig, as if someone other than Pairnets put it up.</summary>
    public bool Signed { get; set; } = true;

    /// <summary>The installer listed for this computer (null where there is none, e.g. ARM Linux).</summary>
    public string? Asset { get; }

    public byte[] AssetBytes { get; }

    public string Sha256 { get; }

    /// <summary>Where version.json, SHA256SUMS.txt and the installer are.</summary>
    public Uri DownloadBase { get; private set; } = null!;

    /// <summary>The release page the apps open to download by hand.</summary>
    public Uri ReleasePage { get; private set; } = null!;

    /// <summary>The key the feed signs with (TEST ONLY): the app's <c>ClientEnvironment.UpdateSigningKey</c>.</summary>
    public static string SigningKey => TestReleaseKey.PublicKeyPem;

    /// <summary>How often version.json was read.</summary>
    public int VersionChecks => Volatile.Read(ref _versionChecks);

    public static async Task<FakeReleaseFeed> StartAsync(string version, string? asset = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var feed = new FakeReleaseFeed(app, version, asset ?? UpdateChecker.AssetForThisPlatform());
        app.MapGet("/releases/latest/download/version.json", () =>
        {
            Interlocked.Increment(ref feed._versionChecks);
            return Results.Bytes(feed.VersionJson(), "application/json");
        });
        app.MapGet("/releases/latest/download/SHA256SUMS.txt", () => Results.Bytes(feed.Sums(), "text/plain"));
        app.MapGet("/releases/latest/download/SHA256SUMS.txt.sig", () =>
            feed.Signed ? Results.Bytes(TestReleaseKey.Sign(feed.Sums()), "application/octet-stream") : Results.NotFound());
        app.MapGet("/releases/latest/download/{name}", (string name) =>
            name == feed.Asset ? Results.Bytes(feed.AssetBytes, "application/octet-stream") : Results.NotFound());
        app.MapGet("/releases/latest", () => Results.Text("<html><body>Pairnets release page</body></html>", "text/html"));
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var root = new Uri(address.TrimEnd('/') + "/");
        feed.DownloadBase = new Uri(root, "releases/latest/download/");
        feed.ReleasePage = new Uri(root, "releases/latest");
        return feed;
    }

    private byte[] VersionJson() => Encoding.UTF8.GetBytes($"{{\"version\":\"{Version}\",\"commit\":\"test\"}}\n");

    /// <summary>SHA256SUMS.txt as the release workflow writes it: the installer and version.json.</summary>
    private byte[] Sums()
    {
        var version = $"{Convert.ToHexString(SHA256.HashData(VersionJson())).ToLowerInvariant()}  version.json\n";
        return Encoding.UTF8.GetBytes(Asset is null ? version : $"{Sha256}  {Asset}\n" + version);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
