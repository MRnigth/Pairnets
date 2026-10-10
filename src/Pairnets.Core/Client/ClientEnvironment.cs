using Pairnets.Core.Settings;

namespace Pairnets.Core.Client;

/// <summary>
/// Where an app keeps its files and looks for its updates. The apps run with <see cref="Default"/>; the tests
/// that press every button give each run its own folders, pipe and update feed, so they never touch the real
/// settings, saved sign-in or logs of the computer they run on.
/// </summary>
public sealed record ClientEnvironment
{
    /// <summary>The settings file (%AppData%\Pairnets\settings.json).</summary>
    public required string SettingsPath { get; init; }

    /// <summary>The folder for sync notes and logs (%LocalAppData%\Pairnets).</summary>
    public required string LocalDir { get; init; }

    public string LogsDir => Path.Combine(LocalDir, "logs");

    /// <summary>The "come to the front" pipe; null is the normal one for this user.</summary>
    public string? ActivationPipeName { get; init; }

    /// <summary>Where version.json, SHA256SUMS.txt and the installers are downloaded from.</summary>
    public Uri UpdateDownloadBase { get; init; } = UpdateChecker.DefaultDownloadBase;

    /// <summary>The release page the app opens when it cannot install an update itself.</summary>
    public Uri UpdateReleasePage { get; init; } = UpdateChecker.DefaultReleasePage;

    /// <summary>The folder the installer puts the app in; null means "where the installer normally puts it".</summary>
    public string? InstallDir { get; init; }

    /// <summary>False in tests: no icon appears in the real notification area or menu bar.</summary>
    public bool ShowTrayIcon { get; init; } = true;

    /// <summary>The real places, as the apps have always used them.</summary>
    public static ClientEnvironment Default => new() { SettingsPath = SettingsStore.DefaultPath, LocalDir = PairnetsPaths.LocalDir };

    /// <summary>Everything under <paramref name="root"/>, with no tray icon and a pipe of its own.</summary>
    public static ClientEnvironment Isolated(string root, Uri? updateDownloadBase = null) => new()
    {
        SettingsPath = Path.Combine(root, "roaming", "settings.json"),
        LocalDir = Path.Combine(root, "local"),
        ActivationPipeName = "pairnets-t" + Guid.NewGuid().ToString("N")[..12],
        UpdateDownloadBase = updateDownloadBase ?? UpdateChecker.DefaultDownloadBase,
        InstallDir = Path.Combine(root, "install"),
        ShowTrayIcon = false,
    };

    /// <summary>The update checker for this environment.</summary>
    public UpdateChecker CreateUpdateChecker(HttpClient? http = null) => new(http ?? new HttpClient(), UpdateDownloadBase, UpdateReleasePage);
}
