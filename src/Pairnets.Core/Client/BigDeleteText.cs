using Pairnets.Core.State;
using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>What "Put them all back" did: files restored, files left alone (made again since), files it could not restore.</summary>
public sealed record BigDeleteRestore(int Restored, int Skipped, int Failed, string? Error);

/// <summary>The words of "Before big deletes" and of the big-delete notice, the same in both apps.</summary>
public static class BigDeleteText
{
    public const string Title = "Before big deletes (100+ files)";

    public const string Note = "One choice for every computer of this nest. An emptied folder always asks.";

    public const string OlderNest = "Update the nest to choose this for all computers.";

    public const string OwnerOnly = "The nest's owner chooses this.";

    /// <summary>The tick box on the delete question: the other computers follow what is decided here.</summary>
    public const string DontAskOthers = "Don't ask my other computers";

    /// <summary>
    /// The tick box for the delete question, or null when it would change nothing (the others already follow, or this
    /// computer can't change the nest's choice). Only for deletions made here: the others then follow without asking.
    /// </summary>
    public static string? TickFor(ClientSession session) =>
        session.CanChangeBigDeletes && session.BigDeletes == BigDeleteModes.AskEverywhere
        && session.PendingDeletes().Any(p => p.Side == DeleteSide.Remote) ? DontAskOthers : null;

    /// <summary>The choices, in the order the Settings list shows them.</summary>
    public static readonly IReadOnlyList<string> Choices = [BigDeleteModes.AskWhereDeleted, BigDeleteModes.AskEverywhere, BigDeleteModes.Never];

    public static string Choice(string mode) => mode switch
    {
        BigDeleteModes.AskWhereDeleted => "Ask on the computer where I deleted them",
        BigDeleteModes.Never => "Never ask",
        _ => "Ask on every computer",
    };

    public static string ChoiceDetail(string mode) => mode switch
    {
        BigDeleteModes.AskWhereDeleted => "The other computers follow without asking.",
        BigDeleteModes.Never => "Big deletes go through at once on every computer.",
        _ => "Each computer asks before it deletes its copies.",
    };

    /// <summary>The notice after a big delete went through.</summary>
    public static string Done(BigDeleteBatch batch) => batch.FromHere
        ? $"{batch.Count} files deleted"
        : $"{batch.Count} files deleted on another computer, and here too";

    /// <summary>Where this computer's copies went: the Recycle Bin on Windows, the Trash on a Mac or Linux.</summary>
    public static string Bin => OperatingSystem.IsWindows() ? "Recycle Bin" : "Trash";

    public static string Safe => $"They are in the {Bin}, and the nest keeps them 30 days.";

    public const string PutBackButton = "Put them all back";

    public const string CloseLabel = "Close";

    /// <summary>How many files make a big delete (the sync engine's <see cref="EngineOptions.MassDeleteThreshold"/>).</summary>
    public const int Threshold = 100;

    public static string ToastTitle(int count) => $"{count} files deleted";

    public const string ToastHint = "Open Pairnets to put them all back.";

    public static string PutBackQuestion(BigDeleteBatch batch) =>
        $"Put back the {batch.Count} files deleted {Format.Moment(batch.At, DateTimeOffset.Now)}?\n\n" +
        "They come back on the nest and on every computer. Files made again since are left as they are.";

    public static string PutBackResult(BigDeleteRestore done) => done.Error is { } error
        ? (done.Restored > 0 ? PutBack(done.Restored) + ". " : string.Empty) + error
        : done.Restored == 0 ? "Nothing to put back: these files are on the nest already."
        : PutBack(done.Restored) + ". They come back on every computer as they sync.";

    public static string PutBack(int restored) => restored == 1 ? "Put back 1 deleted file" : $"Put back {restored} deleted files";

    public static string NotAllBack(int failed) =>
        $"{failed} file{(failed == 1 ? " was" : "s were")} not put back (no copy left on the nest, or it could not be reached). Try again, or look in History.";
}
