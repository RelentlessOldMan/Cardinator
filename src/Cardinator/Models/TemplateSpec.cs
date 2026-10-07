using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Cardinator.Models;

/// <summary>A rectangle in the template's logical coordinate space (default 750x1050).</summary>
public sealed class Region
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }

    [JsonIgnore] public double Right => X + W;
    [JsonIgnore] public double Bottom => Y + H;
}

/// <summary>Font + alignment settings for a text region.</summary>
public sealed class FontSpec
{
    public string Family { get; set; } = "Georgia";
    public double Size { get; set; } = 26;
    public string Color { get; set; } = "#111111";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    /// <summary>left | center | right</summary>
    public string Align { get; set; } = "left";

    /// <summary>Draw a contrasting outline behind the text so it stays legible directly on art.</summary>
    public bool Shadow { get; set; }
    public string ShadowColor { get; set; } = "#000000";
}

/// <summary>Colors used when generating a frame.png procedurally from the spec.</summary>
public sealed class FrameColors
{
    public string Border { get; set; } = "#000000";     // outer black edge
    public string Frame { get; set; } = "#B8912B";      // main frame color
    public string Frame2 { get; set; } = "#EBD489";     // frame highlight (gradient)
    public string Panel { get; set; } = "#EDE7D3";      // title/type/text box fill
    public string PanelBorder { get; set; } = "#6E5A1E";
}

/// <summary>
/// Full description of a card template: canvas size, the rectangles for each region,
/// the fonts for text, and the colors used to generate the frame image.
/// Loaded from template.json. The frame.png is derived from this (or supplied by the user).
/// </summary>
public sealed class TemplateSpec
{
    public string Name { get; set; } = "Untitled";
    public int CanvasWidth { get; set; } = 750;
    public int CanvasHeight { get; set; } = 1050;

    /// <summary>
    /// "Full art" style: the art fills the whole card and the text sits directly on it (with a
    /// legibility scrim + text shadow) instead of on opaque frame panels. The generated frame is
    /// just a thin border. Fonts default to light with shadow — tune size/color per region.
    /// </summary>
    public bool FullArt { get; set; }

    /// <summary>True when frame.png is a user-supplied image (imported), not generated from this spec. The
    /// template loader then never regenerates/overwrites it from the spec — it only creates a procedural
    /// placeholder if the file is missing entirely. Without this, the frame-cache invalidation would
    /// replace an imported frame with a generated one on the next load.</summary>
    public bool CustomFrame { get; set; }

    /// <summary>For a custom frame: the RAW source image filename (in the template folder). When set, the
    /// app composites frame.png live from it using the fit knobs below (zoom/offset/border/corner + the
    /// art-window punch), so the framing stays editable instead of being baked into a flat image.</summary>
    public string FrameSrc { get; set; } = "";

    /// <summary>Custom-frame fit: zoom (1 = cover-fit) and pan offset (logical px) of the source image.</summary>
    public double FrameZoom { get; set; } = 1.0;
    public double FrameOffsetX { get; set; }
    public double FrameOffsetY { get; set; }

    /// <summary>Custom-frame baked border: thickness (logical px, 0 = none) and color, drawn as an even
    /// ring from the rounded card edge inward.</summary>
    public double FrameBorder { get; set; }
    public string FrameBorderColor { get; set; } = "#000000";

    /// <summary>Translucent mask (custom frames): a region painted in the source image with
    /// <see cref="TranslucentKey"/> is converted to a see-through area where the art shows through at
    /// <see cref="TranslucentAmount"/> (1 = fully clear, 0 = opaque), over <see cref="TranslucentBacking"/>.
    /// This is baked into the frame art (unlike the fully-transparent art window), so pick a key color the
    /// art never uses (default magenta).</summary>
    public bool TranslucentEnabled { get; set; }
    public string TranslucentKey { get; set; } = "#FF00FF";
    public double TranslucentAmount { get; set; } = 0.5;
    public string TranslucentBacking { get; set; } = "#000000";

    /// <summary>Frame design: "classic" (default), "clean" (flat/modern), "ornate" (heavy/decorative),
    /// "faded" (frame melts into art), "borderless" (floating panels), "overlay" (cinematic band over
    /// art) or "wave" (full frame + colorful scalloped wave crown across the top).</summary>
    public string FrameStyle { get; set; } = "classic";

