using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cardinator.Models;
using Cardinator.Services;

namespace Cardinator;

/// <summary>
/// A small editor that mixes &amp; matches a template's frame knobs (base style, top emblem, connected
/// panels, background/taper, faded edges, texture, border, colors), with a LIVE preview that re-renders a
/// sample card as you change any knob. On Apply it writes the template's template.json (the cached
/// frame.png then regenerates automatically) and reports the template name back.
/// </summary>
public partial class FrameDesignWindow : Window
{
    private readonly TemplateSpec _spec;      // a working copy
    private readonly string _templateDir;
    private readonly SymbolService _symbols = new();
    private readonly CardRenderer _renderer;
    private readonly CardModel _previewCard;
    private readonly DispatcherTimer _previewTimer;
    private readonly System.Threading.CancellationTokenSource _cts = new();
    private bool _ready;

    /// <summary>Set to the edited template's name when Apply succeeds.</summary>
    public string? AppliedTemplateName { get; private set; }

    public FrameDesignWindow(Template template)
    {
        InitializeComponent();
        _renderer = new CardRenderer(_symbols);
        _spec = template.Spec.Clone();
        // Prefer the folder the frame lives in; fall back to the standard templates dir when a template
        // has no on-disk frame path yet (an in-memory template), so Apply always has a valid place to write.
        _templateDir = Path.GetDirectoryName(template.FramePath) is { Length: > 0 } dir
            ? dir
            : Path.Combine(AppPaths.TemplatesDir, TextUtil.SafeFileName(_spec.Name));
        DataContext = _spec;

        StyleBox.ItemsSource = new[] { "classic", "clean", "ornate", "faded", "modern", "borderless", "overlay", "wave" };
        TopEmblemBox.ItemsSource = new[] { "none", "wave", "regal", "leaves" };
        CrownStyleBox.ItemsSource = new[] { "leaves", "arc", "none" };
        TextureBox.ItemsSource = new[] { "speckle", "none" };
        TaperBox.ItemsSource = new[] { "none", "partial", "full" };
        TaperBox.SelectedItem = !_spec.BottomTaper ? "none" : Norm(_spec.TaperStyle, "partial");

        StyleBox.SelectedItem = Norm(_spec.FrameStyle, "classic");
        TopEmblemBox.SelectedItem = Norm(_spec.TopEmblem, "none");
        CrownStyleBox.SelectedItem = Norm(_spec.CrownStyle, "leaves");
        TextureBox.SelectedItem = Norm(_spec.Texture, "speckle");

        ConnectedChk.IsChecked = _spec.ConnectedPanels;
        BackgroundChk.IsChecked = _spec.PanelBackground;
        FadedChk.IsChecked = _spec.FadedEdges;
        EmbellishChk.IsChecked = _spec.Embellishments;
        PtBevelChk.IsChecked = _spec.PtBevel;
        RoyalChk.IsChecked = _spec.RoyalFrame;
        FullArtChk.IsChecked = _spec.FullArt;
        LegendaryCrownChk.IsChecked = _spec.LegendaryCrown;

        TextureStrengthBox.Text = _spec.TextureStrength.ToString(CultureInfo.InvariantCulture);
        SubBorderBox.Text = _spec.SubBorderThickness.ToString(CultureInfo.InvariantCulture);
        BorderBox.Text = _spec.BorderThickness.ToString(CultureInfo.InvariantCulture);
        CornerBox.Text = _spec.CornerRadius.ToString(CultureInfo.InvariantCulture);
        PanelRadiusBox.Text = _spec.PanelRadius.ToString(CultureInfo.InvariantCulture);

        ColBorder.Text = _spec.Colors.Border;
        ColFrame.Text = _spec.Colors.Frame;
        ColFrame2.Text = _spec.Colors.Frame2;
        ColPanel.Text = _spec.Colors.Panel;
        ColPanelBorder.Text = _spec.Colors.PanelBorder;

        // A representative legendary creature so the preview exercises the crown, mana, type, rules and P/T.
        _previewCard = new CardModel
        {
            Name = "Preview Dragon", ManaCost = "{2}{R}{R}", TypeLine = "Legendary Creature — Dragon",
            RulesText = "Flying, haste\n{T}: Add {R}.", FlavorText = "Live preview of your frame.",
            Power = "5", Toughness = "5", SetCode = "PRV", CollectorNumber = "7", Rarity = "M", Artist = "Preview",
        };
        const string art = "examples/01-real-cards-custom-art/art/swiftspear.png";
        if (File.Exists(art)) _previewCard.ArtPath = art;

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RefreshPreview(); };

