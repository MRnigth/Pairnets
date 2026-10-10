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
/// installer. <see cref="Publish"/> puts a new release up; a tampered one lists a checksum its installer does not have.
/// </summary>
public sealed class FakeReleaseFeed : IAsyncDisposable
{
    private readonly WebApplication _app;
    private volatile Release _release;

    private sealed record Release(string Version, byte[] Asset, string Sums);

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

    public static string AssetName => UpdateChecker.AssetForThisPlatform() ?? "PairnetsSetup.exe";

    public static async Task<FakeReleaseFeed> StartAsync(string version = "99.0.0")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var feed = new FakeReleaseFeed(app, Make(version, false));
        app.Use(async (ctx, next) =>
        {
            feed.Requests.Add(ctx.Request.Path.Value ?? string.Empty);
            await next(ctx);
        });
        app.MapGet("/download/version.json", () => Results.Json(new { version = feed._release.Version, commit = "0000000" }));
        app.MapGet("/download/SHA256SUMS.txt", () => Results.Text(feed._release.Sums));
        app.MapGet("/download/{asset}", (string asset) =>
            asset == AssetName ? Results.Bytes(feed._release.Asset, "application/octet-stream") : Results.NotFound());
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        feed.DownloadBase = new Uri(address.TrimEnd('/') + "/download/");
        return feed;
    }

    /// <summary>Puts up a new release. <paramref name="tampered"/>: the installer is not the one SHA256SUMS.txt lists.</summary>
    public void Publish(string version, bool tampered = false) => _release = Make(version, tampered);

    private static Release Make(string version, bool tampered)
    {
        var asset = Encoding.UTF8.GetBytes($"pretend installer for Pairnets {version}\n" + new string('x', 200_000));
        var listed = tampered ? Encoding.UTF8.GetBytes("a different installer") : asset;
        var sha = Convert.ToHexString(SHA256.HashData(listed)).ToLowerInvariant();
        return new Release(version, asset, $"{sha}  {AssetName}\n");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
