using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Server.Cli;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.E2E;

/// <summary>
/// The server inside the test process (<see cref="TestServer"/>) with its website, mail and Google set up like the
/// other targets. The commands run through <see cref="CliCommands"/> directly; the server notes which endpoint
/// answered every request.
/// </summary>
public sealed class InProcessTarget : IServerTarget
{
    private readonly ConcurrentQueue<(Route Route, int Status)> _answered = new();
    private readonly FakeSmtpServer _smtp;
    private readonly FakeGoogle _google;
    private readonly TempDir _updater; // also holds the website's certificate
    private TestServer _server = null!;

    private InProcessTarget(FakeSmtpServer smtp, FakeGoogle google, TempDir updater)
    {
        _smtp = smtp;
        _google = google;
        _updater = updater;
    }

    public string Name => "in-process";

    public Uri ApiUrl => _server.Url;

    public Uri WebsiteUrl => new($"https://localhost:{_server.HttpsUrl!.Port}/");

    public string Token => _server.Token;

    public bool RealUpdater => false;

    public string DataDir => _server.DataDir;

    public static async Task<InProcessTarget> StartAsync()
    {
        var updater = new TempDir("inproc-updater");
        TestCertificates.Create("localhost").WriteLeafOnlyTo(updater.Combine("tls"));
        var script = updater.Combine("update.sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\n# Stand-in: its presence turns on \"Update server\". It is never run here.\nexit 0\n");
        var target = new InProcessTarget(new FakeSmtpServer(), await FakeGoogle.StartAsync(), updater);
        for (var attempt = 1; ; attempt++)
        {
            // The public name carries the port, like the process target's, so links and passkeys work in a real browser too.
            var httpsPort = ServerProcess.FreePort();
            try
            {
                target._server = await TestServer.StartAsync(config: new()
                {
                    ["Sync:HttpsUrl"] = $"https://127.0.0.1:{httpsPort}",
                    ["Sync:PublicUrl"] = $"https://localhost:{httpsPort}",
                    ["Sync:SmtpHost"] = "127.0.0.1",
                    ["Sync:SmtpPort"] = target._smtp.Port.ToString(CultureInfo.InvariantCulture),
                    ["Sync:SmtpUseTls"] = "false",
                    ["Sync:SmtpFrom"] = ServerProcess.MailFrom,
                    ["Sync:GoogleClientId"] = FakeGoogle.ClientId,
                    ["Sync:GoogleClientSecret"] = FakeGoogle.ClientSecret,
                    ["Sync:GoogleAuthUrl"] = target._google.AuthUrl,
                    ["Sync:GoogleTokenUrl"] = target._google.TokenUrl,
                    ["Sync:UpdaterScript"] = script,
                    ["Sync:UpdateDir"] = updater.Combine("update"),
                    ["Sync:TlsDir"] = updater.Combine("tls"),
                }, https: true, installCertificate: false, configureBuilder: builder =>
                    builder.Services.AddSingleton<IStartupFilter>(new AnsweredRoutesFilter((route, status) => target._answered.Enqueue((route, status)))));
                return target;
            }
            catch (IOException) when (attempt < 3)
            {
                // the free port was taken in the meantime
            }
        }
    }

    public Task<IReadOnlyList<RawMail>> MailsAsync() =>
        Task.FromResult<IReadOnlyList<RawMail>>(_smtp.Mails.Select(m => RawMail.Parse(m.Data)).ToList());

    public async Task<CliResult> RunCliAsync(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await CliCommands.RunAsync([.. args, "--data-dir", _server.DataDir], output, error);
        return new CliResult(code, output.ToString(), error.ToString());
    }

    public Task StopServerAsync() => _server.StopAsync();

    public Task StartServerAsync() => _server.RestartAsync();

    public async Task WriteDataFileAsync(string relative, byte[] content)
    {
        var full = Path.Combine(_server.DataDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content);
    }

    public Task MoveDataFileAsync(string from, string to)
    {
        File.Move(Path.Combine(_server.DataDir, from), Path.Combine(_server.DataDir, to));
        return Task.CompletedTask;
    }

    public Task<bool> UpdateRequestedAsync() => Task.FromResult(File.Exists(_updater.Combine("update", "request")));

    public Task<IReadOnlyList<string>> ServerLogAsync() => Task.FromResult(_server.Logs.Lines);

    public Task<bool> CollectServerSideAsync(TourRecord record)
    {
        foreach (var (route, status) in _answered)
            record.ServerSaw(route, status);
        return Task.FromResult(true);
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        await _google.DisposeAsync();
        await _smtp.DisposeAsync();
        _updater.Dispose();
    }
}
