using System;
using System.Windows;

namespace Cardinator;

/// <summary>A small themed text-prompt dialog. Returns the entered text via <see cref="Value"/>.
/// Optionally shows a prominent "Choose file…" button (see <see cref="ShowBrowse"/>) so a dialog can
/// offer BOTH "pick a file from your PC" and "paste a link" as two clearly visible choices.</summary>
public partial class InputDialog : Window
{
    private Func<string?>? _picker;

    public string Value => Entry.Text.Trim();

    /// <summary>Set when the user picked a file via the "Choose file…" button (rather than typing a link).
    /// When true, <see cref="Value"/> holds the chosen local file path.</summary>
    public bool PickedFile { get; private set; }

    public InputDialog(string prompt, string hint = "", string initial = "")
    {
        InitializeComponent();
        PromptText.Text = prompt;
        HintText.Text = hint;
        HintText.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        Entry.Text = initial;
        Loaded += (_, _) => { Entry.Focus(); Entry.SelectAll(); };
    }

    /// <summary>Adds a "Choose file…" button that runs <paramref name="picker"/> (typically an OpenFileDialog)
    /// when clicked; if the picker returns a path, the dialog closes with that path in <see cref="Value"/> and
    /// <see cref="PickedFile"/> true. <paramref name="buttonText"/> customises the button label.</summary>
    public void ShowBrowse(Func<string?> picker, string buttonText = "Choose file…")
    {
        _picker = picker;
        BrowseBtn.Content = buttonText;
        BrowseBtn.Visibility = Visibility.Visible;
        OrLabel.Visibility = Visibility.Visible;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var path = _picker?.Invoke();
        if (string.IsNullOrWhiteSpace(path)) return;   // cancelled the file picker — stay on the dialog
        Entry.Text = path;
        PickedFile = true;
        DialogResult = true;
        Close();
    }

    private void OnOk(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
