using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Integration;

/// <summary>Choosing a computer's icon through the session (what the Devices page's chooser calls).</summary>
public class DeviceIconSessionTests : IAsyncLifetime
{
    private TestServer _server = null!;
    private readonly TempDir _folder = new("icon-session");
    private readonly TempDir _stateBase = new("icon-session-state");

    public async Task InitializeAsync() => _server = await TestServer.StartAsync();

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _folder.Dispose();
        _stateBase.Dispose();
    }

    private ClientSession Start(string key) => ClientSession.Start(new ClientSettings
    {
        ServerUrl = _server.Url.ToString(),
        ProtectedToken = "unused-by-session",
        Folder = _folder.Path,
        DeviceName = "DESKTOP",
        FirstRunCompleted = true,
    }, key, new PermanentDeleteTrash(), stateBaseDir: _stateBase.Path);

    [Fact]
    public async Task PickingAnIconSavesItOnTheNestAndTheListShowsIt()
    {
        var desktop = _server.MintKey("DESKTOP");
        var laptop = _server.MintKey("LAPTOP");
        await using var session = Start(desktop.Key);

        await session.RefreshDevicesAsync();
        Assert.True(session.Status.DeviceIconsSupported);
        Assert.All(session.Status.Devices!, d => Assert.Null(d.Icon));

        Assert.True(await session.SetDeviceIconAsync(laptop.Id, "tv"));
        Assert.Equal("tv", session.Status.Devices!.Single(d => d.Id == laptop.Id).Icon);
        Assert.Equal(DeviceIconKind.Tv, DeviceRow.From(session.Status.Devices!, "DESKTOP", DateTimeOffset.UtcNow, session.Status).Single(r => r.Id == laptop.Id).Icon);
    }

    [Fact]
    public async Task AValueThatIsNotAnIconIsNotSent()
    {
        var desktop = _server.MintKey("DESKTOP");
        await using var session = Start(desktop.Key);
        await session.RefreshDevicesAsync();

        Assert.False(await session.SetDeviceIconAsync(desktop.Id, "toaster"));
        Assert.False(await session.SetDeviceIconAsync(desktop.Id, ""));
        Assert.Null(session.Status.Devices!.Single(d => d.Id == desktop.Id).Icon);
    }
}
