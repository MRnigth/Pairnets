using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Pairnets.Core;
using Pairnets.Core.Client;

namespace Pairnets.Client.Ui;

/// <summary>
/// "Clean up your nest" (from the nest-full pop-up, or "Clean up…" on the Files page): the biggest files on the nest
/// by kind, each to save a copy of elsewhere or, only when the person is completely sure, to delete for good.
/// </summary>
public partial class CleanupView : UserControl
{
    private const int Shown = 50;

    private readonly ObservableCollection<CleanupRow> _rows = [];
    private FileKind? _kind;

    public CleanupView()
    {
        InitializeComponent();
        BigList.ItemsSource = _rows;
    }

    /// <summary>What the page asks of the main window.</summary>
    public IPageHost? Host { get; set; }

    private INestFiles? Files => Host?.Actions?.Files;

    private Window? Owner => Window.GetWindow(this);

    public IReadOnlyList<CleanupRow> Rows => _rows;

    /// <summary>The kind chosen with the chips (null for all).</summary>
    public FileKind? Kind => _kind;

    public string MessageText => CleanupMessage.Visibility == Visibility.Visible ? CleanupMessageText.Text : string.Empty;

    /// <summary>The kinds the chips offer, as they read ("All types", "Videos · 612 GB", …).</summary>
    public IReadOnlyList<string> ChipTexts => KindChips.Children.OfType<RadioButton>().Select(b => b.Content as string ?? string.Empty).ToList();

    /// <summary>Reads the biggest files again (the page opened, or a file was deleted).</summary>
    public void Refresh(ServerInfo? server)
    {
        CleanupSubtitle.Text = FilesPageText.CleanupSubtitle(server);
        _rows.Clear();
        KindChips.Children.Clear();
        if (Files is not { } files)
        {
            CleanupEmpty.Text = FilesPageText.NotConnected;
            CleanupEmpty.Visibility = Visibility.Visible;
            HistoryRow.Visibility = Visibility.Collapsed;
            CleanupFooter.Text = string.Empty;
            return;
        }
        var totals = files.KindTotals();
        if (_kind is { } chosen && totals.All(t => t.Kind != chosen))
            _kind = null;
        KindChips.Children.Add(Chip(FilesPageText.AllKinds, null, server));
        foreach (var (kind, bytes) in totals)
            KindChips.Children.Add(Chip(FilesPageText.KindChip(kind, bytes), kind, server));
        foreach (var row in CleanupRow.For(files.Biggest(_kind, Shown)))
            _rows.Add(row);
        CleanupEmpty.Text = "There are no files on your nest yet.";
        CleanupEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryRow.Visibility = files.HistoryBytes is null || _kind is not null ? Visibility.Collapsed : Visibility.Visible;
        HistorySize.Text = files.HistoryBytes is { } history ? Format.Bytes(history) : string.Empty;
        CleanupFooter.Text = FilesPageText.CleanupFooter(_rows.Count, files.BytesOnNest() + (files.HistoryBytes ?? 0));
    }

    private RadioButton Chip(string text, FileKind? kind, ServerInfo? server)
    {
        var chip = new RadioButton { Content = text, Style = (Style)FindResource("PgPill"), GroupName = "kind", IsChecked = kind == _kind, Margin = new Thickness(0, 0, 6, 6) };
        chip.Checked += (_, _) =>
        {
            if (_kind == kind)
                return;
            _kind = kind;
            Dispatcher.BeginInvoke(() => Refresh(server)); // not while the chip is still being checked
        };
        return chip;
    }

    private ServerInfo? Server => (Host as MainWindow)?.Status.Server;

    private void OnCleanupDone(object sender, RoutedEventArgs e) => Host?.Navigate(MainPage.Files);

    private void OnOpenHistory(object sender, RoutedEventArgs e) => Host?.Navigate(MainPage.History);

    private void OnCleanupSave(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CleanupRow row)
            _ = SaveCopyAsync(row.Entry);
    }

    private void OnCleanupDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CleanupRow row)
            _ = DeleteForGoodAsync(row.Entry);
    }

    /// <summary>Saves a copy of a file where the person chooses (an external drive, say).</summary>
    public async Task SaveCopyAsync(NestEntry entry)
    {
        if (Files is not { } files || Dialogs.SaveFile(Owner, "Save a copy of " + entry.Name, entry.Name) is not { } destination)
            return;
        var result = await files.SaveCopyAsync(entry.Path, destination);
        Say(result.Ok ? $"Saved a copy of {entry.Name} to {destination}." : result.Problem ?? "The copy was not saved.", result.Ok);
    }

    /// <summary>The strong question, then the file and its older versions are deleted for good.</summary>
    public async Task DeleteForGoodAsync(NestEntry entry)
    {
        if (Files is not { } files)
            return;
        var (title, text) = FilesPageText.SureQuestion(entry);
        if (Dialogs.DeleteForGood(Owner, title, text, () => _ = SaveCopyAsync(entry)) is not { } typed)
            return;
        var result = await files.DeleteForGoodAsync(entry.Path, typed);
        Say(result.Ok ? $"Deleted {entry.Name} for good. Its space is free on your nest." : result.Problem ?? "It was not deleted.", result.Ok);
        Refresh(Server);
    }

    private void Say(string message, bool ok)
    {
        CleanupMessage.Visibility = Visibility.Visible;
        CleanupMessage.SetResourceReference(Border.BackgroundProperty, ok ? "T.OkPill" : "T.BadPill");
        CleanupMessageText.SetResourceReference(TextBlock.ForegroundProperty, ok ? "T.OkText" : "T.BadText");
        CleanupMessageText.Text = message;
    }
}
