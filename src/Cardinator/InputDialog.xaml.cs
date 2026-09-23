using System.Windows;

namespace Cardinator;

/// <summary>A small themed text-prompt dialog. Returns the entered text via <see cref="Value"/>.</summary>
public partial class InputDialog : Window
{
    public string Value => Entry.Text.Trim();

    public InputDialog(string prompt, string hint = "", string initial = "")
    {
        InitializeComponent();
        PromptText.Text = prompt;
        HintText.Text = hint;
        HintText.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        Entry.Text = initial;
        Loaded += (_, _) => { Entry.Focus(); Entry.SelectAll(); };
    }

    private void OnOk(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }

    /// <summary>Shows the dialog and returns the entered text, or null if cancelled/blank.</summary>
    public static string? Ask(Window owner, string prompt, string hint = "", string initial = "")
    {
        var dlg = new InputDialog(prompt, hint, initial) { Owner = owner };
        if (dlg.ShowDialog() != true) return null;
        return string.IsNullOrWhiteSpace(dlg.Value) ? null : dlg.Value;
    }
}
