using Pairnets.Core.Sync;

namespace Pairnets.Core.Client;

/// <summary>
/// The words both apps use for where a nest is saved on this computer: the add window's folder step, the Settings folder
/// row and the question before the folder changes. The folder is this computer's own choice: it is never sent to the nest,
/// so a friend's folder on their computer (and your other computers' folders) stay as they are.
/// </summary>
public static class FolderChoiceText
{
    /// <summary>The add window's folder step heading.</summary>
    public const string StepTitle = "Choose where it's saved";

    /// <summary>The line under the folder step heading.</summary>
    public const string StepLead = "Pick a folder on this computer. Files already in it are kept and merged, never replaced.";

    /// <summary>The main button of the question before the folder changes.</summary>
    public const string SaveHere = "Save it here";

    /// <summary>The other button of that question.</summary>
    public const string Cancel = "Cancel";

    /// <summary>"Where Oskar's Photos is saved on this computer".</summary>
    public static string FieldLabel(string? nest) => $"Where {Name(nest)} is saved on this computer";

    /// <summary>
    /// "Only on this computer. Oskar's folder on their computer stays where it is." on a friend's nest; on your own,
    /// "Only on this computer. Your other computers keep their own folders."
    /// </summary>
    public static string OnlyHere(NestAccess access) =>
        access.IsShared
            ? $"Only on this computer. {OwnerPossessive(access)} folder on their computer stays where it is."
            : "Only on this computer. Your other computers keep their own folders.";

    /// <summary>"Save Oskar's Photos in D:\Photos instead?": the question's heading.</summary>
    public static string ChangeTitle(string? nest, string newFolder) => $"Save {Name(nest)} in {Trim(newFolder)} instead?";

    /// <summary>What happens when the folder changes: the new folder fills from the nest, the old one is kept, nothing else changes.</summary>
    public static IReadOnlyList<string> ChangePoints(NestAccess access, string? oldFolder) =>
    [
        "Pairnets fills the new folder from the nest. Files already in it are kept and merged.",
        string.IsNullOrWhiteSpace(oldFolder)
            ? "Nothing is deleted, here or on the nest."
            : $"Your old folder {Trim(oldFolder)} stays as it is, but no longer syncs. Delete it yourself when you're done.",
        access.IsShared
            ? $"{OwnerPossessive(access)} folder on their computer doesn't change."
            : "Your other computers keep their own folders.",
    ];

    /// <summary>The question as one text, for a pop-up without a list of points.</summary>
    public static string ChangeText(NestAccess access, string? oldFolder) => string.Join("\n\n", ChangePoints(access, oldFolder));

    /// <summary>True when <paramref name="next"/> is another folder than <paramref name="now"/> (an empty one means none was set yet).</summary>
    public static bool IsChange(string? now, string next) =>
        !string.IsNullOrWhiteSpace(now)
        && !string.Equals(Trim(now), Trim(next), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>"Saved in D:\Pairnets\Photos": the Files page's line, with "Change…" beside it.</summary>
    public static string SavedIn(string folder) => "Saved in " + Trim(folder);

    /// <summary>"Oskar's", or "The owner's" while the name is not known.</summary>
    private static string OwnerPossessive(NestAccess access) =>
        access.SharedBy is { Length: > 0 } who ? ShareText.Possessive(who) : "The owner's";

    private static string Name(string? nest) => string.IsNullOrWhiteSpace(nest) ? "this nest" : nest;

    private static string Trim(string folder)
    {
        var trimmed = folder.Trim();
        return trimmed.Length > 1 ? Path.TrimEndingDirectorySeparator(trimmed) : trimmed;
    }
}
