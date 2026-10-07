using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private string _templateDir;              // where Apply writes; repointed by "Save as new…" (standalone)
    private readonly SymbolService _symbols = new();
    private readonly CardRenderer _renderer;
    private readonly CardModel _previewCard;
    private readonly DispatcherTimer _previewTimer;
    private readonly System.Threading.CancellationTokenSource _cts = new();
    private bool _ready;
    private string _savedName;   // the frame's name on disk, to tell an in-place rename apart

    /// <summary>Set when Apply saved the frame IN PLACE under a new name: the name it had before. Cards find
    /// their frame by name, so the caller must re-point the ones that used the old name.</summary>
    public string? RenamedFrom { get; private set; }

    /// <summary>Set to the edited template's name when Apply succeeds.</summary>
    public string? AppliedTemplateName { get; private set; }

    /// <summary>When opened via Show() (e.g. the --editframe CLI) rather than ShowDialog(): Apply saves
    /// in place and keeps the window open, and Cancel just closes — DialogResult is never touched.</summary>
    public bool Standalone { get; set; }

    /// <summary>True when the open frame is a shipped built-in: Apply then steers the user to "Save as new…"
    /// rather than silently overwriting a frame that would self-heal on the next launch anyway.</summary>
    public bool IsBuiltIn { get; set; }

    // Custom frames use the baked frame.png as-is — the procedural style knobs don't regenerate it.
    private readonly bool _isCustom;
    private readonly System.Windows.Media.Imaging.BitmapSource? _customFrameImage;
    private System.Windows.Media.Imaging.BitmapSource? _frameSrcImage;   // raw source for live fit (if any)

    // Interactive region-layout overlay (drag boxes on the preview). Scale is derived from the spec's
    // canvas so it stays correct even if a template isn't the usual 750x1050.
    // On-screen size of the design canvas. It follows the template's own aspect — 450x630 upright, 630x450
    // for a landscape (Battle/Plane) template — so a sideways frame isn't squashed into a portrait box.
    private double CanvasW = 450, CanvasH = 630;
    private double _sx = 450 / 750.0, _sy = 630 / 1050.0;
    private readonly List<RegionUi> _regions = new();
    private RegionUi? _sel;
    private int _drag;                              // 0 none, 1 move, 2 resize
    private int _mask;                              // resize edges: 1=left 2=right 4=top 8=bottom
    private Point _dragStart;
    private double _r0X, _r0Y, _r0W, _r0H;
    private bool _syncing;
    private readonly Stack<LayoutSnap> _undo = new();   // layout-only undo (regions + fonts + footer)
    private readonly Stack<LayoutSnap> _redo = new();

    // Preview viewport transform: scroll-wheel zoom (toward cursor) + middle-drag pan. _fitScale is the
    // scale that fits the card in the pane == "100%"; _viewScale is the current absolute scale.
    private double _viewScale = 1, _viewTx, _viewTy, _fitScale = 1;
    private bool _viewUserAdjusted;     // once true, a pane resize won't yank the view back to fit
    private bool _panning;
    private Point _panStart;
    private double _panTx0, _panTy0;

    /// <summary>A snapshot of the editable layout (region rects, per-region fonts, footer toggle) for undo.</summary>
    private sealed class LayoutSnap
    {
        public (double X, double Y, double W, double H)[] Regions = null!;
        public (double Size, string Color, bool Bold, bool Italic, bool Shadow)?[] Fonts = null!;
        public bool Footer;
    }

    public FrameDesignWindow(Template template, CardModel? previewCard = null)
    {
        InitializeComponent();
        _renderer = new CardRenderer(_symbols);
        _spec = template.Spec.Clone();
        _isCustom = _spec.CustomFrame;
        _savedName = _spec.Name;
        _customFrameImage = template.FrameImage;
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

        NameBox.Text = _spec.Name;
        FooterPlacementBox.ItemsSource = new[] { "frame", "border", "none" };
        FooterPlacementBox.SelectedItem = Norm(_spec.FooterPlacement, "frame");
        ManaSizeBox.Text = _spec.ManaSymbolSize.ToString(CultureInfo.InvariantCulture);
        RulesSizeBox.Text = _spec.RulesSymbolSize.ToString(CultureInfo.InvariantCulture);

        // Use the caller's card when given (so the editor previews the real card), else a representative
        // legendary creature that exercises the crown, mana, type, rules and P/T.
        if (previewCard != null)
        {
            _previewCard = previewCard.Clone();
        }
        else
        {
            _previewCard = new CardModel
            {
                Name = "Preview Dragon", ManaCost = "{2}{R}{R}", TypeLine = "Legendary Creature — Dragon",
                RulesText = "Flying, haste\n{T}: Add {R}.", FlavorText = "Live preview of your frame.",
                Power = "5", Toughness = "5", SetCode = "PRV", CollectorNumber = "7", Rarity = "M", Artist = "Preview",
            };
            const string art = "examples/01-real-cards-custom-art/art/swiftspear.png";
            if (File.Exists(art)) _previewCard.ArtPath = art;
        }

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); RefreshPreview(); };

        // Wire every control to the debounced preview (attached AFTER population so init doesn't fire them).
        foreach (var cb in new[] { StyleBox, TopEmblemBox, CrownStyleBox, TextureBox, TaperBox, FooterPlacementBox })
            cb.SelectionChanged += (_, _) => QueuePreview();
        foreach (var chk in new[] { ConnectedChk, BackgroundChk, FadedChk, EmbellishChk, PtBevelChk, RoyalChk, FullArtChk, LegendaryCrownChk })
            chk.Click += (_, _) => QueuePreview();
        foreach (var tb in new[] { TextureStrengthBox, SubBorderBox, BorderBox, CornerBox, PanelRadiusBox, ColBorder, ColFrame, ColFrame2, ColPanel, ColPanelBorder, NameBox, ManaSizeBox, RulesSizeBox })
            tb.TextChanged += (_, _) => QueuePreview();

        _ready = true;
        BuildLayoutEditor();
        RefreshPreview();

        // Fetch real Scryfall mana symbols in the background so the preview's pips match the exported card.
        // Unsubscribed and cancelled on close so a late callback can't touch a dead dialog.
        _symbols.Updated += OnSymbolsUpdated;
        _ = _symbols.PrimeAsync(ManaText.SymbolTokens(_previewCard.ManaCost, _previewCard.RulesText), _cts.Token);
    }

    private void OnSymbolsUpdated()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _renderer.ClearSymbolCaches();   // re-derive pip-sampled colors from the real symbol art
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
            var frame = (_isCustom && _frameSrcImage != null) ? CustomFrameComposer.Compose(s, _frameSrcImage)
                : (_isCustom && _customFrameImage != null) ? _customFrameImage   // baked custom frame
                : FrameGenerator.GenerateBitmap(s);
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

        if (!string.IsNullOrWhiteSpace(NameBox.Text)) s.Name = NameBox.Text.Trim();
        s.FooterPlacement = (string)(FooterPlacementBox.SelectedItem ?? "frame");
        s.ManaSymbolSize = ParseD(ManaSizeBox.Text, s.ManaSymbolSize);
        s.RulesSymbolSize = ParseD(RulesSizeBox.Text, s.RulesSymbolSize);

        // Custom-frame fit knobs (sliders; only present when a raw source image is loaded).
        if (_frameSrcImage != null)
        {
            s.FrameZoom = ZoomSlider.Value;
            s.FrameOffsetX = Math.Round(OffXSlider.Value);
            s.FrameOffsetY = Math.Round(OffYSlider.Value);
            s.FrameBorder = Math.Round(BorderSlider.Value);
            s.FrameBorderColor = string.IsNullOrWhiteSpace(FrameBorderColorBox.Text) ? s.FrameBorderColor : FrameBorderColorBox.Text.Trim();

            s.TranslucentEnabled = TranslucentChk.IsChecked == true;
            s.TranslucentAmount = Math.Clamp(TranslucentSlider.Value, 0, 1);
            s.TranslucentKey = string.IsNullOrWhiteSpace(TranslucentKeyBox.Text) ? s.TranslucentKey : TranslucentKeyBox.Text.Trim();
            s.TranslucentBacking = string.IsNullOrWhiteSpace(TranslucentBackingBox.Text) ? s.TranslucentBacking : TranslucentBackingBox.Text.Trim();
        }
    }

    private bool _fitSync;

    /// <summary>Two-way bind a slider and its editable number box: drag the slider or type a value.</summary>
    private void WireFit(System.Windows.Controls.Slider slider, TextBox num, string fmt)
    {
        num.Text = slider.Value.ToString(fmt, CultureInfo.InvariantCulture);
        slider.Focusable = true;
        slider.PreviewMouseLeftButtonDown += (_, _) => slider.Focus();   // so Left/Right arrows adjust it
        slider.ValueChanged += (_, _) =>
        {
            if (_fitSync) return;
            _fitSync = true;
            num.Text = slider.Value.ToString(fmt, CultureInfo.InvariantCulture);
            _fitSync = false;
            QueuePreview();
        };
        num.TextChanged += (_, _) =>
        {
            if (_fitSync) return;
            if (double.TryParse(num.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            {
                _fitSync = true;
                slider.Value = Math.Clamp(v, slider.Minimum, slider.Maximum);
                _fitSync = false;
                QueuePreview();
            }
        };
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        // Editing a shipped built-in overwrites the frame everyone starts from (it persists — self-healing
        // only recreates a built-in that's gone missing). Steer the user to save a new, editable copy that
        // leaves the original pristine; they can still choose to overwrite.
        if (IsBuiltIn)
        {
            var choice = ConfirmDialog.Show(this, "Built-in frame",
                $"“{_spec.Name}” is a built-in frame. Saving your own copy keeps the original intact and gives "
                + "you a frame you can freely edit and delete.",
                affirmative: "Save as new…", negative: "Overwrite built-in", cancel: "Cancel");
            if (choice == ConfirmResult.Cancel) return;
            if (choice == ConfirmResult.Affirmative) { OnSaveAsNew(sender, e); return; }
            // Negative → fall through and overwrite the built-in in place.
        }

        ReadControlsInto(_spec);
        _spec.Normalize();

        try
        {
            WriteTemplate(_templateDir, _templateDir);   // save in place
            AppliedTemplateName = _spec.Name;
            if (_spec.Name != _savedName) { RenamedFrom ??= _savedName; _savedName = _spec.Name; }
            if (Standalone)
            {
                Title = $"Frame layout — {_spec.Name}";
                FlashSaved();   // visible confirmation; DialogResult is illegal non-modally
            }
            else
                DialogResult = true;
        }
        catch (Exception ex)
        {
            ConfirmDialog.Show(this, "Frame design", "Could not save the template:\n" + ex.Message,
                affirmative: "OK");
        }
    }

    /// <summary>Saves the current edits as a BRAND-NEW template folder, leaving the original untouched.
    /// Prompts for a name, copies the frame's source bytes into the new folder, and selects it.</summary>
    private void OnSaveAsNew(object sender, RoutedEventArgs e)
    {
        ReadControlsInto(_spec);
        _spec.Normalize();

        var baseName = string.IsNullOrWhiteSpace(_spec.Name) ? "Custom" : _spec.Name;
        var ask = new InputDialog("Save frame as a new template",
            "Enter a name for the new frame. Your original frame stays unchanged.",
            baseName + " copy") { Owner = this };
        if (ask.ShowDialog() != true) return;
        var newName = ask.Value;
        if (newName.Length == 0) { return; }

        try
        {
            var srcDir = _templateDir;
            var newDir = TemplateImporter.UniqueTemplateDir(TextUtil.Slug(newName));
            _spec.Name = newName;
            WriteTemplate(newDir, srcDir);               // copies source/frame bytes from srcDir into newDir

            AppliedTemplateName = newName;
            if (Standalone)
            {
                // Keep editing, but now pointed at the new copy so further Applies save to it.
                _templateDir = newDir;
                NameBox.Text = newName;
                Title = $"Frame layout — {newName}";
                FlashSaved();
            }
            else
                DialogResult = true;   // the main window refreshes its list and selects the new template
        }
        catch (Exception ex)
        {
            ConfirmDialog.Show(this, "Frame design", "Could not save the new template:\n" + ex.Message,
                affirmative: "OK");
        }
    }

    /// <summary>Writes <see cref="_spec"/> + its frame.png into <paramref name="destDir"/>. When saving a copy
    /// (destDir != srcDir) the frame's raw source (or baked frame.png) is copied over from <paramref name="srcDir"/>
    /// so the new template is fully self-contained. Saving in place (destDir == srcDir) behaves exactly as before.</summary>
    private void WriteTemplate(string destDir, string srcDir)
    {
        Directory.CreateDirectory(destDir);   // in case this is a template with no on-disk folder yet
        bool sameDir = string.Equals(
            Path.GetFullPath(destDir).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(srcDir).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        var framePath = Path.Combine(destDir, "frame.png");

        if (_isCustom && _frameSrcImage != null && CustomFrameComposer.HasSource(_spec))
        {
            // Composited custom frame: rebuild frame.png from the source with the current fit knobs.
            var destSrc = Path.Combine(destDir, _spec.FrameSrc);
            if (!sameDir)
            {
                var fromSrc = Path.Combine(srcDir, _spec.FrameSrc);
                if (File.Exists(fromSrc)) File.Copy(fromSrc, destSrc, overwrite: true);
            }
            if (File.Exists(destSrc))
            {
                CustomFrameComposer.Generate(_spec, destSrc, framePath);
                try { File.WriteAllText(framePath + ".hash", CustomFrameComposer.FitHash(_spec, destSrc)); } catch { }
            }
        }
        else if (!_isCustom)
        {
            // Procedural frames regenerate from the spec, so drop the cache to show the change.
            if (File.Exists(framePath)) File.Delete(framePath);
            var hashPath = framePath + ".hash";
            if (File.Exists(hashPath)) File.Delete(hashPath);
        }
        else if (!sameDir)
        {
            // A plain baked custom frame (no source): copy its frame.png into the new folder as-is.
            var fromFrame = Path.Combine(srcDir, "frame.png");
            if (File.Exists(fromFrame)) File.Copy(fromFrame, framePath, overwrite: true);
        }
        // (same-dir baked custom keeps its existing frame.png untouched — unchanged from before.)

        _spec.Save(Path.Combine(destDir, "template.json"));
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (Standalone) Close();
        else DialogResult = false;
    }

    /// <summary>Flashes a green "Saved ✓ HH:mm:ss" in the footer so Apply has visible confirmation. Re-runs on
    /// every Apply (timestamp changes), so even repeated saves read as taking effect.</summary>
    private void FlashSaved()
    {
        SaveStatus.Text = $"Saved ✓   {DateTime.Now:HH:mm:ss}";
        var anim = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1.0,
            System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80))));
        anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1.0,
            System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1800))));
        anim.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0.0,
            System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))));
        SaveStatus.BeginAnimation(OpacityProperty, anim);
    }

    // --- interactive text-region layout -------------------------------------

    private sealed class RegionUi
    {
        public string Key = "", Label = "";
        public Func<Region> Get = null!;
        public Func<FontSpec?> Font = null!;
        public Border Box = null!;
        public Color Color;
    }

    /// <summary>Builds the draggable/resizable region boxes over the preview and wires the selected-region
    /// fields + the footer toggle. Each box edits the real spec region/font, so Apply persists it.</summary>
    private void BuildLayoutEditor()
    {
        if (_spec.IsLandscape) { CanvasW = 630; CanvasH = 450; }
        LayoutCanvas.Width = PreviewImage.Width = CanvasW;
        LayoutCanvas.Height = PreviewImage.Height = CanvasH;
        _sx = CanvasW / Math.Max(1, _spec.CanvasWidth);
        _sy = CanvasH / Math.Max(1, _spec.CanvasHeight);

        // A custom frame's artwork is baked in — the procedural style knobs don't change it, so disable them.
        CustomNote.Visibility = _isCustom ? Visibility.Visible : Visibility.Collapsed;
        if (_isCustom)
        {
            foreach (var c in new Control[]
            {
                StyleBox, TopEmblemBox, CrownStyleBox, LegendaryCrownChk, ConnectedChk, BackgroundChk,
                TaperBox, FadedChk, EmbellishChk, PtBevelChk, RoyalChk, FullArtChk,
                TextureBox, TextureStrengthBox, SubBorderBox, BorderBox,
            })
                c.IsEnabled = false;
        }

        // If this custom frame has a raw source image, enable live framing (zoom/pan/border).
        if (CustomFrameComposer.HasSource(_spec))
        {
            var srcPath = Path.Combine(_templateDir, _spec.FrameSrc);
            if (File.Exists(srcPath))
            {
                try { _frameSrcImage = CustomFrameComposer.LoadBitmap(srcPath); } catch { _frameSrcImage = null; }
            }
        }
        if (_frameSrcImage != null)
        {
            FrameFitPanel.Visibility = Visibility.Visible;
            CustomNote.Visibility = Visibility.Visible;   // still note the style knobs are inert
            ZoomSlider.Value = _spec.FrameZoom;
            OffXSlider.Value = Math.Clamp(_spec.FrameOffsetX, OffXSlider.Minimum, OffXSlider.Maximum);
            OffYSlider.Value = Math.Clamp(_spec.FrameOffsetY, OffYSlider.Minimum, OffYSlider.Maximum);
            BorderSlider.Value = Math.Clamp(_spec.FrameBorder, BorderSlider.Minimum, BorderSlider.Maximum);
            FrameBorderColorBox.Text = _spec.FrameBorderColor;
            WireFit(ZoomSlider, ZoomNum, "0.00");
            WireFit(OffXSlider, OffXNum, "0");
            WireFit(OffYSlider, OffYNum, "0");
            WireFit(BorderSlider, BorderNum, "0");
            FrameBorderColorBox.TextChanged += (_, _) => QueuePreview();

            TranslucentChk.IsChecked = _spec.TranslucentEnabled;
            TranslucentSlider.Value = Math.Clamp(_spec.TranslucentAmount, 0, 1);
            TranslucentKeyBox.Text = _spec.TranslucentKey;
            TranslucentBackingBox.Text = _spec.TranslucentBacking;
            WireFit(TranslucentSlider, TranslucentNum, "0.00");
            TranslucentChk.Click += (_, _) => QueuePreview();
            TranslucentKeyBox.TextChanged += (_, _) => QueuePreview();
            TranslucentBackingBox.TextChanged += (_, _) => QueuePreview();
        }

        PreviewKeyDown += OnEditorKeyDown;
        LayoutCanvas.PreviewMouseLeftButtonDown += OnCanvasPreviewDown;

        // Fit the preview to the pane on first layout, and keep it fitted on resize until the user
        // manually zooms/pans (then leave their view alone).
        PreviewHost.SizeChanged += (_, _) => { if (!_viewUserAdjusted) FitView(); };
        Dispatcher.BeginInvoke(new Action(FitView), DispatcherPriority.Loaded);

        ShowBoxesChk.Click += (_, _) => SetBoxesVisible(ShowBoxesChk.IsChecked == true);

        FooterSingleChk.IsChecked = _spec.FooterSingleLine;
        FooterSingleChk.Click += (_, _) => { PushUndo(); _spec.FooterSingleLine = FooterSingleChk.IsChecked == true; QueuePreview(); };

        void Add(string key, string label, Func<Region> get, Func<FontSpec?> font, byte r, byte g, byte b)
        {
            var col = Color.FromRgb(r, g, b);
            var brush = new SolidColorBrush(col); brush.Freeze();
            var tag = new TextBlock
            {
                Text = label, FontSize = 10, Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), Padding = new Thickness(3, 1, 3, 1),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            var grid = new Grid();
            grid.Children.Add(tag);
            var box = new Border
            {
                BorderBrush = brush, BorderThickness = new Thickness(1.5), Background = Brushes.Transparent,
                Cursor = Cursors.SizeAll, Child = grid,
            };
            var ui = new RegionUi { Key = key, Label = label, Get = get, Font = font, Box = box, Color = col };
            // Move is handled at the canvas level (OnCanvasPreviewDown) so overlapping boxes can cycle.

            void Handle(FrameworkElement h, int mask)
            {
                h.Tag = "resize";   // canvas click-handler skips these so resize works directly
                h.MouseLeftButtonDown += (_, e) => { BeginDrag(ui, 2, mask, e); e.Handled = true; };
                grid.Children.Add(h);
            }
            const double ew = 8, cs = 6;   // edge thickness, corner size
            // Edges (transparent, cursor-only) resize ONE axis; left=1 right=2 top=4 bottom=8.
            Handle(new Border { Width = ew, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent, Cursor = Cursors.SizeWE }, 1);
            Handle(new Border { Width = ew, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch, Background = Brushes.Transparent, Cursor = Cursors.SizeWE }, 2);
            Handle(new Border { Height = ew, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, Cursor = Cursors.SizeNS }, 4);
            Handle(new Border { Height = ew, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Bottom, Background = Brushes.Transparent, Cursor = Cursors.SizeNS }, 8);
            // Corners (visible nubs) resize both axes.
            Handle(new Border { Width = cs, Height = cs, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = brush, Cursor = Cursors.SizeNWSE }, 1 | 4);
            Handle(new Border { Width = cs, Height = cs, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Background = brush, Cursor = Cursors.SizeNESW }, 2 | 4);
            Handle(new Border { Width = cs, Height = cs, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Background = brush, Cursor = Cursors.SizeNESW }, 1 | 8);
            Handle(new Border { Width = cs, Height = cs, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Background = brush, Cursor = Cursors.SizeNWSE }, 2 | 8);

            LayoutCanvas.Children.Add(box);
            _regions.Add(ui);
            PlaceBox(ui);
        }

        Add("art", "Art", () => _spec.ArtWindow, () => null, 0xFF, 0xD3, 0x4E);
        Add("title", "Title", () => _spec.TitleBar, () => _spec.TitleFont, 0x54, 0xB0, 0xFF);
        Add("type", "Type", () => _spec.TypeBar, () => _spec.TypeFont, 0x8C, 0xE0, 0x6A);
        Add("text", "Rules", () => _spec.TextBox, () => _spec.RulesFont, 0xFF, 0x8C, 0x66);
        Add("pt", "P/T", () => _spec.PtBox, () => _spec.PtFont, 0xD0, 0x80, 0xFF);
        Add("credit", "Footer", () => _spec.CreditBar, () => _spec.CreditFont, 0x66, 0xD9, 0xD9);

        SelX.TextChanged += (_, _) => EditSel(r => r.X = ParseD(SelX.Text, r.X));
        SelY.TextChanged += (_, _) => EditSel(r => r.Y = ParseD(SelY.Text, r.Y));
        SelW.TextChanged += (_, _) => EditSel(r => r.W = Math.Max(8, ParseD(SelW.Text, r.W)));
        SelH.TextChanged += (_, _) => EditSel(r => r.H = Math.Max(8, ParseD(SelH.Text, r.H)));
        SelFontSize.TextChanged += (_, _) => EditFont(f => f.Size = Math.Max(1, ParseD(SelFontSize.Text, f.Size)));
        SelFontColor.TextChanged += (_, _) => EditFont(f => f.Color = SelFontColor.Text.Trim());
        SelBold.Click += (_, _) => EditFont(f => f.Bold = SelBold.IsChecked == true);
        SelItalic.Click += (_, _) => EditFont(f => f.Italic = SelItalic.IsChecked == true);
        SelShadow.Click += (_, _) => EditFont(f => f.Shadow = SelShadow.IsChecked == true);
    }

    private void PlaceBox(RegionUi ui)
    {
        var r = ui.Get();
        Canvas.SetLeft(ui.Box, r.X * _sx);
        Canvas.SetTop(ui.Box, r.Y * _sy);
        ui.Box.Width = Math.Max(2, r.W * _sx);
        ui.Box.Height = Math.Max(2, r.H * _sy);
    }

    private void BeginDrag(RegionUi ui, int mode, int mask, MouseButtonEventArgs e)
    {
        SelectRegion(ui);
        PushUndo();                 // one undo step per drag/resize (captured before it moves)
        _drag = mode; _mask = mask;
        _dragStart = e.GetPosition(LayoutCanvas);
        var r = ui.Get(); _r0X = r.X; _r0Y = r.Y; _r0W = r.W; _r0H = r.H;
        LayoutCanvas.CaptureMouse();
        e.Handled = true;
    }

    // Click to move/select; clicking again where boxes overlap cycles to the next one underneath.
    private void OnCanvasPreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && (string?)fe.Tag == "resize") return;   // let resize handles work
        var p = e.GetPosition(LayoutCanvas);
        double lx = p.X / _sx, ly = p.Y / _sy;
        var hits = new List<RegionUi>();
        for (int i = _regions.Count - 1; i >= 0; i--)   // topmost-first (later-added paints on top)
        {
            if (_regions[i].Box.Visibility != Visibility.Visible) continue;
            var r = _regions[i].Get();
            if (lx >= r.X && lx <= r.X + r.W && ly >= r.Y && ly <= r.Y + r.H) hits.Add(_regions[i]);
        }
        if (hits.Count == 0) return;
        int idx = _sel == null ? -1 : hits.IndexOf(_sel);
        var target = idx >= 0 ? hits[(idx + 1) % hits.Count] : hits[0];   // cycle, else topmost
        BeginDrag(target, 1, 0, e);
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == 0 || _sel == null) return;
        var p = e.GetPosition(LayoutCanvas);
        double dx = (p.X - _dragStart.X) / _sx, dy = (p.Y - _dragStart.Y) / _sy;
        var r = _sel.Get();
        if (_drag == 1)
        {
            r.X = Math.Clamp(_r0X + dx, 0, Math.Max(0, _spec.CanvasWidth - r.W));
            r.Y = Math.Clamp(_r0Y + dy, 0, Math.Max(0, _spec.CanvasHeight - r.H));
        }
        else
        {
            double x = _r0X, y = _r0Y, w = _r0W, h = _r0H;
            if ((_mask & 1) != 0) { double right = _r0X + _r0W; x = Math.Clamp(_r0X + dx, 0, right - 8); w = right - x; }
            else if ((_mask & 2) != 0) { w = Math.Clamp(_r0W + dx, 8, _spec.CanvasWidth - _r0X); }
            if ((_mask & 4) != 0) { double bottom = _r0Y + _r0H; y = Math.Clamp(_r0Y + dy, 0, bottom - 8); h = bottom - y; }
            else if ((_mask & 8) != 0) { h = Math.Clamp(_r0H + dy, 8, _spec.CanvasHeight - _r0Y); }
            r.X = x; r.Y = y; r.W = w; r.H = h;
        }
        PlaceBox(_sel);
        SyncSelFields();
        QueuePreview();
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        _drag = 0;
        LayoutCanvas.ReleaseMouseCapture();
    }

    private void SelectRegion(RegionUi ui)
    {
        _sel = ui;
        foreach (var r in _regions)
        {
            r.Box.BorderThickness = new Thickness(r == ui ? 3 : 1.5);
            Panel.SetZIndex(r.Box, r == ui ? 1 : 0);   // selected box on top so it's always grabbable
        }
        SelRegionLabel.Text = "Selected box: " + ui.Label;
        SelFontPanel.IsEnabled = ui.Font() != null;
        LayoutCanvas.Focus();   // so arrow-key nudging goes to the canvas, not a text field
        SyncSelFields();
    }

    private void SyncSelFields()
    {
        if (_sel == null) return;
        _syncing = true;
        var r = _sel.Get();
        SelX.Text = ((int)Math.Round(r.X)).ToString(CultureInfo.InvariantCulture);
        SelY.Text = ((int)Math.Round(r.Y)).ToString(CultureInfo.InvariantCulture);
        SelW.Text = ((int)Math.Round(r.W)).ToString(CultureInfo.InvariantCulture);
        SelH.Text = ((int)Math.Round(r.H)).ToString(CultureInfo.InvariantCulture);
        var f = _sel.Font();
        if (f != null)
        {
            SelFontSize.Text = ((int)Math.Round(f.Size)).ToString(CultureInfo.InvariantCulture);
            SelFontColor.Text = f.Color;
            SelBold.IsChecked = f.Bold;
            SelItalic.IsChecked = f.Italic;
            SelShadow.IsChecked = f.Shadow;
        }
        _syncing = false;
    }

    private void EditSel(Action<Region> apply)
    {
        if (_syncing || _sel == null) return;
        PushUndo();
        apply(_sel.Get());
        PlaceBox(_sel);
        QueuePreview();
    }

    private void EditFont(Action<FontSpec> apply)
    {
        if (_syncing || _sel == null) return;
        var f = _sel.Font(); if (f == null) return;
        PushUndo();
        apply(f);
        QueuePreview();
    }

    // --- undo / redo (layout only: regions + fonts + footer) -----------------

    private LayoutSnap Capture()
    {
        var s = new LayoutSnap
        {
            Footer = _spec.FooterSingleLine,
            Regions = new (double, double, double, double)[_regions.Count],
            Fonts = new (double, string, bool, bool, bool)?[_regions.Count],
        };
        for (int i = 0; i < _regions.Count; i++)
        {
            var r = _regions[i].Get(); s.Regions[i] = (r.X, r.Y, r.W, r.H);
            var f = _regions[i].Font();
            s.Fonts[i] = f == null ? null : (f.Size, f.Color, f.Bold, f.Italic, f.Shadow);
        }
        return s;
    }

    private void ApplyLayout(LayoutSnap s)
    {
        _spec.FooterSingleLine = s.Footer;
        FooterSingleChk.IsChecked = s.Footer;
        for (int i = 0; i < _regions.Count; i++)
        {
            var r = _regions[i].Get(); var v = s.Regions[i];
            r.X = v.X; r.Y = v.Y; r.W = v.W; r.H = v.H;
            var f = _regions[i].Font(); var fv = s.Fonts[i];
            if (f != null && fv != null)
            {
                f.Size = fv.Value.Size; f.Color = fv.Value.Color;
                f.Bold = fv.Value.Bold; f.Italic = fv.Value.Italic; f.Shadow = fv.Value.Shadow;
            }
            PlaceBox(_regions[i]);
        }
        SyncSelFields();
        QueuePreview();
    }

    private static bool SnapsEqual(LayoutSnap a, LayoutSnap b)
    {
        if (a.Footer != b.Footer || a.Regions.Length != b.Regions.Length) return false;
        for (int i = 0; i < a.Regions.Length; i++)
            if (a.Regions[i] != b.Regions[i] || a.Fonts[i] != b.Fonts[i]) return false;
        return true;
    }

    private void PushUndo()
    {
        var s = Capture();
        if (_undo.Count > 0 && SnapsEqual(_undo.Peek(), s)) return;   // skip no-op snapshots
        _undo.Push(s);
        _redo.Clear();
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Capture());
        ApplyLayout(_undo.Pop());
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Capture());
        ApplyLayout(_redo.Pop());
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && e.Key == Key.Z && !shift) { Undo(); e.Handled = true; return; }
        if (ctrl && (e.Key == Key.Y || (e.Key == Key.Z && shift))) { Redo(); e.Handled = true; return; }
        // Esc discards the whole layout session, so don't fire it while the user is typing in a field —
        // there Esc is the usual "get me out of this box" reflex, not "throw away my work".
        if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox)
        { OnCancel(this, new RoutedEventArgs()); e.Handled = true; return; }
        if (e.Key == Key.Enter && Keyboard.FocusedElement is not TextBox) { OnApply(this, new RoutedEventArgs()); e.Handled = true; return; }

        bool typing = Keyboard.FocusedElement is TextBox;

        // Reset the preview to fit-the-pane (100%): Ctrl+0 or F.
        if ((ctrl && (e.Key == Key.D0 || e.Key == Key.NumPad0)) || (!ctrl && e.Key == Key.F && !typing))
        { FitView(); e.Handled = true; return; }

        // Toggle the draggable layout boxes.
        if (!ctrl && e.Key == Key.B && !typing)
        { SetBoxesVisible(!(ShowBoxesChk.IsChecked == true)); e.Handled = true; return; }

        // Arrow-key nudge of the selected box (1px, or 10px with Shift) — ONLY when the canvas has focus,
        // so a focused slider/field gets the arrows instead.
        if (_sel != null && ReferenceEquals(Keyboard.FocusedElement, LayoutCanvas) &&
            e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            double step = shift ? 10 : 1;
            PushUndo();
            var r = _sel.Get();
            if (e.Key == Key.Left) r.X = Math.Clamp(r.X - step, 0, Math.Max(0, _spec.CanvasWidth - r.W));
            else if (e.Key == Key.Right) r.X = Math.Clamp(r.X + step, 0, Math.Max(0, _spec.CanvasWidth - r.W));
            else if (e.Key == Key.Up) r.Y = Math.Clamp(r.Y - step, 0, Math.Max(0, _spec.CanvasHeight - r.H));
            else r.Y = Math.Clamp(r.Y + step, 0, Math.Max(0, _spec.CanvasHeight - r.H));
            PlaceBox(_sel);
            SyncSelFields();
            QueuePreview();
            e.Handled = true;
        }
    }

    /// <summary>Show/hide the draggable region boxes (shared by the checkbox and the B hotkey).</summary>
    private void SetBoxesVisible(bool on)
    {
        if (ShowBoxesChk.IsChecked != on) ShowBoxesChk.IsChecked = on;
        var vis = on ? Visibility.Visible : Visibility.Collapsed;
        foreach (var r in _regions) r.Box.Visibility = vis;
    }

    /// <summary>Pushes the current zoom/pan into the preview's render transform and updates the % badge.</summary>
    private void ApplyView()
    {
        if (ViewXform == null) return;
        ViewXform.Matrix = new Matrix(_viewScale, 0, 0, _viewScale, _viewTx, _viewTy);
        if (ZoomReadout != null)
            ZoomReadout.Text = Math.Round(_viewScale / Math.Max(1e-6, _fitScale) * 100) + "%";
    }

    /// <summary>Scales the card to fit the pane (centered) and treats that as 100%.</summary>
    private void FitView()
    {
        double hw = PreviewHost.ActualWidth, hh = PreviewHost.ActualHeight;
        if (hw < 1 || hh < 1) return;
        const double pad = 24;
        _fitScale = Math.Max(0.05, Math.Min((hw - pad) / CanvasW, (hh - pad) / CanvasH));
        _viewScale = _fitScale;
        _viewTx = (hw - CanvasW * _viewScale) / 2;
        _viewTy = (hh - CanvasH * _viewScale) / 2;
        _viewUserAdjusted = false;
        ApplyView();
    }

    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        double f = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        double ns = Math.Clamp(_viewScale * f, _fitScale * 0.25, _fitScale * 8);
        if (Math.Abs(ns - _viewScale) < 1e-6) return;
        var p = e.GetPosition(PreviewHost);
        // Keep the card point under the cursor pinned while zooming.
        double cx = (p.X - _viewTx) / _viewScale, cy = (p.Y - _viewTy) / _viewScale;
        _viewScale = ns;
        _viewTx = p.X - cx * ns;
        _viewTy = p.Y - cy * ns;
        _viewUserAdjusted = true;
        ApplyView();
        e.Handled = true;
    }

    private void OnPreviewPanDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        _panning = true;
        _panStart = e.GetPosition(PreviewHost);
        _panTx0 = _viewTx; _panTy0 = _viewTy;
        PreviewHost.CaptureMouse();
        e.Handled = true;
    }

    private void OnPreviewPanMove(object sender, MouseEventArgs e)
    {
        if (!_panning) return;
        var p = e.GetPosition(PreviewHost);
        _viewTx = _panTx0 + (p.X - _panStart.X);
        _viewTy = _panTy0 + (p.Y - _panStart.Y);
        _viewUserAdjusted = true;
        ApplyView();
    }

    private void OnPreviewPanUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || !_panning) return;
        _panning = false;
        PreviewHost.ReleaseMouseCapture();
    }

    private static double ParseD(string s, double fallback) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
