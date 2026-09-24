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

    /// <summary>Frame design: "classic" (default), "clean" (flat/modern) or "ornate" (heavy/decorative).</summary>
    public string FrameStyle { get; set; } = "classic";

    /// <summary>Draw ornamental filigree (corner scrolls, art-window curls, top ornament).</summary>
    public bool Embellishments { get; set; } = true;

    /// <summary>Card-edge corner radius (0 = square corners).</summary>
    public double CornerRadius { get; set; } = 30;

    /// <summary>Corner radius of the art window and the text panels.</summary>
    public double PanelRadius { get; set; } = 10;

    public FrameColors Colors { get; set; } = new();

    public Region TitleBar { get; set; } = new() { X = 28, Y = 28, W = 694, H = 66 };
    public Region ArtWindow { get; set; } = new() { X = 44, Y = 104, W = 662, H = 496 };
    public Region TypeBar { get; set; } = new() { X = 28, Y = 610, W = 694, H = 58 };
    public Region TextBox { get; set; } = new() { X = 44, Y = 678, W = 662, H = 300 };
    public Region PtBox { get; set; } = new() { X = 596, Y = 966, W = 126, H = 66 };
    public Region CreditBar { get; set; } = new() { X = 46, Y = 1006, W = 540, H = 28 };

    public FontSpec TitleFont { get; set; } = new() { Family = "Georgia", Size = 34, Bold = true, Align = "left" };
    public FontSpec TypeFont { get; set; } = new() { Family = "Georgia", Size = 25, Bold = true, Align = "left" };
    public FontSpec RulesFont { get; set; } = new() { Family = "Georgia", Size = 25, Align = "left" };
    public FontSpec FlavorFont { get; set; } = new() { Family = "Georgia", Size = 24, Italic = true, Align = "left", Color = "#333333" };
    public FontSpec PtFont { get; set; } = new() { Family = "Georgia", Size = 32, Bold = true, Align = "center" };
    public FontSpec CreditFont { get; set; } = new() { Family = "Segoe UI", Size = 15, Italic = true, Align = "left", Color = "#241F0C" };

    /// <summary>Diameter (logical px) of mana pips drawn in the title bar.</summary>
    public double ManaSymbolSize { get; set; } = 40;

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
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
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
        if (string.IsNullOrWhiteSpace(FrameStyle)) FrameStyle = d.FrameStyle;
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
