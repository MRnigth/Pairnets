using System.Text.Json;

namespace Pairnets.Server.Storage;

/// <summary>
/// What a computer may do on the nest (friend invites, 1.0.61.2): <see cref="Owner"/> (the account that owns the nest;
/// every computer from before friends, and every way in that is not a computer's own key), <see cref="Edit"/> ("Can
/// change") or <see cref="View"/> ("View only"). <see cref="Folders"/> is null for the whole nest, or 1 to
/// <see cref="MaxFolders"/> top-level folder names: a path is shared when it is one of them or lies under one
/// (ordinal, exact case). An encrypted nest is only ever shared whole, because its paths are ids.
/// </summary>
public sealed record DeviceAccess(string Level, IReadOnlyList<string>? Folders = null)
{
    public const string Owner = "owner";
    public const string Edit = "edit";
    public const string View = "view";

    public const int MaxFolders = 20;
    public const int MaxFolderLength = 255;

    /// <summary>The owner's access: everything.</summary>
    public static readonly DeviceAccess Full = new(Owner);

    public bool IsOwner => Level == Owner;

    /// <summary>May upload, delete and restore (not "View only").</summary>
    public bool CanWrite => Level != View;

    /// <summary>Whether <paramref name="path"/> (nest-relative, "/"-separated, as the sync API uses it) is inside the share.</summary>
    public bool Allows(string? path)
    {
        if (Folders is null)
            return true;
        if (string.IsNullOrEmpty(path))
            return false;
        foreach (var folder in Folders)
        {
            if (string.Equals(path, folder, StringComparison.Ordinal)
                || (path.Length > folder.Length && path[folder.Length] == '/' && path.StartsWith(folder, StringComparison.Ordinal)))
                return true;
        }
        return false;
    }

    public static bool IsLevel(string? level) => level is Owner or Edit or View;

    /// <summary>A top-level folder name a share can name: the apps' and the account service's rule (<see cref="Pairnets.Core.Sync.NestAccess.IsFolderName"/>).</summary>
    public static bool IsFolderName(string? name) => Pairnets.Core.Sync.NestAccess.IsFolderName(name) && name!.Length <= MaxFolderLength;

    /// <summary>The Worker's account id of a person ("acc_…"): 1–64 letters, digits, "_" or "-".</summary>
    public static bool IsPersonId(string? id) =>
        id is { Length: > 0 and <= 64 } && id.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');

    /// <summary>
    /// Reads "access" and "folders" from a request body. A missing (or null) "access" is <see cref="Owner"/>, which only
    /// counts when <paramref name="ownerAllowed"/>; the owner never has folders. False for anything else that is not
    /// exactly right (a wrong level, folders that are not 1–20 valid names), so nothing half-understood is stored.
    /// </summary>
    public static bool TryRead(JsonElement body, bool ownerAllowed, out DeviceAccess access)
    {
        access = Full;
        if (body.ValueKind != JsonValueKind.Object)
            return false;
        var level = Owner;
        if (body.TryGetProperty("access", out var given) && given.ValueKind != JsonValueKind.Null)
        {
            if (given.ValueKind != JsonValueKind.String)
                return false;
            level = given.GetString()!;
        }
        IReadOnlyList<string>? folders = null;
        if (body.TryGetProperty("folders", out var list) && list.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadFolders(list, out var read))
                return false;
            folders = read;
        }
        if (!IsLevel(level) || (level == Owner && (!ownerAllowed || folders is not null)))
            return false;
        access = new DeviceAccess(level, folders);
        return true;
    }

    /// <summary>A JSON array of 1–20 folder names (duplicates dropped, order kept).</summary>
    public static bool TryReadFolders(JsonElement list, out IReadOnlyList<string> folders)
    {
        folders = [];
        if (list.ValueKind != JsonValueKind.Array)
            return false;
        var names = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } name || !IsFolderName(name))
                return false;
            if (!names.Contains(name, StringComparer.Ordinal))
                names.Add(name);
        }
        if (names.Count is 0 or > MaxFolders)
            return false;
        folders = names;
        return true;
    }

    /// <summary>The folders as auth.db keeps them (a JSON array), or null for the whole nest.</summary>
    public static string? FoldersToJson(IReadOnlyList<string>? folders) =>
        folders is null ? null : JsonSerializer.Serialize(folders);

    /// <summary>
    /// The folders from auth.db. A value that cannot be read shares nothing (an empty list) rather than everything:
    /// a damaged row must never widen what a friend sees.
    /// </summary>
    public static IReadOnlyList<string>? FoldersFromJson(string? json)
    {
        if (json is null)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return TryReadFolders(doc.RootElement, out var folders) ? folders : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
