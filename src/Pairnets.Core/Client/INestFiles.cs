using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>
/// What the Files page, "Help me clean up" and the Settings page's "Where your files live" ask of the nest's files
/// (<see cref="NestFiles"/>; the UI tests give the pages a stand-in).
/// </summary>
public interface INestFiles
{
    /// <summary>The nest can't be reached: the list is the last one seen.</summary>
    bool NestOffline { get; }

    /// <summary>The nest is (nearly) full: only opening works.</summary>
    bool NestFull { get; }

    IReadOnlyList<NestEntry> List(string folder);

    IReadOnlyList<string> Folders(int depth = 1);

    IReadOnlyList<NestEntry> Search(string text, int max = 200);

    long BytesHere(string folder);

    FilePlacement PlacementOf(string folder);

    void SetPlacement(string folder, FilePlacement placement);

    Task<NestResult> OpenAsync(string path, CancellationToken ct = default);

    Task<NestResult> SaveCopyAsync(string path, string destination, CancellationToken ct = default);

    Task<NestResult> AddAsync(IReadOnlyList<string> localFiles, string folder, CancellationToken ct = default);

    Task<NestResult> RenameAsync(string path, string newName, CancellationToken ct = default);

    Task<NestResult> MoveAsync(string path, string toFolder, CancellationToken ct = default);

    Task<NestResult> DeleteAsync(string path, CancellationToken ct = default);

    IReadOnlyList<(FileKind Kind, long Bytes)> KindTotals();

    IReadOnlyList<NestEntry> Biggest(FileKind? kind = null, int max = 50);

    long BytesOnNest();

    long? HistoryBytes { get; }

    Task<NestResult> DeleteForGoodAsync(string path, string typed, CancellationToken ct = default);
}
