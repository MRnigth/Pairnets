using System.Collections.Immutable;

namespace Pairnets.Core.Client;

/// <summary>
/// The uploads and downloads of the current sync and where each one is (<see cref="StatusSnapshot.Batch"/>):
/// waiting once planned, moving while it transfers, done once synced. Kept in run order; back-to-back passes
/// add to it, like the burst's file count. Not thread-safe: <see cref="ClientSession"/> calls it under its lock.
/// </summary>
internal sealed class SyncBatch(int cap = SyncBatch.DefaultCap)
{
    /// <summary>At most this many files are shown (a huge first sync can have tens of thousands).</summary>
    public const int DefaultCap = 5000;

    private readonly ImmutableList<BatchFile>.Builder _files = ImmutableList.CreateBuilder<BatchFile>();
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly HashSet<int> _moving = [];

    /// <summary>The first file that is not done yet (<see cref="Count"/> when all are).</summary>
    private int _front;

    private IReadOnlyList<BatchFile>? _view;

    public int Count => _files.Count;

    /// <summary>A new burst: forgets every file.</summary>
    public void Clear()
    {
        _files.Clear();
        _index.Clear();
        _moving.Clear();
        _front = 0;
        _view = null;
    }

    /// <summary>The next pass of the same burst starts: a file still shown as moving waits again.</summary>
    public void StopMoving()
    {
        foreach (var i in _moving.ToList())
            Set(_files[i].Path, _files[i].Operation, BatchFileState.Waiting, null);
    }

    /// <summary>A pass planned these files (in run order): each waits its turn, one synced earlier in the burst too.</summary>
    public void Plan(IEnumerable<(string Path, string Operation)> files)
    {
        foreach (var (path, operation) in files)
            Set(path, operation, BatchFileState.Waiting, null);
    }

    /// <summary>A file is moving now (a file the pass did not plan, like a conflict's copy, is added).</summary>
    public void Moving(string path, string operation, int? percent) => Set(path, operation, BatchFileState.Moving, percent);

    public void Done(string path, string operation) => Set(path, operation, BatchFileState.Done, null);

    /// <summary>A transfer stopped without the file being synced (skipped, failed, paused): it waits for the next pass.</summary>
    public void Stopped(string path)
    {
        if (_index.TryGetValue(path, out var i) && _files[i].IsMoving)
            Set(path, _files[i].Operation, BatchFileState.Waiting, null);
    }

    /// <summary>
    /// The files to show, in run order. Past the cap: a window starting a little before the first unfinished
    /// file, and every file moving now even when it lies beyond that window.
    /// </summary>
    public IReadOnlyList<BatchFile> View => _view ??= BuildView();

    private IReadOnlyList<BatchFile> BuildView()
    {
        var all = _files.ToImmutable();
        if (all.Count <= cap)
            return all;
        var start = Math.Clamp(_front - cap / 10, 0, all.Count - cap);
        var end = start + cap;
        while (end > start && _moving.Count(i => i >= end) > start + cap - end)
            end--; // make room for the files moving past the window
        var view = new List<BatchFile>(cap);
        view.AddRange(all.GetRange(start, end - start));
        view.AddRange(_moving.Where(i => i >= end).Order().Select(i => all[i]));
        return view;
    }

    private void Set(string path, string operation, BatchFileState state, int? percent)
    {
        var file = new BatchFile(path, operation, state, state == BatchFileState.Moving ? percent : null);
        if (_index.TryGetValue(path, out var i))
        {
            if (_files[i] == file)
                return;
            _files[i] = file;
        }
        else
        {
            i = _files.Count;
            _index[path] = i;
            _files.Add(file);
        }
        if (state == BatchFileState.Moving)
            _moving.Add(i);
        else
            _moving.Remove(i);
        if (state != BatchFileState.Done)
            _front = Math.Min(_front, i);
        while (_front < _files.Count && _files[_front].IsDone)
            _front++;
        _view = null;
    }
}
