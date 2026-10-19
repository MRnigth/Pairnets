using Pairnets.Core.Settings;
using Pairnets.Core.Sync;

namespace Pairnets.Client.Platform.CloudFiles;

/// <summary>
/// The Pairnets folder as a Windows cloud folder, as the app drives it (<see cref="WindowsCloudFolder"/> in the app; a
/// stand-in in the tests that press every button, which never touch Windows' cloud files).
/// </summary>
public interface ICloudFolder : ICloudFiles, IDisposable
{
    CloudFolderMode Mode { get; }

    /// <summary>Downloads one version of a file when it is opened: (sync path, version, where to, bytes so far, stop).</summary>
    Func<string, string, Stream, Action<long>, CancellationToken, Task>? Fetch { get; set; }

    /// <summary>Brings every file's mark in Explorer in line with the notes (a file's synced version by sync path, or null).</summary>
    void Reconcile(Func<string, string?> syncedHash);

    /// <summary>Downloads what is still on the nest, then makes the folder a plain folder again; null when done, else why not.</summary>
    Task<string?> LeaveAsync(CancellationToken ct);
}
