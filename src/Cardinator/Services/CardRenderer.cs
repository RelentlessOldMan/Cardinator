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

    // Opaque card backing (matches the black card edge) so exports are never see-through inside the card.
    private static readonly SolidColorBrush CardBacking = MakeFrozen(Color.FromRgb(0x08, 0x08, 0x0A));
    private static SolidColorBrush MakeFrozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    // Decoded-art cache so live preview doesn't re-read/decode the image on every keystroke.
    private readonly Dictionary<string, (long stamp, BitmapImage img)> _artCache = new();
    private const int ArtCacheMax = 12;

    public CardRenderer(SymbolService symbols) => _symbols = symbols;

    /// <summary>Drops the caches derived from mana-symbol ART (the big-land coin silhouette and the pip
    /// background colors). Call this when <see cref="SymbolService"/> reports new symbols: the pips
    /// themselves pick up the real Scryfall SVGs immediately, but anything sampled FROM a pip would
    /// otherwise keep the colors it derived from the offline drawn fallback until the app restarted.</summary>
    public void ClearSymbolCaches()
    {
        _iconSil.Clear();
        _pipBg.Clear();
    }

    /// <param name="previewHints">
    /// When true (the live preview), an empty art window shows a subtle "add art" placeholder.
    /// Always false for exports, so a saved PNG never has hint text baked in.
    /// </param>
    public BitmapSource RenderToBitmap(CardModel card, Template template, int supersample = 1, bool previewHints = false)
    {
        // A Battle or Plane on a portrait frame renders with that frame's landscape layout. Resolved HERE so
        // every path (preview, export, batch, print sheets, QA) agrees; it's idempotent for callers that
        // already resolved.
        template = TemplateService.ResolveFor(card, template);
        var spec = template.Spec;
        supersample = Math.Clamp(supersample, 1, 8);
        if (spec.CanvasWidth < 1 || spec.CanvasHeight < 1)
            throw new InvalidOperationException($"Template '{template.Name}' has an invalid canvas size ({spec.CanvasWidth}x{spec.CanvasHeight}).");
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

    private void Draw(DrawingContext dc, CardModel card, Template template, bool previewHints, bool footer = true)
    {
        if (card.IsSplit) { DrawSplit(dc, card, template, previewHints); return; }

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

        // Opaque backing inside the card silhouette. Custom frame sources keep their own transparency,
        // so any area that's transparent in the source and not covered by art would otherwise export
        // see-through — looks black on screen over the dark app, but wrong in a viewer / when printed.
        // Procedural frames are opaque on top of this, so it only shows where the frame is transparent.
        dc.DrawRectangle(CardBacking, null, new Rect(0, 0, W, H));

        // Full-art, borderless and overlay let the art cover the whole card; framed styles clip to the window.
        bool fullBleed = ArtText(spec);
        var artRegion = fullBleed ? new Region { X = 0, Y = 0, W = W, H = H } : spec.ArtWindow;
        DrawArt(dc, card, artRegion, previewHints);

        // Frame overlay (art shows through its transparent window / floating panels).
        dc.DrawImage(template.FrameImage, new Rect(0, 0, W, H));

        // On plain full-art cards, lay subtle scrims behind the text so it stays readable on any art.
        // (borderless/overlay bake their own panels/band into the frame.)
        if (spec.FullArt) DrawScrims(dc, spec);

        if (spec.IsFlipLayout && card.IsFlip)
        {
            // A flip card: this half reads upright on top; the other half is the SAME layout turned upside
            // down, so it's drawn through a 180° rotation about the card's centre — exactly the axis the
            // flip frame was mirrored on.
            DrawFlipHalf(dc, card, spec);
            dc.PushTransform(new RotateTransform(180, W / 2, H / 2));
            if (spec.FullArt) DrawScrims(dc, spec);
            DrawFlipHalf(dc, OtherHalfForRender(card), spec);
            dc.Pop();
        }
        else
        {
            DrawTitleAndMana(dc, card, spec);
            DrawLegendaryCrown(dc, card, spec);
            DrawSubtitle(dc, card, spec);
            DrawTypeLine(dc, card, spec);

            if (card.IsPlaneswalker)
                DrawBadgedRows(dc, ParseAbilities(card.RulesText), spec, loyaltyShields: true,
                    avoid: string.IsNullOrWhiteSpace(card.Loyalty) ? null : LoyaltyRect(spec));
            else if (card.IsSaga)
                DrawBadgedRows(dc, ParseChapters(card.RulesText), spec);
            else if (card.IsClass)
                DrawBadgedRows(dc, ParseClassLevels(card.RulesText), spec);
            else if (card.IsAdventure)
                DrawAdventure(dc, card, spec);
            else if (card.ShowBigLandSymbol)
                DrawBigLandSymbol(dc, card, spec);
            else
            {
                // Creatures nest a P/T box in the bottom-right — reserve that space so rules text wraps around
                // it instead of being hidden underneath.
                Rect? avoid = ArtText(spec) ? null
                    : card.HasDefense ? DefenseRect(spec)
                    : card.HasPowerToughness ? PtRect(spec)
                    : null;
                DrawTextBox(dc, card.RulesText, card.FlavorText, spec.EffectiveTextBox, spec.RulesFont, spec.FlavorFont, spec.RulesSymbolSize, avoid);
            }

            if (card.IsPlaneswalker && !string.IsNullOrWhiteSpace(card.Loyalty))
                DrawLoyalty(dc, card, spec);
            else if (card.HasDefense)
                DrawDefense(dc, card, spec);   // a Battle's starting defense
            else if (card.HasPowerToughness)
                DrawPtBox(dc, card, spec);   // creatures only — the box is drawn here, not baked into the frame

        }

        // Footer placement: "frame" draws it on the colored card (inside the clip, before the border);
        // "border" draws it on the black rim (after the border); "none" skips it.
        var footerPlacement = footer ? (spec.FooterPlacement ?? "frame").Trim().ToLowerInvariant() : "none";
        if (footerPlacement == "frame")
            DrawFooter(dc, card, spec, onBorder: false);

        dc.Pop();   // end rounded-card clip

        // A single, consistent thick black outer border on top of every style — the real-card edge, even
        // thickness all the way around.
        DrawOuterBorder(dc, W, H, cardR, spec);

        if (footerPlacement == "border")
            DrawFooter(dc, card, spec, onBorder: true);

        DrawDfcIndicator(dc, card, spec, W, H);
    }

    // --- split cards ---------------------------------------------------------

    /// <summary>Where a split card's parts go, in READING coordinates: the card turned a quarter clockwise, so
    /// it is <c>CanvasHeight</c> wide and <c>CanvasWidth</c> tall. The front half is on the left and the other
    /// half on the right, each the frame's normal card scaled by <see cref="Scale"/>; a shared reminder line
    /// (Fuse, a Room's door rules) gets a bar across the bottom; the reading view's left strip is the upright
    /// card's bottom edge, where the card's credits go.</summary>
    internal sealed record SplitLayout(Rect Left, Rect Right, double Scale, Rect Bar, string SharedLine,
        double FooterStrip, double ReadingWidth, double ReadingHeight)
    {
        /// <summary>Reading coordinates → the upright card: the reading view's left edge is the card's bottom.</summary>
        public static Matrix ToCard(double canvasHeight) => new(0, -1, 1, 0, 0, canvasHeight);
    }

    internal static SplitLayout SplitGeometry(CardModel card, TemplateSpec spec)
    {
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        double rw = H, rh = W;
        double side = Math.Min(W, H);
        double m = Math.Max(6, side * 0.016);                          // outer margin and the gutter
        double foot = Math.Max(spec.BorderThickness, side * 0.042);    // the card's bottom edge (credits)
        var shared = SplitSharedLine(card).shared;
        double barH = shared.Length > 0 ? side * 0.075 : 0;
        double slotW = (rw - foot - 2 * m) / 2;
        double slotH = rh - 2 * m - (barH > 0 ? barH + m : 0);
        double s = Math.Max(0.05, Math.Min(slotW / W, slotH / H));
        double hw = W * s, hh = H * s, y = m + (slotH - hh) / 2;
        var left = new Rect(foot + (slotW - hw) / 2, y, hw, hh);
        var right = new Rect(foot + slotW + m + (slotW - hw) / 2, y, hw, hh);
        var bar = barH > 0 ? new Rect(left.X, rh - m - barH, right.Right - left.X, barH) : Rect.Empty;
        return new SplitLayout(left, right, s, bar, shared, foot, rw, rh);
    }

    /// <summary>A reminder both halves end with — Fuse, or a Room's "(You may cast either half…)" — is printed
    /// ONCE across the card, so it comes off both halves' rules. Only a "Fuse…" line or a fully parenthesized
    /// one counts, so two halves that merely end alike ("Draw a card.") keep their text.</summary>
    internal static (string front, string half, string shared) SplitSharedLine(CardModel card)
    {
        string a = card.RulesText ?? "", b = card.OtherHalf?.RulesText ?? "";
        static (string body, string last) Last(string t)
        {
            var lines = t.Replace("\r\n", "\n").TrimEnd().Split('\n');
            return (string.Join("\n", lines[..^1]).TrimEnd(), lines[^1].Trim());
        }
        var (ab, al) = Last(a);
        var (bb, bl) = Last(b);
        bool reminder = al.StartsWith("Fuse", StringComparison.OrdinalIgnoreCase)
                        || (al.StartsWith('(') && al.EndsWith(')'));
        if (al.Length == 0 || !reminder || !string.Equals(al, bl, StringComparison.Ordinal)) return (a, b, "");
        return (ab, bb, al);
    }

    /// <summary>The two halves of a split card as they're drawn: plain cards (no other half, no back), the
    /// shared reminder taken off their rules, and the set identity the card's own (as on a real split card).</summary>
    internal static (CardModel front, CardModel half) SplitHalves(CardModel card)
    {
        var (fr, hr, _) = SplitSharedLine(card);
        var front = card.Clone();
        front.OtherHalf = null; front.HalfLayout = ""; front.BackFace = null;
        front.RulesText = fr;
        var half = OtherHalfForRender(card);
        half.IsOtherHalf = false;
        half.RulesText = hr;
        return (front, half);
    }

    /// <summary>A split card: two small cards side by side, laid out in the reading view (turned a quarter
    /// clockwise) and drawn onto the upright card through <see cref="SplitLayout.ToCard"/>. Each half is the
    /// frame's normal card at a smaller scale — so every frame (drawn or picture) works unchanged — without
    /// its own credits; the card's credits run upright along its bottom edge, like a real split card's.</summary>
    private void DrawSplit(DrawingContext dc, CardModel card, Template template, bool previewHints)
    {
        var spec = template.Spec;
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        double cardR = Math.Max(4, spec.CornerRadius);
        var g = SplitGeometry(card, spec);
        var (front, half) = SplitHalves(card);

        // The halves are drawn small, but a split half's few lines read at a normal card's size: start the
        // rules (and flavor/symbols) as large as the scale took away; the text box still shrinks to fit.
        var halfSpec = spec.Clone();
        double grow = Math.Min(1.6, 1 / g.Scale);
        halfSpec.RulesFont.Size *= grow;
        halfSpec.FlavorFont.Size *= grow;
        halfSpec.RulesSymbolSize *= grow;
        var halfTemplate = new Template
        {
            Name = template.Name, Spec = halfSpec, FramePath = template.FramePath,
            FrameImage = template.FrameImage, Variants = template.Variants,
        };

        var clip = new RectangleGeometry(new Rect(0, 0, W, H), cardR, cardR);
        clip.Freeze();
        dc.PushClip(clip);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x0A)), null, new Rect(0, 0, W, H));

        dc.PushTransform(new MatrixTransform(SplitLayout.ToCard(H)));
        foreach (var (part, at) in new[] { (front, g.Left), (half, g.Right) })
        {
            dc.PushTransform(new TranslateTransform(at.X, at.Y));
            dc.PushTransform(new ScaleTransform(g.Scale, g.Scale));
            Draw(dc, part, halfTemplate, previewHints, footer: false);
            dc.Pop();
            dc.Pop();
        }
        if (g.SharedLine.Length > 0) DrawSplitBar(dc, g, spec);
        dc.Pop();

        // The card's credits, upright on its bottom edge (the reading view's left strip).
        var footSpec = spec.Clone();
        footSpec.BorderThickness = g.FooterStrip;
        DrawFooter(dc, card, footSpec, onBorder: true);
        dc.Pop();
    }

    /// <summary>The shared reminder bar across the bottom of a split card's reading view.</summary>
    private static void DrawSplitBar(DrawingContext dc, SplitLayout g, TemplateSpec spec)
    {
        var bar = g.Bar;
        double r = bar.Height * 0.22;
        var fill = new SolidColorBrush(Color.FromRgb(0xEE, 0xE7, 0xD8));
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x5A, 0x52, 0x46)), Math.Max(1, bar.Height * 0.04));
        dc.DrawRoundedRectangle(fill, pen, bar, r, r);
        var font = new FontSpec
        {
            Family = spec.RulesFont.Family, Size = spec.RulesFont.Size, Italic = g.SharedLine.StartsWith('('),
            Align = "left", Color = "#1C1A17",
        };
        var brush = new SolidColorBrush(TemplateSpec.ParseColor(font.Color));
        double pad = bar.Height * 0.35;
        var ft = FitText(g.SharedLine, font, Math.Min(font.Size, bar.Height * 0.48), 7, bar.Width - 2 * pad, brush);
        dc.DrawText(ft, new Point(bar.X + (bar.Width - ft.Width) / 2, bar.Y + (bar.Height - ft.Height) / 2));
    }

    /// <summary>One half of a flip card in the flip layout: name + mana, the type line (shortened so the set
    /// symbol sits clear of the P/T box at its end), the rules/flavor in the half's own box, and the P/T box.
    /// No crown, subtitle or special layouts — real flip halves have none.</summary>
    private void DrawFlipHalf(DrawingContext dc, CardModel half, TemplateSpec spec)
    {
        DrawTitleAndMana(dc, half, spec);
        var typeSpec = spec;
        if (half.HasPowerToughness)
        {
            typeSpec = spec.Clone();
            typeSpec.TypeBar = new Region
            {
                X = spec.TypeBar.X, Y = spec.TypeBar.Y, H = spec.TypeBar.H,
                W = Math.Max(40, spec.PtBox.X - 6 - spec.TypeBar.X),
            };
        }
        DrawTypeLine(dc, half, typeSpec);
        DrawTextBox(dc, half.RulesText, half.FlavorText, spec.TextBox, spec.RulesFont, spec.FlavorFont, spec.RulesSymbolSize, null);
        if (half.HasPowerToughness) DrawPtBox(dc, half, spec);
    }

    /// <summary>The other half as it should draw: the set-level identity (set, rarity, set symbol, collector
    /// number) is the card's, like a real flip card, whatever the half itself holds.</summary>
    internal static CardModel OtherHalfForRender(CardModel card)
    {
        var half = card.OtherHalf!.Clone();
        half.SetCode = card.SetCode;
        half.Rarity = card.Rarity;
        half.SetSymbolPath = card.SetSymbolPath;
        half.CollectorNumber = card.CollectorNumber;
        return half;
    }

    /// <summary>The badge (center, radius) for the DFC indicator, or null when the card shows none. Always
    /// keyed to the title panel's top-left corner — never to the card corner — so it sits in the same spot on
    /// the title bar whatever the frame's border or top decoration (it isn't user-positionable). Only if the
    /// panel is so close to the edge that the badge would run off the card is it pushed inward. Shared by the
    /// drawing and by the title layout, which starts the name just past it.</summary>
    internal static (Point c, double r)? DfcBadge(CardModel card, TemplateSpec spec)
    {
        var style = (card.DfcStyle ?? "").Trim().ToLowerInvariant();
        bool isDfc = card.IsDoubleFaced || card.IsBackFace;
        if (!isDfc || style is "" or "none") return null;
        double side = Math.Min(spec.CanvasWidth, spec.CanvasHeight);   // same badge size on a landscape face
        double r = side * 0.034;
        var bar = spec.TitleBar;
        // The icon's top-left = the title panel's top-left + a fixed offset (where it already sat on the stock
        // frames: bar at 48,46 -> icon at 37,37), never closer than a minimum gap to the card's edge.
        double minEdge = side * 0.016;
        double left = Math.Max(bar.X - side * 0.0147, minEdge);
        double top = Math.Max(bar.Y - side * 0.012, minEdge);
        return (new Point(left + r, top + r), r);
    }

    /// <summary>Draws the double-faced indicator (our own glyphs — no third-party assets): a generic flip
    /// arrow on both faces ("arrow"), or a sun on the front and a crescent moon on the back ("sunmoon").</summary>

    private static void DrawDfcIndicator(DrawingContext dc, CardModel card, TemplateSpec spec, double W, double H)
    {
        var badge = DfcBadge(card, spec);
        if (badge is null) return;
        var (c, r) = badge.Value;
        var style = (card.DfcStyle ?? "").Trim().ToLowerInvariant();

        // Badge: dark translucent disc with a soft light rim, so it reads on art or frame alike.
        var badgeFill = new SolidColorBrush(Color.FromArgb(0xDD, 0x18, 0x18, 0x1C));
        var rim = new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0xED, 0xE6, 0xD0)), Math.Max(1.5, r * 0.10));
        dc.DrawEllipse(badgeFill, rim, c, r, r);

        if (style == "sunmoon")
        {
            if (card.IsBackFace) DrawMoon(dc, c, r); else DrawSun(dc, c, r);
        }
        else // "arrow" (or any unknown style falls back to the universal flip cue)
        {
            DrawFlipArrow(dc, c, r);
        }
    }

    private static void DrawSun(DrawingContext dc, Point c, double r)
    {
        var gold = new SolidColorBrush(Color.FromRgb(0xF0, 0xCB, 0x5A));
        double disc = r * 0.42;
        dc.DrawEllipse(gold, null, c, disc, disc);
        var ray = new Pen(gold, Math.Max(1.4, r * 0.11)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            var p1 = new Point(c.X + Math.Cos(a) * disc * 1.35, c.Y + Math.Sin(a) * disc * 1.35);
            var p2 = new Point(c.X + Math.Cos(a) * r * 0.82, c.Y + Math.Sin(a) * r * 0.82);
            dc.DrawLine(ray, p1, p2);
        }
    }

    private static void DrawMoon(DrawingContext dc, Point c, double r)
    {
        var silver = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF2));
        double mr = r * 0.62;
        var full = new EllipseGeometry(c, mr, mr);
        // Subtract an offset disc to carve the crescent.
        var cut = new EllipseGeometry(new Point(c.X + mr * 0.55, c.Y - mr * 0.18), mr * 0.92, mr * 0.92);
        var crescent = new CombinedGeometry(GeometryCombineMode.Exclude, full, cut);
        crescent.Freeze();
        dc.DrawGeometry(silver, null, crescent);
    }

    private static void DrawFlipArrow(DrawingContext dc, Point c, double r)
    {
        // Two curved arrows forming a circular "this transforms" cue.
        var brush = new SolidColorBrush(Color.FromRgb(0xED, 0xE6, 0xD0));
        var pen = new Pen(brush, Math.Max(1.6, r * 0.14)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        double ar = r * 0.5;
        for (int half = 0; half < 2; half++)
        {
            double sweepStart = half == 0 ? -40 : 140;   // two opposing ~150° arcs
            double a0 = sweepStart * Math.PI / 180, a1 = (sweepStart + 150) * Math.PI / 180;
            var start = new Point(c.X + Math.Cos(a0) * ar, c.Y + Math.Sin(a0) * ar);
            var end = new Point(c.X + Math.Cos(a1) * ar, c.Y + Math.Sin(a1) * ar);
            var fig = new PathFigure { StartPoint = start };
            fig.Segments.Add(new ArcSegment(end, new Size(ar, ar), 0, true, SweepDirection.Clockwise, true));
            var geo = new PathGeometry(); geo.Figures.Add(fig); geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
            // Arrowhead at the end of each arc, tangent to the circle.
            double tan = a1 + Math.PI / 2;
            double hs = r * 0.26;
            var tip = new Point(end.X + Math.Cos(tan) * hs * 0.2, end.Y + Math.Sin(tan) * hs * 0.2);
            var b1 = new Point(end.X + Math.Cos(tan + 2.4) * hs, end.Y + Math.Sin(tan + 2.4) * hs);
            var b2 = new Point(end.X + Math.Cos(tan - 2.4) * hs, end.Y + Math.Sin(tan - 2.4) * hs);
            var head = new PathFigure { StartPoint = tip, IsClosed = true };
            head.Segments.Add(new LineSegment(b1, false));
            head.Segments.Add(new LineSegment(b2, false));
            var hg = new PathGeometry(); hg.Figures.Add(head); hg.Freeze();
            dc.DrawGeometry(brush, null, hg);
        }
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
                Family = spec.CreditFont.Family, Size = Math.Max(7, Math.Min(spec.CreditFont.Size, t - 12)),
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

        // Two tidy tiny lines (collector, then artist), kept small on the card face — or one joined
        // line when the template asks for a single-line footer.
        var lines = new[] { BuildCollectorLine(card), BuildCreditLine(card) }
            .Where(s => s.Length > 0).ToList();
        if (spec.FooterSingleLine && lines.Count > 1)
            lines = new List<string> { string.Join("   •   ", lines) };
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

    /// <summary>Draws a basic land's big centered mana symbol(s) in the text-box region (e.g. a large {G}
    /// for a Forest). One, two, or three symbols; arranged in a row (default) or combined into a single
    /// disc split vertically/horizontally, as a yin-yang (2), or as pie wedges — per card.LandSymbolStyle.</summary>
    private void DrawBigLandSymbol(DrawingContext dc, CardModel card, TemplateSpec spec)
    {
        var toks = card.BigLandSymbols;
        if (toks.Count == 0) return;

        var region = spec.EffectiveTextBox;
        if (region == null) return;
        var box = new Rect(region.X, region.Y, region.W, region.H);

        string style = (card.LandSymbolStyle ?? "").Trim().ToLowerInvariant();
        if (style is "" or "row" || toks.Count == 1)
        {
            DrawLandSymbolRow(dc, toks, box);
            return;
        }

        // Combined coin: one circle cut 2 or 3 ways, each piece its own color, with that color's whole
        // symbol centered in the fattest part of the piece. Every symbol is the SAME size (the largest that
        // fits the smallest piece) and clipped to its piece so nothing crosses a seam or runs off the edge.
        int n = toks.Count;
        double d = Math.Min(box.Height * 0.82, box.Width * 0.82);
        if (d <= 0) return;
        double cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2, r = d / 2;
        var full = new Rect(cx - r, cy - r, d, d);

        // 3-way "curvy" swirl placement is unreliable; a clean 3-wedge pie is the dependable 3-way cut.
        bool curvy = style is "yinyang" or "swirl" or "curvy";
        if (curvy && n >= 3) style = "pie";

        var clips = CombinedClips(style, n, full);
        var places = RegionPlacements(style, n, full);
        double sym = places.Length == 0 ? 0 : places.Min(p => p.dia);   // one size for all → equal symbols
        if (sym <= 0) return;

        // A coin is a circle with a drop shadow — the SAME as a single pip. One shared path draws the shadow;
        // the only difference is what paints the circle's interior (wedges here, a pip image for singles).
        DrawShadowedDisc(dc, new Point(cx, cy), r, () =>
        {
            // 1) Fill each piece with that symbol's own circle color (sampled from the pip art).
            for (int i = 0; i < n; i++)
            {
                var fill = new SolidColorBrush(PipBackground(toks[i])); fill.Freeze();
                dc.PushClip(clips[i]);
                dc.DrawRectangle(fill, null, full);
                dc.Pop();
            }
            // 2) Thin INTERNAL seams between the colored pieces (no outer ring — the rim is just color → shadow,
            //    exactly like a pip). Clip the seams to just inside the rim so only the interior dividers show.
            var edge = new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)), Math.Max(1.0, d * 0.012));
            double inset = Math.Max(1.0, d * 0.012);
            dc.PushClip(new EllipseGeometry(new Point(cx, cy), r - inset, r - inset));
            for (int i = 0; i < n; i++) dc.DrawGeometry(null, edge, clips[i]);
            dc.Pop();
            // 3) The dark ICON SILHOUETTE in each piece, clipped so nothing crosses a seam.
            for (int i = 0; i < n; i++)
            {
                var sil = IconSilhouette(toks[i]);
                if (sil == null) continue;
                var p = places[i].c;
                dc.PushClip(clips[i]);
                dc.DrawImage(sil, new Rect(p.X - sym / 2, p.Y - sym / 2, sym, sym));
                dc.Pop();
            }
        });
    }

    /// <summary>THE land-symbol drop shadow, shared by single pips and combined coins. A land symbol is a circle;
    /// this draws its shadow — the circle in the neutral mana-circle tone, offset (2%,3%) of the diameter at 30% —
    /// then invokes <paramref name="paintInterior"/> to draw the circle's contents on top. Single pips and coins
    /// differ ONLY in that interior, so the shadow can never drift between them again.</summary>
    private void DrawShadowedDisc(DrawingContext dc, Point center, double radius, Action paintInterior)
    {
        var tone = new SolidColorBrush(PipBackground("{B}")); tone.Freeze();
        var shadowCenter = new Point(center.X + radius * 2 * 0.02, center.Y + radius * 2 * 0.03);
        dc.PushOpacity(0.30);
        dc.DrawEllipse(tone, null, shadowCenter, radius, radius);
        dc.Pop();
        paintInterior();
    }

    // A mana pip reduced to just its dark icon as a solid silhouette: we render the pip, key every pixel by
    // darkness (pastel background → transparent, dark icon kept), mask off the pip's outline ring, and recolor
    // what's left to one consistent near-black. Cached per token. Guarantees no pip background can ever show.
    private readonly Dictionary<string, BitmapSource?> _iconSil = new();
    private BitmapSource? IconSilhouette(string token)
    {
        if (_iconSil.TryGetValue(token, out var cached)) return cached;
        BitmapSource? result = null;
        try
        {
            var img = _symbols.GetSymbol(token);
            if (img != null)
            {
                const int S = 160;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) dc.DrawImage(img, new Rect(0, 0, S, S));
                var rtb = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var straight = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);   // un-premultiply
                var px = new byte[S * S * 4];
                straight.CopyPixels(px, S * 4, 0);
                const byte sr = 0x15, sg = 0x15, sb = 0x17;   // one silhouette color for every symbol
                double cc = (S - 1) / 2.0, rin = S * 0.44, rin2 = rin * rin;   // inner circle drops the pip ring
                for (int y = 0; y < S; y++)
                    for (int x = 0; x < S; x++)
                    {
                        int i = (y * S + x) * 4;
                        byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
                        double lum = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;    // bright bg ~1, dark icon ~0
                        // Hard key with a thin anti-aliased ramp: lum<=0.24 is pure icon (keep), lum>=0.46 is
                        // pastel background (drop to FULLY transparent — no faint dark halo disc).
                        double t = Math.Clamp((0.46 - lum) / (0.46 - 0.24), 0, 1);
                        double dx = x - cc, dy = y - cc;
                        if (dx * dx + dy * dy > rin2) t = 0;                         // outside the rim → drop ring
                        byte outA = (byte)Math.Round((a / 255.0) * t * 255);
                        px[i] = (byte)(sb * outA / 255);          // premultiplied Pbgra32
                        px[i + 1] = (byte)(sg * outA / 255);
                        px[i + 2] = (byte)(sr * outA / 255);
                        px[i + 3] = outA;
                    }
                result = BitmapSource.Create(S, S, 96, 96, PixelFormats.Pbgra32, null, px, S * 4);
                result.Freeze();
            }
        }
        catch { /* leave null → symbol just omitted */ }
        _iconSil[token] = result;
        return result;
    }

    // For each piece: the center and diameter of the largest circle that fits in its fat part — so a symbol
    // placed there is centered and never runs past a seam or the rim.
    private static (Point c, double dia)[] RegionPlacements(string style, int n, Rect full)
    {
        double cx = full.X + full.Width / 2, cy = full.Y + full.Height / 2, r = full.Width / 2;
        var res = new (Point, double)[n];
        bool curvy = style is "yinyang" or "swirl" or "curvy";

        if (curvy && n == 2)
        {
            // Symbol sits in each round lobe (centered r/2 above/below), not the tail.
            res[0] = (new Point(cx, cy - r / 2), r * 0.82);
            res[1] = (new Point(cx, cy + r / 2), r * 0.82);
            return res;
        }
        if (style == "pie")
        {
            // Incircle of a sector of angle θ=2π/n: center at r/(1+sin) along the bisector, radius r·sin/(1+sin).
            double theta = 2 * Math.PI / n, s = Math.Sin(theta / 2);
            double dist = r / (1 + s), rho = r * s / (1 + s);
            double start = -Math.PI / 2;
            for (int i = 0; i < n; i++)
            {
                double mid = start + theta * (i + 0.5);
                res[i] = (new Point(cx + dist * Math.Cos(mid), cy + dist * Math.Sin(mid)), 2 * rho * 0.92);
            }
            return res;
        }
        if (style == "splith")
        {
            double h = full.Height / n;
            for (int i = 0; i < n; i++)
            {
                double yc = full.Y + (i + 0.5) * h, dyy = yc - cy;
                double chord = Math.Abs(dyy) < r ? 2 * Math.Sqrt(r * r - dyy * dyy) : 0;
                res[i] = (new Point(cx, yc), Math.Min(h, chord) * 0.86);
            }
            return res;
        }
        // splitv (default): vertical slices
        double w = full.Width / n;
        for (int i = 0; i < n; i++)
        {
            double xc = full.X + (i + 0.5) * w, dxx = xc - cx;
            double chord = Math.Abs(dxx) < r ? 2 * Math.Sqrt(r * r - dxx * dxx) : 0;
            res[i] = (new Point(xc, cy), Math.Min(w, chord) * 0.86);
        }
        return res;
    }

    // Dominant (background) color of a mana pip, sampled once from its art and cached.
    private readonly Dictionary<string, Color> _pipBg = new();
    private Color PipBackground(string token)
    {
        if (_pipBg.TryGetValue(token, out var cached)) return cached;
        var color = Color.FromRgb(0x80, 0x80, 0x80);
        try
        {
            var img = _symbols.GetSymbol(token);
            if (img != null)
            {
                const int S = 24;
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) dc.DrawImage(img, new Rect(0, 0, S, S));
                var rtb = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var px = new byte[S * S * 4];
                rtb.CopyPixels(px, S * 4, 0);
                // Most common BRIGHT opaque color, bucketed — that's the pastel ring, never the dark icon.
                // (A big icon like the forest tree can out-cover the ring, so dark pixels must be excluded or
                //  the fill comes out black.)
                var buckets = new Dictionary<int, (int count, long r, long g, long b)>();
                for (int i = 0; i < px.Length; i += 4)
                {
                    byte b = px[i], g = px[i + 1], rr = px[i + 2], a = px[i + 3];
                    if (a < 200) continue;
                    double lum = (0.299 * rr + 0.587 * g + 0.114 * b) / 255.0;
                    if (lum < 0.38) continue;   // skip the dark icon; keep the pastel background
                    int key = (rr >> 4 << 8) | (g >> 4 << 4) | (b >> 4);
                    var e = buckets.TryGetValue(key, out var v) ? v : (0, 0, 0, 0);
                    buckets[key] = (e.count + 1, e.r + rr, e.g + g, e.b + b);
                }
                if (buckets.Count > 0)
                {
                    var best = buckets.Values.OrderByDescending(v => v.count).First();
                    color = Color.FromRgb((byte)(best.r / best.count), (byte)(best.g / best.count), (byte)(best.b / best.count));
                }
            }
        }
        catch { /* fall back to grey */ }
        _pipBg[token] = color;
        return color;
    }

    /// <summary>Symbols laid out side by side, centered, auto-scaled to fit (handles 1–3+).</summary>
    private void DrawLandSymbolRow(DrawingContext dc, IReadOnlyList<string> toks, Rect box)
    {
        const double gap = 0.14;    // spacing between symbols, as a fraction of symbol size
        double size = Math.Min(box.Height * 0.76, box.Width * 0.84 / (toks.Count + (toks.Count - 1) * gap));
        if (size <= 0) return;
        double totalW = toks.Count * size + (toks.Count - 1) * size * gap;
        double x = box.X + (box.Width - totalW) / 2;
        double y = box.Y + (box.Height - size) / 2;
        foreach (var t in toks)
        {
            var img = _symbols.GetSymbol(t);
            if (img != null)
            {
                var rect = new Rect(x, y, size, size);
                // A pip's circle fills its box (Scryfall mana symbols are r=50 in a 100 box), so its circle is
                // the rect's incircle — same shared shadow as the coin, interior = the pip image.
                DrawShadowedDisc(dc, new Point(rect.X + size / 2, rect.Y + size / 2), size / 2,
                    () => dc.DrawImage(img, rect));
            }
            x += size * (1 + gap);
        }
    }

    /// <summary>Clip geometries (one per symbol) that split a disc for the combined land-symbol styles.</summary>
    private static Geometry[] CombinedClips(string style, int n, Rect full)
    {
        var clips = new Geometry[n];
        double cx = full.X + full.Width / 2, cy = full.Y + full.Height / 2, r = full.Width / 2;
        var disc = new EllipseGeometry(new Point(cx, cy), r, r); disc.Freeze();

        bool curvy = style is "yinyang" or "swirl" or "curvy";

        if (curvy && n == 3)
        {
            // Mitsudomoe / 3-way yin-yang: three comma regions. Each is bounded by a semicircle divider out
            // to the rim, a 120° rim arc, and the next divider back to center — all curving the same way.
            var pts = new Point[3];
            for (int i = 0; i < 3; i++)
            {
                double th = -Math.PI / 2 + i * 2 * Math.PI / 3;
                pts[i] = new Point(cx + r * Math.Cos(th), cy + r * Math.Sin(th));
            }
            for (int i = 0; i < 3; i++)
            {
                var pi = pts[i]; var pj = pts[(i + 1) % 3];
                var fig = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = true };
                fig.Segments.Add(new ArcSegment(pi, new Size(r / 2, r / 2), 0, false, SweepDirection.Clockwise, true));
                fig.Segments.Add(new ArcSegment(pj, new Size(r, r), 0, false, SweepDirection.Clockwise, true));
                fig.Segments.Add(new ArcSegment(new Point(cx, cy), new Size(r / 2, r / 2), 0, false, SweepDirection.Counterclockwise, true));
                var pg = new PathGeometry(); pg.Figures.Add(fig);
                var g = new CombinedGeometry(GeometryCombineMode.Intersect, disc, pg); g.Freeze();
                clips[i] = g;
            }
            return clips;
        }

        if (curvy && n == 2)
        {
            // Classic S-curve, built symmetrically so BOTH halves are solid (don't derive one as
            // disc-minus-the-other — nested Exclude geometry misbehaves as a clip).
            var leftRect = new RectangleGeometry(new Rect(full.X, full.Y, full.Width / 2, full.Height));
            var rightRect = new RectangleGeometry(new Rect(cx, full.Y, full.Width / 2, full.Height));
            var top = new EllipseGeometry(new Point(cx, cy - r / 2), r / 2, r / 2);
            var bot = new EllipseGeometry(new Point(cx, cy + r / 2), r / 2, r / 2);
            // yin: left half + top lobe − bottom lobe
            Geometry a = new CombinedGeometry(GeometryCombineMode.Intersect, disc, leftRect);
            a = new CombinedGeometry(GeometryCombineMode.Union, a, top);
            a = new CombinedGeometry(GeometryCombineMode.Exclude, a, bot);
            a = new CombinedGeometry(GeometryCombineMode.Intersect, a, disc);
            a.Freeze();
            // yang: right half + bottom lobe − top lobe
            Geometry b = new CombinedGeometry(GeometryCombineMode.Intersect, disc, rightRect);
            b = new CombinedGeometry(GeometryCombineMode.Union, b, bot);
            b = new CombinedGeometry(GeometryCombineMode.Exclude, b, top);
            b = new CombinedGeometry(GeometryCombineMode.Intersect, b, disc);
            b.Freeze();
            clips[0] = a; clips[1] = b;
            return clips;
        }

        if (style == "splith")
        {
            double h = full.Height / n;
            for (int i = 0; i < n; i++)
            {
                var slice = new RectangleGeometry(new Rect(full.X, full.Y + i * h, full.Width, h));
                var g = new CombinedGeometry(GeometryCombineMode.Intersect, disc, slice); g.Freeze();
                clips[i] = g;
            }
            return clips;
        }

        if (style == "pie")
        {
            double start = -Math.PI / 2;     // first wedge points up
            for (int i = 0; i < n; i++)
            {
                double a0 = start + 2 * Math.PI * i / n, a1 = start + 2 * Math.PI * (i + 1) / n;
                clips[i] = Wedge(new Point(cx, cy), r, a0, a1);
            }
            return clips;
        }

        // default "splitv": vertical slices
        double w = full.Width / n;
        for (int i = 0; i < n; i++)
        {
            var slice = new RectangleGeometry(new Rect(full.X + i * w, full.Y, w, full.Height));
            var g = new CombinedGeometry(GeometryCombineMode.Intersect, disc, slice); g.Freeze();
            clips[i] = g;
        }
        return clips;
    }

    private static Geometry Wedge(Point c, double r, double a0, double a1)
    {
        var p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
        var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
        var fig = new PathFigure { StartPoint = c, IsClosed = true };
        fig.Segments.Add(new LineSegment(p0, true));
        fig.Segments.Add(new ArcSegment(p1, new Size(r, r), 0, (a1 - a0) > Math.PI, SweepDirection.Clockwise, true));
        var pg = new PathGeometry(); pg.Figures.Add(fig); pg.Freeze();
        return pg;
    }

    private void DrawArt(DrawingContext dc, CardModel card, Region win, bool previewHints)
    {
        var rect = ToRect(win);
        dc.DrawRectangle(CardBacking, null, rect);   // dark backing so any uncovered area never flashes white

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
        double scale = cover * Math.Clamp(card.ArtScale, 0.1, 8);   // clamp both ends against extreme zoom
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
        double maxW = Math.Max(10, rect.Width - 60);
        var ft = new FormattedText(
            "Add art — Change art…, paste, or drag an image in",
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            20, new SolidColorBrush(Color.FromRgb(0xA6, 0xAA, 0xB2)), 1.0)
        {
            MaxTextWidth = maxW,
            TextAlignment = TextAlignment.Center,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        // Centre the (maxW-wide) text box in the art window; TextAlignment.Center then centres each line
        // within it. Previously this ALSO shifted by ft.Width — double-centring that pushed the text right.
        dc.DrawText(ft, new Point(rect.X + (rect.Width - maxW) / 2, rect.Y + (rect.Height - ft.Height) / 2));
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
        // EffectiveTextBox, not TextBox: with the footer on the border (or off), the text box extends lower,
        // and sizing the scrim from the raw TextBox left those last lines sitting on bare art.
        var text = ToRect(spec.EffectiveTextBox);
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
        // When a double-faced indicator sits in the top-left, indent the name past it so it isn't covered.
        double titleLeft = bar.X + pad;
        if (DfcBadge(card, spec) is { } b && b.c.X + b.r + 10 > titleLeft)
            titleLeft = b.c.X + b.r + 10;
        double titleManaGap = 22;
        double titleMaxW = (manaWidth > 0 ? manaX - titleManaGap : bar.Right - pad) - titleLeft;
        var brush = new SolidColorBrush(TemplateSpec.ParseColor(spec.TitleFont.Color));
        var ft = FitText(card.Name, spec.TitleFont, spec.TitleFont.Size, 16, titleMaxW, brush);
        double ty = bar.Y + (bar.Height - ft.Height) / 2;
        DrawGlyphRun(dc, ft, new Point(titleLeft, ty), spec.TitleFont);
    }

    /// <summary>A small dark name-plate under the title holding the card's subtitle (drawn only when set).
    /// Works on any frame; position is the spec's SubtitleBar or a plate derived just below the title bar.</summary>
    private void DrawSubtitle(DrawingContext dc, CardModel card, TemplateSpec spec)
    {
        if (string.IsNullOrWhiteSpace(card.Subtitle)) return;

        Rect plate;
        if (spec.SubtitleBar != null)
            plate = ToRect(spec.SubtitleBar);
        else
        {
            var title = ToRect(spec.TitleBar);
            double h = Math.Max(40, title.Height * 0.66);   // room for the beveled band + recessed text
            double w = title.Width * 0.64;
            // Sit just below the title bar (slight overlap only), so it reads as a second plate.
            plate = new Rect(title.X + (title.Width - w) / 2, title.Bottom - h * 0.10, w, h);
        }
        // Drawn the exact same way as the P/T box — just a flatter box with capsule-rounded corners.
        var inner = DrawPtStylePlate(dc, plate, spec, Math.Min(plate.Height / 2, 22));

        // Text sits in the recessed center; pick a contrasting color for whatever color sits behind it.
        Color textBg = IsComposable(spec) ? TemplateSpec.ParseColor(spec.Colors.Panel)
            : ArtText(spec) ? Color.FromRgb(0x10, 0x12, 0x18)
            : TemplateSpec.ParseColor(spec.Colors.Frame);
        double lum = (0.299 * textBg.R + 0.587 * textBg.G + 0.114 * textBg.B) / 255.0;
        var textColor = lum > 0.55 ? Color.FromRgb(0x1A, 0x16, 0x0C) : Color.FromRgb(0xF2, 0xED, 0xE4);

        // Inherit the frame's title font so the subtitle matches the card's typography (italic, a bit smaller).
        var subFont = new FontSpec
        {
            Family = spec.TitleFont.Family, Size = spec.SubtitleFont.Size,
            Bold = spec.SubtitleFont.Bold, Italic = spec.SubtitleFont.Italic,
            Align = spec.SubtitleFont.Align, Color = spec.SubtitleFont.Color,   // color comes from the brush below
        };
        var brush = new SolidColorBrush(textColor); brush.Freeze();
        const double pad = 14;
        var ft = FitText(card.Subtitle, subFont, subFont.Size, 9, Math.Max(10, inner.Width - 2 * pad), brush);
        double tx = inner.X + (inner.Width - ft.Width) / 2;
        double ty = inner.Y + (inner.Height - ft.Height) / 2;
        DrawGlyphRun(dc, ft, new Point(tx, ty), subFont);
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
        // Art-forward cards and custom frames keep the template's exact placement (so the P/T box can be
        // positioned freely in the layout editor). Procedural framed cards auto-nest it bottom-right.
        if (ArtText(spec) || spec.CustomFrame || spec.IsFlipLayout) return rect;   // flip: at the end of the type line
        const double margin = 8;
        var tb = ToRect(spec.EffectiveTextBox);
        double left = tb.Right - margin - rect.Width;
        // Straddle the description panel's bottom edge: the box's MIDDLE sits on the panel bottom, so the
        // text ends around the box's top half and the box hangs half-below the panel (classic MTG look).
        double top = tb.Bottom - rect.Height / 2;
        // But never let it reach the bottom black border — always keep a clear gap above the rim.
        double border = Math.Max(0, spec.BorderThickness);
        const double borderGap = 16;
        double maxBottom = spec.CanvasHeight - border - borderGap;
        if (top + rect.Height > maxBottom) top = maxBottom - rect.Height;
        return new Rect(left, top, rect.Width, rect.Height);
    }

    private static void DrawPtBox(DrawingContext dc, CardModel card, TemplateSpec spec)
    {
        var rect = PtRect(spec);
        var inner = DrawPtStylePlate(dc, rect, spec);
        DrawCentered(dc, $"{card.Power}/{card.Toughness}", inner, NumeralFont(spec.PtFont));
    }

    /// <summary>Draws the P/T-box plate style (beveled band + recessed center, or the simple fill+bevel
    /// variant) into <paramref name="rect"/> and returns the inner rect where centered text should go.
    /// Shared by the P/T box and the subtitle plate so they look identical.</summary>
    private static Rect DrawPtStylePlate(DrawingContext dc, Rect rect, TemplateSpec spec, double? cornerRadius = null)
    {
        bool onArt = ArtText(spec);
        var borderColor = TemplateSpec.ParseColor(spec.Colors.PanelBorder);
        double r = cornerRadius ?? Math.Max(7, spec.PanelRadius);

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
            return innerm;
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

        return rect;
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

    // Fonts with OLD-STYLE figures (Georgia, Cambria, …) stagger digit heights — e.g. '4' dips below the
    // baseline and '6' rides high — which looks broken in a P/T or loyalty box. For number-only boxes we
    // swap to a lining-figure serif so every digit sits at the same height.
    private static readonly HashSet<string> OldStyleFigureFonts = new(StringComparer.OrdinalIgnoreCase)
        { "Georgia", "Cambria", "Constantia", "Palatino Linotype", "Calibri", "Candara", "Corbel" };

    private static FontSpec NumeralFont(FontSpec f)
    {
        if (!OldStyleFigureFonts.Contains((f.Family ?? "").Trim())) return f;
        return new FontSpec
        {
            Family = "Times New Roman", Size = f.Size, Color = f.Color, Bold = f.Bold,
            Italic = f.Italic, Align = f.Align, Shadow = f.Shadow, ShadowColor = f.ShadowColor,
        };
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
        FontSpec rulesFont, FontSpec flavorFont, double baseSymbolSize, Rect? avoid = null)
    {
        if (string.IsNullOrWhiteSpace(rules) && string.IsNullOrWhiteSpace(flavor)) return;

        double pad = 18;
        var box = new Rect(region.X + pad, region.Y + pad,
            Math.Max(0, region.W - 2 * pad), Math.Max(0, region.H - 2 * pad));

        // Shrink from the preferred size down to a floor, but always run at least once so a template with
        // a small RulesFont.Size still renders its text instead of silently dropping it.
        TextLayout? best = null;
        double minSize = Math.Min(11, rulesFont.Size);
        for (double size = Math.Max(minSize, rulesFont.Size); size >= minSize; size -= 1)
        {
            double scale = size / rulesFont.Size;
            best = LayoutContent(rules, flavor, box, rulesFont, size,
                                 flavorFont, flavorFont.Size * scale, baseSymbolSize * scale, avoid);
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
        FontSpec rulesFont, double rulesSize, FontSpec flavorFont, double flavorSize, double symSize, Rect? avoid)
    {
        var layout = new TextLayout();
        double y = box.Y;

        bool hasRules = !string.IsNullOrWhiteSpace(rules);
        bool hasFlavor = !string.IsNullOrWhiteSpace(flavor);

        if (hasRules)
            y = LayoutParagraphs(rules, rulesFont, rulesSize, symSize, box, layout.Items, y, avoid);

        if (hasFlavor)
        {
            if (hasRules)
            {
                y += rulesSize * 0.55;
                layout.Dividers.Add(y);
                y += rulesSize * 0.55;
            }
            y = LayoutParagraphs(flavor, flavorFont, flavorSize, flavorSize, box, layout.Items, y, avoid);
        }

        layout.Height = y - box.Y;
        return layout;
    }

    private double LayoutParagraphs(string text, FontSpec font, double fontSize, double symSize,
        Rect box, List<Placed> placed, double startY, Rect? avoid = null)
    {
        var brush = new SolidColorBrush(TemplateSpec.ParseColor(font.Color));
        double lineHeight = fontSize * 1.34;
        double x = box.X, y = startY;
        var probe = MakeText("Hg", font, fontSize, brush);
        double spaceWidth = MakeText(" ", font, fontSize, brush).WidthIncludingTrailingWhitespace;
        // Inline symbols read best centered on the text's cap height, not the full line box (which has
        // descent space below the glyphs and would make the symbol look bottom-aligned).
        double symTopOffset = probe.Baseline - fontSize * 0.34 - symSize / 2;

        // Lines whose vertical band overlaps a reserved box (the P/T box) wrap before it.
        double RightAt(double lineTop) =>
            avoid is Rect a && lineTop + lineHeight > a.Top && lineTop < a.Bottom
                ? Math.Min(box.Right, a.Left - 8)
                : box.Right;

        var paragraphs = text.Replace("\r", "").Split('\n');
        foreach (var para in paragraphs)
        {
            if (para.Trim().Length == 0) { y += lineHeight * 0.5; continue; }

            bool first = true;
            foreach (var word in BuildWords(para, font, fontSize, symSize, brush))
            {
                double gap = (first || word.NoLeadingGap) ? 0 : spaceWidth;
                if (!first && x + gap + word.Width > RightAt(y))
                {
                    x = box.X;
                    y += lineHeight;
                    gap = 0;
                }
                x += gap;

                if (word.Text != null)
                    placed.Add(new Placed(x, y, word.Text, null, 0));
                else if (word.Sym != null)
                    placed.Add(new Placed(x, y + symTopOffset, null, word.Sym, symSize));

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
                // TryParse: a 10+ digit level ("Level 2147483648" — a typo or a held key) would otherwise
                // throw OverflowException out of the render and abort a batch/sheet export.
                if (int.TryParse(levelUp.Groups[1].Value, out var parsed)) level = parsed;
            }
            else
            {
                rows.Add((level > 1 ? level.ToString() : null, t));
            }
        }
        return rows;
    }

    private void DrawBadgedRows(DrawingContext dc, List<(string? cost, string text)> rows, TemplateSpec spec, bool loyaltyShields = false, Rect? avoid = null)
    {
        if (rows.Count == 0) return;

        var (best, box) = BestBadgedLayout(rows, spec, loyaltyShields, avoid);
        if (best == null) return;

        // Badges/loyalty shields share the P/T box's look by default: same frame-color fill, panel-border
        // edge and top bevel, so a planeswalker's badges match its creature cousins' P/T box.
        var bFrame = TemplateSpec.ParseColor(spec.Colors.Frame);
        var bFrame2 = TemplateSpec.ParseColor(spec.Colors.Frame2);
        var badgeFill = new LinearGradientBrush(LightenC(bFrame2, 0.10), bFrame, new Point(0, 0), new Point(0, 1));
        badgeFill.Freeze();
        var badgeEdge = new Pen(new SolidColorBrush(TemplateSpec.ParseColor(spec.Colors.PanelBorder)), 2.2);
        badgeEdge.Freeze();
        var badgeHi = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1.4);
        badgeHi.Freeze();
        foreach (var (rect, costFt, text) in best.Badges)
        {
            double nudge = 0;
            if (loyaltyShields)
            {
                int dir = text.StartsWith('+') ? 1 : (text.StartsWith('−') || text.StartsWith('-')) ? -1 : 0;
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(105, 0, 0, 0)), null, LoyaltyShape(new Rect(rect.X - 2, rect.Y + 4, rect.Width, rect.Height), dir));
                dc.DrawGeometry(badgeFill, badgeEdge, LoyaltyShape(rect, dir));
                nudge = dir > 0 ? rect.Height * 0.12 : dir < 0 ? -rect.Height * 0.10 : 0;
            }
            else
            {
                double br = rect.Height * 0.28;
                dc.DrawRoundedRectangle(badgeFill, badgeEdge, rect, br, br);
                dc.DrawLine(badgeHi, new Point(rect.X + br, rect.Y + 2.5), new Point(rect.Right - br, rect.Y + 2.5));
            }
            dc.DrawText(costFt, new Point(rect.X + (rect.Width - costFt.Width) / 2, rect.Y + (rect.Height - costFt.Height) / 2 + nudge));
        }
        foreach (var p in best.Placed)
        {
            // DrawGlyphRun (not DrawText) so a shadowed font gets its outline here too — the full-art,
            // showcase and cinematic templates use white rules text that vanishes on light art without it.
            if (p.Text != null) DrawGlyphRun(dc, p.Text, new Point(p.X, p.Y), spec.RulesFont);
            else if (p.Sym != null) dc.DrawImage(p.Sym, new Rect(p.X, p.Y, p.SymSize, p.SymSize));
        }
        var dividerPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 40, 30, 10)), 1.2);
        foreach (var d in best.Dividers)
            dc.DrawLine(dividerPen, new Point(box.X, d), new Point(box.Right, d));
    }

    /// <summary>Picks the badged-row layout the renderer will actually draw: the text box inset by the
    /// standard padding, with the font shrunk a point at a time until the rows fit. Separated from the
    /// drawing so QA/tests can inspect the chosen layout (see <see cref="InspectPlaneswalkerLayout"/>).</summary>
    private (PwLayout? best, Rect box) BestBadgedLayout(List<(string? cost, string text)> rows, TemplateSpec spec,
        bool loyaltyShields, Rect? avoid)
    {
        double pad = 18;
        var tb0 = spec.EffectiveTextBox;
        var box = new Rect(tb0.X + pad, tb0.Y + pad,
            Math.Max(0, tb0.W - 2 * pad), Math.Max(0, tb0.H - 2 * pad));

        // Always run at least once (floor never above the preferred size) so small-font templates still draw.
        PwLayout? best = null;
        double minSize = Math.Min(11, spec.RulesFont.Size);
        for (double size = Math.Max(minSize, spec.RulesFont.Size); size >= minSize; size -= 1)
        {
            best = LayoutPw(rows, box, spec.RulesFont, size, spec.RulesSymbolSize * (size / spec.RulesFont.Size), loyaltyShields, avoid);
            if (best.Height <= box.Height) break;
        }
        return (best, box);
    }

    /// <summary>QA hook: the rectangles a planeswalker's ability text lays out into, plus the starting-loyalty
    /// shield's rect. The shield is drawn ON TOP of the abilities, so text running underneath it is invisible
    /// rather than ugly — it just silently loses words, which is why this is asserted geometrically.</summary>
    internal (List<Rect> TextRuns, Rect Loyalty) InspectPlaneswalkerLayout(CardModel card, TemplateSpec spec)
    {
        var loyalty = LoyaltyRect(spec);
        var avoid = string.IsNullOrWhiteSpace(card.Loyalty) ? (Rect?)null : loyalty;
        var (best, _) = BestBadgedLayout(ParseAbilities(card.RulesText), spec, loyaltyShields: true, avoid);
        var runs = new List<Rect>();
        foreach (var p in best?.Placed ?? new List<Placed>())
            if (p.Text != null) runs.Add(new Rect(p.X, p.Y, p.Text.Width, p.Text.Height));
            else if (p.Sym != null) runs.Add(new Rect(p.X, p.Y, p.SymSize, p.SymSize));
        return (runs, loyalty);
    }

    private PwLayout LayoutPw(List<(string? cost, string text)> rows, Rect box, FontSpec font, double fontSize, double symSize, bool loyaltyShields = false, Rect? avoid = null)
    {
        var L = new PwLayout();
        var brush = new SolidColorBrush(TemplateSpec.ParseColor(font.Color));
        double lineHeight = fontSize * 1.34;
        double badgeH = loyaltyShields ? fontSize * 1.7 : fontSize * 1.25;   // shields are taller (pointed)
        double gap = fontSize * 0.5;
        double spaceWidth = MakeText(" ", font, fontSize, brush).WidthIncludingTrailingWhitespace;
        var badgeFont = new FontSpec { Family = "Segoe UI", Bold = true };
        double y = box.Y;

        // Lines that overlap a reserved rect (the starting-loyalty shield, bottom-right) wrap early so the
        // last ability's words don't disappear underneath it.
        double RightAt(double lineTop) =>
            avoid is { } a && lineTop + lineHeight > a.Top && lineTop < a.Bottom
                ? Math.Min(box.Right, a.Left - gap)
                : box.Right;

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
                if (!first && x + g + word.Width > RightAt(lineY)) { x = box.X; lineY += lineHeight; g = 0; }
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

    /// <summary>Where the starting-loyalty shield sits: a touch more compact than the P/T box, anchored to
    /// the same bottom-right corner so it still nests in the description panel. Shared with the ability
    /// layout, which must wrap around it instead of running underneath.</summary>
    private static Rect LoyaltyRect(TemplateSpec spec)
    {
        var full = PtRect(spec);
        double w = full.Width * 0.80, h = full.Height * 0.90;
        return new Rect(full.Right - w, full.Bottom - h, w, h);
    }

    private static void DrawLoyalty(DrawingContext dc, CardModel card, TemplateSpec spec)
    {
        var box = LoyaltyRect(spec);

        // Starting loyalty sits in a downward-pointing shield that matches the P/T box / ability shields
        // (frame-colored fill + panel-border edge), not a hardcoded grey/white.
        var lFill = new LinearGradientBrush(LightenC(TemplateSpec.ParseColor(spec.Colors.Frame2), 0.10),
            TemplateSpec.ParseColor(spec.Colors.Frame), new Point(0, 0), new Point(0, 1));
        lFill.Freeze();
        var lEdge = new Pen(new SolidColorBrush(TemplateSpec.ParseColor(spec.Colors.PanelBorder)), 2.5);
        lEdge.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null, LoyaltyShape(new Rect(box.X - 3, box.Y + 4, box.Width, box.Height), -1));
        dc.DrawGeometry(lFill, lEdge, LoyaltyShape(box, -1));

        // Center the number on its true ink bounds within the shield's flat body (above the point), so it
        // sits optically centered rather than floating high off the font's line-box padding.
        double sh = box.Height * 0.30;   // matches LoyaltyShape's point-height fraction
        var ft = MakeText(card.Loyalty, NumeralFont(new FontSpec { Family = spec.PtFont.Family, Bold = true }), spec.PtFont.Size, Brushes.White);
        var bounds = ft.BuildGeometry(new Point(0, 0)).Bounds;
        double x = box.X + box.Width / 2 - (bounds.X + bounds.Width / 2);
        double y = box.Y + (box.Height - sh) / 2 + box.Height * 0.07 - (bounds.Y + bounds.Height / 2);
        dc.DrawText(ft, new Point(x, y));
    }

    /// <summary>A loyalty badge silhouette: dir +1 = point up (activated), -1 = point down (cost / start),
    /// 0 = a flat hexagon (neutral). Fills the given rect.</summary>
    /// <summary>Where a Battle's defense shield sits: anchored to the P/T box's bottom-right corner (so it
    /// nests in the description panel like a P/T box), but taller than wide, like a shield.</summary>
    private static Rect DefenseRect(TemplateSpec spec)
    {
        var full = PtRect(spec);
        double h = full.Height * 1.22, w = h * 0.86;
        return new Rect(full.Right - w, full.Bottom - h, w, h);
    }

    /// <summary>A Battle's starting defense: a curved heater shield with a notched top — deliberately a
    /// different silhouette from the straight-edged loyalty pentagon, so the two never read alike.</summary>
    private static void DrawDefense(DrawingContext dc, CardModel card, TemplateSpec spec)
    {
        var box = DefenseRect(spec);
        var fill = new LinearGradientBrush(LightenC(TemplateSpec.ParseColor(spec.Colors.Frame2), 0.10),
            TemplateSpec.ParseColor(spec.Colors.Frame), new Point(0, 0), new Point(0, 1));
        fill.Freeze();
        var edge = new Pen(new SolidColorBrush(TemplateSpec.ParseColor(spec.Colors.PanelBorder)), 2.5);
        edge.Freeze();
        var rim = new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1.4);
        rim.Freeze();

        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null,
            DefenseShape(new Rect(box.X - 3, box.Y + 4, box.Width, box.Height)));
        dc.DrawGeometry(fill, edge, DefenseShape(box));
        dc.DrawGeometry(null, rim, DefenseShape(Inset(box, box.Width * 0.11)));

        // Centre the number on its ink bounds in the shield's broad upper body (above the taper).
        var ft = MakeText(card.Defense, NumeralFont(new FontSpec { Family = spec.PtFont.Family, Bold = true }),
            spec.PtFont.Size, Brushes.White);
        var b = ft.BuildGeometry(new Point(0, 0)).Bounds;
        double x = box.X + box.Width / 2 - (b.X + b.Width / 2);
        double y = box.Y + box.Height * 0.44 - (b.Y + b.Height / 2);
        dc.DrawText(ft, new Point(x, y));
    }

    /// <summary>Heater-shield outline: a flat top with a small centre notch, straight upper sides, then
    /// curves that meet in a point at the bottom.</summary>
    private static Geometry DefenseShape(Rect r)
    {
        double cx = r.X + r.Width / 2, notch = r.Width * 0.10, shoulder = r.Y + r.Height * 0.42;
        var g = new StreamGeometry();
        using (var s = g.Open())
        {
            s.BeginFigure(new Point(r.X, r.Y), true, true);
            s.LineTo(new Point(cx - notch, r.Y), true, false);
            s.LineTo(new Point(cx, r.Y + notch * 0.9), true, false);
            s.LineTo(new Point(cx + notch, r.Y), true, false);
            s.LineTo(new Point(r.Right, r.Y), true, false);
            s.LineTo(new Point(r.Right, shoulder), true, false);
            s.BezierTo(new Point(r.Right, r.Y + r.Height * 0.78), new Point(cx + r.Width * 0.22, r.Bottom - r.Height * 0.06),
                new Point(cx, r.Bottom), true, false);
            s.BezierTo(new Point(cx - r.Width * 0.22, r.Bottom - r.Height * 0.06), new Point(r.X, r.Y + r.Height * 0.78),
                new Point(r.X, shoulder), true, false);
        }
        g.Freeze();
        return g;
    }

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
