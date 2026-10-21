using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Client.Tests;

/// <summary>
/// 1.0.60.1, the same checks as the Mac/Linux app: the Overview map is balanced (this computer's full name, both lines
/// the same length, "in sync" on the other computer's line like on this one's), and the sign-in email box starts its
/// text right after the envelope (it started 92 px in: the box's padding was counted twice).
/// </summary>
public sealed class MapBalanceTests
{
    private const string Me = "DESKTOP-7K2M9QX";

    private static WpfThread Wpf => WpfThread.Instance;

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

    private static Rect Where(FrameworkElement e, Visual window) =>
        e.TransformToVisual(window).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));

    private static async Task Done()
    {
        await Wpf.Settle();
        Assert.Empty(Wpf.TakeCrashes());
        await Wpf.CloseAllWindows();
    }

    [Theory]
    [InlineData(1180)]
    [InlineData(940)]
    public async Task TheMapIsBalanced(double width)
    {
        var window = await Wpf.Ui(() =>
        {
            var w = new MainWindow(new RecordingActions(new SampleHistory())) { Width = width, Height = 760 };
            w.Show();
            w.ShowStatus(Synced(), @"D:\Sync\Work", Me);
            return w;
        });
        await Wpf.WaitUntil(() => window.IsLoaded, "the window to open");
        await Wpf.Settle();
        await Wpf.Ui(() =>
        {
            var hub = Presser.Elements(window).OfType<HubView>().Single();
            // At the smallest window (940 px) it was shrunk a little before too; at the size it opens at it is not.
            if (width >= 1180)
                Assert.True(hub.Scale > 0.99, $"the map is shrunk to {hub.Scale:0.00}");

            // This computer's name in full when there is room, and always in its tooltip.
            Assert.Equal(Me, hub.ThisName.Text);
            var stack = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(hub.ThisName));
            Assert.Equal(Me, stack.ToolTip);
            if (width >= 1180)
            {
                var whole = new TextBlock { Text = Me, FontFamily = hub.ThisName.FontFamily, FontSize = hub.ThisName.FontSize, FontWeight = hub.ThisName.FontWeight };
                whole.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Assert.True(whole.DesiredSize.Width <= hub.ThisName.ActualWidth + 0.5, "this computer's name is cut off");
            }

            // Both lines are the same length.
            var nest = Where(hub.NestBox, window);
            var thisLine = nest.Left - Where(hub.ThisLineStart, window).Left;
            var otherLine = Where(hub.OtherLineEnd, window).Right - nest.Right;
            Assert.True(Math.Abs(thisLine - otherLine) <= 1.5, $"this computer's line is {thisLine:0} px, PC-1's is {otherLine:0} px");

            // "in sync" sits on PC-1's line, level with it, between the nest and PC-1's icon.
            Assert.True(hub.OtherLabel.IsVisible);
            Assert.Equal("in sync", hub.OtherLabel.Text);
            var label = Where(hub.OtherLabel, window);
            var icon = Where(hub.OtherIcon, window);
            Assert.True(label.Left > nest.Right && label.Right < icon.Left, "PC-1's 'in sync' is not on its line");
            var mine = Where(hub.ThisLabel, window);
            Assert.Equal(mine.Top + mine.Height / 2, label.Top + label.Height / 2, 1);
            Assert.Equal("PC-1: in sync", hub.OtherText);
        });
        await Done();
    }

    [Fact]
    public async Task TheEmailTextStartsRightAfterTheEnvelope()
    {
        var window = await Wpf.Ui(() =>
        {
            var w = new SettingsWindow(new ClientSettings { DeviceName = Me }, new PlainSecrets());
            w.Show();
            return w;
        });
        await Wpf.WaitUntil(() => window.IsLoaded, "the window to open");
        await Wpf.Settle();
        await Wpf.Ui(() =>
        {
            var box = (TextBox)window.SignIn!.FindName("AccountEmailBox");
            box.Text = "maria@example.com";
            box.UpdateLayout();
            var first = box.GetRectFromCharacterIndex(0);
            Assert.InRange(first.Left, 44, 52);
        });
        await Done();
    }

    private sealed class PlainSecrets : ISecretProtector
    {
        public string Protect(string plainText) => plainText;

        public string Unprotect(string protectedText) => protectedText;
    }
}
