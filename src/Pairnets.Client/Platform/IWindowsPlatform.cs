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
}
