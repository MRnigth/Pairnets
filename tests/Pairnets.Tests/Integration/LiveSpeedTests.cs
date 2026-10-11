using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.State;
using Pairnets.Core.Sync;
using Pairnets.Server.Web;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>
/// Real speeds for every computer: each app reports what it moves on the push channel, the server passes it on to the
/// others, and nothing is guessed (the old picture took "a change within 8 seconds" for "sending now").
/// </summary>
public class LiveSpeedTests : IAsyncLifetime
{
    private TestServer _server = null!;

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<HubConnection> ConnectAsync(string key, Action<string, TransferReport>? heard = null)
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_server.Url, "hub"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(key))
            .Build();
        if (heard is not null)
            hub.On<string, TransferReport>(PushNames.PeerTransfer, heard);
        await hub.StartAsync();
        return hub;
    }

    private static (Action<string, TransferReport> Heard, Func<Func<(string Name, TransferReport Report), bool>, Task<(string Name, TransferReport Report)>> Next) Listener()
    {
        var got = new List<(string, TransferReport)>();
        return ((name, report) =>
        {
            lock (got)
                got.Add((name, report));
        }, async match =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                lock (got)
                {
                    foreach (var item in got)
                    {
                        if (match(item))
                            return item;
                    }
                }
                await Task.Delay(25);
            }
            throw new TimeoutException("no such report arrived");
        });
    }

    [Fact]
    public async Task AReportReachesTheOtherComputersAndAComputerThatLeavesMovesNothing()
    {
        var pc1 = _server.MintKey("PC-1");
        var pc2 = _server.MintKey("PC-2");
        var pc3 = _server.MintKey("PC-3");
        var ownReports = new List<(string, TransferReport)>();
        Action<string, TransferReport> ownHeard = (name, report) =>
        {
            lock (ownReports)
                ownReports.Add((name, report));
        };
        var (heard2, next2) = Listener();
        var sender = await ConnectAsync(pc1.Key, ownHeard);
        await using var other = await ConnectAsync(pc2.Key, heard2);

        var report = new TransferReport(3.1 * (1 << 20), 0, 383, 38_206, 1L << 30, 114L << 30);
        await sender.InvokeAsync(PushNames.ReportTransfer, report);

        var (name, got) = await next2(r => r.Name == "PC-1");
        Assert.Equal(report, got);

        // A computer that connects now hears it straight away (reports of the last few seconds only).
        var (heard3, next3) = Listener();
        await using var late = await ConnectAsync(pc3.Key, heard3);
        var replayed = await next3(r => r.Name == "PC-1");
        Assert.Equal(report, replayed.Report with { AgeSeconds = 0 });
        Assert.InRange(replayed.Report.AgeSeconds, 0, LiveTransfers.FreshFor.TotalSeconds); // says how old it is

        // Nonsense is cut back before it is passed on.
        await Task.Delay(LiveTransfers.MinInterval);
        await sender.InvokeAsync(PushNames.ReportTransfer, new TransferReport(-1, -2, 500, 100, 0, 0));
        Assert.Equal(new TransferReport(0, 0, 100, 100, 0, 0), (await next2(r => r.Name == "PC-1" && r.Report.FilesDone == 100)).Report);

        // Gone while it was moving files: the others hear that it moves nothing now.
        await Task.Delay(LiveTransfers.MinInterval);
        await sender.InvokeAsync(PushNames.ReportTransfer, report);
        await next2(r => r.Name == "PC-1" && r.Report == report && r.Report.FilesDone == 383);
        await sender.DisposeAsync();
        await next2(r => r.Name == "PC-1" && r.Report == TransferReport.Idle);
        await next3(r => r.Name == "PC-1" && r.Report == TransferReport.Idle);

        // It never heard its own report.
        lock (ownReports)
            Assert.Empty(ownReports);
    }

    [Fact]
    public void TheServerKeepsReportsForTenSecondsAndStopsAFlood()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 10, 21, 0, 0, TimeSpan.Zero));
        var live = new LiveTransfers(clock);
        var moving = new TransferReport(1000, 0, 1, 10, 0, 0);
        Assert.True(live.Set("c1", "PC-1", moving));
        Assert.False(live.Set("c1", "PC-1", moving)); // too soon: not passed on
        Assert.True(live.Set("c1", "PC-1", TransferReport.Idle)); // stopping always goes through
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(live.Set("c1", "PC-1", moving));
        Assert.Equal([("PC-1", moving)], live.Fresh());
        Assert.Empty(live.Fresh(except: "c1"));

        clock.Advance(LiveTransfers.FreshFor + TimeSpan.FromSeconds(1));
        Assert.Empty(live.Fresh()); // ten seconds without a report: it moves nothing
        Assert.Equal("PC-1", live.Remove("c1")!.Name);
        Assert.Null(live.Remove("c1"));
    }

    /// <summary>A server from before live speeds: its hub has no ReportTransfer.</summary>
    private sealed class OldHub : Hub
    {
        public Task BatchStarted(int count) => Task.CompletedTask;
    }

    [Fact]
    public async Task AnOlderServerMeansNoLiveDataAndNoErrors()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSignalR();
        await using var old = builder.Build();
        old.MapHub<OldHub>("/hub");
        await old.StartAsync();
        var url = new Uri(old.Urls.First() + "/");

        using var folder = new TempDir("old-hub");
        using var stateDir = new TempDir("old-hub-state");
        using var state = new StateDb(Path.Combine(stateDir.Path, "state.db"));
        var engine = new SyncEngine(new EngineOptions { Folder = folder.Path, DeviceName = "PC-1" }, new FakeApi(), state);
        await using var runner = new SyncRunner(engine, new RunnerOptions { ServerUrl = url, Token = "t", DeviceId = "PC-1", EnableWatcher = false, PeriodicInterval = TimeSpan.FromHours(1) });
        runner.Start();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!runner.HubConnected && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(runner.HubConnected);

        await runner.ReportTransferAsync(new TransferReport(1000, 0, 1, 2, 0, 0)); // no exception...
        Assert.True(runner.LiveReportsUnsupported); // ...and no more tries on this connection
        await runner.ReportTransferAsync(TransferReport.Idle);
        Assert.True(runner.HubConnected);
    }

    [Fact]
    public async Task ASessionTellsTheOthersWhatItMovesWhileItMovesItAndOnceWhenItStops()
    {
        using var folder = new TempDir("live-session");
        using var stateBase = new TempDir("live-session-state");
        for (var i = 0; i < 10; i++)
            await File.WriteAllBytesAsync(Path.Combine(folder.Path, $"photo-{i}.jpg"), new byte[64 * 1024]);
        var (heard, next) = Listener();
        await using var other = await ConnectAsync(_server.MintKey("PC-2").Key, heard);

        // Uploads at 200 KB/s, so the batch takes a few seconds.
        await using var session = ClientSession.Start(new ClientSettings
        {
            ServerUrl = _server.Url.ToString(),
            ProtectedToken = "unused",
            Folder = folder.Path,
            DeviceName = "PC-1",
            FirstRunCompleted = true,
            UploadLimitMBps = 0.2,
            ParallelTransfers = 1,
        }, _server.Token, new PermanentDeleteTrash(), stateBaseDir: stateBase.Path, runnerOptions: o => new RunnerOptions
        {
            ServerUrl = o.ServerUrl, Token = o.Token, DeviceId = o.DeviceId, PeriodicInterval = TimeSpan.FromHours(1), WatcherDebounce = TimeSpan.FromMilliseconds(200),
        });

        var moving = await next(r => r.Name == "PC-1" && r.Report.UpBytesPerSecond >= 1);
        Assert.Equal(10, moving.Report.FilesTotal);
        Assert.Equal(0, moving.Report.DownBytesPerSecond);
        Assert.True(moving.Report.Uploading);
        Assert.False(moving.Report.Downloading);
        Assert.InRange(moving.Report.UpBytesPerSecond, 1, 2 * 1024 * 1024); // measured, around the limit
        await next(r => r.Name == "PC-1" && r.Report == TransferReport.Idle);
        Assert.Equal(10, (await _server.Client().GetManifestAsync(null, CancellationToken.None)).Entries.Count);
    }
}
