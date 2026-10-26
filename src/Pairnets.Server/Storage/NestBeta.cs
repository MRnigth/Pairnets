using System.Text.Json;
using Pairnets.Core;

namespace Pairnets.Server.Storage;

/// <summary>
/// Beta updates on the nest (docs/DECISIONS.md, "Beta updates"). The Pairnets service tells the owner's nest whether the
/// account joined the beta program and which channels it may use (<c>PUT /api/relay/beta</c>); each of the owner's
/// computers keeps its choice here, made in its Settings or on the account page, whichever is newest. Only the owner's
/// own computers (with their own key) hear about it: a friend's nest can never put someone's computer on a beta. Kept as
/// two settings in the auth store, so the database itself does not change.
/// </summary>
public sealed class NestBeta(AuthStore store)
{
    public const string SettingProgram = "betaProgram";
    public const string SettingChoices = "betaChoices";
    public const string ByApp = "app";
    public const string ByWeb = "web";

    /// <summary>What the service last sent. <see cref="Rev"/> only goes up: an older message arriving late is ignored.</summary>
    public sealed record Program(bool Member, string? TermsVersion, IReadOnlyList<string> Channels, long Rev);

    public Program Current
    {
        get
        {
            try
            {
                return store.GetSetting(SettingProgram) is { Length: > 0 } json
                    ? JsonSerializer.Deserialize<Program>(json, PairnetsJson.Options) ?? None
                    : None;
            }
            catch (JsonException)
            {
                return None;
            }
        }
    }

    private static readonly Program None = new(false, null, [], 0);

    /// <summary>Takes the service's message unless a newer one came first; returns the program now kept.</summary>
    public Program Set(bool member, string? termsVersion, IEnumerable<string>? channels, long rev)
    {
        var current = Current;
        if (rev < current.Rev)
            return current;
        var allowed = member ? (channels ?? []).Where(ReleaseChannel.IsBeta).Distinct(StringComparer.Ordinal).ToList() : [];
        var next = new Program(member, member ? termsVersion : null, allowed, rev);
        store.SetSetting(SettingProgram, JsonSerializer.Serialize(next, PairnetsJson.Options));
        return next;
    }

    /// <summary>One computer's choice, or null when it never chose.</summary>
    public BetaChoice? ChoiceOf(string deviceId) => Choices().GetValueOrDefault(deviceId);

    /// <summary>
    /// Keeps <paramref name="channel"/> for the computer when it is newer than what is kept; returns the choice kept either
    /// way. "off" is always allowed; a beta only while the program allows it (<see cref="Allows"/>).
    /// </summary>
    public BetaChoice Choose(string deviceId, string channel, DateTimeOffset at, string by)
    {
        var choices = Choices();
        if (choices.TryGetValue(deviceId, out var kept) && kept.At >= at)
            return kept;
        var choice = new BetaChoice(ReleaseChannel.WireName(ReleaseChannel.Normalize(channel)), at, by);
        choices[deviceId] = choice;
        store.SetSetting(SettingChoices, JsonSerializer.Serialize(choices, PairnetsJson.Options));
        return choice;
    }

    /// <summary>True when the computer may choose <paramref name="channel"/> now.</summary>
    public bool Allows(string channel) =>
        channel == ReleaseChannel.Off || channel == ReleaseChannel.Stable || Current is { Member: true } p && p.Channels.Contains(channel);

    /// <summary>What a computer hears in <c>GET /api/me</c>; null on a nest the service does not reach (not linked).</summary>
    public BetaInfo For(PairedDevice? device, bool ownKey, bool linked)
    {
        if (!linked)
            return new BetaInfo(false, Reason: BetaInfo.ReasonNotLinked);
        if (!ownKey || device is null)
            return new BetaInfo(false, Reason: BetaInfo.ReasonSharedPassword);
        if (device.PersonId is not null)
            return new BetaInfo(false, Reason: BetaInfo.ReasonFriend);
        var program = Current;
        return new BetaInfo(program.Member, program.TermsVersion, program.Channels, ChoiceOf(device.Id));
    }

    private Dictionary<string, BetaChoice> Choices()
    {
        try
        {
            return store.GetSetting(SettingChoices) is { Length: > 0 } json
                ? JsonSerializer.Deserialize<Dictionary<string, BetaChoice>>(json, PairnetsJson.Options) ?? new(StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }
}