    /// <summary>Draw ornamental filigree (corner scrolls, art-window curls, top ornament).</summary>
    public bool Embellishments { get; set; } = true;

    /// <summary>Card-edge corner radius (0 = square corners). Kept larger than the border thickness so
    /// the black border's inner corner stays rounded with even thickness all the way around.</summary>
    public double CornerRadius { get; set; } = 48;

    /// <summary>Thickness (logical px) of the black outer card border. Measured off real cards at ~3.7%
    /// of card width (~28px at this scale); raise for a chunkier edge, lower for a slimmer one (0 = none).</summary>
    public double BorderThickness { get; set; } = 28;

    /// <summary>Corner radius of the art window and the text panels.</summary>
    public double PanelRadius { get; set; } = 10;

    /// <summary>Draw an inner bevel inside the power/toughness box (a beveled inset rim) for extra depth.</summary>
    public bool PtBevel { get; set; } = true;

    /// <summary>Where the collector/artist footer text goes: "frame" (on the colored card, above the
    /// border), "border" (on the black bottom border — the border size is unchanged), or "none".</summary>
    public string FooterPlacement { get; set; } = "frame";

    /// <summary>When true, the "frame" footer is a single joined line (collector + artist) instead of
    /// two stacked lines. No effect on "border"/"none" placement.</summary>
    public bool FooterSingleLine { get; set; } = false;

    /// <summary>Draw the ornamental crown along the title of Legendary cards.</summary>
    public bool LegendaryCrown { get; set; } = true;

    /// <summary>Crown design when LegendaryCrown is on: "leaves" (leafy scroll), "arc" (simple gold arc + gem), or "none".</summary>
    public string CrownStyle { get; set; } = "leaves";

    /// <summary>A decorative "topper" drawn across the top of the card, over any frame style:
    /// "none", "wave" (scalloped ribbon), "regal" (notched crown), or "leaves" (leafy scroll).</summary>
    public string TopEmblem { get; set; } = "none";

    /// <summary>Draw an ornate "royal" sub-border ring just inside the black card border (a decorative
    /// inner frame — like the wave crown, but wrapping the whole card). When on, ALL panels + the art
    /// window are inset inward by <see cref="SubBorderThickness"/> to make room for the ring. Off by
    /// default so it's opt-in per template (a gold royal band would clash on flat/modern styles); flip
    /// it on for royal/ornate templates. See <see cref="WithSubBorderApplied"/>.</summary>
    public bool RoyalFrame { get; set; } = false;

    /// <summary>Thickness (logical px) of the royal sub-border ring, which is also the amount every region
    /// is inset inward to make room for it (see <see cref="RoyalFrame"/>).</summary>
    public double SubBorderThickness { get; set; } = 18;

    // --- composable frame knobs (used by frameStyle "modern"/"composable"; mix & match freely) ------
    /// <summary>Join the art + nameplates with a two-tone "hollow" pinline (the M15 connected look).</summary>
    public bool ConnectedPanels { get; set; } = true;
    /// <summary>Fill the card interior with a textured colored background the panels sit on.</summary>
    public bool PanelBackground { get; set; } = true;
    /// <summary>Taper the colored background inward at the bottom of the card. Master on/off; the shape is
    /// chosen by <see cref="TaperStyle"/>.</summary>
    public bool BottomTaper { get; set; } = true;

    /// <summary>How the bottom taper is shaped when <see cref="BottomTaper"/> is on:
    /// "partial" (curves the background in to the foot of the description panel and stops — no dark strip
    /// showing below the panel) or "full" (continues all the way to the very bottom edge of the card).</summary>
    public string TaperStyle { get; set; } = "partial";
    /// <summary>Melt the art-window edges softly into the frame (a faded look), instead of a hard keyline.</summary>
    public bool FadedEdges { get; set; } = false;

    /// <summary>Background texture for styles that use one (ornate, modern): "speckle" or "none".</summary>
    public string Texture { get; set; } = "speckle";

    /// <summary>Texture density multiplier (0 = off, 1 = default). Lets you dial the background grain up or down.</summary>
    public double TextureStrength { get; set; } = 1.0;

    public FrameColors Colors { get; set; } = new();

