using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>
/// Composites a finished card: white art backing -> custom art (cover-fit, pan/zoom) ->
/// frame overlay -> title + mana -> type line -> rules text (with inline symbols) -> power/toughness.
/// Renders to a BitmapSource; the preview uses supersample=1, export uses a higher factor.
/// </summary>
public sealed class CardRenderer
{
    private readonly SymbolService _symbols;

    // Decoded-art cache so live preview doesn't re-read/decode the image on every keystroke.
    private readonly Dictionary<string, (long stamp, BitmapImage img)> _artCache = new();
    private const int ArtCacheMax = 12;

    public CardRenderer(SymbolService symbols) => _symbols = symbols;

    /// <param name="previewHints">
    /// When true (the live preview), an empty art window shows a subtle "add art" placeholder.
    /// Always false for exports, so a saved PNG never has hint text baked in.
    /// </param>
    public BitmapSource RenderToBitmap(CardModel card, Template template, int supersample = 1, bool previewHints = false)
    {
        var spec = template.Spec;
        int w = spec.CanvasWidth * supersample;
        int h = spec.CanvasHeight * supersample;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(supersample, supersample));
            Draw(dc, card, template, previewHints);
            dc.Pop();
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private void Draw(DrawingContext dc, CardModel card, Template template, bool previewHints)
    {
        // When the royal sub-border is on, all regions are inset to make room for it — use the same inset
        // spec the frame generator baked frame.png from, so text/P·T/footer line up with the panels.
        var spec = template.Spec.WithSubBorderApplied();
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        double cardR = Math.Max(4, spec.CornerRadius);

        // Round the whole card's corners on EVERY style: clip all art + frame drawing to the card
        // silhouette so no style shows square corners.
        var cardClip = new RectangleGeometry(new Rect(0, 0, W, H), cardR, cardR);
        cardClip.Freeze();
        dc.PushClip(cardClip);

        // Full-art, borderless and overlay let the art cover the whole card; framed styles clip to the window.
        bool fullBleed = ArtText(spec);
        var artRegion = fullBleed ? new Region { X = 0, Y = 0, W = W, H = H } : spec.ArtWindow;
        DrawArt(dc, card, artRegion, previewHints);

        // Frame overlay (art shows through its transparent window / floating panels).
        dc.DrawImage(template.FrameImage, new Rect(0, 0, W, H));

        // On plain full-art cards, lay subtle scrims behind the text so it stays readable on any art.
        // (borderless/overlay bake their own panels/band into the frame.)
        if (spec.FullArt) DrawScrims(dc, spec);

        DrawTitleAndMana(dc, card, spec);
        DrawLegendaryCrown(dc, card, spec);
        DrawTypeLine(dc, card, spec);

        if (card.IsPlaneswalker)
            DrawBadgedRows(dc, ParseAbilities(card.RulesText), spec, loyaltyShields: true);
        else if (card.IsSaga)
            DrawBadgedRows(dc, ParseChapters(card.RulesText), spec);
        else if (card.IsClass)
            DrawBadgedRows(dc, ParseClassLevels(card.RulesText), spec);
        else if (card.IsAdventure)
            DrawAdventure(dc, card, spec);
        else
            DrawTextBox(dc, card.RulesText, card.FlavorText, spec.EffectiveTextBox, spec.RulesFont, spec.FlavorFont, spec.RulesSymbolSize);

        if (card.IsPlaneswalker && !string.IsNullOrWhiteSpace(card.Loyalty))
            DrawLoyalty(dc, card, spec);
        else if (card.HasPowerToughness)
            DrawPtBox(dc, card, spec);   // creatures only — the box is drawn here, not baked into the frame

        // Footer placement: "frame" draws it on the colored card (inside the clip, before the border);
        // "border" draws it on the black rim (after the border); "none" skips it.
        var footerPlacement = (spec.FrameStyle == null ? "frame" : spec.FooterPlacement ?? "frame").Trim().ToLowerInvariant();
        if (footerPlacement == "frame")
            DrawFooter(dc, card, spec, onBorder: false);

        dc.Pop();   // end rounded-card clip

        // A single, consistent thick black outer border on top of every style — the real-card edge, even
        // thickness all the way around.
        DrawOuterBorder(dc, W, H, cardR, spec);

        if (footerPlacement == "border")
            DrawFooter(dc, card, spec, onBorder: true);
    }

