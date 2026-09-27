using System.Windows;
using Cardinator.Services;

namespace Cardinator;

/// <summary>
/// Collects a pasted deck list (Moxfield export, Archidekt, plain text, …) to import. Steers the user
/// to paste the exported list rather than a bare deck URL, since deck sites block direct link fetches.
/// </summary>
public partial class DeckListWindow : Window
{
    /// <summary>The deck text to import (valid only when the dialog result is true).</summary>
    public string DeckText { get; private set; } = "";

    public DeckListWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => DeckBox.Focus();
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var text = DeckBox.Text.Trim();
        if (text.Length == 0) { StatusText.Text = "Paste a deck list first."; return; }
        if (ImportService.LooksLikeOnlyLinks(text))
        {
            StatusText.Text = "That's a link — on Moxfield click ⋯ More → Export, copy the list, then paste it here.";
            return;
        }
        DeckText = text;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
