namespace Pairnets.Server.Storage;

/// <summary>
/// Restores of history versions (POST /api/history/restore) run in the background. A big version takes as long as
/// copying and hashing it, which can be longer than a proxy waits for an answer (Cloudflare gives up after 100 s).
/// A request waits for its restore at most <see cref="AnswerWithin"/>; if it is not done by then, the answer is
/// 202 and the app asks again with the same ticket, which joins the same restore instead of starting another.
/// A finished restore is remembered for a few minutes under its ticket, so that question gets the result.
/// Requests without a ticket (apps from before) start their own restore, as they always did.
/// </summary>
public sealed class RestoreJobs(SyncStore store, TimeProvider? clock = null)
{
    private static readonly TimeSpan KeepResult = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);

    /// <summary>How long one request waits for its restore before answering "still restoring".</summary>
    public TimeSpan AnswerWithin { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The restore's result, or null while it is still running (ask again with the same ticket).</summary>
    public async Task<ChangeResult?> RunAsync(string path, string id, string? ticket, CancellationToken ct)
    {
        Job job;
        lock (_gate)
        {
            Prune();
            var key = IsTicket(ticket) ? $"{ticket}\n{path}\n{id}" : Guid.NewGuid().ToString("N");
            if (!_jobs.TryGetValue(key, out job!))
            {
                // Runs to the end even if the app stops waiting: the person asked for it.
                job = new Job(Task.Run(() => store.RestoreAsync(path, id, CancellationToken.None)));
                _jobs[key] = job;
                _ = job.Task.ContinueWith(_ => job.FinishedAt = _clock.GetUtcNow(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        if (!job.Task.IsCompleted)
        {
            try
            {
                await Task.WhenAny(job.Task, Task.Delay(AnswerWithin, _clock, ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The app went away; the restore goes on.
            }
        }
        return job.Task.IsCompleted ? await job.Task.ConfigureAwait(false) : null;
    }

    private void Prune()
    {
        var cutoff = _clock.GetUtcNow() - KeepResult;
        foreach (var (key, job) in _jobs.ToList())
        {
            if (job.FinishedAt is { } finished && finished < cutoff)
                _jobs.Remove(key);
        }
    }

    private static bool IsTicket(string? ticket) =>
        ticket is { Length: >= 16 and <= 64 } && ticket.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed class Job(Task<ChangeResult> task)
    {
        public Task<ChangeResult> Task { get; } = task;
        public DateTimeOffset? FinishedAt { get; set; }
    }
}
