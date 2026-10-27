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

    /// <summary>Mac: download Pairnets-macos-&lt;arch&gt;.zip, check the app in it and swap the whole Pairnets.app (<see cref="MacAppUpdate"/>).</summary>
    ReplaceBundle,

    /// <summary>Mac, running from a disk image or Downloads (<see cref="MacAppUpdate.WhyNot"/>): the person downloads the .dmg and drags the app to Applications.</summary>
    ByHand,
}

/// <summary>The release file this copy updates from, and how it installs it.</summary>
/// <param name="Asset">The release file, e.g. "PairnetsSetup.exe"; null for <see cref="UpdateMethod.None"/>.</param>
/// <param name="AppFile">For <see cref="UpdateMethod.ReplaceApp"/>: the app's file inside the package ("Pairnets.exe", "Pairnets").</param>
/// <param name="WhyByHand">For <see cref="UpdateMethod.ByHand"/>: what the card tells the person to do.</param>
public sealed record UpdatePackage(UpdateMethod Method, string? Asset, string? AppFile = null, string? WhyByHand = null)
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
    /// <param name="programPath">The running program (a Mac decides from where it lives whether it can swap itself); null is this process.</param>
    public static UpdatePackage ForThisComputer(bool installedCopy, string? programPath = null)
    {
        if (OperatingSystem.IsWindows())
        {
            if (installedCopy)
                return new(UpdateMethod.Installer, WindowsInstaller);
            return new(UpdateMethod.ReplaceApp, WindowsZip, "Pairnets.exe"); // (the x64 app the zip holds is what this copy is)
        }
        if (OperatingSystem.IsMacOS())
        {
            if (MacAppUpdate.WhyNot(programPath ?? Environment.ProcessPath) is { } why)
                return new(UpdateMethod.ByHand, UpdateChecker.AssetForThisPlatform(), WhyByHand: why);
            return new(UpdateMethod.ReplaceBundle, MacAppUpdate.ZipForThisMac());
        }
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

    /// <summary>The update card's title: "Pairnets 1.0.61.8 is available", or "1.0.61.8 Public beta 3 is ready" for a beta.</summary>
    public static string Available(UpdateInfo update) =>
        update.Release.IsBeta ? $"{update.Display} is ready" : $"Pairnets {update.Version} is available";

    /// <summary>
    /// The channel moved down and there is nothing newer on it yet (<see cref="UpdateService.Waiting"/>): the build running
    /// now stays. Pairnets never goes back to an older version by itself.
    /// </summary>
    public static string WaitingFor(string channel, string current) => ReleaseChannel.IsBeta(channel)
        ? $"Waiting for the next {ReleaseChannel.DisplayName(channel)}. You keep {current} until then."
        : $"Beta updates are off. You keep {current} until the next normal version is newer, then Pairnets moves onto it by itself. Nothing is removed.";

    /// <summary>A newer build is out but could not read this computer's data (<see cref="UpdateService.HeldBackForData"/>).</summary>
    public const string HeldBackForData =
        "A newer version is out, but it can't read this computer's data from the beta yet, so Pairnets waits for the next one.";

    /// <summary>The note the new version shows once after updating itself (next to its window, which opens again).</summary>
    public const string UpdatedTitle = "Pairnets is up to date";

    public static string Updated(string version) =>
        $"Pairnets updated itself to {version}. You are still signed in and your settings are kept.";

    /// <summary>Why a Mac can't swap the app it runs from (a disk image, Downloads).</summary>
    public const string MacNotInApplications =
        "Pairnets is running from the downloaded disk image or Downloads, where it can't replace itself.";

    /// <summary>The Mac card for a copy that can't update itself: <paramref name="why"/> and what to do (the button opens the download page).</summary>
    public static string ByHand(string current, string? why = null) =>
        $"You have {current}. {why ?? MacNotInApplications} Download the new version and drag it to Applications, " +
        "then open it from there: after that it updates by itself. Your settings are kept.";

    /// <summary>Said on the card when the Mac's new version did not start and the old one was put back.</summary>
    public static string MacPutBack(string version) =>
        $"Pairnets {version} didn't start, so your old version was put back. Nothing else changed.";

    /// <summary>Why the update was not installed, in plain words. Nothing was changed in any of these cases.</summary>
    public static string Failed(Exception ex, string? appPath = null) => ex switch
    {
        UpdateVerificationException { NotSigned: true } => ReleaseSigning.NotSignedMessage,
        UpdateVerificationException =>
            "The download didn't match its checksum, so Pairnets didn't install it and kept your current version.",
        UnauthorizedAccessException =>
            $"Pairnets isn't allowed to change its own folder{(appPath is null ? "" : $" ({Path.GetDirectoryName(appPath)})")}, " +
            "so it couldn't replace itself and kept your current version. Move Pairnets to a folder you can change, then try again.",
        InvalidDataException { Message: { Length: > 0 } m } when m.StartsWith("The update", StringComparison.Ordinal) =>
            m + " Nothing was installed.",
        InvalidDataException =>
            "The download wasn't a Pairnets package Pairnets could unpack, so nothing was installed.",
        HttpRequestException or SocketException or TaskCanceledException => "The download failed. " + FriendlyErrors.Describe(ex),
        IOException => "Pairnets couldn't replace its own file, so it kept your current version. " + FriendlyErrors.Describe(ex),
        _ => "The update failed, so Pairnets kept your current version. " + FriendlyErrors.Describe(ex),
    };
}
