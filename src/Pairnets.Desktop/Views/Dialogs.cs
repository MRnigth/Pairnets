using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Pairnets.Core.Client;
using Pairnets.Core.Sync;

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

    // ------------------------------------------------------------------ the Files page's pop-ups

    /// <summary>
    /// A pop-up with a title, a text, points under it and buttons of its own (the last is the main one). Returns the
    /// button pressed, or null when it was closed.
    /// </summary>
    public static Task<string?> ChooseAsync(Window? owner, string title, string text, IReadOnlyList<string> points, params string[] buttons)
    {
        var body = Body(title, text, points);
        return ShowPopupAsync(owner, title, body, buttons.Select((b, i) => (b, i == buttons.Length - 1, (Func<string?>)(() => b))).ToArray());
    }

    /// <summary>Asks for one line of text (a new name); null for Cancel.</summary>
    public static Task<string?> PromptAsync(Window? owner, string title, string text, string initial, string ok)
    {
        var body = Body(title, text, []);
        var box = new TextBox { Text = initial, Classes = { "field" }, Height = 38, MinHeight = 38, CornerRadius = new CornerRadius(11) };
        Avalonia.Automation.AutomationProperties.SetName(box, title);
        box.AttachedToVisualTree += (_, _) => box.Focus();
        body.Children.Add(box);
        return ShowPopupAsync(owner, title, body, [("Cancel", false, () => null), (ok, true, () => box.Text ?? string.Empty)]);
    }

    /// <summary>Lets the person choose one of <paramref name="items"/>; null for Cancel.</summary>
    public static Task<string?> PickOneAsync(Window? owner, string title, string text, IReadOnlyList<string> items, string ok)
    {
        var body = Body(title, text, []);
        var list = new ListBox { ItemsSource = items, Height = 220, SelectedIndex = 0, Classes = { "rows" } };
        Avalonia.Automation.AutomationProperties.SetName(list, title);
        body.Children.Add(list);
        return ShowPopupAsync(owner, title, body, [("Cancel", false, () => null), (ok, true, () => list.SelectedItem as string)]);
    }

    /// <summary>
    /// The strong question before a file is deleted for good: "Delete for good" works only once the box is ticked
    /// and <see cref="NestFiles.DeleteWord"/> typed. Returns the word typed, or null for "Keep it".
    /// </summary>
    public static Task<string?> DeleteForGoodAsync(Window? owner, string title, string text, Action saveCopy)
    {
        var body = Body(title, text, []);
        var save = new Button { Content = FilesPageText.SaveCopyFirst, Classes = { "SecondaryButton" }, HorizontalAlignment = HorizontalAlignment.Left, Name = "SaveCopyFirst" };
        save.Click += (_, _) => saveCopy();
        body.Children.Add(save);
        var tick = new CheckBox { Content = FilesPageText.SureCheck, Name = "SureCheck" };
        body.Children.Add(tick);
        body.Children.Add(new TextBlock { Text = FilesPageText.SureType, FontWeight = FontWeight.SemiBold });
        var word = new TextBox { Classes = { "field" }, Height = 38, MinHeight = 38, CornerRadius = new CornerRadius(11), Name = "SureWord", Watermark = NestFiles.DeleteWord };
        Avalonia.Automation.AutomationProperties.SetName(word, FilesPageText.SureType);
        body.Children.Add(word);
        return ShowPopupAsync(owner, title, body, [(FilesPageText.KeepIt, true, () => null), (FilesPageText.DeleteForGood, false, () => word.Text ?? string.Empty)], buttons =>
        {
            var delete = buttons[^1];
            delete.Classes.Add("danger");
            delete.Name = "DeleteForGoodButton";
            delete.IsEnabled = false;
            void Check() => delete.IsEnabled = FilesPageText.SureReady(tick.IsChecked == true, word.Text);
            tick.IsCheckedChanged += (_, _) => Check();
            word.TextChanged += (_, _) => Check();
        });
    }

    /// <summary>Asks before a folder's files leave this computer; true when the person agrees, or when nothing leaves.</summary>
    public static async Task<bool> ConfirmAwayAsync(Window? owner, string folder, FilePlacement from, FilePlacement to, long bytesHere)
    {
        if (!FilesPageText.AsksFirst(from, to))
            return true;
        var question = FilesPageText.AwayQuestion(folder, to, bytesHere);
        return await ChooseAsync(owner, question.Title, question.Text, question.Points, "Cancel", question.Button) == question.Button;
    }

    /// <summary>Asks where to save a copy of a file; null for Cancel. The tests answer it themselves.</summary>
    internal static Func<Window?, string, string, Task<string?>> SaveFilePicker { get; set; } = SystemSaveFileAsync;

    /// <summary>Asks for files to add; null for Cancel. The tests answer it themselves.</summary>
    internal static Func<Window?, string, Task<IReadOnlyList<string>?>> OpenFilesPicker { get; set; } = SystemOpenFilesAsync;

    public static Task<string?> SaveFileAsync(Window? owner, string title, string fileName) => SaveFilePicker(owner, title, fileName);

    public static Task<IReadOnlyList<string>?> OpenFilesAsync(Window? owner, string title) => OpenFilesPicker(owner, title);

    private static async Task<string?> SystemSaveFileAsync(Window? owner, string title, string fileName)
    {
        if (owner is null)
            return null;
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = title, SuggestedFileName = fileName, ShowOverwritePrompt = true });
        return file?.TryGetLocalPath();
    }

    private static async Task<IReadOnlyList<string>?> SystemOpenFilesAsync(Window? owner, string title)
    {
        if (owner is null)
            return null;
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = true });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        return paths.Count > 0 ? paths : null;
    }

    private static StackPanel Body(string title, string text, IReadOnlyList<string> points)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeight.ExtraBold, TextWrapping = TextWrapping.Wrap });
        if (text.Length > 0)
            body.Children.Add(new TextBlock { Text = text, Classes = { "t2" }, TextWrapping = TextWrapping.Wrap });
        foreach (var point in points)
            body.Children.Add(new TextBlock { Text = "\u2022  " + point, Classes = { "t3" }, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 0, 0) });
        return body;
    }

    /// <summary>A pop-up window with <paramref name="body"/> and buttons along the bottom (each closes it with its answer).</summary>
    private static async Task<string?> ShowPopupAsync(Window? owner, string title, StackPanel body, (string Label, bool Main, Func<string?> Answer)[] buttons,
        Action<Button[]>? wire = null)
    {
        string? result = null;
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var made = new List<Button>();
        foreach (var (label, main, answer) in buttons)
        {
            var b = new Button { Content = label, MinWidth = 80, Height = 38, Padding = new Thickness(16, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Classes.Add(main ? "accent" : "SecondaryButton");
            b.Click += (_, _) =>
            {
                result = answer();
                window.Close();
            };
            row.Children.Add(b);
            made.Add(b);
        }
        wire?.Invoke([.. made]);
        body.Children.Add(row);
        window.Content = new Border { Padding = new Thickness(24, 22, 24, 20), Child = body };
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
