using System.Text.Json;
using Pairnets.Core;

namespace Pairnets.Server.Storage;

/// <summary>One person waiting for the owner to let them in (sent by the Pairnets service; shown on the owner's People list).</summary>
/// <param name="At">When they asked, exactly as the service sent it (a time or a number).</param>
public sealed record FriendRequest(string Id, string? Name, string? Email, string Access, IReadOnlyList<string>? Folders, JsonElement? At);

/// <summary>
/// The people who asked to join this nest and wait for the owner's OK (PUT /api/relay/requests). The service owns the
/// list and sends all of it every time it changes; the nest keeps the latest in memory and in a small JSON file next
/// to auth.db, so the owner's apps can show it (GET /api/people) even while the service is away. At most
/// <see cref="Max"/>.
/// </summary>
public sealed class FriendRequests
{
    public const int Max = 20;

    public const string FileName = "friend-requests.json";

    private readonly string _file;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private IReadOnlyList<FriendRequest> _requests = [];

    public FriendRequests(AuthStore auth, ILogger<FriendRequests> log)
    {
        _file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(auth.DatabasePath))!, FileName);
        _log = log;
        Load();
    }

    public IReadOnlyList<FriendRequest> List()
    {
        lock (_gate)
            return _requests;
    }

    /// <summary>Keeps <paramref name="requests"/> as the whole list (already checked, see <see cref="TryRead"/>).</summary>
    public void Replace(IReadOnlyList<FriendRequest> requests)
    {
        lock (_gate)
        {
            _requests = requests;
            Write(JsonSerializer.Serialize(requests, PairnetsJson.Options));
        }
    }

    /// <summary>
    /// Reads the service's <c>{requests: [{id, name, email, access, folders, at}]}</c>. The error is "bad_request" for a
    /// body of the wrong shape or more than <see cref="Max"/> requests, "bad_access" for an access or folders that are not
    /// exactly right; null when it can be kept.
    /// </summary>
    public static string? TryRead(JsonElement body, out IReadOnlyList<FriendRequest> requests)
    {
        requests = [];
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("requests", out var items) || items.ValueKind != JsonValueKind.Array
            || items.GetArrayLength() > Max)
            return "bad_request";
        var list = new List<FriendRequest>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || Text(item, "id") is not { Length: > 0 and <= 64 } id || id.Any(char.IsControl))
                return "bad_request";
            if (!DeviceAccess.TryRead(item, ownerAllowed: false, out var access))
                return "bad_access";
            JsonElement? at = item.TryGetProperty("at", out var when) && when.ValueKind is JsonValueKind.String or JsonValueKind.Number
                && when.GetRawText().Length <= 64
                ? when.Clone()
                : null;
            list.Add(new FriendRequest(id, Clean(Text(item, "name"), AuthStore.MaxNameLength), Clean(Text(item, "email"), 200),
                access.Level, access.Folders, at));
        }
        requests = list;
        return null;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Clean(string? text, int max) => PlainText.Clean(text, max) is { Length: > 0 } clean ? clean : null;

    private void Load()
    {
        try
        {
            if (!File.Exists(_file))
                return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_file));
            // The file holds the list itself; read it back through the same checks as the service's call.
            using var wrapped = JsonDocument.Parse("{\"requests\":" + doc.RootElement.GetRawText() + "}");
            if (TryRead(wrapped.RootElement, out var requests) is null)
                _requests = requests;
            else
                _log.LogWarning("{File} could not be read; the list of people waiting starts empty", _file);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not read {File}: {Error}; the list of people waiting starts empty", _file, ex.Message);
        }
    }

    private void Write(string json)
    {
        try
        {
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Could not save {File}: {Error}", _file, ex.Message);
        }
    }
}