        // Wire every control to the debounced preview (attached AFTER population so init doesn't fire them).
        foreach (var cb in new[] { StyleBox, TopEmblemBox, CrownStyleBox, TextureBox, TaperBox })
            cb.SelectionChanged += (_, _) => QueuePreview();
        foreach (var chk in new[] { ConnectedChk, BackgroundChk, FadedChk, EmbellishChk, PtBevelChk, RoyalChk, FullArtChk, LegendaryCrownChk })
            chk.Click += (_, _) => QueuePreview();
        foreach (var tb in new[] { TextureStrengthBox, SubBorderBox, BorderBox, CornerBox, PanelRadiusBox, ColBorder, ColFrame, ColFrame2, ColPanel, ColPanelBorder })
            tb.TextChanged += (_, _) => QueuePreview();

        _ready = true;
        RefreshPreview();

        // Fetch real Scryfall mana symbols in the background so the preview's pips match the exported card.
        // Unsubscribed and cancelled on close so a late callback can't touch a dead dialog.
        _symbols.Updated += OnSymbolsUpdated;
        _ = _symbols.PrimeAsync(ManaText.SymbolTokens(_previewCard.ManaCost, _previewCard.RulesText), _cts.Token);
    }

    private void OnSymbolsUpdated()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try { Dispatcher.BeginInvoke(new Action(RefreshPreview)); } catch { /* closing */ }
    }

    protected override void OnClosed(EventArgs e)
    {
        _symbols.Updated -= OnSymbolsUpdated;
        _cts.Cancel();
        _previewTimer.Stop();
        base.OnClosed(e);
    }

    private static string Norm(string? v, string fallback) =>
        string.IsNullOrWhiteSpace(v) ? fallback : v.Trim().ToLowerInvariant();

    private void QueuePreview()
    {
        if (!_ready) return;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    /// <summary>Renders the sample card with the current (unsaved) knob values, entirely in memory.</summary>
    private void RefreshPreview()
    {
        try
        {
            var s = _spec.Clone();
            ReadControlsInto(s);
            s.Normalize();
            var frame = FrameGenerator.GenerateBitmap(s);
            var tpl = new Template { Name = s.Name, Spec = s, FramePath = "", FrameImage = frame };
            PreviewImage.Source = _renderer.RenderToBitmap(_previewCard, tpl, supersample: 1);
        }
        catch { /* transient (e.g. a half-typed color hex) — keep the last good preview */ }
    }

    /// <summary>Reads every control's value into a spec (shared by the live preview and Apply).</summary>
    private void ReadControlsInto(TemplateSpec s)
    {
        s.FrameStyle = (string)StyleBox.SelectedItem;
        s.TopEmblem = (string)TopEmblemBox.SelectedItem;
        s.CrownStyle = (string)CrownStyleBox.SelectedItem;
        s.Texture = (string)TextureBox.SelectedItem;

        s.ConnectedPanels = ConnectedChk.IsChecked == true;
        s.PanelBackground = BackgroundChk.IsChecked == true;
        var taper = (string)TaperBox.SelectedItem ?? "partial";
        s.BottomTaper = taper != "none";
        if (taper != "none") s.TaperStyle = taper;
        s.FadedEdges = FadedChk.IsChecked == true;
        s.Embellishments = EmbellishChk.IsChecked == true;
        s.PtBevel = PtBevelChk.IsChecked == true;
        s.RoyalFrame = RoyalChk.IsChecked == true;
        s.FullArt = FullArtChk.IsChecked == true;
        s.LegendaryCrown = LegendaryCrownChk.IsChecked == true;

        s.TextureStrength = ParseD(TextureStrengthBox.Text, s.TextureStrength);
        s.SubBorderThickness = ParseD(SubBorderBox.Text, s.SubBorderThickness);
        s.BorderThickness = ParseD(BorderBox.Text, s.BorderThickness);
        s.CornerRadius = ParseD(CornerBox.Text, s.CornerRadius);
        s.PanelRadius = ParseD(PanelRadiusBox.Text, s.PanelRadius);

        s.Colors.Border = ColBorder.Text.Trim();
        s.Colors.Frame = ColFrame.Text.Trim();
        s.Colors.Frame2 = ColFrame2.Text.Trim();
        s.Colors.Panel = ColPanel.Text.Trim();
        s.Colors.PanelBorder = ColPanelBorder.Text.Trim();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        ReadControlsInto(_spec);
        _spec.Normalize();

        try
        {
            Directory.CreateDirectory(_templateDir);   // in case this is a template with no on-disk folder yet
            _spec.Save(Path.Combine(_templateDir, "template.json"));
            // The cached frame regenerates automatically (its hash no longer matches), but delete it
            // eagerly so the change is visible immediately.
            var framePath = Path.Combine(_templateDir, "frame.png");
            if (File.Exists(framePath)) File.Delete(framePath);
            var hashPath = framePath + ".hash";
            if (File.Exists(hashPath)) File.Delete(hashPath);
            AppliedTemplateName = _spec.Name;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ConfirmDialog.Show(this, "Frame design", "Could not save the template:\n" + ex.Message,
                affirmative: "OK");
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private static double ParseD(string s, double fallback) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
