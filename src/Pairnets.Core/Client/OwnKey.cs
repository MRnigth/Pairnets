using Pairnets.Core.Api;
using Pairnets.Core.Settings;

namespace Pairnets.Core.Client;

/// <summary>What Settings does with this computer's own key when the form is saved.</summary>
public static class OwnKey
{
    /// <summary>
    /// Keeps the computer's own key unless a token was typed in (that switches back to the shared token), and
    /// sends a changed name to the nest first, so the nest's records (which win) agree. Returns the device id to
    /// keep (null on the shared token) and the name to save; the nest may have made it unique ("LAPTOP (2)").
    /// When the nest cannot be reached, the name stays as it was.
    /// </summary>
    public static async Task<(string? DeviceId, string Name)> CarryOverAsync(ClientSettings original, Uri serverUrl, string token,
        bool tokenTyped, string name, CancellationToken ct = default)
    {
        if (tokenTyped || !original.HasOwnKey)
            return (null, name);
        if (string.Equals(name, original.DeviceName, StringComparison.Ordinal))
            return (original.DeviceId, name);
        try
        {
            using var api = new PairnetsApiClient(serverUrl, token, name);
            return (original.DeviceId, (await api.RenameThisDeviceAsync(name, ct).ConfigureAwait(false)).Name);
        }
        catch (Exception ex) when (ex is PairnetsNetworkException or PairnetsAuthException or PairnetsProtocolException)
        {
            return (original.DeviceId, original.DeviceName ?? name);
        }
    }

    /// <summary>
    /// What to tell the person when a changed name did not reach the nest (<see cref="CarryOverAsync"/> kept the old
    /// one), or null when it did or nothing changed.
    /// </summary>
    public static string? NameNotSaved(ClientSettings original, string? deviceId, string typed, string saved) =>
        deviceId is not null && original.HasOwnKey && !string.Equals(typed, original.DeviceName, StringComparison.Ordinal)
            && string.Equals(saved, original.DeviceName, StringComparison.Ordinal)
            ? $"Your nest could not be reached, so this computer is still called {saved}. Your other settings were saved. Try the new name again when this computer is online."
            : null;

    /// <summary>
    /// The account email to keep when the Settings form is saved: the one this computer signed in with, as long as it
    /// keeps its own key (<paramref name="deviceId"/>) on the same server. A typed token or another address drops it.
    /// </summary>
    public static string? AccountEmailAfterSave(ClientSettings original, Uri serverUrl, string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId) || deviceId != original.DeviceId
            || !PairnetsApiClient.TryParseServerUrl(original.ServerUrl, out var before) || before is null)
            return null;
        var same = Uri.Compare(PairnetsApiClient.NormalizeBase(serverUrl), before, UriComponents.SchemeAndServer | UriComponents.Path,
            UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
        return same ? original.AccountEmail : null;
    }
}
