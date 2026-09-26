using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator;

/// <summary>
/// Runs a Scryfall search and lets the user pick which results to add (with thumbnails + checkboxes),
/// instead of dumping every match straight into the project. Returns the chosen cards via
/// <see cref="SelectedCards"/> when the dialog result is true.
/// </summary>
public partial class ScryfallSearchWindow : Window
{
    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = System.TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd(ScryfallClient.UserAgent);
        return h;
    }

    private readonly ScryfallClient _scryfall;
    private readonly CancellationTokenSource _cts = new();
    private bool _searching;

    public ObservableCollection<ResultItem> Results { get; } = new();

    /// <summary>The cards the user chose to add (empty until Add is clicked).</summary>
    public IReadOnlyList<CardModel> SelectedCards { get; private set; } = System.Array.Empty<CardModel>();

    public ScryfallSearchWindow(ScryfallClient scryfall, string? initialQuery = null)
    {
        _scryfall = scryfall;
        InitializeComponent();
        DataContext = this;
        Results.CollectionChanged += (_, _) => EmptyHint.Visibility =
            Results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        QueryBox.Text = initialQuery ?? "";
        Loaded += (_, _) => QueryBox.Focus();
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnSearch(sender, e);
    }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        if (_searching) return;
        var query = QueryBox.Text.Trim();
        if (query.Length == 0) { StatusText.Text = "Type a search query first."; return; }

        _searching = true;
        _cts.Cancel();   // stop any in-flight thumbnail loads from a previous search
        Results.Clear();
        StatusText.Text = "Searching…";
        try
        {
            var progress = new Progress<string>(s => StatusText.Text = s);
            var found = await _scryfall.SearchAsync(query, 60, progress, CancellationToken.None);
            if (found.Count == 0) { StatusText.Text = $"No cards matched \"{query}\"."; return; }

            foreach (var c in found) Results.Add(new ResultItem(c) { IsChecked = true });
            StatusText.Text = $"{found.Count} result(s) — pick what to add.";
            _ = LoadThumbnailsAsync(Results.ToList());
        }
        catch (ScryfallException ex) { StatusText.Text = ex.Message; }
        catch (System.Exception ex) { StatusText.Text = "Search failed: " + ex.Message; }
        finally { _searching = false; }
    }

    /// <summary>Loads result thumbnails in the background (art crops), so the list is usable immediately.</summary>
    private async Task LoadThumbnailsAsync(List<ResultItem> items)
    {
        var ct = _cts.Token;
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return;
            var url = item.Card.ArtUrl;
            if (string.IsNullOrWhiteSpace(url)) continue;
            try
            {
                var bytes = await Http.GetByteArrayAsync(url, ct);
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                img.DecodePixelWidth = 120;   // keep thumbnails small
                img.StreamSource = new MemoryStream(bytes);
                img.EndInit();
                img.Freeze();
                item.Thumb = img;
            }
            catch (System.OperationCanceledException) { return; }
            catch { /* leave the placeholder for this one */ }
        }
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) { foreach (var r in Results) r.IsChecked = true; }
    private void OnSelectNone(object sender, RoutedEventArgs e) { foreach (var r in Results) r.IsChecked = false; }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var chosen = Results.Where(r => r.IsChecked).Select(r => r.Card).ToList();
        if (chosen.Count == 0) { StatusText.Text = "Check at least one card to add."; return; }
        SelectedCards = chosen;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    protected override void OnClosed(System.EventArgs e)
    {
        _cts.Cancel();
        base.OnClosed(e);
    }

    /// <summary>One search result: the mapped card plus its checkbox state and lazily-loaded thumbnail.</summary>
    public sealed class ResultItem : INotifyPropertyChanged
    {
        public CardModel Card { get; }
        public string Title { get; }
        public string Sub { get; }

        public ResultItem(CardModel card)
        {
            Card = card;
            Title = card.Name;
            var bits = new[] { card.TypeLine, card.SetCode?.ToUpperInvariant(), RarityLabel(card.Rarity) }
                .Where(s => !string.IsNullOrWhiteSpace(s));
            Sub = string.Join("  ·  ", bits);
        }

        private static string RarityLabel(string? r) => (r ?? "").ToUpperInvariant() switch
        {
            "C" => "Common", "U" => "Uncommon", "R" => "Rare", "M" => "Mythic", _ => "",
        };

        private bool _isChecked;
        public bool IsChecked { get => _isChecked; set { _isChecked = value; OnPropertyChanged(); } }

        private ImageSource? _thumb;
        public ImageSource? Thumb { get => _thumb; set { _thumb = value; OnPropertyChanged(); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? p = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