    /// <summary>"" for a normal card layout, or "flip" for a Kamigawa flip card: <see cref="TitleBar"/>,
    /// <see cref="TextBox"/>, <see cref="TypeBar"/> and <see cref="PtBox"/> describe the TOP half (name, rules,
    /// type line with P/T at its end), the art window sits across the middle, and the frame image's bottom
    /// half is the top half mirrored — the renderer draws the other half's text rotated 180° into it.
    /// Built-in frames derive this with <see cref="ToFlip"/>; a picture frame ships it as a "flip" variant.</summary>
    public string CardLayout { get; set; } = "";

    /// <summary>True for a flip-card layout (see <see cref="CardLayout"/>).</summary>
    [JsonIgnore]
    public bool IsFlipLayout => string.Equals((CardLayout ?? "").Trim(), "flip", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a token layout ("token": a tall art window, the type line low, a short text box or
    /// none). Built-in frames derive it with <see cref="ToToken"/>; a picture frame may ship a "token" variant.</summary>
    [JsonIgnore]
    public bool IsTokenLayout => string.Equals((CardLayout ?? "").Trim(), "token", System.StringComparison.OrdinalIgnoreCase);

    public Region TitleBar { get; set; } = new() { X = 48, Y = 56, W = 654, H = 60 };
    public Region ArtWindow { get; set; } = new() { X = 48, Y = 126, W = 654, H = 462 };
    public Region TypeBar { get; set; } = new() { X = 48, Y = 598, W = 654, H = 56 };
    public Region TextBox { get; set; } = new() { X = 48, Y = 662, W = 654, H = 296 };

    /// <summary>The text/rules box, extended downward to reclaim the footer's space when the footer isn't
    /// on the frame (footerPlacement "border" or "none"). Used for both the baked panel and the text.</summary>
    [JsonIgnore]
    public Region EffectiveTextBox
    {
        get
        {
            if (TextBox == null) return TextBox!;
            if (IsFlipLayout) return TextBox;   // the space below a flip half's rules belongs to its type line + the art
            if (TextBox.H < 1) return TextBox;  // no text box at all (a vanilla token): nothing to extend
            var fp = (FooterPlacement ?? "frame").Trim().ToLowerInvariant();
            if (fp == "frame") return TextBox;
            // Footer isn't on the frame — extend the box down, but only enough to leave the SAME margin
            // from the bottom border as the box has on its left side (so left/right/bottom margins match).
            double border = System.Math.Max(0, BorderThickness);
            double sideMargin = System.Math.Max(0, TextBox.X - border);
            double newBottom = CanvasHeight - border - sideMargin;
            if (newBottom <= TextBox.Bottom) return TextBox;
            return new Region { X = TextBox.X, Y = TextBox.Y, W = TextBox.W, H = newBottom - TextBox.Y };
        }
    }
    public Region PtBox { get; set; } = new() { X = 572, Y = 926, W = 126, H = 72 };
    public Region CreditBar { get; set; } = new() { X = 54, Y = 962, W = 460, H = 34 };

    /// <summary>Optional plate for the card's subtitle (drawn only when the card has a subtitle). When null,
    /// a sensible plate is derived just below the title bar.</summary>
    public Region? SubtitleBar { get; set; }

    public FontSpec TitleFont { get; set; } = new() { Family = "Georgia", Size = 34, Bold = true, Align = "left" };
    public FontSpec SubtitleFont { get; set; } = new() { Family = "Georgia", Size = 22, Italic = true, Align = "center", Color = "#ECE6D6" };
    public FontSpec TypeFont { get; set; } = new() { Family = "Georgia", Size = 25, Bold = true, Align = "left" };
    public FontSpec RulesFont { get; set; } = new() { Family = "Georgia", Size = 25, Align = "left" };
    public FontSpec FlavorFont { get; set; } = new() { Family = "Georgia", Size = 24, Italic = true, Align = "left", Color = "#333333" };
    public FontSpec PtFont { get; set; } = new() { Family = "Georgia", Size = 32, Bold = true, Align = "center" };
    public FontSpec CreditFont { get; set; } = new() { Family = "Segoe UI", Size = 13, Italic = true, Align = "left", Color = "#241F0C" };

    /// <summary>Diameter (logical px) of mana pips drawn in the title bar (~real-card size at this scale).</summary>
    public double ManaSymbolSize { get; set; } = 35;

    /// <summary>Height (logical px) of mana/tap symbols embedded inside rules text.</summary>
    public double RulesSymbolSize { get; set; } = 26;

    // Shared across all persisted types — see JsonCompat for the backward-compatibility policy. (Write
    // output is byte-identical to the previous options, so cached frame content-hashes don't churn.)
    private static JsonSerializerOptions JsonOpts => Cardinator.Services.JsonCompat.Options;

    public static TemplateSpec Load(string path)
    {
        try { return LoadFromJson(File.ReadAllText(path)); }
        catch (InvalidDataException) { throw new InvalidDataException($"Could not parse template: {path}"); }
    }

    /// <summary>Parses a template spec from a JSON string (normalizing degenerate values).</summary>
    public static TemplateSpec LoadFromJson(string json)
    {
        var spec = JsonSerializer.Deserialize<TemplateSpec>(json, JsonOpts)
               ?? throw new InvalidDataException("Could not parse template JSON.");
        spec.Normalize();
        return spec;
    }

    public void Save(string path)
        => Cardinator.Services.IoUtil.AtomicWriteText(path, JsonSerializer.Serialize(this, JsonOpts));

    /// <summary>A deep copy (via JSON round-trip), so an editor can tweak a copy and apply on demand.</summary>
    public TemplateSpec Clone()
    {
        var copy = JsonSerializer.Deserialize<TemplateSpec>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts)!;
        copy.Normalize();
        return copy;
    }

    /// <summary>True for a template laid out sideways (wider than tall) — Battles, Planes, Phenomena.</summary>
    [JsonIgnore]
    public bool IsLandscape => CanvasWidth > CanvasHeight;

    /// <summary>
    /// The same template laid out LANDSCAPE: canvas width and height swap, and every region is re-placed so
    /// the frame keeps its look (style, colors, fonts, margins) on a sideways card. That lets every portrait
    /// frame render a Battle or Plane without shipping — and asking the user to pick between — a landscape
    /// copy of each one.
    /// <para>How: the card gets wider (regions spanning the width stretch, right-anchored ones like the P/T
    /// box slide right) and shorter. The lost height comes out of the two flexible spans only — the art
    /// (or, for full-art frames, the open art between the title and the lower panels) and the text box —
    /// art first, but only until it would become a letterbox strip (wider than 3.2:1); the text box absorbs
    /// the rest, never losing more than 60%. That is naturally generous to full-art frames (a tall open art
    /// span to spare, so their short text box barely shrinks) and fair to classic ones. Fixed-height plates (title, type line, P/T, footer)
    /// keep their size and their distance from the nearest edge, so nothing gets squashed.</para>
    /// Returns this spec unchanged when it is already landscape.
    /// </summary>
    public TemplateSpec ToLandscape()
    {
        if (IsLandscape) return this;
        var s = Clone();
        double w0 = CanvasWidth, h0 = CanvasHeight;
        double w1 = h0, h1 = w0;
        double grow = w1 - w0, deficit = h0 - h1;
        s.CanvasWidth = (int)w1;
        s.CanvasHeight = (int)h1;

        bool fullArt = ArtWindow.X <= 1 && ArtWindow.Y <= 1 && ArtWindow.Right >= w0 - 1 && ArtWindow.Bottom >= h0 - 1;

        // The two spans that may shrink: the art (or the open art below the title on a full-art frame) and
        // the text box.
        double a0, a1;
        if (fullArt)
        {
            a0 = TitleBar.Bottom;
            a1 = new[] { TypeBar.Y, TextBox.Y }.Where(y => y > a0).DefaultIfEmpty(TextBox.Y).Min();
        }
        else { a0 = ArtWindow.Y; a1 = ArtWindow.Bottom; }
        double t0 = TextBox.Y, t1 = TextBox.Bottom;
        double aLen = System.Math.Max(1, a1 - a0), tLen = System.Math.Max(1, t1 - t0);

        // Art shrinks first, down to a 3.2:1 strip at the landscape width; the text box takes what's left
        // (at most 60% of it). If both limits bind, the art gives the remainder rather than the words.
        const double maxArtAspect = 3.2;
        double artWidth = fullArt ? w1 : (ArtWindow.X <= w0 * 0.2 && ArtWindow.Right >= w0 * 0.8 ? ArtWindow.W + grow : ArtWindow.W);
        double aMax = System.Math.Max(0, aLen - artWidth / maxArtAspect);
        double aCut = System.Math.Min(deficit, aMax);
        double tCut = deficit - aCut;
        if (tCut > tLen * 0.60) { tCut = tLen * 0.60; aCut = deficit - tCut; }

        static double Seg(double y, double from, double to, double cut) =>
            y <= from ? 0 : y >= to ? cut : cut * (y - from) / (to - from);
        double MapY(double y) => y - Seg(y, a0, a1, aCut) - Seg(y, t0, t1, tCut);

        Region MapX(Region r, Region into)
        {
            bool wide = r.X <= w0 * 0.2 && r.Right >= w0 * 0.8;
            if (wide) { into.X = r.X; into.W = r.W + grow; }
            else if (r.X >= w0 * 0.5) { into.X = r.X + grow; into.W = r.W; }
            else { into.X = r.X; into.W = r.W; }
            return into;
        }

        // Flexible spans: both edges follow the compressed axis, so they lose height.
        Region Flex(Region r)
        {
            var top = MapY(r.Y);
            return MapX(r, new Region { Y = top, H = System.Math.Max(1, MapY(r.Bottom) - top) });
        }

        // Fixed plates keep their height. One that reaches the bottom of the text box (the nested P/T box,
        // the footer) stays anchored to the BOTTOM edge; everything else keeps its offset from the top.
        Region Rigid(Region r)
        {
            double top = r.Bottom >= t1 ? MapY(r.Bottom) - r.H : MapY(r.Y);
            return MapX(r, new Region { Y = top, H = r.H });
        }

        s.TitleBar = Rigid(TitleBar);
        s.TypeBar = Rigid(TypeBar);
        s.PtBox = Rigid(PtBox);
        s.CreditBar = Rigid(CreditBar);
        s.TextBox = Flex(TextBox);
        s.ArtWindow = fullArt ? new Region { X = 0, Y = 0, W = w1, H = h1 } : Flex(ArtWindow);
        if (SubtitleBar != null) s.SubtitleBar = Rigid(SubtitleBar);
        return s;
    }

    /// <summary>
    /// The same template laid out as a FLIP card (Kamigawa): each half reads name bar → rules → type line
    /// (with the P/T box at the end of the type line, as on the real cards — not in the rules box), the art
    /// spans the middle third, and everything is symmetric about the card's centre so the frame's bottom half
    /// can be the top half mirrored. Plates keep their size, colours, fonts and side margins; the credits
    /// move onto the bottom border (upright under the upside-down half). Returns this spec unchanged when it
    /// is already a flip layout.
    /// </summary>
    public TemplateSpec ToFlip()
    {
        if (IsFlipLayout) return this;
        var s = Clone();
        s.CardLayout = "flip";
        double h = CanvasHeight, mid = h / 2.0;
        bool fullArt = ArtWindow.X <= 1 && ArtWindow.Y <= 1 && ArtWindow.Right >= CanvasWidth - 1 && ArtWindow.Bottom >= h - 1;

        double gap = System.Math.Clamp(TextBox.Y - TypeBar.Bottom, 4, 16);          // plate-to-plate spacing
        double artGap = fullArt ? gap : System.Math.Clamp(ArtWindow.Y - TitleBar.Bottom, 4, 20);
        double typeH = TypeBar.H;
        double textTop = TitleBar.Bottom + gap;

        // Art ≈ a third of the card (real flip cards: ~31%); the rules box gets what's left of each half,
        // but never less than ~3 lines — then the art gives way instead.
        double minText = RulesFont.Size * 3.6;
        double typeBottom = mid - h * 0.17 - artGap;
        double textH = typeBottom - typeH - gap - textTop;
        if (textH < minText) { typeBottom += minText - textH; textH = minText; }

        s.TextBox = new Region { X = TextBox.X, Y = textTop, W = TextBox.W, H = textH };
        s.TypeBar = new Region { X = TypeBar.X, Y = typeBottom - typeH, W = TypeBar.W, H = typeH };
        double artTop = typeBottom + artGap;
        s.ArtWindow = fullArt ? new Region { X = 0, Y = 0, W = CanvasWidth, H = h }
                              : new Region { X = ArtWindow.X, Y = artTop, W = ArtWindow.W, H = System.Math.Max(1, h - 2 * artTop) };

        // P/T rides the right end of the type line, a little taller than it (Erayo, Bushi Tenderfoot…).
        double ptH = System.Math.Min(PtBox.H, typeH + 16);
        s.PtBox = new Region { X = TypeBar.Right - PtBox.W, Y = s.TypeBar.Y + typeH / 2 - ptH / 2, W = PtBox.W, H = ptH };

        s.FooterPlacement = "border";   // the bottom of the frame is the other half's name bar
        s.SubtitleBar = null;
        s.LegendaryCrown = false;       // a crown above each name would collide with the mirrored half
        return s;
    }

    /// <summary>
    /// The same template laid out as a TOKEN: the title, the P/T box and the credits stay put, and the art
    /// grows down the card. With <paramref name="textLines"/> &gt; 0 the type line moves down and the text box is
    /// just tall enough for that many lines of rules at full size (at least 3, at most the normal box), as on a
    /// real token, whose box fits its text; with 0 (a vanilla
    /// token, e.g. a 1/1 Soldier) there's no text box at all: the type line sits just above the P/T box and the
    /// art runs down to it. A full-art frame's art already covers the card, so only the plates move. Returns
    /// this spec unchanged when it is already a token or a flip layout.
    /// </summary>
    /// <param name="reserve">Extra height the box needs beyond its lines (a medallion set into its bottom edge,
    /// which the text stops above).</param>
    public TemplateSpec ToToken(int textLines, double reserve = 0)
    {
        if (IsTokenLayout || IsFlipLayout) return this;
        var s = Clone();
        s.CardLayout = "token";
        bool fullArt = ArtWindow.X <= 1 && ArtWindow.Y <= 1 && ArtWindow.Right >= CanvasWidth - 1 && ArtWindow.Bottom >= CanvasHeight - 1;

        double shift;
        if (textLines > 0)
        {
            const double pad = 18;   // the renderer's text inset, top and bottom
            double h = System.Math.Min(TextBox.H, 2 * pad + System.Math.Max(3, textLines) * RulesFont.Size * 1.22 + System.Math.Max(0, reserve));
            shift = TextBox.H - h;
            s.TextBox = new Region { X = TextBox.X, Y = TextBox.Y + shift, W = TextBox.W, H = h };
        }
        else
        {
            // The type line ends just inside the P/T box's top (the box overlaps the frame below it, like a
            // real token's), never below where the text box ended.
            double typeBottom = System.Math.Min(TextBox.Bottom, PtBox.Y + 4);
            shift = System.Math.Max(0, typeBottom - TypeBar.Bottom);
            s.TextBox = new Region { X = TextBox.X, Y = TypeBar.Bottom + shift, W = TextBox.W, H = 0 };
        }
        s.TypeBar = new Region { X = TypeBar.X, Y = TypeBar.Y + shift, W = TypeBar.W, H = TypeBar.H };
        if (!fullArt) s.ArtWindow = new Region { X = ArtWindow.X, Y = ArtWindow.Y, W = ArtWindow.W, H = ArtWindow.H + shift };
        return s;
    }

    /// <summary>Bump this when FrameGenerator's drawing changes in a way that should invalidate every
    /// cached frame.png (so old baked frames are regenerated even if the spec text is unchanged).</summary>
    public const int RenderFormatVersion = 1;

    /// <summary>A stable content hash of this spec (plus the render-format version). The template loader
    /// stores it beside frame.png and regenerates the frame whenever it no longer matches — so a cached
    /// frame baked from a different/older spec can never be composited with mismatched text regions.</summary>
    public string ContentHash()
    {
        var json = RenderFormatVersion + "|" + JsonSerializer.Serialize(this, JsonOpts);
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json));
        return System.Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Returns a copy with every region (title / art / type / text / P·T / credit) inset inward to leave
    /// room for the royal sub-border ring, or this spec unchanged when <see cref="RoyalFrame"/> is off.
    /// Both the frame generator (which bakes frame.png) and the card renderer (which draws the text, P/T
    /// and footer) call this so the baked frame and the drawn content stay aligned. The whole interior is
    /// scaled toward the card center so the relative layout is preserved. Skipped for full-art / full-bleed
    /// layouts (there are no interior panels to make room for, and shrinking the window would break
    /// full-bleed detection).
    /// </summary>
    public TemplateSpec WithSubBorderApplied()
    {
        if (!RoyalFrame || SubBorderThickness <= 0) return this;
        if (FullArt) return this;
        // Full-bleed window (art fills the card): no interior to inset.
        if (ArtWindow != null && ArtWindow.W >= CanvasWidth * 0.92 && ArtWindow.H >= CanvasHeight * 0.92) return this;

        double b = System.Math.Max(0, BorderThickness);
        double d = SubBorderThickness;
        double interiorW = CanvasWidth - 2 * b;
        double interiorH = CanvasHeight - 2 * b;
        if (interiorW <= 4 * d || interiorH <= 4 * d) return this;

        double sx = (interiorW - 2 * d) / interiorW;
        double sy = (interiorH - 2 * d) / interiorH;
        double cx = CanvasWidth / 2.0, cy = CanvasHeight / 2.0;

        Region Map(Region? r) => r == null ? null! : new Region
        {
            X = cx + (r.X - cx) * sx,
            Y = cy + (r.Y - cy) * sy,
            W = r.W * sx,
            H = r.H * sy,
        };

        var copy = Clone();
        copy.RoyalFrame = false;   // the copy is already inset — don't inset again
        copy.TitleBar = Map(TitleBar);
        copy.ArtWindow = Map(ArtWindow);
        copy.TypeBar = Map(TypeBar);
        copy.TextBox = Map(TextBox);
        copy.PtBox = Map(PtBox);
        copy.CreditBar = Map(CreditBar);
        if (SubtitleBar != null) copy.SubtitleBar = Map(SubtitleBar);
        return copy;
    }

