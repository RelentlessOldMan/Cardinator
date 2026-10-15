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

    /// <summary>The folder of the list file opened into the box (its art paths are relative to it); "" when the list
    /// was pasted or fetched.</summary>
    public string BaseDir => StillTheOpenedFile ? _baseDir : "";

    /// <summary>The name of the list file opened into the box, for the status line; "" otherwise.</summary>
    public string FileName => StillTheOpenedFile ? _fileName : "";

    private string _baseDir = "", _fileName = "", _openedText = "";

    /// <summary>The box still holds the opened file's list, perhaps touched up — not a pasted or fetched one put over
    /// it (none of the file's lines left).</summary>
    private bool StillTheOpenedFile
    {
        get
        {
            if (_openedText.Length == 0) return false;
            var now = DeckBox.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet();
            return _openedText.Split('\n').Select(l => l.Trim()).Any(l => l.Length > 0 && now.Contains(l));
        }
    }

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
            _openedText = "";   // a fetched deck isn't the file opened before (its card lines may well overlap)
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

    private void OnOpenFile(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a card list",
            Filter = "Card lists (*.txt;*.csv;*.tsv)|*.txt;*.csv;*.tsv|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        LoadFile(dlg.FileName);
    }

    /// <summary>Puts a list file's text in the box and remembers where it came from.</summary>
    internal void LoadFile(string path)
    {
        try
        {
            DeckBox.Text = ImportService.ReadListFile(path);
            _openedText = DeckBox.Text;
            _baseDir = System.IO.Path.GetDirectoryName(path) ?? "";
            _fileName = System.IO.Path.GetFileName(path);
            StatusText.Text = $"Opened {FileName} — check the list, then Import.";
        }
        catch (Exception ex) { StatusText.Text = "Couldn't open that file: " + ex.Message; }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (TryAccept()) DialogResult = true;
    }

    /// <summary>Checks the box holds a list to import and takes it (<see cref="DeckText"/>); false, with a hint in
    /// the status line, when it doesn't.</summary>
    internal bool TryAccept()
    {
        var text = DeckBox.Text.Trim();
        if (text.Length == 0)
        {
            StatusText.Text = MoxfieldClient.IsMoxfieldUrl(MoxLinkBox.Text)
                ? "Click “Get from Moxfield” to load the deck first."
                : "Paste a card list, open a file, or get one from a Moxfield link.";
            return false;
        }
        if (BaseDir.Length == 0 && ImportService.LooksLikeOnlyLinks(text))
        {
            StatusText.Text = "That's a link — put a Moxfield URL in the box above and click Get from Moxfield, "
                            + "or paste an exported list here.";
            return false;
        }
        DeckText = text;
        return true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
