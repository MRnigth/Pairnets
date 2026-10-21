using System.Security.Cryptography;
using Pairnets.Core.Client;

namespace Pairnets.Core.Settings;

/// <summary>
/// One more nest this computer syncs, each with its own folder (docs/DECISIONS.md, "Several nests at once"). The first
/// nest stays in the top-level fields of <see cref="ClientSettings"/>, so a settings file with one nest looks exactly as
/// before, and an older Pairnets that reads a file with several still syncs the first one.
/// </summary>
public sealed class NestProfile
{
    /// <summary>This app's own name for the nest ("n4f2a9c1e"), never shown; it keys the nest's saved key and notices.</summary>
    public string? Id { get; set; }

    public string? Label { get; set; }

    public string? ServerUrl { get; set; }

    public string? ProtectedToken { get; set; }

    public string? DeviceId { get; set; }

    public string? Folder { get; set; }

    public string? DeviceName { get; set; }

    public bool NameOfferAnswered { get; set; }

    public string? AccountEmail { get; set; }

    public List<string> ExtraIgnore { get; set; } = [];

    public bool AutoUpdateServer { get; set; }

    public string? SkippedServerVersion { get; set; }

    public NestProfile Clone()
    {
        var copy = (NestProfile)MemberwiseClone();
        copy.ExtraIgnore = [.. ExtraIgnore];
        return copy;
    }
}

public sealed partial class ClientSettings
{
    /// <summary>This app's own id for the first nest (see <see cref="NestProfile.Id"/>); set once the first nest exists.</summary>
    public string? NestId { get; set; }

    /// <summary>The first nest's name ("Home nest"), from the account sign-in; null until known.</summary>
    public string? NestLabel { get; set; }

    /// <summary>The nests after the first one, in the order they were added.</summary>
    public List<NestProfile> OtherNests { get; set; } = [];

    /// <summary>The first nest is set up (an address at least): a fresh install, or one signed out of its only nest, has none.</summary>
    public bool HasFirstNest => !string.IsNullOrWhiteSpace(ServerUrl);

    /// <summary>How many nests this computer syncs.</summary>
    public int NestCount => (HasFirstNest ? 1 : 0) + OtherNests.Count;

    /// <summary>Gives every nest its id (the first nest of a settings file from before several nests could be added has none).</summary>
    public void EnsureNestIds()
    {
        if (HasFirstNest && string.IsNullOrEmpty(NestId))
            NestId = NewNestId();
        foreach (var n in OtherNests.Where(n => string.IsNullOrEmpty(n.Id)))
            n.Id = NewNestId();
    }

    /// <summary>
    /// Every nest as settings of its own: the app-wide values (pause, limits, notices...) with that nest's address, key,
    /// folder and name. A session gets one of these; changes go back through <see cref="PutNest"/>.
    /// </summary>
    public IReadOnlyList<ClientSettings> Nests()
    {
        EnsureNestIds();
        var list = new List<ClientSettings>();
        if (HasFirstNest)
            list.Add(ForNest(NestId!)!);
        list.AddRange(OtherNests.Select(n => ForNest(n.Id!)!));
        return list;
    }

    /// <summary>The settings for one nest (see <see cref="Nests"/>), or null when there is no such nest.</summary>
    public ClientSettings? ForNest(string? id)
    {
        EnsureNestIds();
        var copy = Clone();
        copy.OtherNests = [];
        if (id is not null && id == NestId && HasFirstNest)
            return copy;
        if (OtherNests.FirstOrDefault(n => n.Id == id) is not { } profile)
            return null;
        Write(profile, copy);
        return copy;
    }

    /// <summary>
    /// Keeps a nest's own values (address, key, folder, name, account...) from <paramref name="nest"/>, matched by its
    /// <see cref="NestId"/>. App-wide values in it are not taken. False when the nest is not in the list.
    /// </summary>
    public bool PutNest(ClientSettings nest)
    {
        if (nest.NestId is null)
            return false;
        if (nest.NestId == NestId)
        {
            Write(Read(nest), this);
            return true;
        }
        var i = OtherNests.FindIndex(n => n.Id == nest.NestId);
        if (i < 0)
            return false;
        OtherNests[i] = Read(nest);
        return true;
    }

    /// <summary>
    /// Adds a nest just signed in to (its own values from <paramref name="nest"/>) and returns its id. With no nest yet it
    /// becomes the first nest.
    /// </summary>
    public string AddNest(ClientSettings nest)
    {
        var profile = Read(nest);
        if (string.IsNullOrEmpty(profile.Id) || profile.Id == NestId || OtherNests.Any(n => n.Id == profile.Id))
            profile.Id = NewNestId();
        if (!HasFirstNest)
            Write(profile, this);
        else
            OtherNests.Add(profile);
        return profile.Id;
    }

    /// <summary>
    /// Forgets a nest on this computer ("Leave this nest on this PC"). When the first nest goes, the next one moves up
    /// into its place; with no nest left the nest fields are empty and the app asks to sign in.
    /// </summary>
    public void RemoveNest(string id)
    {
        if (id == NestId && HasFirstNest)
        {
            if (OtherNests.Count > 0)
            {
                Write(OtherNests[0], this);
                OtherNests.RemoveAt(0);
            }
            else
            {
                Write(new NestProfile(), this);
            }
            return;
        }
        OtherNests.RemoveAll(n => n.Id == id);
    }

    /// <summary>Why <paramref name="folder"/> can't be <paramref name="nestId"/>'s folder (another nest's folder, or inside or around one), or null.</summary>
    public string? FolderProblem(string folder, string? nestId) =>
        NestFolders.Problem(folder, Nests().Where(n => n.NestId != nestId).Select(n => (Client.NestNames.Title(n, this), n.Folder)));