    /// <summary>Replaces null/degenerate values (from a hand-edited or partial JSON) with defaults.</summary>
    public void Normalize()
    {
        var d = new TemplateSpec();
        Name ??= d.Name;
        CardLayout ??= "";
        if (CanvasWidth <= 0) CanvasWidth = d.CanvasWidth;
        if (CanvasHeight <= 0) CanvasHeight = d.CanvasHeight;
        Colors ??= d.Colors;
        TitleBar ??= d.TitleBar;
        ArtWindow ??= d.ArtWindow;
        TypeBar ??= d.TypeBar;
        TextBox ??= d.TextBox;
        PtBox ??= d.PtBox;
        CreditBar ??= d.CreditBar;
        TitleFont ??= d.TitleFont;
        SubtitleFont ??= d.SubtitleFont;
        TypeFont ??= d.TypeFont;
        RulesFont ??= d.RulesFont;
        FlavorFont ??= d.FlavorFont;
        PtFont ??= d.PtFont;
        CreditFont ??= d.CreditFont;
        if (ManaSymbolSize <= 0) ManaSymbolSize = d.ManaSymbolSize;
        if (RulesSymbolSize <= 0) RulesSymbolSize = d.RulesSymbolSize;
        if (CornerRadius < 0) CornerRadius = d.CornerRadius;
        if (PanelRadius < 0) PanelRadius = d.PanelRadius;
        if (BorderThickness < 0) BorderThickness = d.BorderThickness;
        if (string.IsNullOrWhiteSpace(FrameStyle)) FrameStyle = d.FrameStyle;
        if (string.IsNullOrWhiteSpace(CrownStyle)) CrownStyle = d.CrownStyle;
        if (string.IsNullOrWhiteSpace(FooterPlacement)) FooterPlacement = d.FooterPlacement;
        if (string.IsNullOrWhiteSpace(TopEmblem)) TopEmblem = d.TopEmblem;
        if (string.IsNullOrWhiteSpace(TaperStyle)) TaperStyle = d.TaperStyle;
        if (string.IsNullOrWhiteSpace(Texture)) Texture = d.Texture;
        if (TextureStrength < 0) TextureStrength = d.TextureStrength;
        if (SubBorderThickness < 0) SubBorderThickness = d.SubBorderThickness;
        if (string.IsNullOrWhiteSpace(TranslucentKey)) TranslucentKey = d.TranslucentKey;
        if (string.IsNullOrWhiteSpace(TranslucentBacking)) TranslucentBacking = d.TranslucentBacking;
        TranslucentAmount = Math.Clamp(TranslucentAmount, 0, 1);
    }

    /// <summary>Parses a hex/named color, falling back to black if the string is invalid.</summary>
    public static Color ParseColor(string? hex) => ParseColor(hex, System.Windows.Media.Colors.Black);

    public static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex.Trim())!; }
        catch { return fallback; }
    }
}
