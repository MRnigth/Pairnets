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

namespace Pairnets.Client.Tests;

/// <summary>
/// A release page on localhost, shaped like GitHub's "latest release" downloads: version.json, SHA256SUMS.txt, the
/// installer and the zip (Pairnets.exe for copies no installer looks after), with SHA256SUMS.txt signed by <see cref="Pairnets.Tests.Infrastructure.TestReleaseKey"/> (TEST ONLY; give the
/// app <see cref="SigningKey"/>). <see cref="Publish"/> puts a new release up; a tampered one lists a checksum its
/// installer does not have, an unsigned one has no SHA256SUMS.txt.sig, a broken one has a zip
/// without Pairnets.exe in it.
/// </summary>
public sealed class FakeReleaseFeed : IAsyncDisposable
{
    private readonly WebApplication _app;
    private volatile Release _release;

    private sealed record Release(string Version, byte[] Asset, byte[] App, byte[] Zip, byte[] VersionJson, byte[] Sums, byte[]? Signature);

    private FakeReleaseFeed(WebApplication app, Release release)
    {
        _app = app;
        _release = release;
    }

    /// <summary>Where the app looks for updates (ends in "/").</summary>
    public Uri DownloadBase { get; private set; } = null!;

    /// <summary>The page the app opens when it cannot install the update itself.</summary>
    public Uri ReleasePage => new(DownloadBase, "../release");

    public string Version => _release.Version;

    /// <summary>The installer (PairnetsSetup.exe).</summary>
    public byte[] Asset => _release.Asset;

    /// <summary>The Pairnets.exe inside the zip: what a copy from the zip puts in its own place.</summary>
    public byte[] App => _release.App;

    /// <summary>The paths the app asked for, in order.</summary>
    public Log<string> Requests { get; } = new();

    /// <summary>The key the feed signs with (TEST ONLY): the app's <c>ClientEnvironment.UpdateSigningKey</c>.</summary>
    public static string SigningKey => Pairnets.Tests.Infrastructure.TestReleaseKey.PublicKeyPem;

    public static string AssetName => UpdatePackage.WindowsInstaller;

    public static string ZipName => UpdatePackage.WindowsZip;

    public static async Task<FakeReleaseFeed> StartAsync(string version = "99.0.0")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var feed = new FakeReleaseFeed(app, Make(version, false, true, false));
        app.Use(async (ctx, next) =>
        {
            feed.Requests.Add(ctx.Request.Path.Value ?? string.Empty);
            await next(ctx);
        });
        app.MapGet("/download/version.json", () => Results.Bytes(feed._release.VersionJson, "application/json"));
        app.MapGet("/download/SHA256SUMS.txt", () => Results.Bytes(feed._release.Sums, "text/plain"));
        app.MapGet("/download/SHA256SUMS.txt.sig", () =>
            feed._release.Signature is { } sig ? Results.Bytes(sig, "application/octet-stream") : Results.NotFound());
        app.MapGet("/download/{asset}", (string asset) =>
            asset == AssetName ? Results.Bytes(feed._release.Asset, "application/octet-stream")
            : asset == ZipName ? Results.Bytes(feed._release.Zip, "application/zip")
            : Results.NotFound());
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        feed.DownloadBase = new Uri(address.TrimEnd('/') + "/download/");
        return feed;
    }

    /// <summary>
    /// Puts up a new release. <paramref name="tampered"/>: the installer is not the one SHA256SUMS.txt lists.
    /// <paramref name="signed"/> false: there is no SHA256SUMS.txt.sig. <paramref name="broken"/>: the zip (listed
    /// correctly) has no Pairnets.exe in it.
    /// </summary>
    public void Publish(string version, bool tampered = false, bool signed = true, bool broken = false) =>
        _release = Make(version, tampered, signed, broken);

    private static Release Make(string version, bool tampered, bool signed, bool broken)
    {
        var asset = Encoding.UTF8.GetBytes($"pretend installer for Pairnets {version}\n" + new string('x', 200_000));
        var app = Encoding.UTF8.GetBytes($"pretend Pairnets.exe {version}\n" + new string('y', 100_000));
        var zip = broken
            ? Pairnets.Tests.Infrastructure.TestUpdatePackages.Zip(("README-client.txt", app))
            : Pairnets.Tests.Infrastructure.TestUpdatePackages.Make(ZipName, app);
        string Listed(byte[] file) =>
            Convert.ToHexString(SHA256.HashData(tampered ? Encoding.UTF8.GetBytes("a different file") : file)).ToLowerInvariant();
        var versionJson = Encoding.UTF8.GetBytes($"{{\"version\":\"{version}\",\"commit\":\"0000000\"}}\n");
        var sums = Encoding.UTF8.GetBytes($"{Listed(asset)}  {AssetName}\n{Listed(zip)}  {ZipName}\n"
            + $"{Convert.ToHexString(SHA256.HashData(versionJson)).ToLowerInvariant()}  version.json\n");
        return new Release(version, asset, app, zip, versionJson, sums, signed ? Pairnets.Tests.Infrastructure.TestReleaseKey.Sign(sums) : null);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