    private static NestProfile Read(ClientSettings s) => new()
    {
        Id = s.NestId,
        Label = s.NestLabel,
        ServerUrl = s.ServerUrl,
        ProtectedToken = s.ProtectedToken,
        DeviceId = s.DeviceId,
        Folder = s.Folder,
        DeviceName = s.DeviceName,
        NameOfferAnswered = s.NameOfferAnswered,
        AccountEmail = s.AccountEmail,
        ExtraIgnore = [.. s.ExtraIgnore],
        AutoUpdateServer = s.AutoUpdateServer,
        SkippedServerVersion = s.SkippedServerVersion,
    };

    private static void Write(NestProfile n, ClientSettings s)
    {
        s.NestId = n.Id;
        s.NestLabel = n.Label;
        s.ServerUrl = n.ServerUrl;
        s.ProtectedToken = n.ProtectedToken;
        s.DeviceId = n.DeviceId;
        s.Folder = n.Folder;
        s.DeviceName = n.DeviceName;
        s.NameOfferAnswered = n.NameOfferAnswered;
        s.AccountEmail = n.AccountEmail;
        s.ExtraIgnore = [.. n.ExtraIgnore];
        s.AutoUpdateServer = n.AutoUpdateServer;
        s.SkippedServerVersion = n.SkippedServerVersion;
    }

    /// <summary>
    /// Settings to sign in to one more nest with: the app-wide values, no nest yet, and the id the new nest will get
    /// (so its key can be saved in its own slot before it is added, <see cref="KeySlotFor"/>).
    /// </summary>
    public ClientSettings BlankNest()
    {
        var copy = Clone();
        copy.OtherNests = [];
        Write(new NestProfile { Id = HasFirstNest ? NewNestId() : NestId ?? NewNestId() }, copy);
        return copy;
    }

    /// <summary>
    /// Where a nest's key is saved outside the settings file (Mac, Linux): the first nest in the usual place (null), any
    /// other nest in a slot of its own named after its id, so two nests' keys never overwrite each other.
    /// </summary>
    public string? KeySlotFor(ClientSettings nest) =>
        !HasFirstNest || nest.NestId is null || nest.NestId == NestId ? null : nest.NestId;

    public static string NewNestId() => "n" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
}

/// <summary>The folder rules for several nests: each nest has its own folder, never inside another nest's or around one.</summary>
public static class NestFolders
{
    /// <summary>Why <paramref name="folder"/> can't be used next to the <paramref name="others"/> (name, folder), or null when it can.</summary>
    public static string? Problem(string folder, IEnumerable<(string Name, string? Folder)> others)
    {
        var mine = Normal(folder);
        foreach (var (name, other) in others)
        {
            if (string.IsNullOrWhiteSpace(other))
                continue;
            var theirs = Normal(other);
            if (Same(mine, theirs))
                return $"{name} already uses this folder. Each nest needs its own folder.";
            if (Inside(mine, theirs))
                return $"This folder is inside {name}'s folder. Pick one outside it, or files would sync into both nests.";
            if (Inside(theirs, mine))
                return $"{name}'s folder is inside this one. Pick a folder that doesn't hold another nest's folder.";
        }
        return null;
    }

    /// <summary>The suggested folder for a new nest: Pairnets\&lt;nest name&gt; in the person's home folder (a free name).</summary>
    public static string Suggested(string nestName, IEnumerable<string?> taken, string? home = null)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var clean = new string(nestName.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray()).Trim(' ', '.');
        if (clean.Length == 0)
            clean = "Nest";
        var used = taken.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => Normal(t!)).ToList();
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(home, "Pairnets", n == 1 ? clean : $"{clean} ({n})");
            var c = Normal(candidate);
            if (!used.Any(u => Same(u, c) || Inside(c, u) || Inside(u, c)))
                return candidate;
        }
    }

    private static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static StringComparison Cmp => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool Same(string a, string b) => string.Equals(a, b, Cmp);

    /// <summary><paramref name="inner"/> is somewhere below <paramref name="outer"/>.</summary>
    private static bool Inside(string inner, string outer)
    {
        var withSlash = Path.EndsInDirectorySeparator(outer) ? outer : outer + Path.DirectorySeparatorChar; // ("C:\" stays itself)
        return inner.Length > withSlash.Length && inner.StartsWith(withSlash, Cmp);
    }
}

/// <summary>
/// The saved-key markers of <see cref="ISecretProtector.Protect(string, string?)"/>: "keychain:v1" is the first nest's
/// key, "keychain:v1:k3f9a" another nest's (its slot after the colon).
/// </summary>
public static class SecretSlots
{
    /// <summary>A new slot name for another nest's key.</summary>
    public static string NewSlot() => "k" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    public static bool IsSlot(string? slot) => slot is { Length: >= 1 and <= 24 } && slot.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'));

    public static string Marker(string baseMarker, string? slot)
    {
        if (slot is not null && !IsSlot(slot))
            throw new ArgumentException("A key slot is letters and digits only.", nameof(slot));
        return slot is null ? baseMarker : baseMarker + ":" + slot;
    }

    /// <summary>True when <paramref name="text"/> is <paramref name="baseMarker"/>, with or without a slot; the slot is null for the first nest's key.</summary>
    public static bool TryRead(string baseMarker, string? text, out string? slot)
    {
        slot = null;
        if (text == baseMarker)
            return true;
        if (text is null || !text.StartsWith(baseMarker + ":", StringComparison.Ordinal))
            return false;
        var rest = text[(baseMarker.Length + 1)..];
        if (!IsSlot(rest))
            return false;
        slot = rest;
        return true;
    }
}
