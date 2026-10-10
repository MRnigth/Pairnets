using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.E2E;

/// <summary>The real server program as its own process (<see cref="ServerProcess"/>), stopped and started like a service.</summary>
public sealed class ProcessTarget(ServerProcess server) : IServerTarget
{
    public string Name => "process";

    public ServerProcess Server => server;

    public Uri ApiUrl => server.ApiUrl;

    public Uri WebsiteUrl => server.WebsiteUrl;

    public string Token => server.Token;

    public bool RealUpdater => false;

    public static async Task<ProcessTarget> StartAsync() => new(await ServerProcess.StartAsync());

    public Task<IReadOnlyList<RawMail>> MailsAsync() =>
        Task.FromResult<IReadOnlyList<RawMail>>(server.Smtp.Mails.Select(m => RawMail.Parse(m.Data)).ToList());

    public Task<CliResult> RunCliAsync(params string[] args) => server.RunCliAsync(args);

    public Task StopServerAsync() => server.StopAsync();

    public Task StartServerAsync() => server.StartAgainAsync();

    public async Task WriteDataFileAsync(string relative, byte[] content)
    {
        var full = Path.Combine(server.DataDir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content);
    }

    public Task MoveDataFileAsync(string from, string to)
    {
        File.Move(Path.Combine(server.DataDir, from), Path.Combine(server.DataDir, to));
        return Task.CompletedTask;
    }

    public Task<bool> UpdateRequestedAsync() => Task.FromResult(File.Exists(Path.Combine(server.UpdateDir, "request")));

    public Task<IReadOnlyList<string>> ServerLogAsync() => Task.FromResult(server.Output);

    /// <summary>The server logs one line per request ("GET /api/info -> 200 in 3 ms"); those are its side of the story.</summary>
    public Task<bool> CollectServerSideAsync(TourRecord record)
    {
        record.ServerLogged(server.Output);
        return Task.FromResult(true);
    }

    public ValueTask DisposeAsync() => server.DisposeAsync();
}