    /// <summary>The chunky black card edge every real card has — drawn last, over all styles.</summary>
    private static void DrawOuterBorder(DrawingContext dc, double W, double H, double cardR, TemplateSpec spec)
    {
        // Template-driven thickness (custom-editable). Drawn as a filled ring between two rounded
        // rects. For EVEN thickness around the corners, the inner corner must share the outer corner's
        // center — which means innerRadius = cardR - t exactly (both arcs centered at (cardR, cardR)).
        double t = Math.Max(0, spec.BorderThickness);
        if (t <= 0) return;
        double innerR = Math.Max(0, cardR - t);
        var outer = new RectangleGeometry(new Rect(0, 0, W, H), cardR, cardR);
        var inner = new RectangleGeometry(
            new Rect(t, t, Math.Max(0, W - 2 * t), Math.Max(0, H - 2 * t)), innerR, innerR);
        var ring = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);
        ring.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x0A)), null, ring);
    }

    // --- type line + rarity pip --------------------------------------------

    private static void DrawTypeLine(DrawingContext dc, CardModel card, TemplateSpec spec)
    {
        var bar = ToRect(spec.TypeBar);
        double pad = 16;
        double pipD = bar.Height * 0.62;
        bool hasRarity = !string.IsNullOrWhiteSpace(card.Rarity);
        double rightLimit = hasRarity ? bar.Right - pad - pipD - 8 : bar.Right - pad;

        if (!string.IsNullOrWhiteSpace(card.TypeLine))
        {
            var brush = new SolidColorBrush(TemplateSpec.ParseColor(spec.TypeFont.Color));
            var ft = FitText(card.TypeLine, spec.TypeFont, spec.TypeFont.Size, 12, rightLimit - (bar.X + pad), brush);
            DrawGlyphRun(dc, ft, new Point(bar.X + pad, bar.Y + (bar.Height - ft.Height) / 2), spec.TypeFont);
        }

        if (hasRarity)
        {
            var box = new Rect(bar.Right - pad - pipD, bar.Y + (bar.Height - pipD) / 2, pipD, pipD);
            var custom = TryLoadSetSymbol(card.SetSymbolPath);
            if (custom != null)
                dc.DrawImage(custom, box);   // custom set icon (whole set shares it)
            else
            {
                var (fill, _) = RarityStyle(card.Rarity);
                DrawSetSymbol(dc, box, fill, card.SetCode);
            }
        }
    }

    // Small cache for custom set-symbol images (keyed by path + file stamp), so batch renders don't
    // re-decode the same icon for every card. Static and shared across renderers on multiple STA threads
    // (live preview + export), so it must be concurrent-safe.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long stamp, BitmapSource img)> _setSymbolCache = new();

    private static BitmapSource? TryLoadSetSymbol(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full)) return null;
            var fi = new FileInfo(full);
            long stamp = fi.LastWriteTimeUtc.Ticks ^ fi.Length;
            if (_setSymbolCache.TryGetValue(full, out var e) && e.stamp == stamp) return e.img;

            var bytes = File.ReadAllBytes(full);
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
            if (_setSymbolCache.Count > 32) _setSymbolCache.Clear();   // simple bound; a set shares one icon
            _setSymbolCache[full] = (stamp, img);
            return img;
        }
        catch { return null; }
    }

    private static (Color fill, Color text) RarityStyle(string rarity) => rarity.ToUpperInvariant() switch
    {
        "U" => (Color.FromRgb(0x9F, 0xB2, 0xC4), Color.FromRgb(0x14, 0x20, 0x2C)),
        "R" => (Color.FromRgb(0xC9, 0xA2, 0x27), Color.FromRgb(0x3A, 0x2E, 0x06)),
        "M" => (Color.FromRgb(0xD2, 0x4E, 0x1E), Colors.White),
        _ => (Color.FromRgb(0x1C, 0x1C, 0x1C), Colors.White),   // common
    };

    /// <summary>Draws a faceted, beveled "set symbol" emblem colored by rarity. The silhouette varies
    /// by set code (gem / compass star / shield / rosette) so different sets look distinct.</summary>
    private static void DrawSetSymbol(DrawingContext dc, Rect box, Color color, string? seed)
    {
        double d = Math.Min(box.Width, box.Height);
        double cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2;
        double R = d * 0.5;

        static Color Dk(Color c, double a) =>
            Color.FromRgb((byte)(c.R * (1 - a)), (byte)(c.G * (1 - a)), (byte)(c.B * (1 - a)));

        var fill = new LinearGradientBrush(LightenC(color, 0.55), Dk(color, 0.20), new Point(0.3, 0), new Point(0.65, 1));
        fill.Freeze();
        var edge = new Pen(new SolidColorBrush(Dk(color, 0.55)), Math.Max(1.0, d * 0.07));
        edge.Freeze();
        var facetPen = new Pen(new SolidColorBrush(Color.FromArgb(105, 0, 0, 0)), Math.Max(0.7, d * 0.03));
        facetPen.Freeze();

        int shape = 0;
        if (!string.IsNullOrEmpty(seed)) { int h = 0; foreach (var ch in seed) h = h * 31 + ch; shape = Math.Abs(h) % 4; }

        var facets = new List<(Point, Point)>();
        var geo = new StreamGeometry();
        using (var s = geo.Open())
        {
            switch (shape)
            {
                case 0:   // faceted brilliant gem
                {
                    double w = R * 0.9, h = R;
                    Point tl = new(cx - w * 0.5, cy - h * 0.62), tr = new(cx + w * 0.5, cy - h * 0.62);
                    Point rr = new(cx + w, cy - h * 0.02), bb = new(cx, cy + h), ll = new(cx - w, cy - h * 0.02);
                    s.BeginFigure(tl, true, true);
                    s.LineTo(tr, true, false); s.LineTo(rr, true, false); s.LineTo(bb, true, false); s.LineTo(ll, true, false);
                    facets.Add((ll, rr)); facets.Add((tl, bb)); facets.Add((tr, bb)); facets.Add((new(cx, cy - h * 0.62), bb));
                    break;
                }
                case 1:   // 4-point compass star
                {
                    double a = R, b = R * 0.34;
                    s.BeginFigure(new(cx, cy - a), true, true);
                    s.LineTo(new(cx + b, cy - b), true, false); s.LineTo(new(cx + a, cy), true, false);
                    s.LineTo(new(cx + b, cy + b), true, false); s.LineTo(new(cx, cy + a), true, false);
                    s.LineTo(new(cx - b, cy + b), true, false); s.LineTo(new(cx - a, cy), true, false);
                    s.LineTo(new(cx - b, cy - b), true, false);
                    facets.Add((new(cx, cy - a), new(cx, cy + a))); facets.Add((new(cx - a, cy), new(cx + a, cy)));
                    break;
                }
                case 2:   // shield / crest
                {
                    double w = R * 0.82;
                    s.BeginFigure(new(cx - w, cy - R * 0.8), true, true);
                    s.LineTo(new(cx + w, cy - R * 0.8), true, false);
                    s.LineTo(new(cx + w, cy + R * 0.15), true, false);
                    s.QuadraticBezierTo(new(cx + w, cy + R * 0.72), new(cx, cy + R), true, false);
                    s.QuadraticBezierTo(new(cx - w, cy + R * 0.72), new(cx - w, cy + R * 0.15), true, false);
                    facets.Add((new(cx, cy - R * 0.8), new(cx, cy + R * 0.92)));
                    break;
                }
                default:  // hexagon rosette
                {
                    for (int i = 0; i < 6; i++)
                    {
                        double ang = Math.PI / 6 + i * Math.PI / 3;
                        var p = new Point(cx + R * Math.Cos(ang), cy + R * Math.Sin(ang));
                        if (i == 0) s.BeginFigure(p, true, true); else s.LineTo(p, true, false);
                    }
                    facets.Add((new(cx, cy - R), new(cx, cy + R)));
                    break;
                }
            }
        }
        geo.Freeze();
        dc.DrawGeometry(fill, edge, geo);
        foreach (var (a, bpt) in facets) dc.DrawLine(facetPen, a, bpt);

        // Sparkle highlight, upper-left.
        var hi = new Pen(new SolidColorBrush(Color.FromArgb(175, 255, 255, 255)), Math.Max(0.9, d * 0.06));
        hi.Freeze();
        dc.DrawLine(hi, new Point(cx - R * 0.4, cy - R * 0.42), new Point(cx - R * 0.05, cy - R * 0.55));
    }

    // --- footer (collector / rarity / set / artist) ------------------------

    private static void DrawFooter(DrawingContext dc, CardModel card, TemplateSpec spec, bool onBorder)
    {
        // "border" placement: a single tidy row sitting on the black bottom border (the border size is
        // NOT changed — the text just rides on the existing rim in light ink).
        if (onBorder)
        {
            double t = Math.Max(0, spec.BorderThickness);
            if (t <= 8) return;
            double bw = spec.CanvasWidth, bh = spec.CanvasHeight;
            var line = string.Join("   ", new[] { BuildCollectorLine(card), BuildCreditLine(card) }.Where(s => s.Length > 0));
            if (line.Length == 0) return;
            var bfont = new FontSpec
            {
                Family = spec.CreditFont.Family, Size = Math.Min(spec.CreditFont.Size, t - 12),
                Italic = spec.CreditFont.Italic, Align = "left", Color = "#ECECEC",
            };
            var bbrush = new SolidColorBrush(TemplateSpec.ParseColor(bfont.Color));
            var band = new Rect(t + 12, bh - t, bw - 2 * (t + 12), t);
            var bft = FitText(line, bfont, bfont.Size, 7, band.Width, bbrush);
            dc.DrawText(bft, new Point(band.X, band.Y + (band.Height - bft.Height) / 2));
            return;
        }

        var bar = ToRect(spec.CreditBar);

        // Pick a footer color that reads against whatever it sits on. On framed styles it sits on the
        // frame color; if that's dark, switch to a light, shadowed footer so it never disappears.
        var font = spec.CreditFont;
        // Pick a color that reads against the frame the footer sits on.
        if (!ArtText(spec))
        {
            var bg = TemplateSpec.ParseColor(spec.Colors.Frame);
            double lum = 0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B;
            if (lum < 120)
                font = new FontSpec
                {
                    Family = font.Family, Size = font.Size, Italic = font.Italic, Bold = font.Bold,
                    Align = font.Align, Color = "#F3ECDD", Shadow = true, ShadowColor = "#000000",
                };
        }
        var brush = new SolidColorBrush(TemplateSpec.ParseColor(font.Color));

        // Two tidy tiny lines (collector, then artist), kept small on the card face.
        var lines = new[] { BuildCollectorLine(card), BuildCreditLine(card) }
            .Where(s => s.Length > 0).ToList();
        if (lines.Count == 0) return;

        double lh = font.Size + 2;
        double totalH = lh * lines.Count;
        double y = bar.Y + Math.Max(0, (bar.Height - totalH) / 2);
        foreach (var line in lines)
        {
            var ft = FitText(line, font, font.Size, 8, bar.Width, brush);
            DrawGlyphRun(dc, ft, new Point(bar.X, y), font);
            y += lh;
        }
    }

    internal static string BuildCollectorLine(CardModel card)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(card.CollectorNumber)) parts.Add(card.CollectorNumber.Trim());
        if (!string.IsNullOrWhiteSpace(card.Rarity)) parts.Add(card.Rarity.ToUpperInvariant());
        var left = string.Join(" ", parts);
        var tail = new List<string>();
        if (!string.IsNullOrWhiteSpace(card.SetCode)) tail.Add(card.SetCode.ToUpperInvariant());
        if (left.Length > 0 || tail.Count > 0) tail.Add("EN");
        var right = string.Join(" • ", tail);
        return string.Join(left.Length > 0 && right.Length > 0 ? " • " : "", new[] { left, right }.Where(s => s.Length > 0));
    }

    internal static string BuildCreditLine(CardModel card)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(card.Artist)) parts.Add("Illus. " + card.Artist.Trim());
        if (!string.IsNullOrWhiteSpace(card.Copyright)) parts.Add(card.Copyright.Trim());
        return string.Join("  •  ", parts);
    }

    // --- art ----------------------------------------------------------------

    private void DrawArt(DrawingContext dc, CardModel card, Region win, bool previewHints)
    {
        var rect = ToRect(win);
        dc.DrawRectangle(Brushes.White, null, rect);   // backing so window is never empty

        BitmapImage? img = null;
        if (!string.IsNullOrWhiteSpace(card.ArtPath))
        {
            try { img = LoadArt(Path.GetFullPath(card.ArtPath)); } catch { img = null; }
        }
        if (img == null || img.PixelWidth <= 0 || img.PixelHeight <= 0)
        {
            if (previewHints) DrawArtPlaceholder(dc, rect);   // preview only — never in exports
            return;
        }

        double iw = img.PixelWidth, ih = img.PixelHeight;

        double cover = Math.Max(rect.Width / iw, rect.Height / ih);
        double scale = cover * Math.Max(0.1, card.ArtScale);
        double dw = iw * scale, dh = ih * scale;
        double x = rect.X + (rect.Width - dw) / 2 + card.ArtOffsetX * rect.Width;
        double y = rect.Y + (rect.Height - dh) / 2 + card.ArtOffsetY * rect.Height;

        dc.PushClip(new RectangleGeometry(rect));
        dc.DrawImage(img, new Rect(x, y, dw, dh));
        dc.Pop();
    }

    /// <summary>A soft empty-state hint drawn in the art window in the live preview only.</summary>
    private static void DrawArtPlaceholder(DrawingContext dc, Rect rect)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xEC, 0xED, 0xF0)), null, rect);
        var ft = new FormattedText(
            "Add art — Change art…, paste, or drag an image in",
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            20, new SolidColorBrush(Color.FromRgb(0xA6, 0xAA, 0xB2)), 1.0)
        {
            MaxTextWidth = Math.Max(10, rect.Width - 60),
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        dc.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
    }

    /// <summary>Loads (and caches) the decoded art image, keyed by path + file stamp.</summary>
    private BitmapImage? LoadArt(string path)
    {
        if (!File.Exists(path)) return null;
        long stamp;
        try { var fi = new FileInfo(path); stamp = fi.LastWriteTimeUtc.Ticks ^ fi.Length; }
        catch { stamp = 0; }

        if (_artCache.TryGetValue(path, out var e) && e.stamp == stamp) return e.img;

        BitmapImage img;
        try
        {
            // Read bytes first so the file isn't locked and a decode error can't hold a handle.
            var bytes = File.ReadAllBytes(path);
            img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.StreamSource = new MemoryStream(bytes);
            img.EndInit();
            img.Freeze();
        }
        catch { return null; }

        if (_artCache.Count >= ArtCacheMax) _artCache.Clear();   // simple bound
        _artCache[path] = (stamp, img);
        return img;
    }

    /// <summary>Translucent panels behind the text areas so full-art text stays legible.</summary>
    private static void DrawScrims(DrawingContext dc, TemplateSpec spec)
    {
        var scrim = new SolidColorBrush(Color.FromArgb(0xB0, 0x0A, 0x0B, 0x0F));
        scrim.Freeze();
        const double r = 10, grow = 4;

        // Title is a full-width color row that runs right up under the black border: it spans from the
        // very top edge (0,0) across the full width, so the border (drawn later, on top) overlaps it
        // uniformly on the top and both sides with no sliver of art peeking through above the scrim.
        var titleBar = ToRect(spec.TitleBar);
        dc.DrawRectangle(scrim, null,
            new Rect(0, 0, spec.CanvasWidth, titleBar.Bottom + grow));

        // One panel spanning the type line down through the rules box.
        var type = ToRect(spec.TypeBar);
        var text = ToRect(spec.TextBox);
        var lower = new Rect(Math.Min(type.X, text.X) - grow, type.Y - grow,
            Math.Max(type.Width, text.Width) + 2 * grow,
            (text.Bottom - type.Y) + 2 * grow);
        dc.DrawRoundedRectangle(scrim, null, lower, r, r);
    }

    /// <summary>Draws text, first stroking a contrasting outline behind it when the font asks for it.</summary>
    private static void DrawGlyphRun(DrawingContext dc, FormattedText ft, Point origin, FontSpec font)
    {
        if (font.Shadow)
        {
            var geo = ft.BuildGeometry(origin);
            var pen = new Pen(new SolidColorBrush(TemplateSpec.ParseColor(font.ShadowColor)),
                Math.Max(1.4, ft.Height * 0.08))
            { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }
        dc.DrawText(ft, origin);
    }

    private static Rect Inset(Rect r, double by)
        => new(r.X + by, r.Y + by, Math.Max(0, r.Width - 2 * by), Math.Max(0, r.Height - 2 * by));

    // --- title + mana -------------------------------------------------------

    private void DrawTitleAndMana(DrawingContext dc, CardModel card, TemplateSpec spec)
    {
        var bar = ToRect(spec.TitleBar);
        double pad = 16;

        // Mana symbols, right-aligned. Accept loosely-typed costs like "2RW" too.
        var manaTokens = ManaText.Tokenize(ManaText.NormalizeCost(card.ManaCost)).Where(t => t.IsSymbol).ToList();
        double symSize = spec.ManaSymbolSize;
        double symGap = symSize * 0.08;
        double manaWidth = manaTokens.Count == 0
            ? 0
            : manaTokens.Count * symSize + (manaTokens.Count - 1) * symGap;

        double manaX = bar.Right - pad - manaWidth;
        double symY = bar.Y + (bar.Height - symSize) / 2;
        double cx = manaX;
        var pipShadow = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
        pipShadow.Freeze();
        foreach (var t in manaTokens)
        {
            var sym = _symbols.GetSymbol(t.Value);
            if (sym != null)
            {
                // subtle drop shadow under each pip — down-LEFT, matching every panel (light from top-right)
                dc.DrawEllipse(pipShadow, null,
                    new Point(cx + symSize / 2 - 1.2, symY + symSize / 2 + 2.0), symSize * 0.48, symSize * 0.48);
                dc.DrawImage(sym, new Rect(cx, symY, symSize, symSize));
            }
            cx += symSize + symGap;
        }

        // Title, left-aligned, auto-shrunk to fit remaining width. Keep a clear gap before the mana
        // symbols so the name never crowds them (the title only ever shrinks, never grows past default).
        double titleManaGap = 22;
        double titleMaxW = (manaWidth > 0 ? manaX - titleManaGap : bar.Right - pad) - (bar.X + pad);
        var brush = new SolidColorBrush(TemplateSpec.ParseColor(spec.TitleFont.Color));
        var ft = FitText(card.Name, spec.TitleFont, spec.TitleFont.Size, 16, titleMaxW, brush);
        double ty = bar.Y + (bar.Height - ft.Height) / 2;
        DrawGlyphRun(dc, ft, new Point(bar.X + pad, ty), spec.TitleFont);
    }

    // --- single-line regions (type line, power/toughness) -------------------

    private static void DrawSingleLine(DrawingContext dc, string text, Region region, FontSpec font, double padX)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var rect = ToRect(region);
        var brush = new SolidColorBrush(TemplateSpec.ParseColor(font.Color));
        double maxW = rect.Width - 2 * padX;
        var ft = FitText(text, font, font.Size, 12, maxW, brush);

        double x = font.Align switch
        {
            "center" => rect.X + (rect.Width - ft.Width) / 2,
            "right" => rect.Right - padX - ft.Width,
            _ => rect.X + padX,
        };
        double y = rect.Y + (rect.Height - ft.Height) / 2;
        DrawGlyphRun(dc, ft, new Point(x, y), font);
    }

    /// <summary>An organic leafy/scroll crown along the top of the title bar — for Legendary cards only.</summary>
    private static void DrawLegendaryCrown(DrawingContext dc, CardModel card, TemplateSpec spec)
    {
        if (ArtText(spec) || IsWave(spec)) return;   // art-forward + wave styles have their own crown
        if (!string.Equals(spec.TopEmblem, "none", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(spec.TopEmblem)) return;   // a baked topper replaces the auto-crown
        if (!spec.LegendaryCrown || string.Equals(spec.CrownStyle, "none", StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrEmpty(card.TypeLine) ||
            !card.TypeLine.Contains("Legendary", StringComparison.OrdinalIgnoreCase)) return;

        var bar = ToRect(spec.TitleBar);
        var gold = LightenC(TemplateSpec.ParseColor(spec.Colors.Frame2), 0.30);
        var edge = new Pen(new SolidColorBrush(TemplateSpec.ParseColor(spec.Colors.PanelBorder)), 1.0);
        var fill = new SolidColorBrush(gold);
        double baseY = bar.Y + 2, cx = (bar.X + bar.Right) / 2;

        // A gentle gold arc hugging the top of the title.
        dc.DrawLine(new Pen(new SolidColorBrush(gold), 2), new Point(bar.X + 14, baseY), new Point(bar.Right - 14, baseY));

        // "arc" style stops at the arc + a center gem; "leaves" adds the leafy row.
        if (string.Equals(spec.CrownStyle, "arc", StringComparison.OrdinalIgnoreCase))
        {
            var g2 = new StreamGeometry();
            using (var s = g2.Open())
            {
                s.BeginFigure(new Point(cx, baseY - 16), true, true);
                s.LineTo(new Point(cx + 7, baseY - 8), true, false);
                s.LineTo(new Point(cx, baseY), true, false);
                s.LineTo(new Point(cx - 7, baseY - 8), true, false);
            }
            g2.Freeze();
            dc.DrawGeometry(fill, edge, g2);
            return;
        }

        const int n = 11;
        double startX = bar.X + 24, span = bar.Width - 48, step = span / (n - 1);
        for (int i = 0; i < n; i++)
        {
            double x = startX + i * step;
            double h = (i % 2 == 0) ? 10 : 15;
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(new Point(x, baseY), true, true);
                s.BezierTo(new Point(x - 6, baseY - h * 0.5), new Point(x - 3, baseY - h), new Point(x, baseY - h), true, false);
                s.BezierTo(new Point(x + 3, baseY - h), new Point(x + 6, baseY - h * 0.5), new Point(x, baseY), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(fill, edge, g);
        }
        // Center flourish (small diamond gem).
        var gem = new StreamGeometry();
        using (var s = gem.Open())
        {
            s.BeginFigure(new Point(cx, baseY - 18), true, true);
            s.LineTo(new Point(cx + 7, baseY - 9), true, false);
            s.LineTo(new Point(cx, baseY), true, false);
            s.LineTo(new Point(cx - 7, baseY - 9), true, false);
        }
        gem.Freeze();
        dc.DrawGeometry(fill, edge, gem);
    }

    private static Color LightenC(Color c, double a)
    {
        byte L(byte v) => (byte)Math.Clamp(v + (255 - v) * a, 0, 255);
        return Color.FromRgb(L(c.R), L(c.G), L(c.B));
    }

    /// <summary>Draws the power/toughness box (panel + shadow + text) — only called for creatures.</summary>
    private static bool IsBorderless(TemplateSpec spec)
        => string.Equals(spec.FrameStyle, "borderless", StringComparison.OrdinalIgnoreCase);

    private static bool IsOverlay(TemplateSpec spec)
        => string.Equals(spec.FrameStyle, "overlay", StringComparison.OrdinalIgnoreCase);

    private static bool IsWave(TemplateSpec spec)
        => string.Equals(spec.FrameStyle, "wave", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the art window is (nearly) the whole card — a full-bleed layout regardless of style.</summary>
    private static bool IsFullBleedWindow(TemplateSpec spec) =>
        spec.ArtWindow != null && spec.ArtWindow.W >= spec.CanvasWidth * 0.92 && spec.ArtWindow.H >= spec.CanvasHeight * 0.92;

    /// <summary>Styles where the art fills the whole card and text sits on top of it.</summary>
    private static bool ArtText(TemplateSpec spec) => spec.FullArt || IsBorderless(spec) || IsOverlay(spec) || IsFullBleedWindow(spec);

    private static bool IsComposable(TemplateSpec spec) =>
        string.Equals(spec.FrameStyle, "modern", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(spec.FrameStyle, "composable", StringComparison.OrdinalIgnoreCase);

    private static Color DarkenC(Color c, double a)
    {
        byte D(byte v) => (byte)Math.Clamp(v * (1 - a), 0, 255);
        return Color.FromRgb(D(c.R), D(c.G), D(c.B));
    }

    /// <summary>Where the P/T (or loyalty) box sits. On framed cards it nests in the bottom-right of the
    /// description panel with a small even margin from the panel's right + bottom edges; on art-forward
    /// cards it keeps the template's placement (it floats over the art). Size comes from the template.</summary>
    private static Rect PtRect(TemplateSpec spec)
    {
        var rect = ToRect(spec.PtBox);
        if (ArtText(spec)) return rect;
        const double margin = 8;
        var tb = ToRect(spec.EffectiveTextBox);
        return new Rect(tb.Right - margin - rect.Width, tb.Bottom - margin - rect.Height, rect.Width, rect.Height);
    }

    private static void DrawPtBox(DrawingContext dc, CardModel card, TemplateSpec spec)
    {
        var rect = PtRect(spec);
        bool onArt = ArtText(spec);
        var borderColor = TemplateSpec.ParseColor(spec.Colors.PanelBorder);
        double r = Math.Max(7, spec.PanelRadius);

        // Modern style: a thick 3D beveled box that matches the nameplates — a raised frame-colored band
        // (bright top, dark bottom) around a recessed text area, with an inner bevel.
        if (IsComposable(spec))
        {
            var frame2m = TemplateSpec.ParseColor(spec.Colors.Frame2);
            var panelm = TemplateSpec.ParseColor(spec.Colors.Panel);
            var dkm = DarkenC(borderColor, 0.05);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null,
                new Rect(rect.X - 2, rect.Y + 4, rect.Width, rect.Height), r, r);
            var bandm = new LinearGradientBrush(LightenC(frame2m, 0.42), DarkenC(frame2m, 0.22), new Point(0, 0), new Point(0, 1));
            bandm.Freeze();
            dc.DrawRoundedRectangle(bandm, new Pen(new SolidColorBrush(dkm), 3.5), rect, r, r);
            dc.DrawLine(new Pen(new SolidColorBrush(LightenC(frame2m, 0.62)), 2), new Point(rect.X + r, rect.Y + 3), new Point(rect.Right - r, rect.Y + 3));
            dc.DrawLine(new Pen(new SolidColorBrush(DarkenC(frame2m, 0.38)), 1.6), new Point(rect.X + r, rect.Bottom - 3), new Point(rect.Right - r, rect.Bottom - 3));
            var innerm = Inset(rect, 7);
            double irm = Math.Max(3, r - 4);
            var innerFillm = new LinearGradientBrush(DarkenC(panelm, 0.06), LightenC(panelm, 0.05), new Point(0, 0), new Point(0, 1));
            innerFillm.Freeze();
            dc.DrawRoundedRectangle(innerFillm, new Pen(new SolidColorBrush(DarkenC(frame2m, 0.28)), 1.4), innerm, irm, irm);
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), 1), new Point(innerm.X + 3, innerm.Y + 1.5), new Point(innerm.Right - 3, innerm.Y + 1.5));
            DrawCentered(dc, $"{card.Power}/{card.Toughness}", innerm, spec.PtFont);
            return;
        }

        // Subtle drop shadow (bottom-left) so the box lifts off the frame without floating.
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(105, 0, 0, 0)), null,
            new Rect(rect.X - 3, rect.Y + 5, rect.Width, rect.Height), r, r);

        // The box is part of the FRAME: on framed cards it's the metallic frame color (a gold box on a
        // gold card), so it belongs to the border rather than looking slapped on. On art-forward cards
        // there's no frame metal, so use a translucent dark plate with a bright edge instead.
        Brush fill;
        Pen border;
        if (onArt)
        {
            var g = LightenC(TemplateSpec.ParseColor(spec.Colors.Frame2), 0.20);
            fill = new SolidColorBrush(Color.FromArgb(220, 16, 18, 24));
            border = new Pen(new SolidColorBrush(g), 3);
        }
        else
        {
            var frame = TemplateSpec.ParseColor(spec.Colors.Frame);
            var frame2 = TemplateSpec.ParseColor(spec.Colors.Frame2);
            fill = new LinearGradientBrush(LightenC(frame2, 0.10), frame, new Point(0, 0), new Point(0, 1));
            border = new Pen(new SolidColorBrush(borderColor), 3);
        }
        fill.Freeze();
        border.Freeze();
        dc.DrawRoundedRectangle(fill, border, rect, r, r);
        // Bevel: bright top edge + dark bottom edge, both hugging the rounded corners.
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1.6),
            new Point(rect.X + r, rect.Y + 3), new Point(rect.Right - r, rect.Y + 3));
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(95, 0, 0, 0)), 1.4),
            new Point(rect.X + r, rect.Bottom - 3), new Point(rect.Right - r, rect.Bottom - 3));

        // Optional inner bevel: an inset rim lit from the upper-left (light top-left → dark bottom-right).
        if (spec.PtBevel)
        {
            var innerRect = Inset(rect, 5);
            double ir = Math.Max(3, r - 5);
            var bevel = new LinearGradientBrush(
                Color.FromArgb(180, 255, 255, 255), Color.FromArgb(160, 0, 0, 0),
                new Point(1, 0), new Point(0, 1));   // light top-right -> dark bottom-left (matches shadows)
            bevel.Freeze();
            var bevelPen = new Pen(bevel, 2.4);
            bevelPen.Freeze();
            dc.DrawRoundedRectangle(null, bevelPen, innerRect, ir, ir);
        }

        DrawCentered(dc, $"{card.Power}/{card.Toughness}", rect, spec.PtFont);
    }

    /// <summary>Draws text centered on both axes using its true ink bounds (so it sits optically
    /// centered, not offset by the font's line-height padding).</summary>
    private static void DrawCentered(DrawingContext dc, string text, Rect rect, FontSpec font)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var brush = new SolidColorBrush(TemplateSpec.ParseColor(font.Color));
        var ft = FitText(text, font, font.Size, 12, rect.Width - 14, brush);
        var bounds = ft.BuildGeometry(new Point(0, 0)).Bounds;   // actual ink extents
        double x = rect.X + rect.Width / 2 - (bounds.X + bounds.Width / 2);
        double y = rect.Y + rect.Height / 2 - (bounds.Y + bounds.Height / 2);
        DrawGlyphRun(dc, ft, new Point(x, y), font);
    }

    // --- text box: rules + flavor, inline symbols, word wrap, auto-shrink ---

    private sealed record Placed(double X, double Y, FormattedText? Text, ImageSource? Sym, double SymSize);

    private sealed class TextLayout
    {
        public List<Placed> Items { get; } = new();
        public List<double> Dividers { get; } = new();
        public double Height { get; set; }
    }

    private void DrawTextBox(DrawingContext dc, string rules, string flavor, Region region,
        FontSpec rulesFont, FontSpec flavorFont, double baseSymbolSize)
    {
        if (string.IsNullOrWhiteSpace(rules) && string.IsNullOrWhiteSpace(flavor)) return;

        double pad = 18;
        var box = new Rect(region.X + pad, region.Y + pad,
            Math.Max(0, region.W - 2 * pad), Math.Max(0, region.H - 2 * pad));

        TextLayout? best = null;
        for (double size = rulesFont.Size; size >= 11; size -= 1)
        {
            double scale = size / rulesFont.Size;
            best = LayoutContent(rules, flavor, box, rulesFont, size,
                                 flavorFont, flavorFont.Size * scale, baseSymbolSize * scale);
            if (best.Height <= box.Height) break;
        }
        if (best == null) return;

        foreach (var p in best.Items)
        {
            if (p.Text != null) DrawGlyphRun(dc, p.Text, new Point(p.X, p.Y), rulesFont);
            else if (p.Sym != null) dc.DrawImage(p.Sym, new Rect(p.X, p.Y, p.SymSize, p.SymSize));
        }

        var dividerPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 40, 30, 10)), 1.2);
        foreach (var d in best.Dividers)
            dc.DrawLine(dividerPen, new Point(box.X + box.Width * 0.12, d),
                                    new Point(box.Right - box.Width * 0.12, d));
    }

    private TextLayout LayoutContent(string rules, string flavor, Rect box,
        FontSpec rulesFont, double rulesSize, FontSpec flavorFont, double flavorSize, double symSize)
    {
        var layout = new TextLayout();
        double y = box.Y;

        bool hasRules = !string.IsNullOrWhiteSpace(rules);
        bool hasFlavor = !string.IsNullOrWhiteSpace(flavor);

        if (hasRules)
            y = LayoutParagraphs(rules, rulesFont, rulesSize, symSize, box, layout.Items, y);

        if (hasFlavor)
        {
            if (hasRules)
            {
                y += rulesSize * 0.55;
                layout.Dividers.Add(y);
                y += rulesSize * 0.55;
            }
            y = LayoutParagraphs(flavor, flavorFont, flavorSize, flavorSize, box, layout.Items, y);
        }

        layout.Height = y - box.Y;
        return layout;
    }

    private double LayoutParagraphs(string text, FontSpec font, double fontSize, double symSize,
        Rect box, List<Placed> placed, double startY)
    {
        var brush = new SolidColorBrush(TemplateSpec.ParseColor(font.Color));
        double lineHeight = fontSize * 1.34;
        double x = box.X, y = startY;
        double spaceWidth = MakeText(" ", font, fontSize, brush).WidthIncludingTrailingWhitespace;

        var paragraphs = text.Replace("\r", "").Split('\n');
        foreach (var para in paragraphs)
        {
            if (para.Trim().Length == 0) { y += lineHeight * 0.5; continue; }

            bool first = true;
            foreach (var word in BuildWords(para, font, fontSize, symSize, brush))
            {
                double gap = (first || word.NoLeadingGap) ? 0 : spaceWidth;
                if (!first && x + gap + word.Width > box.Right)
                {
                    x = box.X;
                    y += lineHeight;
                    gap = 0;
                }
                x += gap;

                if (word.Text != null)
                    placed.Add(new Placed(x, y, word.Text, null, 0));
                else if (word.Sym != null)
                    placed.Add(new Placed(x, y + (lineHeight - symSize) / 2, null, word.Sym, symSize));

                x += word.Width;
                first = false;
            }

            x = box.X;
            y += lineHeight + lineHeight * 0.35;   // line close + paragraph gap
        }

        return y;
    }

    private readonly record struct Word(FormattedText? Text, ImageSource? Sym, double Width, bool NoLeadingGap);

    private IEnumerable<Word> BuildWords(string para, FontSpec font, double fontSize, double symSize, Brush brush)
    {
        // Reminder text in (parentheses) renders italic, like real cards.
        var italicFont = new FontSpec
        {
            Family = font.Family, Size = font.Size, Color = font.Color,
            Bold = font.Bold, Italic = true, Align = font.Align,
        };
        int parenDepth = 0;

        foreach (var tok in ManaText.Tokenize(para))
        {
            if (tok.IsSymbol)
            {
                var sym = _symbols.GetSymbol(tok.Value);
                yield return new Word(null, sym, symSize, NoLeadingGap: false);
                continue;
            }

            foreach (var raw in tok.Value.Split(' '))
            {
                if (raw.Length == 0) continue;
                bool italic = parenDepth > 0 || raw.Contains('(');
                parenDepth += raw.Count(c => c == '(') - raw.Count(c => c == ')');
                if (parenDepth < 0) parenDepth = 0;

                var ft = MakeText(raw, italic ? italicFont : font, fontSize, brush);
                bool noGap = ",.:;)".IndexOf(raw[0]) >= 0;
                yield return new Word(ft, null, ft.WidthIncludingTrailingWhitespace, noGap);
            }
        }
    }

    // --- planeswalker layout ------------------------------------------------

    private sealed class PwLayout
    {
        public List<Placed> Placed { get; } = new();
        public List<(Rect rect, FormattedText cost, string text)> Badges { get; } = new();
        public List<double> Dividers { get; } = new();
        public double Height { get; set; }
    }

    /// <summary>Parses planeswalker rules into (loyalty cost, ability text) rows.</summary>
    internal static List<(string? cost, string text)> ParseAbilities(string? rules)
    {
        var rows = new List<(string?, string)>();
        foreach (var raw in (rules ?? "").Replace("\r", "").Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            var m = Regex.Match(t, @"^([+\-−–]?\d+)\s*:\s*(.+)$");
            if (m.Success)
            {
                var cost = m.Groups[1].Value.Replace('-', '−').Replace('–', '−');
                rows.Add((cost, m.Groups[2].Value.Trim()));
            }
            else rows.Add((null, t));
        }
        return rows;
    }

    /// <summary>Parses saga rules into (chapter marker, text) rows, e.g. "I, II — ...".</summary>
    internal static List<(string? cost, string text)> ParseChapters(string? rules)
    {
        var rows = new List<(string?, string)>();
        foreach (var raw in (rules ?? "").Replace("\r", "").Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            var m = Regex.Match(t, @"^([IVXLC]+(?:\s*,\s*[IVXLC]+)*)\s*[—–-]\s*(.+)$");
            if (m.Success) rows.Add((m.Groups[1].Value.Replace(" ", ""), m.Groups[2].Value.Trim()));
            else rows.Add((null, t));
        }
        return rows;
    }

    /// <summary>
    /// Parses Class rules into badged rows. A "{cost}: Level N" line unlocks level N and is shown
    /// plainly; the ability line(s) that follow are badged with that level number. Base (level 1)
    /// abilities and reminder text stay un-badged.
    /// </summary>
    internal static List<(string? cost, string text)> ParseClassLevels(string? rules)
    {
        var rows = new List<(string?, string)>();
        int level = 1;
        foreach (var raw in (rules ?? "").Replace("\r", "").Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;

            var levelUp = Regex.Match(t, @"^\{.*\}\s*:\s*Level\s+(\d+)\s*$", RegexOptions.IgnoreCase);
            if (levelUp.Success)
            {
                rows.Add((null, t));                 // show the level-up cost line as-is
                level = int.Parse(levelUp.Groups[1].Value);
            }
            else
            {
                rows.Add((level > 1 ? level.ToString() : null, t));
            }
        }
        return rows;
    }

    private void DrawBadgedRows(DrawingContext dc, List<(string? cost, string text)> rows, TemplateSpec spec, bool loyaltyShields = false)
    {
        if (rows.Count == 0) return;

        double pad = 18;
        var tb0 = spec.EffectiveTextBox;
        var box = new Rect(tb0.X + pad, tb0.Y + pad,
            Math.Max(0, tb0.W - 2 * pad), Math.Max(0, tb0.H - 2 * pad));

        PwLayout? best = null;
        for (double size = spec.RulesFont.Size; size >= 11; size -= 1)
        {
            best = LayoutPw(rows, box, spec.RulesFont, size, spec.RulesSymbolSize * (size / spec.RulesFont.Size), loyaltyShields);
            if (best.Height <= box.Height) break;
        }
        if (best == null) return;

        var badgeFill = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        var badgeEdge = new Pen(Brushes.White, 2);
        foreach (var (rect, costFt, text) in best.Badges)
        {
            double nudge = 0;
            if (loyaltyShields)
            {
                int dir = text.StartsWith('+') ? 1 : (text.StartsWith('−') || text.StartsWith('-')) ? -1 : 0;
                dc.DrawGeometry(badgeFill, badgeEdge, LoyaltyShape(rect, dir));
                nudge = dir > 0 ? rect.Height * 0.12 : dir < 0 ? -rect.Height * 0.10 : 0;
            }
            else
            {
                dc.DrawRoundedRectangle(badgeFill, null, rect, rect.Height * 0.28, rect.Height * 0.28);
            }
            dc.DrawText(costFt, new Point(rect.X + (rect.Width - costFt.Width) / 2, rect.Y + (rect.Height - costFt.Height) / 2 + nudge));
        }
        foreach (var p in best.Placed)
        {
            if (p.Text != null) dc.DrawText(p.Text, new Point(p.X, p.Y));
            else if (p.Sym != null) dc.DrawImage(p.Sym, new Rect(p.X, p.Y, p.SymSize, p.SymSize));
        }
        var dividerPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 40, 30, 10)), 1.2);
        foreach (var d in best.Dividers)
            dc.DrawLine(dividerPen, new Point(box.X, d), new Point(box.Right, d));
    }

    private PwLayout LayoutPw(List<(string? cost, string text)> rows, Rect box, FontSpec font, double fontSize, double symSize, bool loyaltyShields = false)
    {
        var L = new PwLayout();
        var brush = new SolidColorBrush(TemplateSpec.ParseColor(font.Color));
        double lineHeight = fontSize * 1.34;
        double badgeH = loyaltyShields ? fontSize * 1.7 : fontSize * 1.25;   // shields are taller (pointed)
        double gap = fontSize * 0.5;
        double spaceWidth = MakeText(" ", font, fontSize, brush).WidthIncludingTrailingWhitespace;
        var badgeFont = new FontSpec { Family = "Segoe UI", Bold = true };
        double y = box.Y;

        for (int r = 0; r < rows.Count; r++)
        {
            var (cost, text) = rows[r];
            double badgeW = 0;
            FormattedText? costFt = null;
            if (cost != null)
            {
                costFt = MakeText(cost, badgeFont, fontSize * 0.95, Brushes.White);
                badgeW = loyaltyShields ? Math.Max(costFt.Width + fontSize * 0.9, badgeH * 1.05) : costFt.Width + fontSize * 0.9;
            }

            double startX = box.X + (cost != null ? badgeW + gap : 0);
            double x = startX, lineY = y;
            bool first = true;
            foreach (var word in BuildWords(text, font, fontSize, symSize, brush))
            {
                double g = (first || word.NoLeadingGap) ? 0 : spaceWidth;
                if (!first && x + g + word.Width > box.Right) { x = box.X; lineY += lineHeight; g = 0; }
                x += g;
                if (word.Text != null) L.Placed.Add(new Placed(x, lineY, word.Text, null, 0));
                else if (word.Sym != null) L.Placed.Add(new Placed(x, lineY + (lineHeight - symSize) / 2, null, word.Sym, symSize));
                x += word.Width;
                first = false;
            }

            if (cost != null)
                L.Badges.Add((new Rect(box.X, y + (lineHeight - badgeH) / 2, badgeW, badgeH), costFt!, cost!));

            double rowBottom = Math.Max(lineY + lineHeight, y + badgeH);
            y = rowBottom + fontSize * 0.4;
            if (r < rows.Count - 1) L.Dividers.Add(y - fontSize * 0.2);
        }

        L.Height = y - box.Y;
        return L;
    }

    private static void DrawLoyalty(DrawingContext dc, CardModel card, TemplateSpec spec)
    {
        // The starting-loyalty badge is a touch more compact than the P/T box, anchored to the same
        // bottom-right corner so it still nests in the description panel.
        var full = PtRect(spec);
        double w = full.Width * 0.80, h = full.Height * 0.90;
        var box = new Rect(full.Right - w, full.Bottom - h, w, h);

        // Starting loyalty sits in a downward-pointing shield (matching the ability shields).
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null, LoyaltyShape(new Rect(box.X - 3, box.Y + 4, box.Width, box.Height), -1));
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)), new Pen(Brushes.White, 2.5), LoyaltyShape(box, -1));

        // Center the number on its true ink bounds within the shield's flat body (above the point), so it
        // sits optically centered rather than floating high off the font's line-box padding.
        double sh = box.Height * 0.30;   // matches LoyaltyShape's point-height fraction
        var ft = MakeText(card.Loyalty, new FontSpec { Family = "Georgia", Bold = true }, spec.PtFont.Size, Brushes.White);
        var bounds = ft.BuildGeometry(new Point(0, 0)).Bounds;
        double x = box.X + box.Width / 2 - (bounds.X + bounds.Width / 2);
        double y = box.Y + (box.Height - sh) / 2 + box.Height * 0.07 - (bounds.Y + bounds.Height / 2);
        dc.DrawText(ft, new Point(x, y));
    }

    /// <summary>A loyalty badge silhouette: dir +1 = point up (activated), -1 = point down (cost / start),
    /// 0 = a flat hexagon (neutral). Fills the given rect.</summary>
    private static Geometry LoyaltyShape(Rect r, int dir)
    {
        double cx = r.X + r.Width / 2;
        double sh = r.Height * 0.30;   // point height
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            if (dir > 0)   // point up
            {
                s.BeginFigure(new Point(cx, r.Y), true, true);
                s.LineTo(new Point(r.Right, r.Y + sh), true, false);
                s.LineTo(new Point(r.Right, r.Bottom), true, false);
                s.LineTo(new Point(r.X, r.Bottom), true, false);
                s.LineTo(new Point(r.X, r.Y + sh), true, false);
            }
            else if (dir < 0)   // point down
            {
                s.BeginFigure(new Point(r.X, r.Y), true, true);
                s.LineTo(new Point(r.Right, r.Y), true, false);
                s.LineTo(new Point(r.Right, r.Bottom - sh), true, false);
                s.LineTo(new Point(cx, r.Bottom), true, false);
                s.LineTo(new Point(r.X, r.Bottom - sh), true, false);
            }
            else   // neutral flat hexagon (points left/right)
            {
                double cy = r.Y + r.Height / 2, w = r.Width * 0.26;
                s.BeginFigure(new Point(r.X, cy), true, true);
                s.LineTo(new Point(r.X + w, r.Y), true, false);
                s.LineTo(new Point(r.Right - w, r.Y), true, false);
                s.LineTo(new Point(r.Right, cy), true, false);
                s.LineTo(new Point(r.Right - w, r.Bottom), true, false);
                s.LineTo(new Point(r.X + w, r.Bottom), true, false);
            }
        }
        g.Freeze();
        return g;
    }

    // --- adventure (creature + spell sub-box) -------------------------------

    private void DrawAdventure(DrawingContext dc, CardModel card, TemplateSpec spec)
    {
        var tb = ToRect(spec.EffectiveTextBox);
        double advH = tb.Height * 0.44;
        var advRect = new Rect(tb.X + 10, tb.Y + 8, tb.Width - 20, advH - 14);

        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(30, 0, 0, 0)),
            new Pen(new SolidColorBrush(Color.FromArgb(130, 60, 50, 20)), 1.5), advRect, 8, 8);

        double pad = 14;
        double innerX = advRect.X + pad, innerR = advRect.Right - pad;
        var brush = new SolidColorBrush(TemplateSpec.ParseColor(spec.TypeFont.Color));

        // name + adventure mana cost
        double symSize = spec.RulesSymbolSize;
        double symGap = symSize * 0.08;
        var costTokens = ManaText.Tokenize(ManaText.NormalizeCost(card.AdventureCost)).Where(t => t.IsSymbol).ToList();
        double manaW = costTokens.Count == 0 ? 0 : costTokens.Count * symSize + (costTokens.Count - 1) * symGap;

        double rowY = advRect.Y + pad * 0.6;
        var nameFt = FitText(card.AdventureName,
            new FontSpec { Family = spec.TitleFont.Family, Bold = true, Color = spec.TypeFont.Color },
            spec.TypeFont.Size, 12, innerR - innerX - manaW - 6, brush);
        dc.DrawText(nameFt, new Point(innerX, rowY));

        double cx = innerR - manaW, symY = rowY + (nameFt.Height - symSize) / 2;
        foreach (var t in costTokens)
        {
            var s = _symbols.GetSymbol(t.Value);
            if (s != null) dc.DrawImage(s, new Rect(cx, symY, symSize, symSize));
            cx += symSize + symGap;
        }

        // adventure type line
        double typeY = rowY + nameFt.Height + 1;
        var typeFt = MakeText(card.AdventureType,
            new FontSpec { Family = spec.TypeFont.Family, Italic = true, Color = spec.TypeFont.Color },
            spec.TypeFont.Size * 0.8, brush);
        dc.DrawText(typeFt, new Point(innerX, typeY));

        // adventure rules text
        double advTextTop = typeY + typeFt.Height;
        var advTextRegion = new Region { X = advRect.X + 2, Y = advTextTop - 6, W = Math.Max(0, advRect.Width - 4), H = Math.Max(0, advRect.Bottom - advTextTop + 2) };
        DrawTextBox(dc, card.AdventureText, "", advTextRegion, spec.RulesFont, spec.FlavorFont, spec.RulesSymbolSize * 0.9);

        // creature rules below the sub-box
        var rulesRegion = new Region { X = tb.X, Y = tb.Y + advH, W = tb.Width, H = Math.Max(0, tb.Height - advH) };
        DrawTextBox(dc, card.RulesText, card.FlavorText, rulesRegion, spec.RulesFont, spec.FlavorFont, spec.RulesSymbolSize);
    }

    // --- text helpers -------------------------------------------------------

    private static FormattedText FitText(string text, FontSpec font, double startSize, double minSize, double maxWidth, Brush brush)
    {
        FormattedText ft = MakeText(text, font, startSize, brush);
        double size = startSize;
        while (ft.Width > maxWidth && size > minSize)
        {
            size -= 1;
            ft = MakeText(text, font, size, brush);
        }
        return ft;
    }

    private static readonly FontFamily DefaultFamily = new("Georgia");

    private static FormattedText MakeText(string text, FontSpec font, double size, Brush brush)
    {
        var typeface = new Typeface(
            SafeFontFamily(font.Family),
            font.Italic ? FontStyles.Italic : FontStyles.Normal,
            font.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);

        return new FormattedText(
            text ?? "",
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            Math.Max(1.0, size),                 // FormattedText requires a positive em size
            brush,
            1.0)
        {
            TextAlignment = TextAlignment.Left,
        };
    }

    /// <summary>A FontFamily from the spec, falling back to a default for a blank/invalid name.</summary>
    private static FontFamily SafeFontFamily(string? family)
    {
        if (string.IsNullOrWhiteSpace(family)) return DefaultFamily;
        try { return new FontFamily(family); } catch { return DefaultFamily; }
    }

    // Clamp to non-negative so a small/odd template (e.g. an adventure sub-box on a compact text
    // panel) can never produce a negative-sized Rect and throw.
    private static Rect ToRect(Region r) => new(r.X, r.Y, Math.Max(0, r.W), Math.Max(0, r.H));
}
