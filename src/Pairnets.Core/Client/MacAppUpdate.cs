using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;

namespace Pairnets.Core.Client;

/// <summary>Runs the Mac's own tools (ditto, codesign, open...) without a shell; tests give it stand-ins.</summary>
public interface IMacTools
{
    /// <summary>Runs <paramref name="file"/> and waits. Exit code and output; -1 when it could not be started.</summary>
    (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args);

    /// <summary>Starts <paramref name="file"/> and does not wait for it (it outlives this app). Throws when it cannot start.</summary>
    void StartDetached(string file, IReadOnlyList<string> args);
}

/// <summary>The real tools.</summary>
public sealed class MacTools : IMacTools
{
    public (int ExitCode, string Output) Run(string file, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null)
                return (-1, string.Empty);
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120_000))
            {
                p.Kill();
                return (-1, "timed out");
            }
            return (p.ExitCode, output.Result + error.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, ex.Message);
        }
    }

    public void StartDetached(string file, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }
}

/// <summary>
/// The Mac app updates itself like the others: <c>Pairnets-macos-&lt;arch&gt;.zip</c> from the signed release (already checked
/// by <see cref="UpdateChecker.DownloadVerifiedAsync"/>) is unpacked into a hidden folder next to Pairnets.app and checked
/// (right app, right version, intact signature) while Pairnets keeps running. Then a small script
/// (deploy/macos/update-app.sh, carried inside the app) waits for this app to quit, swaps the two, opens the new one and
/// waits for it to report it runs. When it does not, the old app is put back and opened, with a note on what happened.
/// </summary>
public static class MacAppUpdate
{
    public const string BundleName = "Pairnets.app";
    public const string BundleId = "app.pairnets.client";
    private const string StagingFolder = ".Pairnets-update";
    private const string BackupFolder = ".Pairnets-previous";

    /// <summary>The zip for this Mac in the release (the .dmg stays for people who download by hand).</summary>
    public static string? ZipForThisMac() => UpdateChecker.AssetForThisPlatform()?.Replace(".dmg", ".zip", StringComparison.Ordinal);

    /// <summary>"/Applications/Pairnets.app" for ".../Pairnets.app/Contents/MacOS/Pairnets"; null when the program is not in an app.</summary>
    public static string? BundleOf(string? programPath)
    {
        if (string.IsNullOrEmpty(programPath))
            return null;
        var macOs = Path.GetDirectoryName(programPath);
        var contents = macOs is null ? null : Path.GetDirectoryName(macOs);
        var bundle = contents is null ? null : Path.GetDirectoryName(contents);
        return bundle is not null && Path.GetFileName(macOs) == "MacOS" && Path.GetFileName(contents) == "Contents"
            && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? bundle : null;
    }

    /// <summary>
    /// Why this copy can't replace itself (null when it can). macOS runs an app that was opened straight from a disk image
    /// or from Downloads from a hidden read-only copy ("App Translocation"), and a mounted image can't be written to.
    /// </summary>
    public static string? WhyNot(string? programPath)
    {
        var bundle = BundleOf(programPath);
        if (bundle is null)
            return "Pairnets isn't running as an app.";
        var unix = bundle.Replace('\\', '/');
        if (unix.Contains("/AppTranslocation/", StringComparison.Ordinal) || unix.StartsWith("/Volumes/", StringComparison.Ordinal))
            return UpdateTexts.MacNotInApplications;
        return null;
    }

    /// <summary>Where the hidden folders of an update live: next to the app, so the final move never crosses a disk.</summary>
    public static string StagingDir(string bundle) => Path.Combine(Path.GetDirectoryName(bundle)!, StagingFolder);

    public static string BackupDir(string bundle) => Path.Combine(Path.GetDirectoryName(bundle)!, BackupFolder);

    /// <summary>The file the new app writes as soon as it runs ("I started").</summary>
    public static string ReadyFile(string localDir) => Path.Combine(localDir, "mac-update.ready");

    /// <summary>The file the script writes when the update did not happen (the app shows what it says).</summary>
    public static string FailedFile(string localDir) => Path.Combine(localDir, "mac-update.failed");

    /// <summary>The script's step-by-step log (the folder "Report a bug" collects).</summary>
    public static string LogFile(string logsDir) => Path.Combine(logsDir, "mac-update.log");

