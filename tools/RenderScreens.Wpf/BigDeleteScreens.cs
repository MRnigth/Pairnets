using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Tools;

/// <summary>
/// "Before big deletes" (2026-10-26): the nest's one choice in Settings, the delete question with "Don't ask my other
/// computers", and the card that puts a big delete back.
/// </summary>
internal static class BigDeleteScreens
{
    public static void Render(string outDir, string suffix, bool print)
    {
        var utc = DateTimeOffset.UtcNow;
        var server = new ServerInfo("id", 1, 1, PairnetsInfo.ProductVersion, 412L << 30, 1L << 40, BigDeletes: BigDeleteModes.AskWhereDeleted);
        var window = new MainWindow(new Stage6.Actions()) { Width = 1200, Height = 860 };
        Program.Place(window);
        var idle = StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = utc.AddMinutes(-1), Server = server, Devices = [] };
        window.ShowStatus(idle, @"D:\Sync\Pairnets", "DESKTOP");

        // 1. Settings: the choice in the nest card.
        var settings = new SettingsView(Settings(), new Program.NoProtector(), null, "Nest " + PairnetsInfo.ProductVersion);
        settings.ShowBigDeletes(BigDeleteModes.AskWhereDeleted, canChange: true, nestKnows: true);
        window.ShowSettingsPage(settings);
        window.UpdateLayout();
        Program.Save(window, outDir, $"big-deletes-settings-{suffix}.png", print);

        // 2. The delete question on the computer where the files were deleted, with the tick box.
        var text = "Pairnets stopped because this sync would delete 912 file(s):\n  0 on this computer (deleted on the other one)\n  912 on the server (deleted on this computer)\n\n"
            + string.Join("\n", Enumerable.Range(1, 6).Select(i => $"  server: Photos/2019/IMG_{4100 + i}.jpg")) + "\n  … and 906 more\n\n"
            + "Deleted files are kept 30 days in History.";
        var (yes, no) = OverviewModel.DeleteButtons(912);
        var popup = Dialogs.ConfirmPreview(text, "Pairnets – allow deletions?", yes, no, BigDeleteText.DontAskOthers);
        var body = (System.Windows.UIElement)popup.Content;
        popup.Content = null;
        popup.Content = new System.Windows.Controls.Border { Child = body, Width = popup.Width };
        Program.Place(popup);
        Program.Save(popup, outDir, $"big-deletes-question-{suffix}.png", print);
        popup.Close();

        // 3. Needs you: the card after the delete went through on another computer (this one followed).
        var batch = new BigDeleteBatch(utc.AddMinutes(-4), FromHere: false,
            Enumerable.Range(1, 912).Select(i => new BigDeleteFile($"Photos/2019/IMG_{4100 + i}.jpg", null)).ToList());
        window.ShowStatus(idle with { BigDelete = batch }, @"D:\Sync\Pairnets", "DESKTOP");
        window.ShowAttention(
        [
            new AttentionItem(BigDeleteText.Done(batch), BigDeleteText.Safe, BigDeleteText.PutBackButton, () => { }, At: batch.At,
                SecondLabel: BigDeleteText.CloseLabel, Second: () => { }),
        ]);
        window.Navigate(MainPage.Attention);
        window.UpdateLayout();
        Program.Save(window, outDir, $"big-deletes-card-{suffix}.png", print);
        window.AllowClose = true;
        window.Close();
    }

    private static ClientSettings Settings() => new()
    {
        ServerUrl = "https://sync.example.com/n/nst_example/", Folder = @"D:\Sync\Pairnets", DeviceName = "DESKTOP", DeviceId = "id",
        AccountEmail = "alex@example.com",
    };
}
