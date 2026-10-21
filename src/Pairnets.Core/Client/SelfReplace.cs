using System.Formats.Tar;
using System.IO.Compression;

namespace Pairnets.Core.Client;

/// <summary>
/// Updates a copy of Pairnets that no installer looks after (Pairnets.exe from the zip, the Linux app) by swapping its own
/// file. The new app is unpacked next to it as "&lt;app&gt;.new"; the running file is renamed to "&lt;app&gt;.old" (Windows and
/// Linux both allow renaming a program while it runs); the new one takes its name. The app then starts the new file with
/// <see cref="AfterUpdateArgument"/> and quits; the new app waits for it to be gone and removes the .old file
/// (<see cref="CleanUp"/>). Anything that fails puts the old file back, so the app that runs is never half replaced.
/// The package itself has already been checked against the signed SHA256SUMS.txt (<see cref="UpdateChecker.DownloadVerifiedAsync"/>).
/// </summary>
public static class SelfReplace
{
    /// <summary>Given to the new app: wait for the old one to quit before taking over (only one Pairnets runs at a time).</summary>
    public const string AfterUpdateArgument = "--after-update";

    /// <summary>How long the new app waits for the old one to quit.</summary>
    public static readonly TimeSpan WaitForOldApp = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Takes <paramref name="appFile"/> out of <paramref name="package"/> (a .zip or .tar.gz) and puts it in place of
    /// <paramref name="appPath"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The package is not an archive with that file in it; nothing was changed.</exception>
    /// <exception cref="UnauthorizedAccessException">The app's folder cannot be changed; nothing was changed.</exception>
    /// <exception cref="IOException">A file could not be moved; the old app was put back.</exception>
    public static void Install(string package, string appFile, string appPath)
    {
        var fresh = appPath + ".new";
        var old = appPath + ".old";
        try
        {
            Unpack(package, appFile, fresh);
            if (File.Exists(old))
                File.Delete(old);
            File.Move(appPath, old);
            try
            {
                File.Move(fresh, appPath);
            }
            catch
            {
                File.Move(old, appPath);
                throw;
            }
        }
        finally
        {
            if (File.Exists(fresh))
                File.Delete(fresh);
        }
    }

    /// <summary>Removes what an update left next to <paramref name="appPath"/> (the replaced app). Never throws.</summary>
    public static void CleanUp(string? appPath)
    {
        if (string.IsNullOrEmpty(appPath))
            return;
        foreach (var leftover in new[] { appPath + ".old", appPath + ".new" })
        {
            try
            {
                if (File.Exists(leftover))
                    File.Delete(leftover);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use or locked: the next start tries again.
            }
        }
    }

    private static void Unpack(string package, string appFile, string destination)
    {
        try
        {
            if (package.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                UnpackZip(package, appFile, destination);
            else if (package.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                UnpackTarGz(package, appFile, destination);
            else
                throw new InvalidDataException($"Pairnets can't unpack {Path.GetFileName(package)}.");
        }
        catch (Exception ex) when (ex is FormatException or EndOfStreamException)
        {
            throw new InvalidDataException("The update package is damaged.", ex);
        }
    }

    private static void UnpackZip(string package, string appFile, string destination)
    {
        using var zip = ZipFile.OpenRead(package);
        var entry = zip.Entries.FirstOrDefault(e => e.Name == appFile)
            ?? throw new InvalidDataException($"The update package has no {appFile}.");
        entry.ExtractToFile(destination, overwrite: true);
    }

    private static void UnpackTarGz(string package, string appFile, string destination)
    {
        using var file = File.OpenRead(package);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || Path.GetFileName(entry.Name.TrimEnd('/')) != appFile || entry.DataStream is null)
                continue;
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                entry.DataStream.CopyTo(output);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            return;
        }
        throw new InvalidDataException($"The update package has no {appFile}.");
    }
}
