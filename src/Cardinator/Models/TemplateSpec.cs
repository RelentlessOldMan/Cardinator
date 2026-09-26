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

    public FontSpec TitleFont { get; set; } = new() { Family = "Georgia", Size = 34, Bold = true, Align = "left" };
    public FontSpec TypeFont { get; set; } = new() { Family = "Georgia", Size = 25, Bold = true, Align = "left" };
    public FontSpec RulesFont { get; set; } = new() { Family = "Georgia", Size = 25, Align = "left" };
    public FontSpec FlavorFont { get; set; } = new() { Family = "Georgia", Size = 24, Italic = true, Align = "left", Color = "#333333" };
    public FontSpec PtFont { get; set; } = new() { Family = "Georgia", Size = 32, Bold = true, Align = "center" };
    public FontSpec CreditFont { get; set; } = new() { Family = "Segoe UI", Size = 13, Italic = true, Align = "left", Color = "#241F0C" };

    /// <summary>Diameter (logical px) of mana pips drawn in the title bar (~real-card size at this scale).</summary>
    public double ManaSymbolSize { get; set; } = 35;

    /// <summary>Height (logical px) of mana/tap symbols embedded inside rules text.</summary>
    public double RulesSymbolSize { get; set; } = 26;

    [JsonIgnore]
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static TemplateSpec Load(string path)
    {
        var json = File.ReadAllText(path);
        var spec = JsonSerializer.Deserialize<TemplateSpec>(json, JsonOpts)
               ?? throw new InvalidDataException($"Could not parse template: {path}");
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
        return copy;
    }

    /// <summary>Replaces null/degenerate values (from a hand-edited or partial JSON) with defaults.</summary>
    public void Normalize()
    {
        var d = new TemplateSpec();
        Name ??= d.Name;
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
