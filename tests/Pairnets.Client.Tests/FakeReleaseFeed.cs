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
/// A release page on localhost, shaped like GitHub's "latest release" downloads: version.json, SHA256SUMS.txt and the
/// installer, with SHA256SUMS.txt signed by <see cref="Pairnets.Tests.Infrastructure.TestReleaseKey"/> (TEST ONLY; give the
/// app <see cref="SigningKey"/>). <see cref="Publish"/> puts a new release up; a tampered one lists a checksum its
/// installer does not have, an unsigned one has no SHA256SUMS.txt.sig.
/// </summary>
public sealed class FakeReleaseFeed : IAsyncDisposable
{
    private readonly WebApplication _app;
    private volatile Release _release;

    private sealed record Release(string Version, byte[] Asset, byte[] VersionJson, byte[] Sums, byte[]? Signature);

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

    public byte[] Asset => _release.Asset;

    /// <summary>The paths the app asked for, in order.</summary>
    public Log<string> Requests { get; } = new();

    /// <summary>The key the feed signs with (TEST ONLY): the app's <c>ClientEnvironment.UpdateSigningKey</c>.</summary>
    public static string SigningKey => Pairnets.Tests.Infrastructure.TestReleaseKey.PublicKeyPem;

    public static string AssetName => UpdateChecker.AssetForThisPlatform() ?? "PairnetsSetup.exe";

    public static async Task<FakeReleaseFeed> StartAsync(string version = "99.0.0")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var feed = new FakeReleaseFeed(app, Make(version, false, true));
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
            asset == AssetName ? Results.Bytes(feed._release.Asset, "application/octet-stream") : Results.NotFound());
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        feed.DownloadBase = new Uri(address.TrimEnd('/') + "/download/");
        return feed;
    }

    /// <summary>
    /// Puts up a new release. <paramref name="tampered"/>: the installer is not the one SHA256SUMS.txt lists.
    /// <paramref name="signed"/> false: there is no SHA256SUMS.txt.sig.
    /// </summary>
    public void Publish(string version, bool tampered = false, bool signed = true) => _release = Make(version, tampered, signed);

    private static Release Make(string version, bool tampered, bool signed)
    {
        var asset = Encoding.UTF8.GetBytes($"pretend installer for Pairnets {version}\n" + new string('x', 200_000));
        var listed = tampered ? Encoding.UTF8.GetBytes("a different installer") : asset;
        var sha = Convert.ToHexString(SHA256.HashData(listed)).ToLowerInvariant();
        var versionJson = Encoding.UTF8.GetBytes($"{{\"version\":\"{version}\",\"commit\":\"0000000\"}}\n");
        var sums = Encoding.UTF8.GetBytes(
            $"{sha}  {AssetName}\n{Convert.ToHexString(SHA256.HashData(versionJson)).ToLowerInvariant()}  version.json\n");
        return new Release(version, asset, versionJson, sums, signed ? Pairnets.Tests.Infrastructure.TestReleaseKey.Sign(sums) : null);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
