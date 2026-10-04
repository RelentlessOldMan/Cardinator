using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator;

/// <summary>
/// A spacious modal editor for the less-common card fields. Binds directly to the live CardModel,
/// so edits update the main preview in real time; closing just dismisses the dialog. Includes a
/// Scryfall search that fills the printed DETAILS (mana/type/rules/stats/printing) from a real card
/// — deliberately leaving the card's own name, art and frame untouched, so you can name a card
/// anything and still borrow another card's stats.
/// </summary>
public partial class DetailsWindow : Window
{
    private readonly CardModel _card;
    private readonly CardModel _snapshot;   // state on open, so Cancel can discard this session's edits
    private readonly ScryfallClient _scryfall;

    public DetailsWindow(CardModel card, ScryfallClient? scryfall = null)
    {
        InitializeComponent();
        _card = card;
        _snapshot = card.Clone();
        _scryfall = scryfall ?? new ScryfallClient();
        DataContext = card;
        SearchBox.Text = card.Name;   // a convenient default; edit it to search for something else
        LandStyleBox.ItemsSource = new[] { "row", "splitv", "splith", "pie", "yinyang" };
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();   // keep edits (they're already live)

    private void OnBrowseArt(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose artwork for this face",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*",
        };
        if (dlg.ShowDialog() == true) _card.ArtPath = dlg.FileName;   // two-way bound; updates the field + preview
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _card.CopyFrom(_snapshot);   // discard everything changed in this dialog (incl. a Scryfall fill)
        Close();
    }

    private async void OnSearch(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await SearchAsync(); }
    }

    /// <summary>Looks up a card on Scryfall and fills the detail fields — never the name, art, or frame.</summary>
    private async Task SearchAsync()
    {
        if (!SearchBtn.IsEnabled) return;   // a lookup is already running (guards repeated Enter presses)
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) { SearchStatus.Text = "Type a card name to look up."; return; }

        SearchBtn.IsEnabled = false;
        SearchStatus.Text = $"Looking up “{query}” on Scryfall…";
        try
        {
            var faces = await _scryfall.LookupFacesAsync(query);
            if (faces.Count == 0) { SearchStatus.Text = $"No card found for “{query}”."; return; }

            var f = faces[0];
            CardDetailsFill.ApplyScryfall(_card, f);   // printed details only — name, art and frame left alone

            var extra = faces.Count > 1 ? $" (“{f.Name}” is the front of a double-faced card)" : "";
            SearchStatus.Text = $"Filled details from “{f.Name}”. Name, art and frame unchanged.{extra}";
        }
        catch (ScryfallException ex)
        {
            SearchStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            SearchStatus.Text = "Lookup failed: " + ex.Message;
        }
        finally
        {
            SearchBtn.IsEnabled = true;
        }
    }
}
