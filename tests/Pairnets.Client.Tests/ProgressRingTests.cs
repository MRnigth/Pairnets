using System.Windows;
using System.Windows.Threading;
using Pairnets.Client.Ui;
using Pairnets.Core.Client;

namespace Pairnets.Client.Tests;

/// <summary>
/// The Developer beta look's progress ring (Overview heading card). The control itself does not depend on the look switch,
/// so it is checked here in a window far off screen on the hidden desktop.
/// </summary>
public sealed class ProgressRingTests
{
    private static WpfThread Wpf => WpfThread.Instance;

    private static ProgressRingModel Syncing(double done = 0.3, double moving = 0.1) => new(done, moving, "37", "of 120", RingTone.Accent, false, true);

    private static ProgressRingModel Calm() => new(1, 0, "✓", string.Empty, RingTone.Ok, false, false);

    private static ProgressRingModel Thinking() => new(0, 0, string.Empty, string.Empty, RingTone.Accent, true, true);

    /// <summary>A ring in a window far off screen, loaded (the orbit and the dots only run in a loaded, visible ring).</summary>
    private static async Task<ProgressRing> Shown()
    {
        var ring = await Wpf.Ui(() =>
        {
            var r = new ProgressRing();
            new Window { Content = r, Width = 300, Height = 300, Left = -20000, ShowInTaskbar = false, ShowActivated = false }.Show();
            return r;
        });
        await Wpf.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle).Task;
        return ring;
    }

    private static Task Close(ProgressRing ring) => Wpf.Ui(() => Window.GetWindow(ring)?.Close());

    [Fact]
    public async Task TheFirstShowSitsAtItsValuesAndTheOrbitLivesOnlyWhileSyncing()
    {
        var ring = await Shown();
        await Wpf.Ui(() =>
        {
            ring.Apply(Syncing());
            Assert.Equal(0.3, ring.DoneShown, 3);
            Assert.Equal(0.4, ring.ReachShown, 3);
            Assert.Equal("37", ring.BigText);
            Assert.Equal("of 120", ring.SmallText);
            Assert.True(ring.OrbitAttached);
            Assert.False(ring.DotsAttached);
        });
        await Close(ring);
    }

    [Fact]
    public async Task WhenSyncingEndsTheOrbitLeavesTheVisualTree()
    {
        var ring = await Shown();
        await Wpf.Ui(() =>
        {
            ring.Apply(Syncing());
            Assert.True(ring.OrbitAttached);
            ring.Apply(Calm());
        });
        await Task.Delay(900); // the 300 ms fade-out, then it is removed
        await Wpf.Ui(() =>
        {
            Assert.False(ring.OrbitAttached);
            Assert.Equal("✓", ring.BigText);
            Assert.Equal(RingTone.Ok, ring.Tone);
        });
        await Close(ring);
    }

    [Fact]
    public async Task ThreeDotsShowWhileThereIsNoCountAndGoWhenItArrives()
    {
        var ring = await Shown();
        await Wpf.Ui(() =>
        {
            ring.Apply(Thinking());
            Assert.True(ring.DotsAttached);
            Assert.Equal(string.Empty, ring.BigText);
            ring.Apply(Syncing());
            Assert.False(ring.DotsAttached);
        });
        await Close(ring);
    }

    [Fact]
    public async Task TheDescriptionIsWhatAScreenReaderSays()
    {
        var ring = await Shown();
        await Wpf.Ui(() =>
        {
            var model = Syncing();
            ring.Apply(model);
            Assert.Equal(model.Description, System.Windows.Automation.AutomationProperties.GetName(ring));
        });
        await Close(ring);
    }
}
