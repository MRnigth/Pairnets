using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using Pairnets.Client.Platform;

namespace Pairnets.Tools;

/// <summary>
/// Pictures of Pairnets in the real File Explorer, taken on a throwaway CI computer:
/// <c>ExplorerShots &lt;output-dir&gt; --i-am-ci</c>. It adds Pairnets to that computer's Explorer (left side, right-click
/// items, folder icon) with sample files, opens Explorer, takes the pictures and takes Pairnets out again. It refuses
/// to run without <c>--i-am-ci</c> and the CI variable, so it can never change a person's own computer.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length < 1 || !args.Contains("--i-am-ci") || Environment.GetEnvironmentVariable("CI") != "true")
        {
            Console.Error.WriteLine("ExplorerShots changes this computer's Explorer: it only runs in CI (ExplorerShots <dir> --i-am-ci, with CI=true).");
            return 2;
        }
        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folder = Path.Combine(home, "Pairnets");
        MakeSampleFolder(folder);

        var ok = WindowsPlatform.SetExplorerEntry(folder, Path.Combine(AppContext.BaseDirectory, "Pairnets.exe"), Path.Combine(home, "PairnetsLocal"));
        Console.WriteLine($"Pairnets added to Explorer: {ok}");
        Thread.Sleep(3000);
        try
        {
            Shot(folder, Path.Combine(output, "step1-explorer-left-side.png"), null);
            Shot(home, Path.Combine(output, "step1-blue-folder-icon.png"), null);
            Shot(folder, Path.Combine(output, "step2-right-click.png"), "Budget");
        }
        finally
        {
            WindowsPlatform.SetExplorerEntry(null, null, Path.Combine(home, "PairnetsLocal"));
            using var software = Registry.CurrentUser.OpenSubKey("Software");
            Console.WriteLine($"Pairnets taken out of Explorer again: {ExplorerShell.RegisteredFolder(software!) is null}");
        }
        return 0;
    }

    private static void MakeSampleFolder(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "Photos"));
        Directory.CreateDirectory(Path.Combine(folder, "School"));
        File.WriteAllText(Path.Combine(folder, "Budget 2026.xlsx"), "sample");
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "sample");
        File.WriteAllBytes(Path.Combine(folder, "Holiday.mp4"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(folder, "Photos", "Beach.jpg"), new byte[2048]);
    }

    /// <summary>Opens Explorer on <paramref name="path"/>, optionally selects a file and opens its right-click menu, and saves a picture.</summary>
    private static void Shot(string path, string png, string? rightClick)
    {
        var before = ExplorerWindows();
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false })?.Dispose();
        var window = IntPtr.Zero;
        for (var i = 0; i < 40 && window == IntPtr.Zero; i++)
        {
            Thread.Sleep(250);
            window = ExplorerWindows().Except(before).FirstOrDefault();
        }
        if (window == IntPtr.Zero)
        {
            Console.Error.WriteLine("No Explorer window opened for " + path);
            return;
        }
        SetWindowPos(window, IntPtr.Zero, 40, 40, 1100, 680, 0x0040);
        SetForegroundWindow(window);
        Thread.Sleep(2500);
        var area = new Rectangle(40, 40, 1100, 680);
        if (rightClick is not null)
        {
            SendKeys.SendWait(rightClick);
            Thread.Sleep(800);
            SendKeys.SendWait("+{F10}"); // Shift+F10: the full right-click menu
            Thread.Sleep(2000);
            area = new Rectangle(40, 40, 1400, 900);
        }
        using (var bitmap = new Bitmap(area.Width, area.Height))
        {
            using (var g = Graphics.FromImage(bitmap))
                g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            bitmap.Save(png, ImageFormat.Png);
        }
        Console.WriteLine("Saved " + png);
        if (rightClick is not null)
            SendKeys.SendWait("{ESC}");
        PostMessage(window, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(800);
    }

    private static List<IntPtr> ExplorerWindows()
    {
        var found = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            var name = new System.Text.StringBuilder(64);
            GetClassName(h, name, name.Capacity);
            if (name.ToString() == "CabinetWClass" && IsWindowVisible(h))
                found.Add(h);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
