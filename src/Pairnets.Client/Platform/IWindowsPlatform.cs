using Microsoft.Extensions.Logging;
using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Client.Platform;

/// <summary>
/// What the Windows app asks of Windows itself (the same idea as the Mac/Linux app's IPlatformServices). The app
/// runs with <see cref="WindowsPlatform"/>; the tests that press every button give it a stand-in that only writes
/// down what was asked, so no program starts and nothing changes in the registry or the Recycle Bin.
/// </summary>
public interface IWindowsPlatform
{
    /// <summary>Keeps this computer's key with DPAPI, for this Windows user only.</summary>
    ISecretProtector Secrets { get; }

    /// <summary>Where files deleted on another computer go (the Recycle Bin).</summary>
    ILocalTrash CreateTrash(ILogger log);

    bool IsAutoStartEnabled();

    void SetAutoStart(bool enabled);

    /// <summary>Opens a file, folder or web page with its default program.</summary>
    void Open(string path);

    /// <summary>Shows a file selected in Explorer.</summary>
    void Reveal(string path);

    /// <summary>Starts a downloaded installer (PairnetsSetup.exe) with these arguments.</summary>
    void RunInstaller(string path, string arguments);

    /// <summary>Starts the Pairnets.exe that just replaced this one (a copy from the zip updating itself).</summary>
    void StartUpdatedApp(string path, string arguments);

    /// <summary>
    /// Puts Pairnets in File Explorer for <paramref name="folder"/> (left side, right-click items, folder icon), or takes
    /// it away for null. False when part of it could not be done (it is tried again at the next start).
    /// </summary>
    /// <param name="leftSide">False while the folder is a Windows cloud folder (Windows shows it in the left side then).</param>
    /// <param name="rightClick">False: no right-click items.</param>
    /// <param name="folderIcon">False: the folder keeps its plain icon.</param>
    bool SetExplorerEntry(string? folder, bool leftSide = true, bool rightClick = true, bool folderIcon = true);

    /// <summary>
    /// What the person ticked on the installer's "Select Additional Tasks" page, left for the app by an installer run by
    /// hand (never by a silent update), with an id for that run; null when there is none.
    /// </summary>
    (string Id, string Choices)? ReadSetupChoices() => null;

    /// <summary>
    /// Makes the folder a Windows cloud folder in <paramref name="mode"/> (ticks in Explorer; Files On-Demand) and
    /// connects to it, or returns null with <paramref name="problem"/> saying why it stays a plain folder.
    /// </summary>
    /// <param name="placement">With Files On-Demand, the folders it is for ("Where your files live"); null: the whole folder.</param>
    CloudFiles.ICloudFolder? StartCloudFolder(string folder, CloudFolderMode mode, ILogger log, out string? problem,
        IReadOnlyDictionary<string, Pairnets.Core.Sync.FilePlacement>? placement = null);

    /// <summary>True when Windows still has a Pairnets cloud folder (also after the switch was turned off).</summary>
    bool HasCloudFolder();

    /// <summary>
    /// Makes every Pairnets cloud folder a plain folder again without reaching the nest ("Reset this app"). Returns how
    /// many files stay behind as placeholders (their content was only on the nest).
    /// </summary>
    int ForgetCloudFolders();
}
