using System;
using System.Threading;
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
    private static bool _useArt;            // "Also use its art", remembered while the app runs
    // Cancelled when the dialog closes, so a lookup still running can't write into the card afterwards
    // (after Cancel restored it, or after the main window already took the edits as one undo step).
    private readonly CancellationTokenSource _closing = new();

    /// <param name="halfLayout">For a two-part card's other half: the parent card's layout ("flip" / "split").</param>
    public DetailsWindow(CardModel card, ScryfallClient? scryfall = null, string halfLayout = "")
    {
        InitializeComponent();
        _card = card;
        _snapshot = card.Clone();
        _scryfall = scryfall ?? new ScryfallClient();
        DataContext = card;
        SearchBox.Text = card.Name;   // a convenient default; edit it to search for something else
        UseArtBox.IsChecked = _useArt;
        UseArtBox.Checked += (_, _) => _useArt = true;
        UseArtBox.Unchecked += (_, _) => _useArt = false;
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

    protected override void OnClosed(EventArgs e)
    {
        _closing.Cancel();
        base.OnClosed(e);
    }

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
            var ct = _closing.Token;
            var faces = await _scryfall.LookupFacesAsync(query, ct);
            ct.ThrowIfCancellationRequested();
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
            bool melded = canTakeABack && await CardDetailsFill.AttachMeldAsync(_scryfall, _card, f, ct);
            ct.ThrowIfCancellationRequested();
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
            // "Also use its art": the looked-up card's art replaces this card's (and fills a new back/half's).
            string kept = "Name, art and frame unchanged.";
            if (UseArtBox.IsChecked == true)
            {
                SearchStatus.Text = $"Filled details from “{f.Name}” — downloading its art…";
                kept = await UseScryfallArtAsync(f) ? "Used its art; name and frame unchanged." : "Its art couldn't be downloaded — name, art and frame unchanged.";
            }
            SearchStatus.Text = $"Filled details from “{f.Name}”. {kept}{extra}";
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { /* the dialog closed */ }
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

    /// <summary>Puts the looked-up card's art on this card, framing reset, and on a back face or other half the lookup
    /// just added (when it has none). False when the card's own art couldn't be downloaded.</summary>
    private async Task<bool> UseScryfallArtAsync(CardModel face)
    {
        bool ok = false;
        if (!string.IsNullOrWhiteSpace(face.ArtUrl))
        {
            try
            {
                var path = await ArtDownloader(face.ArtUrl);
                _closing.Token.ThrowIfCancellationRequested();
                _card.ArtPath = path;
                _card.ArtScale = 1.0;
                _card.ArtOffsetX = 0;
                _card.ArtOffsetY = 0;
                ok = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* keep the card's own art */ }
        }
        foreach (var part in new[] { _card.BackFace, _card.OtherHalf })
        {
            if (part == null || !string.IsNullOrWhiteSpace(part.ArtPath) || string.IsNullOrWhiteSpace(part.ArtUrl)) continue;
            string path;
            try { path = await ArtDownloader(part.ArtUrl); } catch { continue; }
            _closing.Token.ThrowIfCancellationRequested();
            part.ArtPath = path;
        }
        if (_card.IsSplit) CardDetailsFill.SplitSharedArt(_card);
        return ok;
    }

    /// <summary>Downloads an image into the art cache; swappable so tests stay offline.</summary>
    internal Func<string, Task<string>> ArtDownloader { get; set; } = url => ImageIntake.DownloadAsync(url);
}