    /// <summary>
    /// Unpacks <paramref name="zip"/> next to <paramref name="bundle"/> and checks the app in it. Returns its path. Anything
    /// wrong: the unpacked files are deleted, nothing else was touched.
    /// </summary>
    /// <exception cref="InvalidDataException">Not a Pairnets app, another app, the wrong version, or a damaged signature.</exception>
    /// <exception cref="UnauthorizedAccessException">The app's folder cannot be changed.</exception>
    public static string Stage(string zip, string bundle, string expectedVersion, IMacTools tools)
    {
        var staging = StagingDir(bundle);
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        try
        {
            if (tools.Run("/usr/bin/ditto", ["-x", "-k", zip, staging]) is not (0, _))
                throw new InvalidDataException("The update package is damaged.");
            var app = Path.Combine(staging, BundleName);
            var plist = Path.Combine(app, "Contents", "Info.plist");
            if (!File.Exists(plist) || !File.Exists(Path.Combine(app, "Contents", "MacOS", "Pairnets")))
                throw new InvalidDataException($"The update package has no {BundleName}.");
            var (id, version) = ReadPlist(plist);
            if (id != BundleId)
                throw new InvalidDataException("The update package is another app.");
            // (version.json carries at most four numbers; the app's own version may have a fifth: 1.0.61.10.1)
            if (version != expectedVersion && version?.StartsWith(expectedVersion + ".", StringComparison.Ordinal) != true)
                throw new InvalidDataException($"The update package is version {version}, not {expectedVersion}.");
            if (tools.Run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app]) is not (0, _))
                throw new InvalidDataException("The update's signature is damaged.");
            tools.Run("/usr/bin/xattr", ["-dr", "com.apple.quarantine", app]);
            return app;
        }
        catch
        {
            Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    /// <summary>Starts the swap script. The caller quits at once; the script waits for this process (<paramref name="pid"/>) to be gone.</summary>
    public static void StartSwap(string bundle, string stagedApp, string version, int pid, string localDir, string logsDir, IMacTools tools)
    {
        Directory.CreateDirectory(localDir);
        File.Delete(FailedFile(localDir));
        var script = Path.Combine(StagingDir(bundle), "update-app.sh");
        File.WriteAllText(script, ReadScript());
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        tools.StartDetached("/bin/bash", [script, pid.ToString(), bundle, stagedApp, BackupDir(bundle), ReadyFile(localDir), FailedFile(localDir), LogFile(logsDir), version]);
    }

    /// <summary>The new app says it runs (it was started with <see cref="SelfReplace.AfterUpdateArgument"/>): the script then deletes the backup.</summary>
    public static void SignalStarted(string localDir)
    {
        try
        {
            Directory.CreateDirectory(localDir);
            File.WriteAllText(ReadyFile(localDir), "running");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The script then waits out its time and puts the old version back: nothing worse than that.
        }
    }

    /// <summary>What the script says when the update did not happen, once; null when there is nothing to tell.</summary>
    public static string? TakeFailedNote(string localDir)
    {
        var file = FailedFile(localDir);
        try
        {
            if (!File.Exists(file))
                return null;
            var text = File.ReadAllText(file).Trim();
            File.Delete(file);
            return text.Length > 0 ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Removes the hidden folders an interrupted update left next to the app. Never throws.</summary>
    public static void CleanUp(string? programPath)
    {
        if (BundleOf(programPath) is not { } bundle)
            return;
        foreach (var dir in new[] { StagingDir(bundle), BackupDir(bundle) })
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The next start tries again.
            }
        }
    }

    internal static (string? Id, string? Version) ReadPlist(string plist)
    {
        try
        {
            var keys = XDocument.Load(plist).Descendants("dict").FirstOrDefault()?.Elements().ToList() ?? [];
            string? Value(string key)
            {
                var i = keys.FindIndex(e => e.Name == "key" && e.Value == key);
                return i >= 0 && i + 1 < keys.Count ? keys[i + 1].Value : null;
            }
            return (Value("CFBundleIdentifier"), Value("CFBundleShortVersionString"));
        }
        catch (System.Xml.XmlException)
        {
            throw new InvalidDataException("The update package is damaged.");
        }
    }

    private static string ReadScript()
    {
        using var stream = typeof(MacAppUpdate).Assembly.GetManifestResourceStream("Pairnets.update-app.sh")
            ?? throw new InvalidOperationException("The update script is missing from this build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
