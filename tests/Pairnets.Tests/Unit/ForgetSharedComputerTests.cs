using Microsoft.Extensions.Logging.Abstractions;
using Pairnets.Server.Services;
using Pairnets.Server.Storage;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tests.Unit;

/// <summary>A computer that only ever used the old shared token has no key to remove, but its row can be forgotten.</summary>
public sealed class ForgetSharedComputerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnlyAComputerOnTheSharedTokenIsForgottenAndItStaysForgotten()
    {
        using var dir = new TempDir("devices");
        var clock = new ManualClock(Start);
        var registry = new DeviceRegistry(new ServerPaths(dir.Path), NullLogger<DeviceRegistry>.Instance, clock);
        registry.Seen("OLD-DESKTOP", "1.0.38; Windows"); // shared token: known by the name it sends
        registry.Seen("id:laptop1", "LAPTOP", "1.1.0; Windows"); // its own key
        var closed = false;
        registry.Connected("conn-1", "OLD-DESKTOP", abort: () => closed = true);

        Assert.False(registry.ForgetShared("LAPTOP")); // has a key: "Remove from Pairnets" is for that one
        Assert.False(registry.ForgetShared("id:laptop1"));
        Assert.False(registry.ForgetShared("NOBODY"));
        Assert.True(registry.ForgetShared("old-desktop")); // names match whatever their case, like everywhere in the list
        Assert.False(registry.ForgetShared("OLD-DESKTOP"));

        Assert.True(closed); // a push connection it still had is closed
        Assert.Equal(["LAPTOP"], registry.List().Select(d => d.Name));
        registry.Dispose();
        var reloaded = new DeviceRegistry(new ServerPaths(dir.Path), NullLogger<DeviceRegistry>.Instance, clock);
        Assert.Equal(["LAPTOP"], reloaded.List().Select(d => d.Name));
    }

    [Fact]
    public void AForgottenComputerThatConnectsAgainIsListedAgain()
    {
        using var dir = new TempDir("devices");
        var registry = new DeviceRegistry(new ServerPaths(dir.Path), NullLogger<DeviceRegistry>.Instance, new ManualClock(Start));
        registry.Seen("OLD-DESKTOP", null);
        Assert.True(registry.ForgetShared("OLD-DESKTOP"));

        registry.Seen("OLD-DESKTOP", null); // the shared token was still on

        Assert.Equal(["OLD-DESKTOP"], registry.List().Select(d => d.Name));
    }
}
