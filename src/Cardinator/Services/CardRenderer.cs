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
        var spec = template.Spec;
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
            DrawBadgedRows(dc, ParseAbilities(card.RulesText), spec);
        else if (card.IsSaga)
            DrawBadgedRows(dc, ParseChapters(card.RulesText), spec);
        else if (card.IsClass)
            DrawBadgedRows(dc, ParseClassLevels(card.RulesText), spec);
        else if (card.IsAdventure)
            DrawAdventure(dc, card, spec);
        else
            DrawTextBox(dc, card.RulesText, card.FlavorText, spec.TextBox, spec.RulesFont, spec.FlavorFont, spec.RulesSymbolSize);

        if (card.IsPlaneswalker && !string.IsNullOrWhiteSpace(card.Loyalty))
            DrawLoyalty(dc, card, spec);
        else if (card.HasPowerToughness)
            DrawPtBox(dc, card, spec);   // creatures only — the box is drawn here, not baked into the frame

        DrawFooter(dc, card, spec);

        dc.Pop();   // end rounded-card clip

        // A single, consistent thick black outer border on top of every style — the real-card edge.
        DrawOuterBorder(dc, W, H, cardR, spec);
    }

    /// <summary>The chunky black card edge every real card has — drawn last, over all styles.</summary>
    private static void DrawOuterBorder(DrawingContext dc, double W, double H, double cardR, TemplateSpec spec)
    {
        // Art-forward styles keep a slightly slimmer edge so the art still dominates.
        double t = ArtText(spec) ? 13 : 17;
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x0A)), t);
        pen.Freeze();
        var rect = new Rect(t / 2, t / 2, W - t, H - t);
        double r = Math.Max(1, cardR - t / 2);
        dc.DrawRoundedRectangle(null, pen, rect, r, r);
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
            var (fill, text) = RarityStyle(card.Rarity);
            var box = new Rect(bar.Right - pad - pipD, bar.Y + (bar.Height - pipD) / 2, pipD, pipD);
            dc.DrawEllipse(new SolidColorBrush(fill), new Pen(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), 1.5),
                new Point(box.X + pipD / 2, box.Y + pipD / 2), pipD / 2, pipD / 2);
            var glyphBrush = new SolidColorBrush(text);
            var ft = MakeText(card.Rarity.ToUpperInvariant()[..1],
                new FontSpec { Family = "Segoe UI", Bold = true }, pipD * 0.62, glyphBrush);
            dc.DrawText(ft, new Point(box.X + (pipD - ft.Width) / 2, box.Y + (pipD - ft.Height) / 2));
        }
    }

    private static (Color fill, Color text) RarityStyle(string rarity) => rarity.ToUpperInvariant() switch
    {
        "U" => (Color.FromRgb(0x9F, 0xB2, 0xC4), Color.FromRgb(0x14, 0x20, 0x2C)),
        "R" => (Color.FromRgb(0xC9, 0xA2, 0x27), Color.FromRgb(0x3A, 0x2E, 0x06)),
        "M" => (Color.FromRgb(0xD2, 0x4E, 0x1E), Colors.White),
        _ => (Color.FromRgb(0x1C, 0x1C, 0x1C), Colors.White),   // common
    };

    // --- footer (collector / rarity / set / artist) ------------------------

    private static void DrawFooter(DrawingContext dc, CardModel card, TemplateSpec spec)
    {
        var bar = ToRect(spec.CreditBar);

        // Pick a footer color that reads against whatever it sits on. On framed styles it sits on the
        // frame color; if that's dark, switch to a light, shadowed footer so it never disappears.
        var font = spec.CreditFont;
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

        // Two tidy tiny lines (collector, then artist) — kept small so they sit fully on the card
        // face and never spill onto the black border. Real cards print these very small.
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

        void Panel(Region region)
        {
            var box = Inset(ToRect(region), -grow);
            dc.DrawRoundedRectangle(scrim, null, box, r, r);
        }

        Panel(spec.TitleBar);
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
        foreach (var t in manaTokens)
        {
            var sym = _symbols.GetSymbol(t.Value);
            if (sym != null) dc.DrawImage(sym, new Rect(cx, symY, symSize, symSize));
            cx += symSize + symGap;
        }

        // Title, left-aligned, auto-shrunk to fit remaining width.
        double titleMaxW = (manaWidth > 0 ? manaX - 8 : bar.Right - pad) - (bar.X + pad);
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
        if (ArtText(spec)) return;   // art-forward styles skip the crown
        if (string.IsNullOrEmpty(card.TypeLine) ||
            !card.TypeLine.Contains("Legendary", StringComparison.OrdinalIgnoreCase)) return;

        var bar = ToRect(spec.TitleBar);
        var gold = LightenC(TemplateSpec.ParseColor(spec.Colors.Frame2), 0.30);
        var edge = new Pen(new SolidColorBrush(TemplateSpec.ParseColor(spec.Colors.PanelBorder)), 1.0);
        var fill = new SolidColorBrush(gold);
        double baseY = bar.Y + 2, cx = (bar.X + bar.Right) / 2;

        // A gentle gold arc hugging the top of the title, then a row of small leaves + center gem.
        dc.DrawLine(new Pen(new SolidColorBrush(gold), 2), new Point(bar.X + 14, baseY), new Point(bar.Right - 14, baseY));

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

    /// <summary>Styles where the art fills the whole card and text sits on top of it.</summary>
    private static bool ArtText(TemplateSpec spec) => spec.FullArt || IsBorderless(spec) || IsOverlay(spec);

    private static void DrawPtBox(DrawingContext dc, CardModel card, TemplateSpec spec)
    {
        var rect = ToRect(spec.PtBox);
        bool onArt = ArtText(spec);
        var panelColor = onArt
            ? Color.FromArgb(215, 18, 20, 26)   // translucent dark to match floating panels
            : TemplateSpec.ParseColor(spec.Colors.Panel);
        var borderColor = TemplateSpec.ParseColor(spec.Colors.PanelBorder);
        var gold = LightenC(TemplateSpec.ParseColor(spec.Colors.Frame2), 0.30);
        double r = Math.Max(7, spec.PanelRadius);

        // Drop shadow on the bottom-LEFT (light reads from the upper-right).
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null,
            new Rect(rect.X - 5, rect.Y + 6, rect.Width, rect.Height), r, r);

        // A metallic "seat" ring so the box reads as pressed INTO the frame, not slapped on top.
        var seat = Inset(rect, -6);
        var seatFill = new LinearGradientBrush(LightenC(gold, 0.25), Darken(gold, 0.15), new Point(0, 0), new Point(0, 1));
        seatFill.Freeze();
        dc.DrawRoundedRectangle(seatFill, new Pen(new SolidColorBrush(borderColor), 3), seat, r + 3, r + 3);

        // Panel: vertical gradient fill + a bold border.
        var fill = new LinearGradientBrush(LightenC(panelColor, 0.16), panelColor, new Point(0, 0), new Point(0, 1));
        fill.Freeze();
        dc.DrawRoundedRectangle(fill, new Pen(new SolidColorBrush(borderColor), 3), rect, r, r);
        // Top bevel highlight + bottom shade.
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(160, 255, 255, 255)), 1.8),
            new Point(rect.X + r, rect.Y + 3.5), new Point(rect.Right - r, rect.Y + 3.5));
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1.4),
            new Point(rect.X + r, rect.Bottom - 3), new Point(rect.Right - r, rect.Bottom - 3));

        DrawCentered(dc, $"{card.Power}/{card.Toughness}", rect, spec.PtFont);
    }

    /// <summary>Draws text centered on both axes within a box (nudged up slightly to sit optically centered).</summary>
    private static void DrawCentered(DrawingContext dc, string text, Rect rect, FontSpec font)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var brush = new SolidColorBrush(TemplateSpec.ParseColor(font.Color));
        var ft = FitText(text, font, font.Size, 12, rect.Width - 14, brush);
        double x = rect.X + (rect.Width - ft.Width) / 2;
        double y = rect.Y + (rect.Height - ft.Height) / 2 - 1;   // optical nudge; glyph box sits a hair low
        DrawGlyphRun(dc, ft, new Point(x, y), font);
    }

    private static Color Darken(Color c, double a)
    {
        byte D(byte v) => (byte)Math.Clamp(v * (1 - a), 0, 255);
        return Color.FromRgb(D(c.R), D(c.G), D(c.B));
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
        public List<(Rect rect, FormattedText cost)> Badges { get; } = new();
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

    private void DrawBadgedRows(DrawingContext dc, List<(string? cost, string text)> rows, TemplateSpec spec)
    {
        if (rows.Count == 0) return;

        double pad = 18;
        var box = new Rect(spec.TextBox.X + pad, spec.TextBox.Y + pad,
            Math.Max(0, spec.TextBox.W - 2 * pad), Math.Max(0, spec.TextBox.H - 2 * pad));

        PwLayout? best = null;
        for (double size = spec.RulesFont.Size; size >= 11; size -= 1)
        {
            best = LayoutPw(rows, box, spec.RulesFont, size, spec.RulesSymbolSize * (size / spec.RulesFont.Size));
            if (best.Height <= box.Height) break;
        }
        if (best == null) return;

        var badgeFill = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26));
        foreach (var (rect, costFt) in best.Badges)
        {
            dc.DrawRoundedRectangle(badgeFill, null, rect, rect.Height * 0.28, rect.Height * 0.28);
            dc.DrawText(costFt, new Point(rect.X + (rect.Width - costFt.Width) / 2, rect.Y + (rect.Height - costFt.Height) / 2));
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

    private PwLayout LayoutPw(List<(string? cost, string text)> rows, Rect box, FontSpec font, double fontSize, double symSize)
    {
        var L = new PwLayout();
        var brush = new SolidColorBrush(TemplateSpec.ParseColor(font.Color));
        double lineHeight = fontSize * 1.34;
        double badgeH = fontSize * 1.25;
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
                badgeW = costFt.Width + fontSize * 0.9;
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
                L.Badges.Add((new Rect(box.X, y + (lineHeight - badgeH) / 2, badgeW, badgeH), costFt!));

            double rowBottom = Math.Max(lineY + lineHeight, y + badgeH);
            y = rowBottom + fontSize * 0.4;
            if (r < rows.Count - 1) L.Dividers.Add(y - fontSize * 0.2);
        }

        L.Height = y - box.Y;
        return L;
    }

    private static void DrawLoyalty(DrawingContext dc, CardModel card, TemplateSpec spec)
    {
        var box = ToRect(spec.PtBox);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
            new Pen(Brushes.White, 2.5), box, 10, 10);
        var ft = MakeText(card.Loyalty, new FontSpec { Family = "Georgia", Bold = true }, spec.PtFont.Size, Brushes.White);
        dc.DrawText(ft, new Point(box.X + (box.Width - ft.Width) / 2, box.Y + (box.Height - ft.Height) / 2));
    }

    // --- adventure (creature + spell sub-box) -------------------------------

    private void DrawAdventure(DrawingContext dc, CardModel card, TemplateSpec spec)
    {
        var tb = ToRect(spec.TextBox);
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
        var rulesRegion = new Region { X = spec.TextBox.X, Y = tb.Y + advH, W = spec.TextBox.W, H = Math.Max(0, tb.Height - advH) };
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
