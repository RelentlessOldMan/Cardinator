using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator;

/// <summary>
/// Everything a whole set shares, in one place: its printing details (set code, rarity, artist, copyright, set symbol,
/// the frame new cards get) and the back of its cards. It replaces the separate "Set defaults…" and "Set fields on
/// all…" windows — they edited the same fields with two different meanings, which "Use these for" now spells out:
/// <list type="bullet">
/// <item><see cref="SetSettingsMode.NewCardsOnly"/>: new and imported cards inherit them (a blank means no default).</item>
/// <item><see cref="SetSettingsMode.FillBlanks"/>: …and cards already in the set get them where they're blank.</item>
/// <item><see cref="SetSettingsMode.Replace"/>: …and every card (or just the selected ones) gets every filled-in field,
/// replacing its own; a ticked "empty" box clears that field on them.</item>
/// </list>
/// </summary>
public partial class SetSettingsWindow : Window
{
    private const string NoFrame = "(none)";
    private const string NoRarity = "(none)";
    private readonly string _setName;
    private bool _ready;

    public SetProfile Profile { get; private set; } = new();
    public string DefaultFrame { get; private set; } = "";
    public SetSettingsMode Mode { get; private set; } = SetSettingsMode.NewCardsOnly;
    /// <summary>Replace mode: only the cards selected in the list (when some, not all, were).</summary>
    public bool OnlySelected { get; private set; }
    public bool ClearSetCode { get; private set; }
    public bool ClearArtist { get; private set; }
    public bool ClearCopyright { get; private set; }
    public bool ClearSetSymbol { get; private set; }
    /// <summary>Replace mode: the frame goes on the cards even when it's the one the set already had.</summary>
    public bool FrameOnEveryCard { get; private set; }

    /// <param name="selectedCount">How many cards are selected in the list when that's some but not all of them
    /// (Replace can then stick to those); null otherwise.</param>
    public SetSettingsWindow(SetProfile current, string currentFrame, IEnumerable<string> templateNames, string setName,
        int? selectedCount = null)
    {
        InitializeComponent();
        _setName = setName;

        SetBox.Text = current.SetCode;
        ArtistBox.Text = current.Artist;
        CopyrightBox.Text = current.Copyright;
        SetSymbolBox.Text = current.SetSymbolPath;

        RarityBox.ItemsSource = new[] { NoRarity, "C", "U", "R", "M" };
        RarityBox.SelectedItem = string.IsNullOrWhiteSpace(current.Rarity) ? NoRarity : current.Rarity;
        if (RarityBox.SelectedItem == null) RarityBox.SelectedIndex = 0;

        // A house frame that isn't installed here stays listed, so Save without touching it doesn't clear it.
        var frames = templateNames.ToList();
        if (!string.IsNullOrWhiteSpace(currentFrame) && !frames.Contains(currentFrame)) frames.Add(currentFrame);
        FrameBox.ItemsSource = new[] { NoFrame }.Concat(frames);
        FrameBox.SelectedItem = string.IsNullOrWhiteSpace(currentFrame) ? NoFrame : currentFrame;
        if (FrameBox.SelectedItem == null) FrameBox.SelectedIndex = 0;

        if (selectedCount is int n)
        {
            OnlySelectedBox.Content = $"Only the {n} selected card{(n == 1 ? "" : "s")}";
            OnlySelectedBox.IsChecked = true;
            OnlySelectedBox.Visibility = Visibility.Visible;
        }

        var back = current.CardBack ?? new CardBackDesign();
        WordmarkBox.Text = back.Wordmark;
        BackColorBox.Text = back.Color;
        BackAccentBox.Text = back.Accent;
        UseSymbolBox.IsChecked = back.UseSetSymbol;
        BackImageBox.Text = back.ImagePath;
        (back.Style switch
        {
            CardBackDesign.DesignedStyle => BackDesigned,
            CardBackDesign.PictureStyle => BackPicture,
            _ => BackDefault,
        }).IsChecked = true;

        _ready = true;
        RefreshBack();
    }

