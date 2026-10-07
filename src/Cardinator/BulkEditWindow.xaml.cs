using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Cardinator;

/// <summary>
/// A small dialog for bulk-editing common metadata across every card in the project. Each field is
/// optional: a blank text box or the "(no change)" option leaves that field untouched. Returns the chosen
/// values as nullable properties (null = "leave as-is").
/// </summary>
public partial class BulkEditWindow : Window
{
    private const string NoChange = "(no change)";

    public string? SetCode { get; private set; }
    public string? Artist { get; private set; }
    public string? Copyright { get; private set; }
    public string? Rarity { get; private set; }
    public string? TemplateName { get; private set; }
    public string? SetSymbolPath { get; private set; }

    /// <param name="selectedCount">When only some cards were chosen (the selection), how many — the text then
    /// says so instead of "every card"; null = the whole set.</param>
    public BulkEditWindow(IEnumerable<string> templateNames, int? selectedCount = null)
    {
        InitializeComponent();
        if (selectedCount is int n)
        {
            HeadingText.Text = $"Set fields on {n} selected card{(n == 1 ? "" : "s")}";
            ScopeText.Text = "Blank fields are left unchanged. Applies only to the cards you selected.";
            ClearText.Text = "Clear on the selected cards (empties the field)";
        }
        RarityBox.ItemsSource = new[] { NoChange, "C", "U", "R", "M" };
        RarityBox.SelectedIndex = 0;
        FrameBox.ItemsSource = new[] { NoChange }.Concat(templateNames);
        FrameBox.SelectedIndex = 0;
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        // A checked "clear" box wins: it returns "" (apply sets the field to empty), vs. null = leave as-is.
        SetCode = ClearSet.IsChecked == true ? "" : OrNull(SetBox.Text);
        Artist = ClearArtist.IsChecked == true ? "" : OrNull(ArtistBox.Text);
        Copyright = ClearCopyright.IsChecked == true ? "" : OrNull(CopyrightBox.Text);
        Rarity = RarityBox.SelectedItem is string r && r != NoChange ? r : null;
        TemplateName = FrameBox.SelectedItem is string f && f != NoChange ? f : null;
        SetSymbolPath = ClearSymbol.IsChecked == true ? "" : OrNull(SetSymbolBox.Text);
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

    private static string? OrNull(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
