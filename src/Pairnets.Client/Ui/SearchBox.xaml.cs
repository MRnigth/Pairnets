using System.Windows;
using System.Windows.Controls;

namespace Pairnets.Client.Ui;

/// <summary>A search field with a magnifier and a hint ("Search files and folders") that disappears while you type.</summary>
public partial class SearchBox : UserControl
{
    public SearchBox()
    {
        InitializeComponent();
        Hint.Visibility = Visibility.Visible;
    }

    /// <summary>Raised whenever the text changes.</summary>
    public event Action? TextChanged;

    public string Text
    {
        get => SearchInput.Text;
        set => SearchInput.Text = value;
    }

    /// <summary>The hint shown while the box is empty (also its name for screen readers).</summary>
    public string Placeholder
    {
        get => Hint.Text;
        set
        {
            Hint.Text = value;
            System.Windows.Automation.AutomationProperties.SetName(SearchInput, value);
        }
    }

    /// <summary>Whether the hint is showing (the box is empty).</summary>
    public bool HintVisible => Hint.Visibility == Visibility.Visible;

    /// <summary>Puts the cursor in the box with its words chosen (Ctrl+F).</summary>
    public void FocusInput()
    {
        SearchInput.Focus();
        System.Windows.Input.Keyboard.Focus(SearchInput);
        SearchInput.SelectAll();
    }

    /// <summary>True while the cursor is in the box.</summary>
    public bool HasFocus => SearchInput.IsKeyboardFocused;

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        Hint.Visibility = SearchInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TextChanged?.Invoke();
    }
}
