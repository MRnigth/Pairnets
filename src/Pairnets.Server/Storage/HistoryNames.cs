using System.Globalization;
using System.Text.RegularExpressions;

namespace Pairnets.Server.Storage;

/// <summary>History version ids: "yyyyMMddTHHmmssfffZ-hash8".</summary>
public static partial class HistoryNames
{
    private const string TimestampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

    [GeneratedRegex("^[0-9]{8}T[0-9]{9}Z-[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    public static string MakeId(DateTimeOffset storedAt, string hash8) =>
        storedAt.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture) + "-" + hash8;

    public static bool TryParse(string id, out DateTimeOffset storedAt, out string hash8)
    {
        storedAt = default;
        hash8 = string.Empty;
        if (!IsValidId(id))
            return false;
        if (!DateTime.TryParseExact(id[..19], TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            return false;
        storedAt = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
        hash8 = id[20..];
        return true;
    }
}
