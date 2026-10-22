using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pairnets.Client.Ui;
using Pairnets.Core;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;
using Pairnets.Tests.Infrastructure;

namespace Pairnets.Tools;

/// <summary>
/// "Invite a friend to this folder…" (1.0.61.2, step A): the Files page with a top-level folder's "…" menu open, and the
/// invite window it opens with that folder ticked.
/// </summary>
internal static class InviteFolder
{
    private sealed class Actions : IMainActions
    {
        public INestFiles? Files { get; } = new FakeNestFiles();
        public bool CanInviteToFolder => true;
        public void FixBlocked() { }
        public void SyncNow() { }
        public void TogglePause() { }
        public void OpenFolder() { }
        public void ShowSettings() { }
        public void ViewLog() { }
        public void UpdateNow() { }
        public void DismissUpdate() { }
        public void DownloadNow() { }
        public void UpdateServer() { }
        public void ReportBug() { }
        public void RefreshDevices() { }
        public Task<bool> SetDeviceIcon(string id, string icon) => Task.FromResult(true);
        public void AddComputer() { }
        public void ManageDevices() { }
        public void RevealFile(string syncPath) { }
        public IHistorySource? History => null;
    }

    public static void Render(string outDir, string suffix, bool print)
    {
        var utc = DateTimeOffset.UtcNow;
        var server = new ServerInfo("id", 1, 1, "1.0.61", 89L << 30, 1L << 40);
        var idle = StatusSnapshot.Initial with { Status = RunnerStatus.Idle, Text = "Up to date", LastSyncAt = utc.AddMinutes(-1), Server = server };

        var window = new MainWindow(new Actions()) { Width = 1200, Height = 760 };
        Program.Place(window);
        window.ShowStatus(idle, @"D:\Sync\Work", "DESKTOP");
        window.Navigate(MainPage.Files);
        Program.Settle(window);
        var more = Find<Button>(window, b => b.DataContext is NestRow { Name: "Documents" } && b.ContextMenu is not null);
        more.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, more));
        Program.Settle(window);
        SaveWithMenu(window, more, outDir, $"windows-invite-folder-menu-{suffix}.png", print);
        more.ContextMenu!.IsOpen = false;
        window.Close();

        var nest = Path.Combine(Path.GetTempPath(), "pairnets-invite-shot");
        foreach (var (name, bytes) in new[] { ("Documents", 3_200_000), ("Music", 41_000_000), ("Photos", 4_200_000), ("School", 900_000) })
        {
            Directory.CreateDirectory(Path.Combine(nest, name));
            File.WriteAllBytes(Path.Combine(nest, name, "sample.bin"), new byte[bytes]);
        }
        var invite = new InviteWindow("Home nest", "nst_0123456789abcdefghjkmnpqrs", nest, sealedNest: false, _ => { }, startFolder: "Documents");
        Program.Place(invite);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!invite.FoldersLoaded && DateTime.UtcNow < until)
            Program.Settle(invite, 100);
        Program.Settle(invite, 1500);
        invite.UpdateLayout();
        SaveWhole(invite, outDir, $"windows-invite-folder-window-{suffix}.png", print);
        invite.Close();
        Directory.Delete(nest, recursive: true);
    }

    private static T Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found && match(found))
                return found;
            if (TryFind(child, match) is { } deeper)
                return deeper;
        }
        throw new InvalidOperationException("not found: " + typeof(T).Name);
    }

    private static T? TryFind<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        try
        {
            return Find(root, match);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The whole client area drawn straight from the window (its content has a margin the brush in <see cref="Program.Save"/> crops).</summary>
    private static void SaveWhole(Window window, string outDir, string name, bool print)
    {
        var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        const double scale = 1.5;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(outDir, name);
        using (var file = File.Create(path))
            encoder.Save(file);
        Console.WriteLine($"Wrote {path}");
        if (print)
            Console.WriteLine(Convert.ToBase64String(File.ReadAllBytes(path)));
    }

    /// <summary>The window as <see cref="Program.Save"/> draws it, with the open menu (a pop-up of its own) drawn on top under its button.</summary>
    private static void SaveWithMenu(Window window, Button button, string outDir, string name, bool print)
    {
        var content = (FrameworkElement)window.Content;
        var menu = button.ContextMenu!;
        var width = content.ActualWidth;
        var height = content.ActualHeight;
        var at = button.TranslatePoint(new Point(0, button.ActualHeight + 2), content);
        var left = Math.Min(at.X, width - menu.ActualWidth - 8);
        const double scale = 1.5;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height) }, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(menu) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, menu.ActualWidth, menu.ActualHeight) }, null,
                new Rect(left, at.Y, menu.ActualWidth, menu.ActualHeight));
        }
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(outDir, name);
        using (var file = File.Create(path))
            encoder.Save(file);
        Console.WriteLine($"Wrote {path}");
        if (print)
            Console.WriteLine(Convert.ToBase64String(File.ReadAllBytes(path)));
    }
}