    /// <summary>The card back as the controls describe it now.</summary>
    internal CardBackDesign CurrentBack() => new()
    {
        Style = BackDesigned.IsChecked == true ? CardBackDesign.DesignedStyle
              : BackPicture.IsChecked == true ? CardBackDesign.PictureStyle : CardBackDesign.DefaultStyle,
        Wordmark = (WordmarkBox.Text ?? "").Trim(),
        Color = (BackColorBox.Text ?? "").Trim(),
        Accent = (BackAccentBox.Text ?? "").Trim(),
        UseSetSymbol = UseSymbolBox.IsChecked == true,
        ImagePath = (BackImageBox.Text ?? "").Trim(),
    };

    private void OnAnyChange(object sender, RoutedEventArgs e) { if (_ready) RefreshBack(); }

    private void RefreshBack()
    {
        DesignedPanel.Visibility = BackDesigned.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PicturePanel.Visibility = BackPicture.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        try { BackPreview.Source = BackRenderer.Render(CurrentBack(), _setName, (SetSymbolBox.Text ?? "").Trim(), supersample: 1); }
        catch { BackPreview.Source = null; }
    }

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (ReplacePanel != null) ReplacePanel.Visibility = ModeReplace.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (SaveButton != null)
            SaveButton.Content = ModeReplace.IsChecked == true ? "Save and apply" : ModeFill.IsChecked == true ? "Save and fill in" : "Save";
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        Collect();
        DialogResult = true;
    }

    /// <summary>Reads the controls into the outputs (<see cref="Profile"/>, <see cref="Mode"/>, …).</summary>
    internal void Collect()
    {
        var back = CurrentBack();
        Profile = new SetProfile
        {
            SetCode = (SetBox.Text ?? "").Trim(),
            Artist = (ArtistBox.Text ?? "").Trim(),
            Copyright = (CopyrightBox.Text ?? "").Trim(),
            SetSymbolPath = (SetSymbolBox.Text ?? "").Trim(),
            Rarity = RarityBox.SelectedItem is string r && r != NoRarity ? r : "",
            CardBack = back.IsDefault ? null : back,
        };
        DefaultFrame = FrameBox.SelectedItem is string f && f != NoFrame ? f : "";
        Mode = ModeReplace.IsChecked == true ? SetSettingsMode.Replace
             : ModeFill.IsChecked == true ? SetSettingsMode.FillBlanks : SetSettingsMode.NewCardsOnly;
        OnlySelected = Mode == SetSettingsMode.Replace && OnlySelectedBox.Visibility == Visibility.Visible && OnlySelectedBox.IsChecked == true;
        bool replace = Mode == SetSettingsMode.Replace;
        ClearSetCode = replace && ClearSetBox.IsChecked == true;
        ClearArtist = replace && ClearArtistBox.IsChecked == true;
        ClearCopyright = replace && ClearCopyrightBox.IsChecked == true;
        ClearSetSymbol = replace && ClearSymbolBox.IsChecked == true;
        FrameOnEveryCard = replace && FrameOnEveryCardBox.IsChecked == true && DefaultFrame.Length > 0;
    }

    private static string? PickImage(string title)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*" };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private void OnBrowseSymbol(object sender, RoutedEventArgs e) { if (PickImage("Choose a set symbol image") is { } p) SetSymbolBox.Text = p; }
    private void OnBrowseBack(object sender, RoutedEventArgs e) { if (PickImage("Choose a picture for the card back") is { } p) BackImageBox.Text = p; }

    private void OnExportBack(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Save the card back", Filter = "PNG image|*.png", FileName = "card-back.png" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            CardExporter.Save(BackRenderer.Render(CurrentBack(), _setName, (SetSymbolBox.Text ?? "").Trim()), dlg.FileName);
        }
        catch (System.Exception ex)
        {
            ConfirmDialog.Show(this, "Card back", "Couldn't save the card back:\n" + ex.Message, affirmative: "OK");
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}

public enum SetSettingsMode { NewCardsOnly, FillBlanks, Replace }
