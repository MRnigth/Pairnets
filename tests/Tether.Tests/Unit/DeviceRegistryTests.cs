using Microsoft.Extensions.Logging.Abstractions;
using Tether.Core;
using Tether.Core.Client;
using Tether.Server.Services;
using Tether.Server.Storage;
using Tether.Tests.Infrastructure;

namespace Tether.Tests.Unit;

public class DeviceRegistryTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RemembersEachComputerItsAppAndLastChangeAcrossRestarts()
    {
        using var dir = new TempDir("devices");
        var clock = new ManualClock(Start);
        var registry = new DeviceRegistry(new ServerPaths(dir.Path), NullLogger<DeviceRegistry>.Instance, clock);
        registry.Seen("DESKTOP", "1.0.38; Windows");
        registry.Seen("unknown", "1.0.38; Windows"); // no name: not a device
        clock.Advance(TimeSpan.FromMinutes(1));
        registry.Seen("MacBook", "1.0.37; macOS");
        registry.Changed("MacBook");
        clock.Advance(TimeSpan.FromMinutes(10));
        registry.Connected("conn-1", "DESKTOP"); // live hub connection: online even though quiet

        var list = registry.List();
        Assert.Equal(["MacBook", "DESKTOP"], list.Select(d => d.Name));
        var mac = list[0];
        Assert.False(mac.Online); // quiet for 10 minutes, no connection
        Assert.Equal("1.0.37", mac.AppVersion);
        Assert.Equal("macOS", mac.System);
        Assert.Equal(Start.AddMinutes(1), mac.LastChange);
        Assert.True(list[1].Online);

        registry.Disconnected("conn-1");
        Assert.False(registry.List().Single(d => d.Name == "DESKTOP").Online);

        registry.Dispose(); // saves
        var reloaded = new DeviceRegistry(new ServerPaths(dir.Path), NullLogger<DeviceRegistry>.Instance, clock);
        Assert.Equal(2, reloaded.List().Count);
        Assert.Equal("Windows", reloaded.List().Single(d => d.Name == "DESKTOP").System);
    }

    [Theory]
    [InlineData("1.0.38; Windows", "1.0.38", "Windows")]
    [InlineData("1.0.38", "1.0.38", null)]
    [InlineData("", null, null)]
    public void ParsesTheClientHeader(string header, string? version, string? system)
    {
        var (v, s) = DeviceRegistry.ParseClient(header);
        Assert.Equal(version, v);
        Assert.Equal(system, s);
    }

    [Fact]
    public void RowsPutThisComputerFirstAndSayWhenOthersWereSeen()
    {
        var devices = new[]
        {
            new DeviceInfo("Laptop", Start.AddDays(-3), Start.AddHours(-3), false, "1.0.37", "Linux", null),
            new DeviceInfo("DESKTOP", Start.AddDays(-5), Start, true, "1.0.38", "Windows", Start.AddMinutes(-5)),
            new DeviceInfo("MacBook", Start.AddDays(-1), Start, true, "1.0.38", "macOS", null),
        };
        var rows = DeviceRow.From(devices, "macbook", Start);
        Assert.Equal(["MacBook", "DESKTOP", "Laptop"], rows.Select(r => r.Name));
        Assert.Equal("MacBook (this computer)", rows[0].Title);
        Assert.Equal("Online", rows[1].StatusText);
        Assert.Contains("Windows · Tether 1.0.38 · last change 5 min ago", rows[1].Detail);
        Assert.Equal("Last seen 3 h ago", rows[2].StatusText);
        Assert.Contains("no changes yet", rows[2].Detail);
    }
}
