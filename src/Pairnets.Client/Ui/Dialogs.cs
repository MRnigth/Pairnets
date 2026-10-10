using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace Pairnets.Client.Ui;

/// <summary>
/// The app's message boxes, folder picker and clipboard, in one place. The tests that press every button set
/// <see cref="Responder"/>: it answers instead of a person, so no dialog opens and the real clipboard is left alone.
/// </summary>
public static class Dialogs
{
    /// <summary>A question Pairnets asks in a message box.</summary>
    internal sealed record Question(string Text, string Title, MessageBoxButton Buttons, MessageBoxImage Image);

    /// <summary>Stands in for the person in tests.</summary>
    internal interface IResponder
    {
        MessageBoxResult Answer(Question question);

        /// <summary>The folder "chosen" in the folder picker, or null for Cancel.</summary>
        string? PickFolder(string title, string? initialDirectory);

        /// <summary>False acts like another program holding the clipboard.</summary>
        bool Copy(string text);
    }

    /// <summary>Null in the app: real dialogs and the real clipboard.</summary>
    internal static IResponder? Responder { get; set; }

    /// <summary>Shows a message box (in front of <paramref name="owner"/> when given) and returns the button pressed.</summary>
    public static MessageBoxResult Ask(Window? owner, string text, string title = "Pairnets", MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        if (Responder is { } responder)
            return responder.Answer(new Question(text, title, buttons, image));
        return owner is not null
            ? MessageBox.Show(owner, text, title, buttons, image, defaultResult)
            : MessageBox.Show(text, title, buttons, image, defaultResult);
    }

    /// <summary>Lets the person choose a folder; null when they cancel.</summary>
    public static string? PickFolder(Window? owner, string title, string? initialDirectory = null)
    {
        if (Responder is { } responder)
            return responder.PickFolder(title, initialDirectory);
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (initialDirectory is not null && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;
        return (owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FolderName : null;
    }

    /// <summary>Puts text on the clipboard. False when another program holds it; the button can be pressed again.</summary>
    public static bool CopyText(string text)
    {
        if (Responder is { } responder)
            return responder.Copy(text);
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }
}
