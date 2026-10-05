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

    /// <param name="halfLayout">For a two-part card's other half: the parent card's layout ("flip" / "split").</param>
    public DetailsWindow(CardModel card, ScryfallClient? scryfall = null, string halfLayout = "")
    {
        InitializeComponent();
        _card = card;
        _snapshot = card.Clone();
        _scryfall = scryfall ?? new ScryfallClient();
        DataContext = card;
        SearchBox.Text = card.Name;   // a convenient default; edit it to search for something else
        LandStyleBox.ItemsSource = new[] { "row", "splitv", "splith", "pie", "yinyang" };
        OrientationBox.ItemsSource = OrientationOptions;
        if (string.IsNullOrWhiteSpace(card.Orientation)) OrientationBox.SelectedIndex = 0;   // "" shows as Automatic
        if (card.IsOtherHalf && string.Equals(halfLayout, "split", StringComparison.OrdinalIgnoreCase))
            Title = "Other half (split card)";
        else if (card.IsOtherHalf)
        {
            // The upside-down half of a flip card is cast by flipping, never paid for — real ones print no cost.
            ManaCostBox.IsEnabled = false;
            ManaCostBox.ToolTip = "The flipped half of a flip card has no mana cost.";
            Title = "Flipped half";
        }
    }

    /// <summary>A choice in the orientation dropdown. "" (Automatic) is the stored default, so files written
    /// before orientation existed — and cards nobody touched — keep following the card type.</summary>
    public sealed record OrientationOption(string Value, string Label);

    private static readonly OrientationOption[] OrientationOptions =
    {
        new("", "Automatic (by card type)"),
        new("portrait", "Portrait (upright)"),
        new("landscape", "Landscape (sideways)"),
    };

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

            // A real double-faced card brings its back with it (G6) — but only onto a FRONT that hasn't got
            // one yet: a back face can't have a back of its own, and an existing one is the user's own work.
            // Cancel still discards it, because CopyFrom restores the snapshot's back face too.
            string extra = "";
            // Same for a flip card's other half: only onto a plain card that has neither.
            bool canTakeABack = !_card.IsBackFace && !_card.IsOtherHalf && !_card.IsDoubleFaced && _card.OtherHalf == null;
            if (canTakeABack) CardDetailsFill.AttachFaces(_card, faces);
            bool melded = canTakeABack && await CardDetailsFill.AttachMeldAsync(_scryfall, _card, f);
            if (melded)
                extra = $" Its back is the {_card.MeldHalf} half of the melded card “{_card.BackFace!.Name}”"
                        + (_card.MeldWith.Length > 0 ? $" (the other half goes on “{_card.MeldWith}”)." : ".");
            else if (canTakeABack && _card.IsDoubleFaced)
                extra = $" Added the back face “{_card.BackFace!.Name}” — use “Show back” to preview it.";
            else if (canTakeABack && _card.IsSplit)
                extra = $" Made it a split card with “{_card.OtherHalf!.Name}” as the other half.";
            else if (canTakeABack && _card.OtherHalf != null)
                extra = $" Added the flipped half “{_card.OtherHalf.Name}” — use “Show flipped” to preview it.";
            else if (faces.Count > 1)
                extra = $" (“{f.Name}” has another half: look it up from the main window to add it.)";
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
