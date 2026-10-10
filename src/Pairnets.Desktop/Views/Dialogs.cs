using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace Pairnets.Desktop.Views;

/// <summary>Minimal message and yes/no dialogs (Avalonia has no built-in message box), and the folder picker.</summary>
public static class Dialogs
{
    public static Task InfoAsync(Window? owner, string title, string text) => ShowAsync(owner, title, text, ["OK"]);

    public static async Task<bool> ConfirmAsync(Window? owner, string title, string text, string yes = "Yes", string no = "No") =>
        await ShowAsync(owner, title, text, [yes, no]) == yes;

    /// <summary>Asks for a folder; null when none was chosen.</summary>
    public static Task<string?> PickFolderAsync(Window? owner, string title) => FolderPicker(owner, title);

    /// <summary>
    /// The folder picker behind <see cref="PickFolderAsync"/>: the system's own. The system picker does nothing
    /// without a screen, so the tests that press every button answer it themselves.
    /// </summary>
    internal static Func<Window?, string, Task<string?>> FolderPicker { get; set; } = SystemFolderPickerAsync;

    private static async Task<string?> SystemFolderPickerAsync(Window? owner, string title)
    {
        if (owner is null)
            return null;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    private static async Task<string?> ShowAsync(Window? owner, string title, string text, string[] buttons)
    {
        string? result = null;
        var window = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        foreach (var label in buttons)
        {
            var b = new Button { Content = label, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Click += (_, _) =>
            {
                result = label;
                window.Close();
            };
            row.Children.Add(b);
        }
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new SelectableTextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                row,
            },
        };
        if (owner is { IsVisible: true })
            await window.ShowDialog(owner);
        else
        {
            var done = new TaskCompletionSource();
            window.Closed += (_, _) => done.TrySetResult();
            window.Show();
            await done.Task;
        }
        return result;
    }
}
