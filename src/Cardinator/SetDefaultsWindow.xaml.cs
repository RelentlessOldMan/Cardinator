using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Cardinator.Models;

namespace Cardinator;

/// <summary>
/// Edits the set's house defaults (W1): the <see cref="SetProfile"/> metadata plus the default frame.
/// Unlike "Set fields on all", a blank field here means "no default for this field" (it's stored as-is),
/// and the values persist on the project so every new/imported card inherits them.
/// </summary>
public partial class SetDefaultsWindow : Window
{
    private const string NoFrame = "(none)";
    private const string NoRarity = "(none)";

    /// <summary>The edited profile (metadata defaults).</summary>
    public SetProfile Profile { get; private set; } = new();

    /// <summary>The chosen default frame name, or "" for none.</summary>
    public string DefaultFrame { get; private set; } = "";

    /// <summary>Whether to also push these defaults onto cards already in the set.</summary>
    public bool ApplyToExisting { get; private set; }

    public SetDefaultsWindow(SetProfile current, string currentFrame, IEnumerable<string> templateNames)
    {
        InitializeComponent();

        SetBox.Text = current.SetCode;
        ArtistBox.Text = current.Artist;
        CopyrightBox.Text = current.Copyright;
        SetSymbolBox.Text = current.SetSymbolPath;

        RarityBox.ItemsSource = new[] { NoRarity, "C", "U", "R", "M" };
        RarityBox.SelectedItem = string.IsNullOrWhiteSpace(current.Rarity) ? NoRarity : current.Rarity;
        if (RarityBox.SelectedItem == null) RarityBox.SelectedIndex = 0;

        // A house frame that isn't installed here stays listed, so OK without touching it doesn't clear it.
        var frames = templateNames.ToList();
        if (!string.IsNullOrWhiteSpace(currentFrame) && !frames.Contains(currentFrame)) frames.Add(currentFrame);
        FrameBox.ItemsSource = new[] { NoFrame }.Concat(frames);
        FrameBox.SelectedItem = string.IsNullOrWhiteSpace(currentFrame) ? NoFrame : currentFrame;
        if (FrameBox.SelectedItem == null) FrameBox.SelectedIndex = 0;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        Profile = new SetProfile
        {
            SetCode = (SetBox.Text ?? "").Trim(),
            Artist = (ArtistBox.Text ?? "").Trim(),
            Copyright = (CopyrightBox.Text ?? "").Trim(),
            SetSymbolPath = (SetSymbolBox.Text ?? "").Trim(),
            Rarity = RarityBox.SelectedItem is string r && r != NoRarity ? r : "",
        };
        DefaultFrame = FrameBox.SelectedItem is string f && f != NoFrame ? f : "";
        ApplyToExisting = ApplyExistingBox.IsChecked == true;
        DialogResult = true;
    }

    private void OnBrowseSymbol(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a set symbol image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*",
        };
        if (dlg.ShowDialog() == true) SetSymbolBox.Text = dlg.FileName;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
