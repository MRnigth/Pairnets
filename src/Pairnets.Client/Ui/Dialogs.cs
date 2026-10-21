using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Pairnets.Client.Themes;
using Pairnets.Core.Client;

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

        /// <summary>The button pressed in a pop-up with buttons of its own (<see cref="Choose"/>), or null for closing it.</summary>
        string? Choose(Choice choice) => null;

        /// <summary>The text typed into a pop-up that asks for one (<see cref="Prompt"/>), or null for Cancel.</summary>
        string? Prompt(string title, string initial) => null;

        /// <summary>The item chosen from a list (<see cref="PickOne"/>), or null for Cancel.</summary>
        string? PickOne(string title, IReadOnlyList<string> items) => null;

        /// <summary>The word typed before "Delete for good", or null for "Keep it".</summary>
        string? DeleteForGood(string title) => null;

        /// <summary>Where "Save a copy…" saves, or null for Cancel.</summary>
        string? SaveFile(string title, string fileName) => null;

        /// <summary>The files "Add files" adds, or null for Cancel.</summary>
        IReadOnlyList<string>? OpenFiles(string title) => null;
    }

    /// <summary>A pop-up with buttons of its own: the last one is the main one.</summary>
    internal sealed record Choice(string Title, string Text, IReadOnlyList<string> Points, IReadOnlyList<string> Buttons);

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

    /// <summary>
    /// Asks a question whose buttons say what they do ("Delete 37 files" / "Cancel"), not Yes/No. True for
    /// <paramref name="yes"/>. Cancel is the default button (Enter and Esc), so a hurried key press changes nothing.
    /// </summary>
    public static bool Confirm(Window? owner, string text, string title, string yes, string no = "Cancel")
    {
        if (Responder is { } responder)
            return responder.Answer(new Question(text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)) == MessageBoxResult.Yes;
        var window = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };
        if (owner is { IsVisible: true })
            window.Owner = owner;
        Themes.ThemeManager.Attach(window);
        var yesButton = new Button { Content = yes, MinWidth = 110, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 0, 8, 0) };
        yesButton.SetResourceReference(FrameworkElement.StyleProperty, "SubtleButton");
        yesButton.SetResourceReference(Control.ForegroundProperty, "T.BadText");
        yesButton.Click += (_, _) => window.DialogResult = true;
        var noButton = new Button { Content = no, MinWidth = 90, Padding = new Thickness(16, 0, 16, 0), IsDefault = true, IsCancel = true };
        noButton.SetResourceReference(FrameworkElement.StyleProperty, "SubtleButton");
        var buttons = new System.Windows.Controls.StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(yesButton);
        buttons.Children.Add(noButton);
        var body = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        body.SetResourceReference(Control.ForegroundProperty, "T.Text");
        var content = new System.Windows.Controls.StackPanel { Margin = new Thickness(22, 20, 22, 18) };
        content.Children.Add(body);
        content.Children.Add(buttons);
        window.Content = content;
        window.Loaded += (_, _) => noButton.Focus();
        return window.ShowDialog() == true;
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
    // ------------------------------------------------------------------ the Files page's pop-ups

    /// <summary>
    /// A pop-up in the app's own look: a title, a text, points under it and buttons (the last is the main one).
    /// Returns the button pressed, or null when it was closed.
    /// </summary>
    public static string? Choose(Window? owner, string title, string text, IReadOnlyList<string> points, params string[] buttons)
    {
        if (Responder is { } responder)
            return responder.Choose(new Choice(title, text, points, buttons));
        string? result = null;
        var popup = Popup(owner, title, text, points, out var body);
        body.Children.Add(ButtonRow(popup, buttons.Select((b, i) => (b, i == buttons.Length - 1, (Action)(() => result = b))).ToArray()));
        popup.ShowDialog();
        return result;
    }

    /// <summary>Asks for one line of text (a new name); null for Cancel.</summary>
    public static string? Prompt(Window? owner, string title, string text, string initial, string ok)
    {
        if (Responder is { } responder)
            return responder.Prompt(title, initial);
        string? result = null;
        var popup = Popup(owner, title, text, [], out var body);
        var box = new TextBox { Text = initial, Style = Find<Style>("PgInput"), Margin = new Thickness(0, 4, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(box, title);
        body.Children.Add(box);
        body.Children.Add(ButtonRow(popup, [("Cancel", false, () => { }), (ok, true, () => result = box.Text)]));
        popup.Loaded += (_, _) =>
        {
            box.Focus();
            box.Select(0, System.IO.Path.GetFileNameWithoutExtension(initial).Length);
        };
        popup.ShowDialog();
        return result;
    }

    /// <summary>Lets the person choose one of <paramref name="items"/>; null for Cancel.</summary>
    public static string? PickOne(Window? owner, string title, string text, IReadOnlyList<string> items, string ok)
    {
        if (Responder is { } responder)
            return responder.PickOne(title, items);
        string? result = null;
        var popup = Popup(owner, title, text, [], out var body);
        var list = new ListBox { ItemsSource = items, Height = 220, Style = Find<Style>("PgFileList"), SelectedIndex = 0, Margin = new Thickness(0, 4, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(list, title);
        body.Children.Add(list);
        body.Children.Add(ButtonRow(popup, [("Cancel", false, () => { }), (ok, true, () => result = list.SelectedItem as string)]));
        popup.ShowDialog();
        return result;
    }

    /// <summary>
    /// The strong question before a file is deleted for good: "Delete for good" works only once the box is ticked
    /// and <see cref="NestFiles.DeleteWord"/> typed. Returns the word typed, or null for "Keep it".
    /// </summary>
    public static string? DeleteForGood(Window? owner, string title, string text, Action saveCopy)
    {
        if (Responder is { } responder)
            return responder.DeleteForGood(title);
        string? result = null;
        var popup = Popup(owner, title, text, [], out var body);
        var save = new Button { Content = FilesPageText.SaveCopyFirst, Style = Find<Style>("PgButton"), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 0, 14, 0) };
        save.Click += (_, _) => saveCopy();
        body.Children.Add(save);
        var tick = new CheckBox { Content = FilesPageText.SureCheck, Margin = new Thickness(0, 14, 0, 0) };
        tick.SetResourceReference(Control.ForegroundProperty, "T.Text");
        body.Children.Add(tick);
        var ask = new TextBlock { Text = FilesPageText.SureType, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) };
        ask.SetResourceReference(TextBlock.ForegroundProperty, "T.Text");
        body.Children.Add(ask);
        var word = new TextBox { Style = Find<Style>("PgInput") };
        System.Windows.Automation.AutomationProperties.SetName(word, FilesPageText.SureType);
        body.Children.Add(word);
        var row = ButtonRow(popup, [(FilesPageText.KeepIt, true, () => { }), (FilesPageText.DeleteForGood, false, () => result = word.Text)]);
        var delete = row.Children.OfType<Button>().Last();
        delete.Style = Find<Style>("PgDanger");
        delete.IsEnabled = false;
        void Check() => delete.IsEnabled = FilesPageText.SureReady(tick.IsChecked == true, word.Text);
        tick.Click += (_, _) => Check();
        word.TextChanged += (_, _) => Check();
        body.Children.Add(row);
        popup.ShowDialog();
        return result;
    }

    /// <summary>Asks where to save a copy of a file; null for Cancel.</summary>
    public static string? SaveFile(Window? owner, string title, string fileName)
    {
        if (Responder is { } responder)
            return responder.SaveFile(title, fileName);
        var dialog = new SaveFileDialog { Title = title, FileName = fileName, OverwritePrompt = true };
        return (owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FileName : null;
    }

    /// <summary>Asks for files to add; null for Cancel.</summary>
    public static IReadOnlyList<string>? OpenFiles(Window? owner, string title)
    {
        if (Responder is { } responder)
            return responder.OpenFiles(title);
        var dialog = new OpenFileDialog { Title = title, Multiselect = true };
        return (owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true ? dialog.FileNames : null;
    }

    /// <summary>
    /// Asks before a folder's files leave this PC (On-Demand or only on the nest); true when the person agrees, or
    /// when nothing leaves.
    /// </summary>
    public static bool ConfirmAway(Window? owner, string folder, Pairnets.Core.Sync.FilePlacement from, Pairnets.Core.Sync.FilePlacement to, long bytesHere)
    {
        if (!FilesPageText.AsksFirst(from, to))
            return true;
        var question = FilesPageText.AwayQuestion(folder, to, bytesHere);
        return Choose(owner, question.Title, question.Text, question.Points, "Cancel", question.Button) == question.Button;
    }

    private static T? Find<T>(string key) where T : class => Application.Current?.TryFindResource(key) as T;

    /// <summary>A themed pop-up window with its title and text; <paramref name="body"/> takes what follows.</summary>
    private static Window Popup(Window? owner, string title, string text, IReadOnlyList<string> points, out StackPanel body)
    {
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Owner = owner is { IsVisible: true } ? owner : null,
        };
        ThemeManager.Attach(window);
        window.SetResourceReference(Control.BackgroundProperty, "T.Card");
        body = new StackPanel { Margin = new Thickness(24, 22, 24, 20) };
        var heading = new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.ExtraBold, TextWrapping = TextWrapping.Wrap };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "T.Text");
        body.Children.Add(heading);
        if (text.Length > 0)
        {
            var words = new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 6) };
            words.SetResourceReference(TextBlock.ForegroundProperty, "T.TextSoft");
            body.Children.Add(words);
        }
        foreach (var point in points)
        {
            var line = new TextBlock { Text = "\u2022  " + point, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 4, 0, 0) };
            line.SetResourceReference(TextBlock.ForegroundProperty, "T.TextSoft");
            body.Children.Add(line);
        }
        window.Content = body;
        return window;
    }

    /// <summary>The buttons along the bottom, right-aligned; each closes the pop-up after its action.</summary>
    private static StackPanel ButtonRow(Window window, (string Label, bool Main, Action Act)[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var (label, main, act) in buttons)
        {
            var button = new Button { Content = label, Style = Find<Style>(main ? "PgPrimary" : "PgButton"), Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(8, 0, 0, 0), IsDefault = main };
            button.Click += (_, _) =>
            {
                act();
                window.Close();
            };
            row.Children.Add(button);
        }
        return row;
    }
}
