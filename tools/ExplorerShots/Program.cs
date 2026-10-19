using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Pairnets.Client.Platform;
using Pairnets.Client.Platform.CloudFiles;
using Pairnets.Core.Settings;

namespace Pairnets.Tools;

/// <summary>
/// Pictures of Pairnets in the real File Explorer, taken on a throwaway CI computer:
/// <c>ExplorerShots &lt;output-dir&gt; --i-am-ci</c>. It adds Pairnets to that computer's Explorer (left side, right-click
/// items, folder icon; then the cloud folder with ticks, and Files On-Demand) with sample files, opens Explorer, takes
/// the pictures and takes Pairnets out again. It refuses to run without <c>--i-am-ci</c> and the CI variable, so it can
/// never change a person's own computer.
/// </summary>
public static class Program
{
    private static string _output = string.Empty;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length < 1 || !args.Contains("--i-am-ci") || Environment.GetEnvironmentVariable("CI") != "true")
        {
            Console.Error.WriteLine("ExplorerShots changes this computer's Explorer: it only runs in CI (ExplorerShots <dir> --i-am-ci, with CI=true).");
            return 2;
        }
        _output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(_output);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Path.Combine(home, "PairnetsLocal");
        var folder = Path.Combine(home, "Pairnets");
        MakeSampleFolder(folder);
        var exe = Path.Combine(AppContext.BaseDirectory, "Pairnets.exe");
        var failures = 0;

        // ---- steps 1 and 2: the left side, the folder icon, the right-click items
        Console.WriteLine($"Pairnets added to Explorer: {WindowsPlatform.SetExplorerEntry(folder, exe, local)}");
        Thread.Sleep(3000);
        Shot("shell:::" + ExplorerShell.FolderClsid, "step1-explorer-left-side.png");
        Shot(home, "step1-blue-folder-icon.png");
        Shot(folder, "step2-right-click.png", select: "Budget", menu: true, moreOptions: true);

        // ---- step 3: a cloud folder, every file kept here, ticks from Windows
        using var log = LoggerFactory.Create(b => b.AddProvider(new ConsoleLog()).SetMinimumLevel(LogLevel.Debug));
        var problem = WindowsCloudFolder.WhyNot(folder);
        Console.WriteLine("Cloud folder possible here: " + (problem ?? "yes"));
        var icon = Path.Combine(local, "Explorer", "AppIcon.ico");
        var cloud = WindowsCloudFolder.Start(folder, CloudFolderMode.KeepAllHere, icon, log.CreateLogger("cloud"), out problem);
        Console.WriteLine("SPIKE: Windows accepted the Pairnets folder as a cloud folder from a normal (unpackaged) program: "
            + (cloud is not null ? "YES" : "NO - " + problem));
        if (cloud is null)
            failures++;
        else
        {
            WindowsPlatform.SetExplorerEntry(folder, exe, local, leftSide: false); // Windows shows the cloud folder in the left side itself
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith("Notes.txt", StringComparison.Ordinal))
                    cloud.MarkSynced(file, "sample-" + Path.GetFileName(file));
            }
            File.AppendAllText(Path.Combine(folder, "Notes.txt"), " changed here, not synced yet");
            cloud.Reconcile(f => f.EndsWith("Notes.txt", StringComparison.Ordinal) || f.EndsWith("desktop.ini", StringComparison.Ordinal) ? null : "sample-" + Path.GetFileName(f));
            Thread.Sleep(4000);
            Report(folder);
            Shot(folder, "step3-ticks.png", details: true);
            Shot("shell:::" + ExplorerShell.FolderClsid, "step3-left-side.png");
            cloud.Dispose();

            // ---- step 4: Files On-Demand, files that stay on the nest until opened
            var onDemand = WindowsCloudFolder.Start(folder, CloudFolderMode.OnDemand, icon, log.CreateLogger("cloud"), out problem);
            if (onDemand is null)
            {
                Console.WriteLine("On-Demand did not start: " + problem);
                failures++;
            }
            else
            {
                var sample = Encoding.UTF8.GetBytes(new string('x', 6 * 1024 * 1024));
                onDemand.Fetch = async (rel, hash, stream, progress, ct) =>
                {
                    // A slow pretend nest, so Explorer's download shows.
                    for (var sent = 0; sent < sample.Length; sent += 256 * 1024)
                    {
                        var piece = Math.Min(256 * 1024, sample.Length - sent);
                        await stream.WriteAsync(sample.AsMemory(sent, piece), ct);
                        progress(sent + piece);
                        await Task.Delay(150, ct);
                    }
                };
                var when = DateTime.UtcNow.AddDays(-3);
                foreach (var name in new[] { "Beach video.mp4", "Trip photos.zip", "Thesis.docx" })
                    Console.WriteLine($"Placed {name} on the nest only: {onDemand.PlaceOnline(Path.Combine(folder, name), "v1-" + name, sample.Length, when)}");
                Thread.Sleep(3000);
                Report(folder);
                Shot(folder, "step4-files-on-demand.png", details: true);
                Shot(folder, "step4-right-click.png", select: "Beach", menu: true, details: true);

                // Opening a file whose content is on the nest: Windows asks Pairnets for it; Explorer shows it downloading.
                Process? open = null;
                Shot(folder, "step4-downloading.png", details: true, during: () =>
                {
                    open = Process.Start(new ProcessStartInfo("cmd.exe", $"/c copy /y \"{Path.Combine(folder, "Trip photos.zip")}\" NUL")
                        { CreateNoWindow = true, UseShellExecute = false });
                    Thread.Sleep(2000);
                });
                var done = open?.WaitForExit(60_000) == true;
                Console.WriteLine($"Opened a file on the nest only: {(done ? "downloaded" : "still downloading after a minute")} (exit {(done ? open!.ExitCode : -1)})");
                open?.Dispose();
                Thread.Sleep(1500);
                Report(folder);
                Shot(folder, "step4-after-opening.png", details: true);

                // "Free up space" on it again (what Explorer's menu item sets).
                Run("attrib.exe", $"+U -P \"{Path.Combine(folder, "Trip photos.zip")}\"");
                Thread.Sleep(4000);
                Report(folder);

                var leave = onDemand.LeaveAsync(CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine("Back to a plain folder: " + (leave ?? "done"));
                Report(folder);
                onDemand.Dispose();
            }
        }

        WindowsCloudFolder.ForgetAll();
        WindowsPlatform.SetExplorerEntry(null, null, local);
        using var software = Registry.CurrentUser.OpenSubKey("Software");
        Console.WriteLine($"Pairnets taken out of Explorer again: {ExplorerShell.RegisteredFolder(software!) is null && WindowsCloudFolder.Registered().Count == 0}");
        return failures;
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

    /// <summary>Prints every file's attributes, so the log shows what Windows thinks of it.</summary>
    private static void Report(string folder)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories))
        {
            Pairnets.Core.Sync.CloudPlaceholders.TryGetAttributesAndTag(path, out var attributes, out var tag);
            Console.WriteLine($"  {Path.GetRelativePath(folder, path),-28} {attributes} tag=0x{tag:X8} size={(File.Exists(path) ? new FileInfo(path).Length : 0)}");
        }
    }

    private static void Run(string file, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(file, arguments) { CreateNoWindow = true, UseShellExecute = false });
        p?.WaitForExit(30_000);
    }

    /// <summary>Opens Explorer on <paramref name="path"/>, optionally selects a file and opens its right-click menu, and saves a picture.</summary>
    private static void Shot(string path, string png, string? select = null, bool menu = false, bool moreOptions = false, bool details = false,
        Action? during = null)
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
        SetWindowPos(window, IntPtr.Zero, 0, 0, 1100, 700, 0x0040);
        SetForegroundWindow(window);
        Thread.Sleep(2500);
        if (details)
        {
            SendKeys.SendWait("^+6"); // Ctrl+Shift+6: the Details view, with the Status column
            Thread.Sleep(1500);
        }
        if (select is not null)
        {
            SendKeys.SendWait(select);
            Thread.Sleep(800);
        }
        if (menu)
        {
            SendKeys.SendWait("+{F10}"); // Shift+F10: the right-click menu
            Thread.Sleep(1500);
            if (moreOptions)
            {
                SendKeys.SendWait("w"); // "Show more options": the full menu, where Pairnets' items are
                Thread.Sleep(1500);
            }
        }
        during?.Invoke();
        ScreenShot(png, menu ? new Rectangle(0, 0, 1400, 900) : new Rectangle(0, 0, 1100, 700));
        if (menu)
            SendKeys.SendWait("{ESC}{ESC}");
        PostMessage(window, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        Thread.Sleep(800);
    }

    private static void ScreenShot(string png, Rectangle? area = null)
    {
        var bounds = area ?? Screen.PrimaryScreen!.Bounds;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(bitmap))
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        var path = Path.Combine(_output, png);
        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine("Saved " + path);
    }

    private static List<IntPtr> ExplorerWindows()
    {
        var found = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            var name = new StringBuilder(64);
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
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>Writes the cloud folder's log lines to the CI log.</summary>
    private sealed class ConsoleLog : ILoggerProvider, ILogger
    {
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Console.WriteLine($"  [{logLevel}] {formatter(state, exception)}{(exception is null ? "" : " " + exception.Message)}");
        public void Dispose()
        {
        }
    }
}
