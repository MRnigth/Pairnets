using Microsoft.AspNetCore.SignalR;
using Pairnets.Core;
using Pairnets.Core.Api;
using Pairnets.Core.Sync;
using Pairnets.Server.Storage;
using Pairnets.Server.Web;

namespace Pairnets.Server.Services;

/// <summary>What one pass of a link did.</summary>
public sealed record LinkPassResult(LinkState State, int Pulled, int Pushed, string? Error = null);

/// <summary>
/// One pass of one link (docs/DECISIONS.md, "4 nests per account, and linked nests"). This nest acts like a computer of
/// the other nest:
/// <list type="bullet">
/// <item>It reads the other nest's changes since the last pass and writes them into the link's folder here, through
/// <see cref="SyncStore"/>, so history, conflicts and the live hub work as for any change.</item>
/// <item>With <see cref="LinkAccess.Edit"/> it then sends what this nest's computers changed in that folder.</item>
/// </list>
/// The hashes both sides had when last in step (<see cref="LinkFile"/>) tell a change on one side from a change on both.
/// When both changed, the other nest's version takes the name and this nest's is kept as a conflict copy. Without edit
/// access, local changes stay here and are overwritten (into history) only when the other nest changes the same file.
/// </summary>
public sealed class LinkSync(SyncStore store, LinkStore links, IHubContext<SyncHub> hub, DeviceRegistry devices, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>The name this nest's changes from a link carry in the apps' activity ("Oskar's Home nest").</summary>
    public static string DeviceName(NestLink link) => link.OwnerName + "'s " + link.NestName;

    public async Task<LinkPassResult> RunAsync(NestLink link, IPairnetsApi api, CancellationToken ct)
    {
        if (!link.IsActive)
            return new LinkPassResult(LinkState.Unlinked, 0, 0);
        if (store.Sealed)
            return Stop(link, LinkState.Problem, "This nest is encrypted, so it can't keep a linked folder.");
        int pulled, pushed;
        try
        {
            var manifest = await api.GetManifestAsync(link.RemoteServerId is null ? null : link.RemoteCursor, ct).ConfigureAwait(false);
            if (link.RemoteServerId is not null && (manifest.ServerId != link.RemoteServerId || manifest.Version < link.RemoteCursor))
            {
                // The other nest was set up again: compare everything from the start.
                links.Restart(link.Id);
                link = links.Get(link.Id)!;
                manifest = await api.GetManifestAsync(null, ct).ConfigureAwait(false);
            }
            var (done, settledAll) = await PullAsync(link, api, manifest, ct).ConfigureAwait(false);
            pulled = done;
            var localVersion = store.CurrentVersion;
            pushed = link.Access == LinkAccess.Edit ? await PushAsync(link, api, ct).ConfigureAwait(false) : 0;
            links.Record(link.Id, LinkState.InStep, null, manifest.ServerId, settledAll ? manifest.Version : null, localVersion);
            return new LinkPassResult(LinkState.InStep, pulled, pushed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PairnetsAuthException)
        {
            return Stop(link, LinkState.Problem, "The other nest no longer lets this nest in. It may have been unlinked there.");
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsSlowDownException or HttpRequestException or TimeoutException
                                   or OperationCanceledException or IOException)
        {
            return Stop(link, LinkState.Waiting, "Waiting for " + DeviceName(link) + ".");
        }
        catch (PairnetsProtocolException ex)
        {
            return Stop(link, LinkState.Problem, ex.Message);
        }
    }

    private LinkPassResult Stop(NestLink link, LinkState state, string error)
    {
        links.Record(link.Id, state, error);
        return new LinkPassResult(state, 0, 0, error);
    }

    /// <summary>The other nest's changes into this nest. Settled: every entry handled, so the cursor may move past them.</summary>
    private async Task<(int Done, bool Settled)> PullAsync(NestLink link, IPairnetsApi api, ManifestResponse manifest, CancellationToken ct)
    {
        var done = 0;
        var settled = true;
        foreach (var e in manifest.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!link.Shares(e.Path))
                continue;
            var localPath = link.LocalPath(e.Path);
            var local = store.Manifest.Get(localPath);
            var localHash = local is { Deleted: false } ? local.Hash : null;
            var seen = links.GetFile(link.Id, e.Path);
            if (e.Deleted)
            {
                if (localHash is null)
                    links.ForgetFile(link.Id, e.Path);
                else if (seen is not null && localHash == seen.LocalHash)
                {
                    var gone = await store.DeleteAsync(localPath, localHash).ConfigureAwait(false);
                    if (gone.Status is ChangeStatus.Ok or ChangeStatus.NotFound)
                    {
                        links.ForgetFile(link.Id, e.Path);
                        if (gone.Changed)
                            await ChangedAsync(link, localPath).ConfigureAwait(false);
                        done++;
                    }
                    else
                        settled = false;
                }
                else if (link.Access == LinkAccess.Edit)
                    links.SetFile(link.Id, e.Path, null, seen?.LocalHash); // changed here: sent again as a new file
                else
                    links.ForgetFile(link.Id, e.Path); // changed here: it stays here as this nest's own file
                continue;
            }
            if (localHash == e.Hash)
            {
                links.SetFile(link.Id, e.Path, e.Hash, e.Hash);
                continue;
            }
            if (seen is not null && seen.RemoteHash == e.Hash)
                continue; // nothing new over there; a change here is the push's
            var changedHere = localHash is not null && localHash != seen?.LocalHash;
            if (changedHere && link.Access == LinkAccess.Edit && !await KeepConflictCopyAsync(link, localPath, local!, ct).ConfigureAwait(false))
            {
                settled = false;
                continue;
            }
            if (await TakeAsync(link, api, e, localPath, localHash, ct).ConfigureAwait(false))
                done++;
            else
                settled = false;
        }
        return (done, settled);
    }

    /// <summary>Downloads one file of the other nest and writes it here if this nest's copy is still the one it saw.</summary>
    private async Task<bool> TakeAsync(NestLink link, IPairnetsApi api, ManifestEntry e, string localPath, string? localHash, CancellationToken ct)
    {
        var tmp = Path.Combine(store.Paths.Tmp, "link-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            await using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var got = await api.DownloadAsync(e.Path, file, null, ct).ConfigureAwait(false);
                if (!got.Found)
                    return true; // deleted over there meanwhile: the next pass sees the tombstone
                file.Position = 0;
                var put = await store.PutAsync(localPath, localHash ?? "none", got.ModifiedMs, file, ct).ConfigureAwait(false);
                if (put.Status is ChangeStatus.Ok or ChangeStatus.Unchanged)
                {
                    links.SetFile(link.Id, e.Path, got.Hash, put.Entry?.Hash ?? got.Hash);
                    if (put.Changed)
                        await ChangedAsync(link, localPath).ConfigureAwait(false);
                    return true;
                }
                return false; // a computer here changed it meanwhile, or the name can't be used here: tried again next pass
            }
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch (IOException)
            {
                // tmp/ is emptied at the next start.
            }
        }
    }

    /// <summary>Both nests changed a file: this nest's version is kept as "name (conflict NEST date).ext" before it is replaced.</summary>
    private async Task<bool> KeepConflictCopyAsync(NestLink link, string localPath, ManifestEntry local, CancellationToken ct)
    {
        var opened = await store.OpenReadAsync(localPath, ct).ConfigureAwait(false);
        if (opened is null)
            return true;
        await using (opened.Stream.ConfigureAwait(false))
        {
            var copy = ConflictNames.Make(localPath, "this nest", _clock.GetLocalNow().DateTime, p => store.Manifest.Get(p) is { Deleted: false });
            var put = await store.PutAsync(copy, "none", local.ModifiedMs, opened.Stream, ct).ConfigureAwait(false);
            if (!put.Changed)
                return false;
            await ChangedAsync(link, copy).ConfigureAwait(false);
            return true;
        }
    }

    /// <summary>What this nest's computers changed in the link's folder, sent to the other nest (edit access only).</summary>
    private async Task<int> PushAsync(NestLink link, IPairnetsApi api, CancellationToken ct)
    {
        var done = 0;
        var (entries, _) = store.ReadManifest(link.LocalCursor);
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (link.RemotePath(e.Path) is not { } remotePath || !link.Shares(remotePath))
                continue;
            var seen = links.GetFile(link.Id, remotePath);
            if (e.Deleted)
            {
                if (seen?.RemoteHash is not { } remoteHash)
                {
                    links.ForgetFile(link.Id, remotePath);
                    continue;
                }
                var gone = await api.DeleteAsync(remotePath, remoteHash, ct).ConfigureAwait(false);
                if (gone.Outcome is ApiOutcome.Ok or ApiOutcome.NotFound)
                {
                    links.ForgetFile(link.Id, remotePath);
                    done++;
                }
                continue; // a conflict: the other nest's change comes back with the next pull
            }
            if (seen is not null && seen.LocalHash == e.Hash && seen.RemoteHash is not null)
                continue; // the pull just wrote it, or it was sent already
            var opened = await store.OpenReadAsync(e.Path, ct).ConfigureAwait(false);
            if (opened is null)
                continue;
            await using (opened.Stream.ConfigureAwait(false))
            {
                var (result, sentHash, _) = await api.UploadAsync(remotePath, seen?.RemoteHash ?? "none", opened.Entry.ModifiedMs, opened.Stream, null, ct)
                    .ConfigureAwait(false);
                if (result.Outcome == ApiOutcome.Ok)
                {
                    links.SetFile(link.Id, remotePath, result.Entry?.Hash ?? sentHash, opened.Entry.Hash);
                    done++;
                }
            }
        }
        return done;
    }

    /// <summary>Like any computer's change: the Devices page's "last change" and the "Changed" message to every app.</summary>
    private async Task ChangedAsync(NestLink link, string path)
    {
        devices.Changed("link:" + link.Id);
        try
        {
            await hub.Clients.All.SendAsync(SyncHub.ChangedMethod, DeviceName(link), path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The apps also see it at their next pass.
        }
    }
}
