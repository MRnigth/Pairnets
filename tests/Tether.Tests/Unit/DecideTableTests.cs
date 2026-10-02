using Tether.Core.Sync;

namespace Tether.Tests.Unit;

/// <summary>
/// Exhaustive test of the three-hash decision. Every combination of L, S, B drawn from
/// {null, a, b, c} (64 rows) is reduced to its equality pattern and checked against the table.
/// </summary>
public class DecideTableTests
{
    // Pattern notation: one character per (L, S, B); '-' = null, letters = first-appearance labels.
    private static readonly Dictionary<string, SyncAction> Table = new()
    {
        ["---"] = SyncAction.None,          // nothing anywhere
        ["--a"] = SyncAction.ClearBase,     // deleted on both sides
        ["-a-"] = SyncAction.Download,      // new on server
        ["-aa"] = SyncAction.DeleteRemote,  // deleted here, untouched on server
        ["-ab"] = SyncAction.Download,      // deleted here but edited on server: edit wins
        ["a--"] = SyncAction.Upload,        // new here
        ["a-a"] = SyncAction.DeleteLocal,   // deleted on server, untouched here
        ["a-b"] = SyncAction.Upload,        // deleted on server but edited here: edit wins
        ["aa-"] = SyncAction.RecordBase,    // identical on first sync
        ["aaa"] = SyncAction.None,          // in sync
        ["aab"] = SyncAction.RecordBase,    // both made the same change
        ["ab-"] = SyncAction.Conflict,      // created on both sides with different content
        ["aba"] = SyncAction.Download,      // only server changed
        ["abb"] = SyncAction.Upload,        // only local changed
        ["abc"] = SyncAction.Conflict,      // both changed differently
    };

    public static IEnumerable<object?[]> AllCombinations()
    {
        string?[] values = [null, "a", "b", "c"];
        foreach (var l in values)
        foreach (var s in values)
        foreach (var b in values)
            yield return [l, s, b];
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void DecisionMatchesTable(string? l, string? s, string? b)
    {
        var pattern = Pattern(l, s, b);
        Assert.True(Table.TryGetValue(pattern, out var expected), $"pattern {pattern} missing from table");
        Assert.Equal(expected, SyncDecision.Decide(l, s, b));
    }

    [Fact]
    public void TableCoversExactlyTheReachablePatterns()
    {
        var patterns = AllCombinations().Select(c => Pattern((string?)c[0], (string?)c[1], (string?)c[2])).ToHashSet();
        Assert.Equal(64, AllCombinations().Count());
        Assert.Equal(15, patterns.Count);
        Assert.True(patterns.SetEquals(Table.Keys));
    }

    [Fact]
    public void NeverDeletesOrOverwritesUnsyncedLocalContent()
    {
        // Safety property: an action that removes or replaces the local file is only chosen
        // when the local content equals the agreed base (so it is recoverable from the server).
        foreach (var c in AllCombinations())
        {
            var (l, s, b) = ((string?)c[0], (string?)c[1], (string?)c[2]);
            var action = SyncDecision.Decide(l, s, b);
            if (action is SyncAction.DeleteLocal || (action is SyncAction.Download && l is not null))
                Assert.Equal(b, l);
            if (action is SyncAction.DeleteRemote || (action is SyncAction.Upload && s is not null))
                Assert.Equal(b, s);
        }
    }

    private static string Pattern(string? l, string? s, string? b)
    {
        var labels = new Dictionary<string, char>();
        char Label(string? v)
        {
            if (v is null)
                return '-';
            if (!labels.TryGetValue(v, out var c))
            {
                c = (char)('a' + labels.Count);
                labels[v] = c;
            }
            return c;
        }
        return new string([Label(l), Label(s), Label(b)]);
    }
}
