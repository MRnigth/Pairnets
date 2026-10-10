using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Playwright;
using Pairnets.Tests.E2E;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Browser.Tests;

/// <summary>
/// One test server and one headless Chromium for all the website tests. The server is the real program as its own
/// process, or the one installed on the Linux test box when PAIRNETS_E2E_TARGET=installed-linux. Chromium trusts
/// exactly the server's run-time certificate (pinned by its public key), so the pages count as secure and passkeys
/// work; "localhost" goes straight to 127.0.0.1.
/// </summary>
public sealed class WebsiteFixture : IAsyncLifetime
{
    public IServerTarget Target { get; private set; } = null!;

    public NestApi Api { get; private set; } = null!;

    public IBrowser Browser { get; private set; } = null!;

    public ClickLog Log { get; } = new();

    /// <summary>Where a failing flow leaves its screenshot and trace (PAIRNETS_E2E_ARTIFACTS, else TestResults/browser).</summary>
    public string Artifacts { get; } = Environment.GetEnvironmentVariable("PAIRNETS_E2E_ARTIFACTS") is { Length: > 0 } dir
        ? dir
        : RepoPaths.Of("tests", "Pairnets.Browser.Tests", "TestResults", "browser");

    private IPlaywright? _playwright;

    public async Task InitializeAsync()
    {
        // The browser itself, once per machine (CI also installs the system libraries it needs: playwright.ps1 install --with-deps chromium).
        var installed = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (installed != 0)
            throw new InvalidOperationException($"Installing Chromium for the website tests failed (exit {installed}).");

        if (E2EEnvironment.WantsInstalledLinux)
        {
            Target = InstalledLinuxTarget.FromEnvironment();
            // A fresh start: the nest sends at most five emails an hour, and earlier runs on this box may have used some.
            await Target.StopServerAsync();
            await Target.StartServerAsync();
        }
        else
        {
            Target = await ProcessTarget.StartAsync();
        }
        Api = new NestApi(Target);
        await Api.InitializeAsync();

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            Args =
            [
                "--ignore-certificate-errors-spki-list=" + await PublicKeyPinAsync(Target.WebsiteUrl),
                "--host-resolver-rules=MAP localhost 127.0.0.1",
            ],
        });
    }

    /// <summary>A new browser window with its own cookies, watched for errors, clicks and API requests.</summary>
    public async Task<NestPage> OpenAsync(string flow)
    {
        var context = await Browser.NewContextAsync(new() { BaseURL = Target.WebsiteUrl.ToString(), ViewportSize = new() { Width = 1200, Height = 900 } });
        context.SetDefaultTimeout(30_000);
        await context.ExposeFunctionAsync<string, string, bool>("__pnRecord", (kind, key) =>
        {
            Log.Record(kind, key);
            return true;
        });
        await context.AddInitScriptAsync(ClickLog.RecorderScript);
        context.Request += (_, request) => Log.Request(request.Method, request.Url);
        // "Install Pairnets from pairnets.app/add" opens the public site: answered here, the tests never leave the machine.
        await context.RouteAsync("https://pairnets.app/**", route => route.FulfillAsync(new()
        {
            Status = 200,
            ContentType = "text/html",
            Body = "<!doctype html><title>Add a computer</title><p>Stand-in for pairnets.app/add</p>",
        }));
        await context.Tracing.StartAsync(new() { Title = flow, Screenshots = true, Snapshots = true });
        var page = await context.NewPageAsync();
        return await NestPage.CreateAsync(flow, context, page, this);
    }

    /// <summary>The base64 SHA-256 of the server certificate's public key, as Chromium's pin list wants it.</summary>
    private static async Task<string> PublicKeyPinAsync(Uri site)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", site.Port);
        await using var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(site.Host);
        using var certificate = new X509Certificate2(tls.RemoteCertificate!);
        return Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null)
            await Browser.DisposeAsync();
        _playwright?.Dispose();
        Api?.Dispose();
        if (Target is not null)
            await Target.DisposeAsync();
    }
}
