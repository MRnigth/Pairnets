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

    /// <summary>The nest kept the computer's old name: renaming it is on the Settings page ("This computer's name").</summary>
    public static string RenameFailed(string name) =>
        $"Your nest could not rename this computer, so it stays {name}. You can rename it later on the Settings page, under \"This computer's name\".";

    /// <summary>Under the email box when "Continue with email" can't go on: nothing typed, or not an email address.</summary>
    public static string EmailProblem(string? typed) => string.IsNullOrWhiteSpace(typed)
        ? "Type your email address first."
        : "That doesn't look like an email address. Check it and try again.";

    public static bool SigningInAgain(ClientSettings current) =>
        !string.IsNullOrWhiteSpace(current.ServerUrl) && !string.IsNullOrWhiteSpace(current.Folder)
        && !string.IsNullOrWhiteSpace(current.DeviceName) && string.IsNullOrWhiteSpace(current.ProtectedToken);
}
