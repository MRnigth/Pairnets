using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;
using Pairnets.Desktop.Views;

namespace Pairnets.Tests.Ui;

/// <summary>
/// 1.0.60.1: the Overview map is balanced (this computer's full name, both lines the same length, "in sync" on the other
/// computer's line like on this one's), and the sign-in email box starts its text right after the envelope. Pictures are
/// written when PAIRNETS_SCREENSHOT_DIR is set.
/// </summary>
public class MapBalanceTests
{
    private const string Me = "DESKTOP-7K2M9QX";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static StatusSnapshot Synced() => StatusSnapshot.Initial with
    {
        Status = RunnerStatus.Idle,
        Text = "Up to date",
        LastSyncAt = Now,
        Server = new ServerInfo("id", 1, 1, "1.0.60", 89L << 30, 1L << 40),
        Devices =
        [
            new DeviceInfo(Me, Now.AddDays(-5), Now, true, "1.0.60", "Windows"),
            new DeviceInfo("PC-1", Now.AddDays(-5), Now, true, "1.0.60", "Windows"),
        ],
    };

    private static void Layout(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static Rect Where(Visual v, Visual window) => new Rect(v.Bounds.Size).TransformToAABB(v.TransformToVisual(window)!.Value);

    private static void Save(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(420);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var outDir = Environment.GetEnvironmentVariable("PAIRNETS_SCREENSHOT_DIR");
        if (outDir is null)
            return;
        Directory.CreateDirectory(outDir);
        frame!.Save(Path.Combine(outDir, name));
    }

    [AvaloniaTheory]
    [InlineData(1200)]
    [InlineData(980)]
    public void TheMapIsBalanced(double width)
    {
        var window = new MainWindow(new SampleActions()) { Width = width, Height = 760 };
        window.Show();
        window.ShowStatus(Synced(), "/home/me/Work", Me);
        Layout(window);
        var hub = Screen.All<HubView>(window).Single();
        Assert.True(hub.Scale > 0.99, $"the map is shrunk to {hub.Scale:0.00}");

        // This computer's name in full when there is room, and always in its tooltip.
        Assert.Equal(Me, hub.ThisName.Text);
        Assert.Equal(Me, ToolTip.GetTip(hub.ThisName.GetVisualAncestors().OfType<StackPanel>().ElementAt(1)));
        if (width >= 1200)
            Assert.DoesNotContain(hub.ThisName.TextLayout.TextLines, l => l.HasCollapsed);

        // Both lines are the same length.
        var nest = Where(hub.NestBox, window);
        var thisLine = nest.Left - Where(hub.ThisLineStart, window).Left;
        var otherLine = Where(hub.OtherLineEnd, window).Right - nest.Right;
        Assert.True(Math.Abs(thisLine - otherLine) <= 1.5, $"this computer's line is {thisLine:0} px, PC-1's is {otherLine:0} px");

        // "in sync" sits on PC-1's line, level with it, between the nest and PC-1's icon.
        Assert.True(hub.OtherLabel.IsEffectivelyVisible);
        Assert.Equal("in sync", hub.OtherLabel.Text);
        var label = Where(hub.OtherLabel, window);
        var icon = Where(hub.OtherIcon, window);
        Assert.True(label.Left > nest.Right && label.Right < icon.Left, "PC-1's 'in sync' is not on its line");
        Assert.Equal(Where(hub.ThisLabel, window).Center.Y, label.Center.Y, 1);
        Assert.Equal("PC-1: in sync", window.MapOtherText);

        Save(window, $"avalonia-map-balanced-{width:0}.png");
        window.AllowClose = true;
        window.Close();
    }

    [AvaloniaFact]
    public void TheEmailTextStartsRightAfterTheEnvelope()
    {
        var host = new SettingsWindow(new ClientSettings { DeviceName = Me }, new PlainSecrets(), autoStart: false);
        host.Show();
        var box = Screen.Named<TextBox>(host, "AccountEmailBox");
        box.Text = "maria@example.com";
        Layout(host);
        var text = box.GetVisualDescendants().OfType<TextPresenter>().Single();
        var left = Where(text, box).Left;
        Assert.InRange(left, 44, 52);
        Save(host, "avalonia-sign-in-email.png");
        host.Close();
    }

    private sealed class PlainSecrets : ISecretProtector
    {
        public string Protect(string plainText) => plainText;

        public string Unprotect(string protectedText) => protectedText;
    }
}
