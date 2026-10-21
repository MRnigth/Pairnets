using System.Net.Sockets;
using System.Runtime.InteropServices;
using Pairnets.Core.Api;

namespace Pairnets.Core.Client;

/// <summary>How this copy of Pairnets installs an update.</summary>
public enum UpdateMethod
{
    /// <summary>No download for this computer (32-bit, ARM Linux).</summary>
    None,

    /// <summary>Windows, put there by PairnetsSetup.exe: download the installer and run it silently.</summary>
    Installer,

    /// <summary>
    /// A copy no installer looks after (Pairnets.exe from the zip on Windows, the Linux app): download the package, swap
    /// the app's own file (<see cref="SelfReplace"/>) and start the new one.
    /// </summary>
    ReplaceApp,

    /// <summary>Mac: the person downloads the .dmg and replaces the app (an app bundle is not swapped yet).</summary>
    ByHand,
}

/// <summary>The release file this copy updates from, and how it installs it.</summary>
/// <param name="Asset">The release file, e.g. "PairnetsSetup.exe"; null for <see cref="UpdateMethod.None"/>.</param>
/// <param name="AppFile">For <see cref="UpdateMethod.ReplaceApp"/>: the app's file inside the package ("Pairnets.exe", "Pairnets").</param>
public sealed record UpdatePackage(UpdateMethod Method, string? Asset, string? AppFile = null)
{
    public const string WindowsInstaller = "PairnetsSetup.exe";
    public const string WindowsZip = "pairnets-client-win-x64.zip";
    public const string LinuxDesktop = "pairnets-desktop-linux-x64.tar.gz";

    /// <summary>
    /// How an installed copy runs PairnetsSetup.exe for an update: fully hidden (no wizard, no progress window). The
    /// installer is only seen on the first install; it starts the new Pairnets with <see cref="SelfReplace.AfterUpdateArgument"/>.
    /// </summary>
    public const string InstallerArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS";

    public static UpdatePackage None { get; } = new(UpdateMethod.None, null);

    /// <summary>
    /// The package for this computer. <paramref name="installedCopy"/>: the Windows app runs from the folder
    /// PairnetsSetup.exe put it in (only then can the installer replace it).
    /// </summary>
    public static UpdatePackage ForThisComputer(bool installedCopy)
    {
        if (OperatingSystem.IsWindows())
        {
            if (installedCopy)
                return new(UpdateMethod.Installer, WindowsInstaller);
            return new(UpdateMethod.ReplaceApp, WindowsZip, "Pairnets.exe"); // (the x64 app the zip holds is what this copy is)
        }
        if (OperatingSystem.IsMacOS())
            return new(UpdateMethod.ByHand, UpdateChecker.AssetForThisPlatform());
        if (OperatingSystem.IsLinux() && RuntimeInformation.OSArchitecture == Architecture.X64)
            return new(UpdateMethod.ReplaceApp, LinuxDesktop, "Pairnets");
        return None;
    }
}

/// <summary>The words both apps' update card uses, so the Windows and the Mac/Linux app say the same thing.</summary>
public static class UpdateTexts
{
    public const string UpdateNow = "Update now";
    public const string TryAgain = "Try again";
    public const string OpenDownloadPage = "Open download page";

    public static string Ready(string current) =>
        $"You have {current}. The update takes about 10 seconds and Pairnets restarts by itself. You stay signed in.";

    /// <summary>The note the new version shows once after updating itself (instead of opening its window).</summary>
    public const string UpdatedTitle = "Pairnets is up to date";

    public static string Updated(string version) =>
        $"Pairnets updated itself to {version}. You are still signed in and your settings are kept.";

    /// <summary>The Mac card: why Pairnets opens the download page instead of updating.</summary>
    public static string ByHand(string current) =>
        $"You have {current}. Pairnets can't update itself on a Mac yet: open the download page, get the new version " +
        "and drag it to Applications (your settings are kept).";

    /// <summary>Why the update was not installed, in plain words. Nothing was changed in any of these cases.</summary>
    public static string Failed(Exception ex, string? appPath = null) => ex switch
    {
        UpdateVerificationException { NotSigned: true } => ReleaseSigning.NotSignedMessage,
        UpdateVerificationException =>
            "The download didn't match its checksum, so Pairnets didn't install it and kept your current version.",
        UnauthorizedAccessException =>
            $"Pairnets isn't allowed to change its own folder{(appPath is null ? "" : $" ({Path.GetDirectoryName(appPath)})")}, " +
            "so it couldn't replace itself and kept your current version. Move Pairnets to a folder you can change, then try again.",
        InvalidDataException =>
            "The download wasn't a Pairnets package Pairnets could unpack, so nothing was installed.",
        HttpRequestException or SocketException or TaskCanceledException => "The download failed. " + FriendlyErrors.Describe(ex),
        IOException => "Pairnets couldn't replace its own file, so it kept your current version. " + FriendlyErrors.Describe(ex),
        _ => "The update failed, so Pairnets kept your current version. " + FriendlyErrors.Describe(ex),
    };
}
