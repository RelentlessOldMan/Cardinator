using System.Windows;
using Cardinator.Services;

namespace Cardinator;

/// <summary>
/// Collects a deck to import: either fetch a Moxfield deck by link (hidden WebView2) — which fills the
/// box with a decklist you can review/edit — or paste an exported list (Moxfield/Archidekt/plain text).
/// Everything flows through the same <see cref="ImportService"/> parser on Import.
/// </summary>
public partial class DeckListWindow : Window
{
    /// <summary>The deck text to import (valid only when the dialog result is true).</summary>
    public string DeckText { get; private set; } = "";

    public DeckListWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => MoxLinkBox.Focus();
    }

    private async void OnGetMoxfield(object sender, RoutedEventArgs e)
    {
        var url = MoxLinkBox.Text.Trim();
        var id = MoxfieldClient.ExtractDeckId(url);
        if (id == null) { StatusText.Text = "Paste a Moxfield deck link first (moxfield.com/decks/…)."; return; }

        // Busy state: disable the fetch/import while the hidden browser works, show the animated bar.
        GetMoxButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        FetchProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Loading from Moxfield… (first time can take a few seconds)";
        try
        {
            var progress = new Progress<string>(s => StatusText.Text = s);
            var json = await MoxfieldFetcher.FetchDeckJsonAsync(id, progress);
            var list = MoxfieldClient.ToDeckList(json);
            if (string.IsNullOrWhiteSpace(list))
            {
                StatusText.Text = "That deck came back empty — check the link, or paste the exported list.";
                return;
            }
            DeckBox.Text = list;
            StatusText.Text = "Loaded — review the list, then Import.";
        }
        catch (MoxfieldException ex) { StatusText.Text = ex.Message; }
        catch (Exception ex) { StatusText.Text = "Moxfield fetch failed: " + ex.Message; }
        finally
        {
            FetchProgress.Visibility = Visibility.Collapsed;
            GetMoxButton.IsEnabled = true;
            ImportButton.IsEnabled = true;
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var text = DeckBox.Text.Trim();
        if (text.Length == 0)
        {
            StatusText.Text = MoxfieldClient.IsMoxfieldUrl(MoxLinkBox.Text)
                ? "Click “Get from Moxfield” to load the deck first."
                : "Paste a deck list, or fetch one from a Moxfield link.";
            return;
        }
        if (ImportService.LooksLikeOnlyLinks(text))
        {
            StatusText.Text = "That's a link — put a Moxfield URL in the box above and click Get from Moxfield, "
                            + "or paste an exported list here.";
            return;
        }
        DeckText = text;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
