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
    private readonly Dictionary<string, (long stamp, BitmapSource img)> _artCache = new();
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
        if (card.IsMeldBack) { DrawMeldBack(dc, card, template, previewHints); return; }

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
            else if (card.HasBands)
                DrawLevelUp(dc, card, spec, BottomOrnament(template, spec));   // leveler / Station / Case bands — any P/T box is in a band
            else if (ParseTopBand(card) is { } topBand)
                DrawTopBandCard(dc, card, spec, topBand,   // the rules below always wrap around the P/T box
                    card.HasDefense ? DefenseRect(spec) : card.HasPowerToughness ? PtRect(spec) : null);
            else if (card.IsAdventure)
                DrawAdventure(dc, card, spec);
            else if (card.ShowBigLandSymbol)
                DrawBigLandSymbol(dc, card, spec);
            else
            {
                DrawTextBox(dc, card.RulesText, card.FlavorText, spec.EffectiveTextBox, spec.RulesFont, spec.FlavorFont, spec.RulesSymbolSize, RulesAvoid(card, spec),
                    ArtText(spec) ? null : BottomOrnament(template, spec));
            }

            if (card.IsPlaneswalker && !string.IsNullOrWhiteSpace(card.Loyalty))
                DrawLoyalty(dc, card, spec);
            else if (card.HasDefense)
                DrawDefense(dc, card, spec);   // a Battle's starting defense
            else if (card.HasPowerToughness && !card.HasBands)
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

    // --- split and aftermath cards ---------------------------------------------

    /// <summary>One half of a split or aftermath card as it's drawn: the plain card it's drawn as, the template
    /// it's drawn with, and <see cref="ToCard"/> — where that template's own canvas lands on the upright card
    /// (scale, quarter turn and offset). Everything that has to know where a half is (drawing, the preview's
    /// hit-testing and drag-to-pan) goes through this one matrix.</summary>
    internal sealed record SplitPart(CardModel Card, Template Template, Matrix ToCard, double Scale)
    {
        public double Width => Template.Spec.CanvasWidth;
        public double Height => Template.Spec.CanvasHeight;

        /// <summary>A point on the upright card in this half's own canvas, or null when it's outside the half.</summary>
        public Point? FromCard(Point p)
        {
            var m = ToCard; m.Invert();
            var q = m.Transform(p);
            return q.X >= 0 && q.Y >= 0 && q.X <= Width && q.Y <= Height ? q : null;
        }

        /// <summary>A movement on the upright card as a movement in this half's own canvas.</summary>
        public Vector FromCard(Vector v)
        {
            var m = ToCard; m.Invert();
            return m.Transform(v);
        }
    }

    /// <summary>How a split or aftermath card is put together. Split: in READING coordinates (the card turned a
    /// quarter clockwise) the first half is on the left and the other half on the right, each the frame's normal
    /// card made smaller; a shared reminder (Fuse, a Room's door rules) gets a <see cref="Bar"/> across the
    /// bottom. Aftermath: the first half is upright across the top in the frame's wide (sideways) layout, and the
    /// other half below it is the normal card turned a quarter the other way (its top along the card's right
    /// edge). Either way the card's credits run upright along its bottom edge, <see cref="FooterStrip"/> deep.</summary>
    internal sealed record SplitLayout(SplitPart First, SplitPart Other, string SharedLine, Rect Bar, double FooterStrip,
        bool Aftermath, double BarDivide = 0)
    {
        /// <summary>Split reading coordinates → the upright card: the reading view's left edge is the card's bottom.</summary>
        public static Matrix ReadingToCard(double canvasHeight) => new(0, -1, 1, 0, 0, canvasHeight);
    }

    internal static SplitLayout SplitGeometry(CardModel card, Template template)
    {
        var spec = template.Spec;
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        double side = Math.Min(W, H);
        double m = Math.Max(6, side * 0.016);                          // outer margin and the gutter
        double foot = Math.Max(spec.BorderThickness, side * 0.042);    // the card's bottom edge (credits)
        var (front, half) = SplitHalves(card);
        var halfT = HalfTemplate(card, template);
        double W2 = halfT.Spec.CanvasWidth, H2 = halfT.Spec.CanvasHeight;

        if (card.IsAftermath)
        {
            // The first half reads upright in the frame's sideways layout: a drawn frame derives it, a picture
            // frame uses its landscape version (or, lacking one, its normal upright picture).
            front.Orientation = "landscape";
            var wide = TemplateService.ResolveFor(front, template);
            double tw = wide.Spec.CanvasWidth, th = wide.Spec.CanvasHeight;
            double availW = W - 2 * m, availH = H - foot - 2 * m;
            double s1 = Math.Min(availW / tw, availH * 0.56 / th);
            var top = new Matrix(s1, 0, 0, s1, (W - tw * s1) / 2, m);

            // The other half: the normal card turned a quarter counter-clockwise to read — its canvas x runs DOWN
            // the card and its y runs LEFT from the right edge.
            double regionY = m + th * s1 + m, regionH = H - foot - regionY;
            double s2 = Math.Max(0.05, Math.Min(regionH / W2, availW / H2));
            double readW = W2 * s2, readH = H2 * s2;
            var bottom = new Matrix(0, s2, -s2, 0, (W + readH) / 2, regionY + (regionH - readW) / 2);
            return new SplitLayout(new SplitPart(front, wide, top, s1), new SplitPart(half, halfT, bottom, s2),
                "", Rect.Empty, foot, Aftermath: true);
        }

        double rw = H, rh = W;
        var shared = SplitSharedLine(card).shared;
        double barH = shared.Length > 0 ? side * 0.075 : 0;
        double slotW = (rw - foot - 2 * m) / 2;
        double slotH = rh - 2 * m - (barH > 0 ? barH + m : 0);
        // Each half as big as its slot allows (a half with a frame of its own may be another shape), centred in it.
        double s = Math.Max(0.05, Math.Min(slotW / W, slotH / H));
        double sh = Math.Max(0.05, Math.Min(slotW / W2, slotH / H2));
        var left = new Rect(foot + (slotW - W * s) / 2, m + (slotH - H * s) / 2, W * s, H * s);
        var right = new Rect(foot + slotW + m + (slotW - W2 * sh) / 2, m + (slotH - H2 * sh) / 2, W2 * sh, H2 * sh);
        var bar = barH > 0 ? new Rect(left.X, rh - m - barH, right.Right - left.X, barH) : Rect.Empty;
        Matrix Place(Rect r, double scale)
        {
            var mx = new Matrix(scale, 0, 0, scale, r.X, r.Y);
            mx.Append(SplitLayout.ReadingToCard(H));
            return mx;
        }
        return new SplitLayout(new SplitPart(front, template, Place(left, s), s), new SplitPart(half, halfT, Place(right, sh), sh),
            shared, bar, foot, Aftermath: false, BarDivide: foot + slotW + m / 2);
    }

    /// <summary>The frame a split or aftermath card's other half is drawn with: its own
    /// (<see cref="CardModel.HalfTemplateName"/>) when that frame is installed, else the card's.</summary>
    internal static Template HalfTemplate(CardModel card, Template template)
        => TemplateService.Named(card.HalfTemplateName) ?? template;

    /// <summary>A reminder both halves of a split card end with — Fuse, or a Room's "(You may cast either half…)"
    /// — is printed ONCE across the card, so it comes off both halves' rules. Only a "Fuse…" line or a fully
    /// parenthesized one counts, so two halves that merely end alike ("Draw a card.") keep their text. An
    /// aftermath card has no shared line (its Aftermath reminder is the second half's own).</summary>
    internal static (string front, string half, string shared) SplitSharedLine(CardModel card)
    {
        string a = card.RulesText ?? "", b = card.OtherHalf?.RulesText ?? "";
        if (card.IsAftermath) return (a, b, "");
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

    /// <summary>A split or aftermath card (<see cref="SplitGeometry"/>): each half is drawn as a plain card with
    /// its own template through its <see cref="SplitPart.ToCard"/> — so every frame (drawn or picture) works
    /// unchanged — without its own credits; the card's credits run upright along its bottom edge.</summary>
    private void DrawSplit(DrawingContext dc, CardModel card, Template template, bool previewHints)
    {
        var spec = template.Spec;
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        double cardR = Math.Max(4, spec.CornerRadius);
        var g = SplitGeometry(card, template);

        var clip = new RectangleGeometry(new Rect(0, 0, W, H), cardR, cardR);
        clip.Freeze();
        dc.PushClip(clip);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x0A)), null, new Rect(0, 0, W, H));

        foreach (var part in new[] { g.First, g.Other })
        {
            dc.PushTransform(new MatrixTransform(part.ToCard));
            Draw(dc, part.Card, Grown(part.Template, part.Scale), previewHints, footer: false);
            dc.Pop();
        }
        if (g.SharedLine.Length > 0)
        {
            dc.PushTransform(new MatrixTransform(SplitLayout.ReadingToCard(H)));
            DrawSplitBar(dc, g, spec);
            dc.Pop();
        }

        // The card's credits, upright on its bottom edge.
        var footSpec = spec.Clone();
        footSpec.BorderThickness = g.FooterStrip;
        DrawFooter(dc, card, footSpec, onBorder: true);
        dc.Pop();
    }

    /// <summary>A half is drawn small, but its few lines should read at a normal card's size: start the rules
    /// (and flavor/symbols) as large as the scale took away; the text box still shrinks them to fit.</summary>
    private static Template Grown(Template template, double scale)
    {
        var s = template.Spec.Clone();
        double grow = Math.Min(1.6, 1 / Math.Max(0.05, scale));
        s.RulesFont.Size *= grow;
        s.FlavorFont.Size *= grow;
        s.RulesSymbolSize *= grow;
        return new Template
        {
            Name = template.Name, Spec = s, FramePath = template.FramePath,
            FrameImage = template.FrameImage, Variants = template.Variants,
        };
    }

    /// <summary>The shared reminder bar across the bottom of a split card's reading view: each end in its half's
    /// colour (from its mana cost, like a real Fuse bar — red under <i>Wear</i>, white under <i>Tear</i>), kept
    /// light so the dark text reads, blending where the halves meet.</summary>
    private static void DrawSplitBar(DrawingContext dc, SplitLayout g, TemplateSpec spec)
    {
        var bar = g.Bar;
        double r = bar.Height * 0.22;
        var fill = SplitBarFill(g);
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

    /// <summary>The Fuse bar's fill: the first half's colour on the left end, the other half's on the right, a
    /// short blend at <see cref="SplitLayout.BarDivide"/>. Both lightened towards paper for the dark text.</summary>
    internal static Brush SplitBarFill(SplitLayout g)
    {
        var bar = g.Bar;
        Color Tint(CardModel c) => LightenC(PrototypeTint(c.ManaCost ?? ""), 0.45);
        Color a = Tint(g.First.Card), b = Tint(g.Other.Card);
        if (a == b) { var solid = new SolidColorBrush(a); solid.Freeze(); return solid; }
        double mid = bar.Width <= 0 ? 0.5 : Math.Clamp((g.BarDivide - bar.X) / bar.Width, 0.1, 0.9);
        double blend = 0.06;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop(a, 0));
        brush.GradientStops.Add(new GradientStop(a, mid - blend));
        brush.GradientStops.Add(new GradientStop(b, mid + blend));
        brush.GradientStops.Add(new GradientStop(b, 1));
        brush.Freeze();
        return brush;
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

    // --- meld (two cards' backs make one big card) ---------------------------

    /// <summary>The melded card itself (<i>Brisela</i>) from a meld part's back face: the same fields drawn as a
    /// normal, whole card — no half, no double-faced indicator.</summary>
    internal static CardModel MeldedCard(CardModel back)
    {
        var c = back.Clone();   // a clone is never a back face
        c.MeldHalf = "";
        c.DfcStyle = "";
        return c;
    }

    /// <summary>Where the melded card sits on a meld part's back. The two parts laid side by side make one wide
    /// rectangle; the melded card fills it, made bigger and turned a quarter counter-clockwise (so it reads with the
    /// pair turned a quarter clockwise, like a split card), its top half on the left card and its bottom half on
    /// the right. Each back shows its own half: the "top" part is the left card, the "bottom" part the right.</summary>
    internal static SplitPart MeldPart(CardModel back, Template template)
    {
        var spec = template.Spec;
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        double s = Math.Min(2 * W / H, H / W);                      // as big as fits the pair (×1.4 on a 5:7 card)
        double ox = (2 * W - s * H) / 2, oy = (H - s * W) / 2;      // centred on the pair
        double dx = back.IsMeldBottom ? W : 0;                      // the right card sees the pair shifted left
        // Melded (x, y) → pair (ox + s·y, oy + s·(W − x)): a quarter turn counter-clockwise, top to the left.
        var m = new Matrix(0, -s, s, 0, ox - dx, oy + s * W);
        return new SplitPart(MeldedCard(back), template, m, s);
    }

    /// <summary>A meld part's back: its half of the melded card (drawn whole, through <see cref="MeldPart"/>, and
    /// cut off at the card's edge), inside the card's own black border.</summary>
    private void DrawMeldBack(DrawingContext dc, CardModel card, Template template, bool previewHints)
    {
        var spec = template.Spec;
        double W = spec.CanvasWidth, H = spec.CanvasHeight;
        double cardR = Math.Max(4, spec.CornerRadius);
        var part = MeldPart(card, template);

        var clip = new RectangleGeometry(new Rect(0, 0, W, H), cardR, cardR);
        clip.Freeze();
        dc.PushClip(clip);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x08, 0x08, 0x0A)), null, new Rect(0, 0, W, H));
        dc.PushTransform(new MatrixTransform(part.ToCard));
        Draw(dc, part.Card, template, previewHints);
        dc.Pop();
        dc.Pop();
        DrawOuterBorder(dc, W, H, cardR, spec);
    }

    /// <summary>The meld icon on a meld part's front: two halves of a triangle closing together.</summary>
    private static void DrawMeldGlyph(DrawingContext dc, Point c, double r)
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xED, 0xE6, 0xD0));
        double h = r * 1.05, w = r * 0.62, gap = r * 0.09;
        double top = c.Y - h / 2, bottom = c.Y + h / 2;
        foreach (var side in new[] { -1, 1 })
        {
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(new Point(c.X + side * gap, top), true, true);
                s.LineTo(new Point(c.X + side * (gap + w), bottom), true, false);
                s.LineTo(new Point(c.X + side * gap, bottom), true, false);
            }
            g.Freeze();
            dc.DrawGeometry(brush, null, g);
        }
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

        if (style == "meld")
        {
            DrawMeldGlyph(dc, c, r);
        }
        else if (style == "sunmoon")
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

        BitmapSource? img = null;
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
    private BitmapSource? LoadArt(string path)
    {
        if (!File.Exists(path)) return null;
        long stamp;
        try { var fi = new FileInfo(path); stamp = fi.LastWriteTimeUtc.Ticks ^ fi.Length; }
        catch { stamp = 0; }

        if (_artCache.TryGetValue(path, out var e) && e.stamp == stamp) return e.img;

        BitmapSource img;
        // Read bytes first so the file isn't locked and a decode error can't hold a handle; phone photos
        // come out upright (EXIF orientation applied).
        try { img = ImageIntake.LoadOriented(File.ReadAllBytes(path)); }
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
        // A token's name is centred, as printed (only when it has no cost to share the bar with); an
        // indicator's indent is kept on both sides so the name stays centred on the card.
        bool centred = card.IsToken && manaWidth == 0;
        if (centred) titleMaxW = bar.Width - 2 * (titleLeft - bar.X);
        var ft = FitText(card.Name, spec.TitleFont, spec.TitleFont.Size, 16, titleMaxW, brush);
        double ty = bar.Y + (bar.Height - ft.Height) / 2;
        if (centred) titleLeft = bar.X + (bar.Width - ft.Width) / 2;
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
    /// <summary>The box plain rules text wraps around: creatures nest a P/T box (a battle its defense) in the
    /// bottom-right, so the text ends before it instead of hiding underneath — on full-art frames too, whose box
    /// sits inside the text area.</summary>
    internal static Rect? RulesAvoid(CardModel card, TemplateSpec spec) =>
        card.HasDefense ? DefenseRect(spec) : card.HasPowerToughness ? PtRect(spec) : null;

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
        // A vanilla token has no panel: the box sits just under its type line (overlapping it by the 4 px
        // TemplateSpec.ToToken leaves), clear of the type line's text and set symbol.
        if (spec.IsTokenLayout && tb.Height < 1) top = spec.TypeBar.Bottom - 4;
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

    /// <param name="ornament">A medallion set into the box's bottom edge (<see cref="BottomOrnament"/>): text that
    /// would run onto it shrinks to end above it instead. Short text is laid out exactly as without one.</param>
    private void DrawTextBox(DrawingContext dc, string rules, string flavor, Region region,
        FontSpec rulesFont, FontSpec flavorFont, double baseSymbolSize, Rect? avoid = null, Rect? ornament = null)
    {
        if (string.IsNullOrWhiteSpace(rules) && string.IsNullOrWhiteSpace(flavor)) return;

        double pad = 18;
        var box = new Rect(region.X + pad, region.Y + pad,
            Math.Max(0, region.W - 2 * pad), Math.Max(0, region.H - 2 * pad));
        if (ornament is { } orn && orn.Top - 4 < box.Bottom && orn.Left < box.Right && orn.Right > box.Left)
            box.Height = Math.Max(Math.Min(box.Height, 20), orn.Top - 4 - box.Y);

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

        // Lines whose vertical band overlaps a reserved box (the P/T box) wrap before it — when it's actually
        // in the right part of the text column (a box beside or left of it would leave no room at all).
        double RightAt(double lineTop) =>
            avoid is Rect a && lineTop + lineHeight > a.Top && lineTop < a.Bottom
                && a.Left < box.Right && a.Left - 8 > box.X + box.Width / 3
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

    // --- prototype / mutate: a band across the top of the text box -------------

    /// <summary>The band a Prototype or Mutate card prints across the top of its text box, read from the rules'
    /// first line. <see cref="Text"/> is what the band says; for a prototype, <see cref="Cost"/> and
    /// <see cref="Pt"/> are its own smaller mana cost and power/toughness, drawn at the band's right end, and
    /// the band is tinted in that cost's colours. <see cref="Rest"/> is the rest of the rules, drawn below.</summary>
    internal sealed record TopBand(string Kind, string Text, string Cost, string Pt, string Rest);

    private static readonly Regex PrototypeLine = new(
        @"^Prototype\s+((?:\{[^}]+\})+)\s*[—–-]\s*([0-9X*+\-]+\s*/\s*[0-9X*+\-]+)\s*(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MutateLine = new(@"^Mutate\s+(?:\{[^}]+\})+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>A Prototype ("Prototype {2}{R} — 3/2 (…)") or Mutate ("Mutate {1}{G}{G} (…)") card's band, from
    /// the first line of its rules the way Scryfall writes them; null for any other card.</summary>
    internal static TopBand? ParseTopBand(CardModel card)
    {
        var lines = (card.RulesText ?? "").Replace("\r", "").Split('\n');
        int first = Array.FindIndex(lines, l => l.Trim().Length > 0);
        if (first < 0) return null;
        var line = lines[first].Trim();
        string rest = string.Join("\n", lines.Skip(first + 1)).Trim();
        var p = PrototypeLine.Match(line);
        if (p.Success)
        {
            var reminder = p.Groups[3].Value.Trim();
            return new TopBand("prototype", reminder.Length > 0 ? "Prototype " + reminder : "Prototype",
                p.Groups[1].Value, Regex.Replace(p.Groups[2].Value, @"\s+", ""), rest);
        }
        if (MutateLine.IsMatch(line)) return new TopBand("mutate", line, "", "", rest);
        return null;
    }

    /// <summary>The band colour for a prototype cost: its one colour, gold for two or more, grey for colourless.</summary>
    internal static Color PrototypeTint(string cost)
    {
        var colours = ManaText.Tokenize(cost).Where(t => t.IsSymbol)
            .SelectMany(t => t.Value.Trim('{', '}').ToUpperInvariant().Split('/'))
            .Where(c => c is "W" or "U" or "B" or "R" or "G").Distinct().ToList();
        return colours.Count switch
        {
            0 => Color.FromRgb(0xB9, 0xB9, 0xBC),
            > 1 => Color.FromRgb(0xE2, 0xC2, 0x5E),
            _ => colours[0] switch
            {
                "W" => Color.FromRgb(0xEF, 0xE6, 0xC8),
                "U" => Color.FromRgb(0x8F, 0xBC, 0xE3),
                "B" => Color.FromRgb(0x9C, 0x90, 0x8C),
                "R" => Color.FromRgb(0xE8, 0x8A, 0x74),
                _ => Color.FromRgb(0x8C, 0xC4, 0x93),
            },
        };
    }

    /// <summary>Where a top-band card's parts go: the band across the top of the text box (with, for a
    /// prototype, a right-hand column for its cost and P/T plate), the rest of the text box below it, and the
    /// one rules size both share — shrunk until the band and the rest fit together.</summary>
    internal (Rect Band, Rect BandText, Rect? Cost, Rect? Pt, Rect Below, double Size) TopBandLayout(
        CardModel card, TemplateSpec spec, TopBand band, Rect? avoid)
    {
        var tb = ToRect(spec.EffectiveTextBox);
        double inset = 3, pad = 12, gap = 10;
        var box = new Rect(tb.X + inset, tb.Y + inset, Math.Max(0, tb.Width - 2 * inset), Math.Max(0, tb.Height - 2 * inset));
        bool proto = band.Kind == "prototype";
        // Smaller than the corner box, and never more than about a third of a short (full-art) text box.
        double ptScale = Math.Min(0.62, box.Height * 0.3 / Math.Max(1, spec.PtBox.H));
        double ptW = spec.PtBox.W * ptScale, ptH = spec.PtBox.H * ptScale;
        int costCount = ManaText.Tokenize(band.Cost).Count(t => t.IsSymbol);

        double size = spec.RulesFont.Size, bandH = 0, symSize = 0, colW = 0;
        double minSize = Math.Min(10, spec.RulesFont.Size);
        for (size = Math.Max(minSize, spec.RulesFont.Size); size >= minSize; size -= 1)
        {
            double scale = size / spec.RulesFont.Size;
            symSize = spec.RulesSymbolSize * scale;
            colW = proto ? Math.Max(ptW, costCount * symSize * 1.08) : 0;
            var textCol = new Rect(box.X + pad, 0, Math.Max(20, box.Width - 2 * pad - (proto ? colW + gap : 0)), 100000);
            double textH = LayoutContent(band.Text, "", textCol, spec.RulesFont, size, spec.FlavorFont,
                spec.FlavorFont.Size * scale, symSize, null).Height;
            bandH = Math.Max(textH, proto ? symSize + 4 + ptH : 0) + 2 * pad * 0.7;
            var below = new Rect(box.X + 18 - inset, 0, Math.Max(0, box.Width - 2 * (18 - inset)), 100000);
            double restH = band.Rest.Length + card.FlavorText.Length == 0 ? 0
                : LayoutContent(band.Rest, card.FlavorText, below, spec.RulesFont, size, spec.FlavorFont,
                    spec.FlavorFont.Size * scale, symSize, avoid is { } a ? new Rect(a.X, a.Y - box.Y - bandH, a.Width, a.Height) : null).Height + 2 * 18;
            if (bandH + restH <= box.Height) break;
        }

        var bandRect = new Rect(box.X, box.Y, box.Width, Math.Min(bandH, box.Height));
        var bandText = new Rect(box.X + pad, box.Y, Math.Max(20, box.Width - 2 * pad - (proto ? colW + gap : 0)), bandRect.Height);
        Rect? cost = null, pt = null;
        if (proto)
        {
            double colX = box.Right - pad * 0.6 - colW;
            double stackH = symSize + 4 + ptH, top = box.Y + (bandRect.Height - stackH) / 2;
            cost = new Rect(colX + colW - costCount * symSize * 1.08, top, costCount * symSize * 1.08, symSize);
            pt = new Rect(colX + colW - ptW, top + symSize + 4, ptW, ptH);
        }
        var belowRect = new Rect(box.X - inset, bandRect.Bottom, tb.Width, Math.Max(0, tb.Bottom - bandRect.Bottom));
        return (bandRect, bandText, cost, pt, belowRect, size);
    }

    /// <summary>A Prototype or Mutate card's text box: the band across the top (tinted in the prototype cost's
    /// colours, or shaded for mutate) holding its first line — a prototype's own small cost and P/T plate at
    /// the right end — and the rest of the rules and flavor below, around the card's P/T box as usual.</summary>
    private void DrawTopBandCard(DrawingContext dc, CardModel card, TemplateSpec spec, TopBand band, Rect? avoid)
    {
        var (bandRect, bandText, cost, pt, below, size) = TopBandLayout(card, spec, band, avoid);
        double scale = size / spec.RulesFont.Size;
        double r = Math.Min(10, bandRect.Height * 0.15);

        Color edge;
        if (band.Kind == "prototype")
        {
            var tint = PrototypeTint(band.Cost);
            edge = DarkenC(tint, 0.35);
            var fill = new LinearGradientBrush(Color.FromArgb(235, LightenC(tint, 0.12).R, LightenC(tint, 0.12).G, LightenC(tint, 0.12).B),
                Color.FromArgb(235, tint.R, tint.G, tint.B), new Point(0, 0), new Point(0, 1));
            fill.Freeze();
            dc.DrawRoundedRectangle(fill, new Pen(new SolidColorBrush(edge), 2), bandRect, r, r);
        }
        else
        {
            // A shade set apart from the rest of the box: darker on a light text box, lighter on a dark one
            // (frames whose rules text is light, e.g. Midnight and full-art).
            var ink = TemplateSpec.ParseColor(spec.RulesFont.Color);
            bool darkBox = 0.299 * ink.R + 0.587 * ink.G + 0.114 * ink.B > 150;
            edge = darkBox ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(110, 40, 30, 10);
            dc.DrawRoundedRectangle(new SolidColorBrush(darkBox ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(38, 30, 20, 0)),
                null, bandRect, r, r);
            dc.DrawLine(new Pen(new SolidColorBrush(edge), 1.2), bandRect.BottomLeft, bandRect.BottomRight);
        }

        // A prototype band is always a light tint, so its text is dark ink without the outline frames with
        // white rules text (full-art, Midnight…) use — white on a light band would vanish.
        var bandFont = band.Kind != "prototype" ? spec.RulesFont : new FontSpec
        {
            Family = spec.RulesFont.Family, Size = spec.RulesFont.Size, Bold = spec.RulesFont.Bold,
            Italic = spec.RulesFont.Italic, Align = spec.RulesFont.Align, Color = "#1C1A17",
        };
        var probe = LayoutContent(band.Text, "", new Rect(bandText.X, 0, bandText.Width, 100000), bandFont, size,
            spec.FlavorFont, spec.FlavorFont.Size * scale, spec.RulesSymbolSize * scale, null);
        double top = bandText.Y + Math.Max(0, (bandText.Height - probe.Height) / 2);
        var lay = LayoutContent(band.Text, "", new Rect(bandText.X, top, bandText.Width, bandText.Height), bandFont, size,
            spec.FlavorFont, spec.FlavorFont.Size * scale, spec.RulesSymbolSize * scale, null);
        foreach (var p in lay.Items)
        {
            if (p.Text != null) DrawGlyphRun(dc, p.Text, new Point(p.X, p.Y), bandFont);
            else if (p.Sym != null) dc.DrawImage(p.Sym, new Rect(p.X, p.Y, p.SymSize, p.SymSize));
        }

        if (cost is { } c)
        {
            double s = c.Height, x = c.X;
            foreach (var t in ManaText.Tokenize(band.Cost).Where(t => t.IsSymbol))
            {
                if (_symbols.GetSymbol(t.Value) is { } sym) dc.DrawImage(sym, new Rect(x, c.Y, s, s));
                x += s * 1.08;
            }
        }
        if (pt is { } pr)
        {
            double pr2 = pr.Height * 0.3;
            var plate = new LinearGradientBrush(LightenC(edge, 0.15), edge, new Point(0, 0), new Point(0, 1));
            plate.Freeze();
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, new Rect(pr.X - 2, pr.Y + 3, pr.Width, pr.Height), pr2, pr2);
            dc.DrawRoundedRectangle(plate, new Pen(new SolidColorBrush(DarkenC(edge, 0.3)), 1.6), pr, pr2, pr2);
            var f = NumeralFont(spec.PtFont);
            DrawCentered(dc, band.Pt, pr, new FontSpec { Family = f.Family, Bold = true, Size = pr.Height * 0.62, Color = "#FFFFFF" });
        }

        var rules = new FontSpec
        {
            Family = spec.RulesFont.Family, Size = size, Bold = spec.RulesFont.Bold, Italic = spec.RulesFont.Italic,
            Align = spec.RulesFont.Align, Color = spec.RulesFont.Color, Shadow = spec.RulesFont.Shadow, ShadowColor = spec.RulesFont.ShadowColor,
        };
        var flavor = new FontSpec
        {
            Family = spec.FlavorFont.Family, Size = spec.FlavorFont.Size * scale, Bold = spec.FlavorFont.Bold, Italic = spec.FlavorFont.Italic,
            Align = spec.FlavorFont.Align, Color = spec.FlavorFont.Color, Shadow = spec.FlavorFont.Shadow, ShadowColor = spec.FlavorFont.ShadowColor,
        };
        DrawTextBox(dc, band.Rest, card.FlavorText, new Region { X = below.X, Y = below.Y, W = below.Width, H = below.Height },
            rules, flavor, spec.RulesSymbolSize * scale, avoid);
    }

    // --- level up -------------------------------------------------------------

    /// <summary>One band of a leveler's text box: the level range it applies from (null for the base band, e.g.
    /// "2-6" or "7+"), the power/toughness it has there (may be empty) and its rules. <paramref name="Kind"/> is
    /// what its badge shows: "level" (LEVEL over the range), "station" (the threshold, "12+"), "solve" (a magnifying
    /// glass — a Case's To solve part) or "solved" (a check mark).</summary>
    internal sealed record LevelBand(string? Level, string Pt, string Text, string Kind = "level");

    private static readonly Regex StationLine = new(@"^(\d+)\+\s*\|\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex StationCreatureAt = new(@"creature at (\d+)\+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Splits a Station card's rules into bands, reading Scryfall's wording: the lines before the first
    /// "7+ | …" threshold (an ETB, the Station reminder) are the base band; each threshold line starts a band, and
    /// every line after it belongs to it until the next threshold (<i>Uthros Research Craft</i>'s 12+ band is
    /// "Flying" AND "This Spacecraft gets +1/+0…"). The P/T box goes on the band where it becomes a creature
    /// ("It's an artifact creature at 12+"), else the last band — never the base band.</summary>
    internal static List<LevelBand> ParseStation(CardModel card)
    {
        var bands = new List<(string? level, List<string> text)> { (null, new List<string>()) };
        foreach (var raw in (card.RulesText ?? "").Replace("\r", "").Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            var m = StationLine.Match(t);
            if (m.Success)
            {
                bands.Add((m.Groups[1].Value + "+", new List<string>()));
                if (m.Groups[2].Value.Trim().Length > 0) bands[^1].text.Add(m.Groups[2].Value.Trim());
            }
            else bands[^1].text.Add(t);
        }
        if (bands[0].text.Count == 0) bands.RemoveAt(0);

        int ptAt = -1;
        if (card.HasPowerToughness)
        {
            var at = StationCreatureAt.Match(card.RulesText ?? "");
            ptAt = at.Success ? bands.FindIndex(b => b.level == at.Groups[1].Value + "+") : -1;
            if (ptAt < 0) ptAt = bands.FindLastIndex(b => b.level != null);
        }
        return bands.Select((b, i) => new LevelBand(b.level, i == ptAt ? $"{card.Power}/{card.Toughness}" : "",
            string.Join("\n", b.text), "station")).ToList();
    }

    private static readonly Regex CaseLine = new(@"^(To solve|Solved)\s*[—–-]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Splits a Case's rules into bands: the opening ability (base band), the "To solve — …" condition with
    /// its reminder, and the "Solved — …" ability, each keeping its own wording. A creature Case's P/T box goes on
    /// the Solved band.</summary>
    internal static List<LevelBand> ParseCase(CardModel card)
    {
        var bands = new List<(string kind, List<string> text)> { ("", new List<string>()) };
        foreach (var raw in (card.RulesText ?? "").Replace("\r", "").Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            var m = CaseLine.Match(t);
            if (m.Success) bands.Add((m.Groups[1].Value.StartsWith("To", StringComparison.OrdinalIgnoreCase) ? "solve" : "solved", new List<string>()));
            bands[^1].text.Add(t);
        }
        if (bands[0].text.Count == 0) bands.RemoveAt(0);
        int ptAt = card.HasPowerToughness ? bands.Count - 1 : -1;
        return bands.Select((b, i) => new LevelBand(b.kind.Length == 0 ? null : b.kind,
            i == ptAt ? $"{card.Power}/{card.Toughness}" : "", string.Join("\n", b.text), b.kind.Length == 0 ? "level" : b.kind)).ToList();
    }

    /// <summary>A banded card's bands: leveler, Station or Case.</summary>
    internal static List<LevelBand> ParseBands(CardModel card)
        => card.IsLevelUp ? ParseLevelUp(card) : card.IsStation ? ParseStation(card) : card.IsCase ? ParseCase(card) : ParseLevelUp(card);

    private static readonly Regex LevelLine = new(@"^LEVEL\s+(\d+(?:\s*-\s*\d+|\+))$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PtLine = new(@"^[0-9X*+\-]+\s*/\s*[0-9X*+\-]+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Splits a leveler's rules into bands, reading Scryfall's wording: everything before the first
    /// "LEVEL n-m" line is the base band (with the card's own P/T); each "LEVEL n-m" / "LEVEL n+" line starts a
    /// band, whose next line, if it is a bare "3/3", is that band's P/T and the rest its rules.</summary>
    internal static List<LevelBand> ParseLevelUp(CardModel card)
    {
        var bands = new List<LevelBand>();
        string? level = null;
        string pt = card.HasPowerToughness ? $"{card.Power}/{card.Toughness}" : "";
        var text = new List<string>();
        bool expectPt = false;
        void Flush() => bands.Add(new LevelBand(level, pt, string.Join("\n", text)));
        foreach (var raw in (card.RulesText ?? "").Replace("\r", "").Split('\n'))
        {
            var t = raw.Trim();
            var m = LevelLine.Match(t);
            if (m.Success)
            {
                Flush();
                level = Regex.Replace(m.Groups[1].Value, @"\s+", "");
                pt = ""; text.Clear(); expectPt = true;
                continue;
            }
            if (expectPt && PtLine.IsMatch(t)) { pt = Regex.Replace(t, @"\s+", ""); expectPt = false; continue; }
            if (t.Length == 0) continue;
            expectPt = false;
            text.Add(t);
        }
        Flush();
        // A leveler with nothing before its first LEVEL line still shows its base P/T band.
        return bands;
    }

    /// <summary>Where each band of a leveler goes, and the rules size they all share: the text box is split into
    /// bands stacked top to bottom, each tall enough for its text, its level badge and its P/T box, with the
    /// spare height shared out evenly. One size for every band (shrunk until they all fit) so they read alike.</summary>
    internal (List<(LevelBand band, Rect rect, Rect text, Rect? badge, Rect? pt)> Bands, double Size) LevelUpLayout(
        CardModel card, TemplateSpec spec, Rect? ornament = null)
    {
        var bands = ParseBands(card);
        var tb = ToRect(spec.EffectiveTextBox);
        // A frame ornament on the text box's bottom edge (a medallion): the bottom band's text stays above it.
        double lost = ornament is { } o ? Math.Max(0, tb.Bottom - o.Top) : 0;
        double inset = 3;
        var box = new Rect(tb.X + inset, tb.Y + inset, Math.Max(0, tb.Width - 2 * inset), Math.Max(0, tb.Height - 2 * inset));
        // The bands' P/T boxes are a little smaller than the corner box — and never taller than a band's fair
        // share of a short text box (full-art frames), or they'd crowd the rules down to the smallest size.
        double ptScale = Math.Min(0.78, box.Height / Math.Max(1, bands.Count) * 0.72 / Math.Max(1, spec.PtBox.H));
        double ptW = spec.PtBox.W * ptScale, ptH = spec.PtBox.H * ptScale, gap = 10, pad = 12;

        double size = spec.RulesFont.Size;
        double[] need = new double[bands.Count];
        Rect TextCol(LevelBand b, double sz)
        {
            double left = box.X + pad + (b.Level != null ? BadgeWidth(sz) + gap : 0);
            double right = box.Right - pad - (b.Pt.Length > 0 ? ptW + gap : 0);
            return new Rect(left, 0, Math.Max(20, right - left), 100000);
        }
        double minSize = Math.Min(10, spec.RulesFont.Size);
        for (size = Math.Max(minSize, spec.RulesFont.Size); size >= minSize; size -= 1)
        {
            double scale = size / spec.RulesFont.Size;
            for (int i = 0; i < bands.Count; i++)
            {
                var b = bands[i];
                double textH = b.Text.Length == 0 ? 0
                    : LayoutContent(b.Text, "", TextCol(b, size), spec.RulesFont, size, spec.FlavorFont, spec.FlavorFont.Size * scale,
                        spec.RulesSymbolSize * scale, null).Height;
                need[i] = Math.Max(textH, Math.Max(b.Pt.Length > 0 ? ptH : 0, b.Level != null ? BadgeHeight(size) : 0)) + 2 * pad * 0.6;
            }
            if (need.Sum() + lost <= box.Height) break;
        }

        double spare = Math.Max(0, box.Height - need.Sum()) / Math.Max(1, bands.Count);
        var result = new List<(LevelBand, Rect, Rect, Rect?, Rect?)>();
        double y = box.Y;
        for (int i = 0; i < bands.Count; i++)
        {
            var b = bands[i];
            double h = need[i] + spare;
            var rect = new Rect(box.X, y, box.Width, h);
            var col = TextCol(b, size);
            Rect? badge = b.Level == null ? null
                : new Rect(box.X + pad * 0.6, y + (h - BadgeHeight(size)) / 2, BadgeWidth(size), BadgeHeight(size));
            Rect? pt = b.Pt.Length == 0 ? null : new Rect(box.Right - pad * 0.6 - ptW, y + (h - ptH) / 2, ptW, ptH);
            var textRect = new Rect(col.X, y, col.Width, h);
            if (ornament is { } orn && orn.Top < y + h && orn.Left < col.Right && orn.Right > col.Left)
                textRect = new Rect(col.X, y, col.Width, Math.Max(1, orn.Top - 4 - y));
            result.Add((b, rect, textRect, badge, pt));
            y += h;
        }
        return (result, size);
    }

    private static double BadgeWidth(double size) => size * 3.1;
    private static double BadgeHeight(double size) => size * 2.5;

    /// <summary>A leveler's text box (<i>Student of Warfare</i>): bands stacked top to bottom, each a shade darker
    /// than the one above, the level bands with an arrow-shaped LEVEL badge on the left, and every band with
    /// its own P/T box on the right (the base band shows the card's P/T, so there's no corner box).</summary>
    private void DrawLevelUp(DrawingContext dc, CardModel card, TemplateSpec spec, Rect? ornament = null)
    {
        var (bands, size) = LevelUpLayout(card, spec, ornament);
        if (bands.Count == 0) return;
        double scale = size / spec.RulesFont.Size;
        var divider = new Pen(new SolidColorBrush(Color.FromArgb(90, 40, 30, 10)), 1.2);
        divider.Freeze();

        for (int i = 0; i < bands.Count; i++)
        {
            var (band, rect, text, badge, pt) = bands[i];
            if (i > 0)
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)Math.Min(60, 16 * i), 60, 40, 10)), null, rect);
                dc.DrawLine(divider, rect.TopLeft, rect.TopRight);
            }

            if (band.Text.Length > 0)
            {
                var probe = LayoutContent(band.Text, "", new Rect(text.X, 0, text.Width, 100000), spec.RulesFont, size,
                    spec.FlavorFont, spec.FlavorFont.Size * scale, spec.RulesSymbolSize * scale, null);
                double top = text.Y + Math.Max(0, (text.Height - probe.Height) / 2);
                var lay = LayoutContent(band.Text, "", new Rect(text.X, top, text.Width, text.Height), spec.RulesFont, size,
                    spec.FlavorFont, spec.FlavorFont.Size * scale, spec.RulesSymbolSize * scale, null);
                foreach (var p in lay.Items)
                {
                    if (p.Text != null) DrawGlyphRun(dc, p.Text, new Point(p.X, p.Y), spec.RulesFont);
                    else if (p.Sym != null) dc.DrawImage(p.Sym, new Rect(p.X, p.Y, p.SymSize, p.SymSize));
                }
            }

            if (badge is { } b) DrawLevelBadge(dc, b, band, spec);
            if (pt is { } r)
            {
                var inner = DrawPtStylePlate(dc, r, spec);
                var f = NumeralFont(spec.PtFont);
                f = new FontSpec
                {
                    Family = f.Family, Size = f.Size * 0.85, Bold = f.Bold, Italic = f.Italic, Align = f.Align,
                    Color = f.Color, Shadow = f.Shadow, ShadowColor = f.ShadowColor,
                };
                DrawCentered(dc, band.Pt, inner, f);
            }
        }
    }

    /// <summary>The arrow-shaped LEVEL badge: a plate in the frame's colours pointing right, "LEVEL" small over
    /// the range ("2-6", "7+").</summary>
    private static void DrawLevelBadge(DrawingContext dc, Rect r, LevelBand band, TemplateSpec spec)
    {
        string level = band.Level ?? "";
        double tip = r.Height * 0.32;
        var fig = new PathFigure { StartPoint = r.TopLeft, IsClosed = true };
        fig.Segments.Add(new LineSegment(new Point(r.Right - tip, r.Top), true));
        fig.Segments.Add(new LineSegment(new Point(r.Right, r.Top + r.Height / 2), true));
        fig.Segments.Add(new LineSegment(new Point(r.Right - tip, r.Bottom), true));
        fig.Segments.Add(new LineSegment(r.BottomLeft, true));
        var geo = new PathGeometry(new[] { fig });
        geo.Freeze();

        var frame = TemplateSpec.ParseColor(spec.Colors.Frame);
        var frame2 = TemplateSpec.ParseColor(spec.Colors.Frame2);
        var fill = new LinearGradientBrush(LightenC(frame2, 0.35), DarkenC(frame, 0.10), new Point(0, 0), new Point(0, 1));
        fill.Freeze();
        dc.PushTransform(new TranslateTransform(-2, 3));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, geo);
        dc.Pop();
        dc.DrawGeometry(fill, new Pen(new SolidColorBrush(TemplateSpec.ParseColor(spec.Colors.PanelBorder)), 2), geo);

        // Ink that reads on the plate, whatever the frame's colour.
        var mid = LightenC(frame2, 0.15);
        bool light = 0.299 * mid.R + 0.587 * mid.G + 0.114 * mid.B > 140;
        var ink = light ? Color.FromRgb(0x1C, 0x18, 0x12) : Colors.White;
        var small = new FontSpec { Family = "Segoe UI", Bold = true, Size = r.Height * 0.24, Color = light ? "#1C1812" : "#FFFFFF" };
        var big = new FontSpec { Family = spec.PtFont.Family, Bold = true, Size = r.Height * 0.46, Color = small.Color };
        var brush = new SolidColorBrush(ink);
        double w = r.Width - tip - 4;
        if (band.Kind is "solve" or "solved")
        {
            DrawCaseIcon(dc, new Rect(r.X + 2, r.Y, w, r.Height), band.Kind == "solved", brush);
            return;
        }
        if (band.Kind == "station")
        {
            // Just the threshold, big: "12+".
            var num = FitText(level, NumeralFont(big), r.Height * 0.52, 8, w, brush);
            dc.DrawText(num, new Point(r.X + 2 + (w - num.Width) / 2, r.Y + (r.Height - num.Height) / 2));
            return;
        }
        var top = FitText("LEVEL", small, small.Size, 6, w, brush);
        var range = FitText(level, NumeralFont(big), big.Size, 8, w, brush);
        double total = top.Height * 0.85 + range.Height * 0.9;
        double y = r.Y + (r.Height - total) / 2;
        dc.DrawText(top, new Point(r.X + 2 + (w - top.Width) / 2, y));
        dc.DrawText(range, new Point(r.X + 2 + (w - range.Width) / 2, y + top.Height * 0.85));
    }

    // Keyed by the frame image: a split half's enlarged-font copy of a template shares its frame, and its answer.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ImageSource, System.Runtime.CompilerServices.StrongBox<Rect?>> _ornaments = new();

    /// <summary>A frame ornament on the text box's bottom edge, in canvas coordinates — the medallion the Partial
    /// Cardboard Chemist frames (and many imported ones) set into the middle of the box's bottom border — or null.
    /// Found in the frame image: rows up from the box's bottom, across its middle half, where a narrow run of pixels
    /// differs from the box's own colour. Full-width rows (the border line) don't count, nor does anything under
    /// 12px tall or over 40% of the box (texture, not an ornament). Worked out once per frame.</summary>
    internal static Rect? BottomOrnament(Template template, TemplateSpec spec)
    {
        if (spec.FullArt) return null;
        if (template.FrameImage is not BitmapSource frame) return null;
        return _ornaments.GetValue(frame, f => new System.Runtime.CompilerServices.StrongBox<Rect?>(FindBottomOrnament((BitmapSource)f, spec))).Value;
    }

    /// <summary>Records the medallion of a frame image made from another (a token version), where the search
    /// can't be trusted: its box is shorter than the medallion's limits allow, though the medallion is unchanged.</summary>
    internal static void KnowOrnament(ImageSource frame, Rect? ornament)
        => _ornaments.AddOrUpdate(frame, new System.Runtime.CompilerServices.StrongBox<Rect?>(ornament));

    private static Rect? FindBottomOrnament(BitmapSource frame, TemplateSpec spec)
    {
        try
        {
            var tb = ToRect(spec.EffectiveTextBox);
            if (tb.Width < 40 || tb.Height < 40 || spec.CanvasWidth <= 0 || spec.CanvasHeight <= 0) return null;
            var src = frame.Format == PixelFormats.Bgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int fw = src.PixelWidth, fh = src.PixelHeight;
            double sx = (double)fw / spec.CanvasWidth, sy = (double)fh / spec.CanvasHeight;
            var px = new byte[fw * fh * 4];
            src.CopyPixels(px, fw * 4, 0);
            int x0 = (int)((tb.X + tb.Width * 0.25) * sx), x1 = (int)((tb.Right - tb.Width * 0.25) * sx);
            x0 = Math.Clamp(x0, 0, fw - 1); x1 = Math.Clamp(x1, x0 + 1, fw);

            (int b, int g, int r, int a) At(int x, int y)
            {
                int i = (Math.Clamp(y, 0, fh - 1) * fw + Math.Clamp(x, 0, fw - 1)) * 4;
                return (px[i], px[i + 1], px[i + 2], px[i + 3]);
            }
            // The box's own colour: the median of its middle, where frames draw no ornament.
            var sample = new List<(int b, int g, int r, int a)>();
            for (int y = (int)((tb.Y + tb.Height * 0.35) * sy); y < (tb.Y + tb.Height * 0.5) * sy; y += 3)
                for (int x = x0; x < x1; x += 3) sample.Add(At(x, y));
            if (sample.Count == 0) return null;
            int Med(Func<(int b, int g, int r, int a), int> f) { var v = sample.Select(f).OrderBy(n => n).ToList(); return v[v.Count / 2]; }
            var refc = (b: Med(c => c.b), g: Med(c => c.g), r: Med(c => c.r), a: Med(c => c.a));

            // Per canvas row (bottom up): the share of the middle half that differs, and where.
            int bottom = (int)Math.Floor(tb.Bottom) - 1, limit = (int)(tb.Bottom - tb.Height * 0.4);
            int top = -1, gap = 0, minX = int.MaxValue, maxX = int.MinValue;
            bool started = false;
            for (int cy = bottom; cy >= limit; cy--)
            {
                int y = (int)(cy * sy), hits = 0, n = 0, rowMin = int.MaxValue, rowMax = int.MinValue;
                for (int x = x0; x < x1; x += 2)
                {
                    var c = At(x, y);
                    n++;
                    int d = Math.Abs(c.b - refc.b) + Math.Abs(c.g - refc.g) + Math.Abs(c.r - refc.r) + Math.Abs(c.a - refc.a);
                    if (d > 90) { hits++; rowMin = Math.Min(rowMin, x); rowMax = Math.Max(rowMax, x); }
                }
                double frac = n == 0 ? 0 : (double)hits / n;
                bool ornamentRow = frac > 0.06 && frac <= 0.8;
                if (!started)
                {
                    if (ornamentRow) { started = true; top = cy; minX = rowMin; maxX = rowMax; }
                    else if (bottom - cy > 20) return null;   // nothing set into the bottom edge
                    continue;
                }
                if (ornamentRow) { top = cy; gap = 0; minX = Math.Min(minX, rowMin); maxX = Math.Max(maxX, rowMax); }
                else if (++gap > 4) break;
            }
            if (!started) return null;
            double height = tb.Bottom - top;
            if (height < 12 || height > tb.Height * 0.38) return null;
            double left = minX / sx - 6, right = maxX / sx + 6;
            return new Rect(left, top, Math.Max(1, right - left), height);
        }
        catch { return null; }
    }

    /// <summary>A Case badge's icon: a magnifying glass for To solve, a check mark for Solved.</summary>
    private static void DrawCaseIcon(DrawingContext dc, Rect r, bool solved, Brush ink)
    {
        double s = Math.Min(r.Width, r.Height) * 0.62;
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        var pen = new Pen(ink, Math.Max(2, s * 0.13)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        if (solved)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(c.X - s * 0.42, c.Y + s * 0.02), false, false);
                ctx.LineTo(new Point(c.X - s * 0.12, c.Y + s * 0.32), true, true);
                ctx.LineTo(new Point(c.X + s * 0.44, c.Y - s * 0.34), true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }
        else
        {
            double rad = s * 0.28;
            var lens = new Point(c.X - s * 0.1, c.Y - s * 0.1);
            dc.DrawEllipse(null, pen, lens, rad, rad);
            double k = rad / Math.Sqrt(2);
            dc.DrawLine(pen, new Point(lens.X + k, lens.Y + k), new Point(c.X + s * 0.4, c.Y + s * 0.4));
        }
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

    // --- adventure (storybook: the spell on the left page, the creature on the right) ---------------

    /// <summary>Where an adventure card's text box parts go, the modern storybook way (<i>Bonecrusher Giant</i>
    /// from Wilds of Eldraine on): the text box opens like a book, the adventure spell on the left page — a
    /// name bar with its cost, its type line, then its rules — and the creature's rules and flavor on the right
    /// page, around the P/T box. Both pages share one rules size, shrunk until both fit.</summary>
    internal (Rect Left, Rect NameBar, Rect TypeRow, Rect LeftText, Rect Right, double Size) AdventureLayout(
        CardModel card, TemplateSpec spec, Rect? avoid)
    {
        var tb = ToRect(spec.EffectiveTextBox);
        double inset = 3, pad = 10, gap = 4;
        var box = new Rect(tb.X + inset, tb.Y + inset, Math.Max(0, tb.Width - 2 * inset), Math.Max(0, tb.Height - 2 * inset));
        double leftW = Math.Max(0, box.Width * 0.5 - gap / 2);
        var left = new Rect(box.X, box.Y, leftW, box.Height);
        var right = new Rect(left.Right + gap, box.Y, Math.Max(0, box.Right - left.Right - gap), box.Height);

        double size = spec.RulesFont.Size, nameH = 0, typeH = 0;
        double minSize = Math.Min(9, spec.RulesFont.Size);
        for (size = Math.Max(minSize, spec.RulesFont.Size); size >= minSize; size -= 1)
        {
            double scale = size / spec.RulesFont.Size;
            nameH = AdventureNameHeight(size);
            typeH = size * 1.5;
            double sym = spec.RulesSymbolSize * scale;
            var lcol = new Rect(left.X + pad, left.Y + nameH + typeH + pad * 0.6, Math.Max(20, left.Width - 2 * pad), 100000);
            double lh = (card.AdventureText ?? "").Trim().Length == 0 ? 0
                : LayoutContent(card.AdventureText!, "", lcol, spec.RulesFont, size, spec.FlavorFont, spec.FlavorFont.Size * scale, sym, null).Height;
            var rcol = new Rect(right.X + pad, right.Y + pad, Math.Max(20, right.Width - 2 * pad), 100000);
            double rh = card.RulesText.Trim().Length + card.FlavorText.Trim().Length == 0 ? 0
                : LayoutContent(card.RulesText, card.FlavorText, rcol, spec.RulesFont, size, spec.FlavorFont, spec.FlavorFont.Size * scale, sym, avoid).Height;
            if (nameH + typeH + pad * 0.6 + lh + pad <= box.Height && rh + 2 * pad <= box.Height) break;
        }
        size = Math.Max(size, minSize);
        nameH = AdventureNameHeight(size);
        typeH = size * 1.5;

        var nameBar = new Rect(left.X, left.Y, left.Width, Math.Min(nameH, left.Height));
        var typeRow = new Rect(left.X + pad, nameBar.Bottom, Math.Max(0, left.Width - 2 * pad), Math.Min(typeH, Math.Max(0, left.Bottom - nameBar.Bottom)));
        var leftText = new Rect(left.X + pad, typeRow.Bottom + pad * 0.6, Math.Max(20, left.Width - 2 * pad),
            Math.Max(0, left.Bottom - typeRow.Bottom - pad * 1.6));
        var rightText = new Rect(right.X + pad, right.Y + pad, Math.Max(20, right.Width - 2 * pad), Math.Max(0, right.Height - 2 * pad));
        return (left, nameBar, typeRow, leftText, rightText, size);
    }

    private static double AdventureNameHeight(double size) => size * 2.0;

    private void DrawAdventure(DrawingContext dc, CardModel card, TemplateSpec spec)
    {
        // The creature's rules always wrap around its P/T box (the right page ends right above it).
        Rect? avoid = card.HasDefense ? DefenseRect(spec) : card.HasPowerToughness ? PtRect(spec) : null;
        var (left, nameBar, typeRow, leftText, rightText, size) = AdventureLayout(card, spec, avoid);
        double scale = size / spec.RulesFont.Size;
        double sym = spec.RulesSymbolSize * scale;

        // Left page: a light wash of the spell's colour, its name bar a stronger band of the same colour.
        var tint = PrototypeTint(card.AdventureCost);
        var edge = DarkenC(tint, 0.35);
        double r = Math.Min(10, nameBar.Height * 0.3);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(60, tint.R, tint.G, tint.B)),
            new Pen(new SolidColorBrush(Color.FromArgb(150, edge.R, edge.G, edge.B)), 1.4), left, r, r);
        var light = LightenC(tint, 0.12);
        var fill = new LinearGradientBrush(Color.FromArgb(240, light.R, light.G, light.B),
            Color.FromArgb(240, tint.R, tint.G, tint.B), new Point(0, 0), new Point(0, 1));
        fill.Freeze();
        dc.DrawRoundedRectangle(fill, new Pen(new SolidColorBrush(edge), 1.8), nameBar, r, r);

        // Name and cost on the bar — always dark ink, as the bar is always a light tint.
        var ink = new SolidColorBrush(Color.FromRgb(0x1C, 0x1A, 0x17));
        var costTokens = ManaText.Tokenize(ManaText.NormalizeCost(card.AdventureCost)).Where(t => t.IsSymbol).ToList();
        double nameSym = Math.Min(sym, nameBar.Height * 0.72), symGap = nameSym * 0.08;
        double manaW = costTokens.Count == 0 ? 0 : costTokens.Count * nameSym + (costTokens.Count - 1) * symGap;
        double padX = 10;
        var nameFt = FitText(card.AdventureName,
            new FontSpec { Family = spec.TitleFont.Family, Bold = true, Color = "#1C1A17" },
            size * 1.1, 8, Math.Max(10, nameBar.Width - 2 * padX - manaW - 6), ink);
        dc.DrawText(nameFt, new Point(nameBar.X + padX, nameBar.Y + (nameBar.Height - nameFt.Height) / 2));
        double cx = nameBar.Right - padX - manaW, symY = nameBar.Y + (nameBar.Height - nameSym) / 2;
        foreach (var t in costTokens)
        {
            if (_symbols.GetSymbol(t.Value) is { } s) dc.DrawImage(s, new Rect(cx, symY, nameSym, nameSym));
            cx += nameSym + symGap;
        }

        // The spell's type line, in the page's ink.
        var typeFont = new FontSpec
        {
            Family = spec.TypeFont.Family, Italic = true, Color = spec.RulesFont.Color,
            Shadow = spec.RulesFont.Shadow, ShadowColor = spec.RulesFont.ShadowColor,
        };
        var typeFt = FitText(card.AdventureType, typeFont, size * 0.95, 8, Math.Max(10, typeRow.Width),
            new SolidColorBrush(TemplateSpec.ParseColor(spec.RulesFont.Color)));
        DrawGlyphRun(dc, typeFt, new Point(typeRow.X, typeRow.Y + (typeRow.Height - typeFt.Height) / 2), typeFont);

        var rules = SizedFont(spec.RulesFont, size);
        var flavor = SizedFont(spec.FlavorFont, spec.FlavorFont.Size * scale);
        DrawLaidOut(dc, LayoutContent(card.AdventureText ?? "", "", leftText, rules, size, flavor, flavor.Size, sym, null), rules, leftText);
        DrawLaidOut(dc, LayoutContent(card.RulesText, card.FlavorText, rightText, rules, size, flavor, flavor.Size, sym, avoid), rules, rightText);
    }

    private static FontSpec SizedFont(FontSpec f, double size) => new()
    {
        Family = f.Family, Size = size, Bold = f.Bold, Italic = f.Italic, Align = f.Align,
        Color = f.Color, Shadow = f.Shadow, ShadowColor = f.ShadowColor,
    };

    /// <summary>Draws text laid out by <see cref="LayoutContent"/> (its glyphs, symbols and flavor divider).</summary>
    private void DrawLaidOut(DrawingContext dc, TextLayout layout, FontSpec rulesFont, Rect box)
    {
        foreach (var p in layout.Items)
        {
            if (p.Text != null) DrawGlyphRun(dc, p.Text, new Point(p.X, p.Y), rulesFont);
            else if (p.Sym != null) dc.DrawImage(p.Sym, new Rect(p.X, p.Y, p.SymSize, p.SymSize));
        }
        var dividerPen = new Pen(new SolidColorBrush(Color.FromArgb(70, 40, 30, 10)), 1.2);
        foreach (var d in layout.Dividers)
            dc.DrawLine(dividerPen, new Point(box.X + box.Width * 0.12, d), new Point(box.Right - box.Width * 0.12, d));
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
