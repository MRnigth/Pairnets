using Pairnets.Core.Settings;

namespace Pairnets.Core.Client;

/// <summary>The sign-in window's first words in both apps.</summary>
public static class SignInTexts
{
    /// <summary>
    /// The title and line under it on the first step. A computer that still knows its nest, folder and name but has no key
    /// (signed out, or the key could not be kept) is told it is signing in again, and to what: never a blank first run.
    /// </summary>
    public static (string Title, string Sub) First(ClientSettings current) =>
        SigningInAgain(current)
            ? ("Sign in again", $"This computer is signed out. Sign in to keep syncing {current.Folder} as {current.DeviceName}; your settings are kept.")
            : ("Sign in to Pairnets", "Keep one folder the same on all your computers.");

    public static bool SigningInAgain(ClientSettings current) =>
        !string.IsNullOrWhiteSpace(current.ServerUrl) && !string.IsNullOrWhiteSpace(current.Folder)
        && !string.IsNullOrWhiteSpace(current.DeviceName) && string.IsNullOrWhiteSpace(current.ProtectedToken);
}
