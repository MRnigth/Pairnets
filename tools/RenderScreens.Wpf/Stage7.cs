using System.Windows.Controls;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Tools;

/// <summary>Stage 7 (1.0.60.1): the Overview map with a long computer name, and the sign-in email box with text in it.</summary>
internal static class Stage7
{
    public static void Render(string outDir, string suffix, bool print)
    {
        var utc = DateTimeOffset.UtcNow;
        var server = new ServerInfo("id", 1, 1, "1.0.60", 89L << 30, 1L << 40);
        const string me = "DESKTOP-7K2M9QX";
        IReadOnlyList<DeviceInfo> devices =
        [
            new(me, utc.AddDays(-30), utc, true, "1.0.60", "Windows", utc.AddMinutes(-5), "d1", "desktop"),
            new("PC-1", utc.AddDays(-30), utc, true, "1.0.60", "Windows", utc.AddMinutes(-9), "d2", "desktop"),
        ];
        var idle = StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = utc.AddMinutes(-1), Server = server, Devices = devices, DeviceIconsSupported = true };
        foreach (var width in new[] { 1200, 960 })
        {
            var window = new MainWindow(new Stage6.Actions()) { Width = width, Height = 760 };
            Program.Place(window);
            window.ShowStatus(idle, @"D:\Sync\Work", me);
            Program.Save(window, outDir, $"windows-stage7-map-{width}-{suffix}.png", print);
            window.Close();
        }

        var signIn = new SettingsWindow(new ClientSettings { DeviceName = me }, new Program.NoProtector());
        Program.Place(signIn);
        if (signIn.SignIn!.FindName("AccountEmailBox") is TextBox box)
            box.Text = "maria@example.com";
        Program.Save(signIn, outDir, $"windows-stage7-email-{suffix}.png", print);
        signIn.Close();
    }
}
