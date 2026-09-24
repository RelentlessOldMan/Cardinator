using Cardinator.Models;

namespace Cardinator.Services;

/// <summary>The starter templates Cardinator generates on first run. All share the same layout
/// and fonts, differing only in frame colors — a good base to tweak or add to.</summary>
public static class BuiltInTemplates
{
    public static IEnumerable<TemplateSpec> All()
    {
        yield return Make("Gold Multicolor", "#B8912B", "#F0DA97", "#ECE6D2", "#6E5A1E", "ornate");
        yield return Make("Ocean Blue", "#2E6E9E", "#9FC7E0", "#DCE8F0", "#234F6E", "clean");
        yield return Make("Forest Green", "#3E7A44", "#A9CF9F", "#E1ECDD", "#2A5730", "classic");
        yield return Make("Crimson Red", "#A23A2E", "#E3A79A", "#F0DED9", "#6E241C", "ornate");
        yield return Make("Slate Artifact", "#6E7378", "#C4C9CE", "#E6E8EA", "#3E4348", "clean");
        yield return Make("Planeswalker", "#6B4E8E", "#C9B3E0", "#E7DEF0", "#3E2E56", "classic");
        yield return Make("Midnight", "#23262E", "#4A5060", "#D9DDE6", "#12141A", "clean");
        yield return Make("Parchment", "#C9B88C", "#EBDDB4", "#F3ECD8", "#8A784A", "ornate");
        yield return Make("Sunset Orange", "#C46A24", "#F0BC7E", "#F5E4CE", "#7E4012", "faded");
        yield return MakeFullArt();
        yield return MakeShowcase();
        yield return MakeCinematic();
        yield return MakeTidecaller();
    }

    /// <summary>
    /// A "wave" showcase frame: a full classic frame plus a colorful scalloped wave crown flowing
    /// across the top of the card (over the frame, above the title) — the flowing/colorful edge look.
    /// The title bar is dropped a little to leave room for the crown.
    /// </summary>
    private static TemplateSpec MakeTidecaller() => new()
    {
        Name = "Tidecaller",
        CanvasWidth = 750,
        CanvasHeight = 1050,
        FrameStyle = "wave",
        Embellishments = false,   // the wave crown is the top ornament
        Colors = new FrameColors { Border = "#0A0A0A", Frame = "#1E6E7A", Frame2 = "#5FD3DE", Panel = "#E4F2F1", PanelBorder = "#12454C" },
        TitleBar = new Region { X = 44, Y = 92, W = 662, H = 62 },
        ArtWindow = new Region { X = 60, Y = 166, W = 630, H = 448 },
        TypeBar = new Region { X = 44, Y = 624, W = 662, H = 56 },
        TextBox = new Region { X = 60, Y = 690, W = 630, H = 288 },
        PtBox = new Region { X = 578, Y = 928, W = 128, H = 74 },
        CreditBar = new Region { X = 60, Y = 988, W = 460, H = 38 },
        TitleFont = new FontSpec { Family = "Georgia", Size = 33, Bold = true, Align = "left", Color = "#0C2A2E" },
        TypeFont = new FontSpec { Family = "Georgia", Size = 24, Bold = true, Align = "left", Color = "#0C2A2E" },
        RulesFont = new FontSpec { Family = "Georgia", Size = 25, Align = "left", Color = "#12242A" },
        FlavorFont = new FontSpec { Family = "Georgia", Size = 24, Italic = true, Align = "left", Color = "#33474C" },
        PtFont = new FontSpec { Family = "Georgia", Size = 32, Bold = true, Align = "center", Color = "#0C2A2E" },
        CreditFont = new FontSpec { Family = "Segoe UI", Size = 13, Italic = true, Align = "left", Color = "#0C2A2E" },
        ManaSymbolSize = 40,
        RulesSymbolSize = 26,
    };

    /// <summary>
    /// A "full art with a frame over it" card: the artwork fills the whole card, and a cinematic
    /// dark band with gold trim sits over the lower portion where the type line + rules text are
    /// drawn directly on the art (à la mtgcardsmith showcase full-arts). P/T box on creatures.
    /// </summary>
    private static TemplateSpec MakeCinematic() => new()
    {
        Name = "Cinematic",
        CanvasWidth = 750,
        CanvasHeight = 1050,
        FrameStyle = "overlay",
        Colors = new FrameColors { Border = "#0A0A0A", Frame = "#B8912B", Frame2 = "#F0DA97", Panel = "#12141B", PanelBorder = "#6E5A1E" },
        TitleBar = new Region { X = 44, Y = 40, W = 662, H = 64 },
        ArtWindow = new Region { X = 0, Y = 0, W = 750, H = 1050 },
        TypeBar = new Region { X = 44, Y = 720, W = 662, H = 52 },
        TextBox = new Region { X = 52, Y = 780, W = 646, H = 150 },
        PtBox = new Region { X = 578, Y = 908, W = 128, H = 74 },
        CreditBar = new Region { X = 50, Y = 966, W = 460, H = 40 },
        TitleFont = new FontSpec { Family = "Georgia", Size = 35, Bold = true, Align = "left", Color = "#F6E9C7", Shadow = true },
        TypeFont = new FontSpec { Family = "Georgia", Size = 24, Bold = true, Align = "left", Color = "#F6E9C7", Shadow = true },
        RulesFont = new FontSpec { Family = "Georgia", Size = 24, Align = "left", Color = "#F4F5F8", Shadow = true },
        FlavorFont = new FontSpec { Family = "Georgia", Size = 23, Italic = true, Align = "left", Color = "#DADDE4", Shadow = true },
        PtFont = new FontSpec { Family = "Georgia", Size = 32, Bold = true, Align = "center", Color = "#F6E9C7", Shadow = true },
        CreditFont = new FontSpec { Family = "Segoe UI", Size = 13, Italic = true, Align = "left", Color = "#E7E3D6", Shadow = true },
        ManaSymbolSize = 40,
        RulesSymbolSize = 26,
    };

    /// <summary>
    /// A "full art" template: the artwork fills the whole card and the text sits directly on it,
    /// with a light legibility scrim and white shadowed fonts. A good starting point for cards
    /// whose background already is the finished art.
    /// </summary>
    private static TemplateSpec MakeFullArt() => new()
    {
        Name = "Full Art",
        CanvasWidth = 750,
        CanvasHeight = 1050,
        FullArt = true,
        Colors = new FrameColors
        {
            Border = "#0A0A0A", Frame = "#C9A24B", Frame2 = "#E7D08A", Panel = "#101216", PanelBorder = "#000000",
        },
        TitleBar = new Region { X = 40, Y = 34, W = 670, H = 64 },
        ArtWindow = new Region { X = 0, Y = 0, W = 750, H = 1050 },
        TypeBar = new Region { X = 40, Y = 742, W = 670, H = 52 },
        TextBox = new Region { X = 40, Y = 800, W = 670, H = 176 },
        PtBox = new Region { X = 578, Y = 924, W = 128, H = 74 },
        CreditBar = new Region { X = 44, Y = 986, W = 460, H = 40 },
        TitleFont = new FontSpec { Family = "Georgia", Size = 36, Bold = true, Align = "left", Color = "#FFFFFF", Shadow = true },
        TypeFont = new FontSpec { Family = "Georgia", Size = 24, Bold = true, Align = "left", Color = "#FFFFFF", Shadow = true },
        RulesFont = new FontSpec { Family = "Georgia", Size = 24, Align = "left", Color = "#FFFFFF", Shadow = true },
        FlavorFont = new FontSpec { Family = "Georgia", Size = 23, Italic = true, Align = "left", Color = "#ECECEC", Shadow = true },
        PtFont = new FontSpec { Family = "Georgia", Size = 32, Bold = true, Align = "center", Color = "#FFFFFF", Shadow = true },
        CreditFont = new FontSpec { Family = "Segoe UI", Size = 15, Italic = true, Align = "left", Color = "#E8E8E8", Shadow = true },
        ManaSymbolSize = 40,
        RulesSymbolSize = 26,
    };

    /// <summary>Borderless "showcase": full-bleed art with translucent floating panels for the text.</summary>
    private static TemplateSpec MakeShowcase() => new()
    {
        Name = "Showcase",
        CanvasWidth = 750,
        CanvasHeight = 1050,
        FrameStyle = "borderless",
        Colors = new FrameColors { Border = "#0A0A0A", Frame = "#2A2E38", Frame2 = "#5A6172", Panel = "#12141B", PanelBorder = "#0A0B10" },
        TitleBar = new Region { X = 40, Y = 36, W = 670, H = 62 },
        ArtWindow = new Region { X = 0, Y = 0, W = 750, H = 1050 },
        TypeBar = new Region { X = 40, Y = 748, W = 670, H = 50 },
        TextBox = new Region { X = 40, Y = 800, W = 670, H = 176 },
        PtBox = new Region { X = 578, Y = 924, W = 128, H = 74 },
        CreditBar = new Region { X = 44, Y = 986, W = 460, H = 40 },
        TitleFont = new FontSpec { Family = "Georgia", Size = 34, Bold = true, Align = "left", Color = "#FFFFFF", Shadow = true },
        TypeFont = new FontSpec { Family = "Georgia", Size = 23, Bold = true, Align = "left", Color = "#FFFFFF", Shadow = true },
        RulesFont = new FontSpec { Family = "Georgia", Size = 23, Align = "left", Color = "#F2F3F6", Shadow = true },
        FlavorFont = new FontSpec { Family = "Georgia", Size = 22, Italic = true, Align = "left", Color = "#D8DBE2", Shadow = true },
        PtFont = new FontSpec { Family = "Georgia", Size = 32, Bold = true, Align = "center", Color = "#FFFFFF", Shadow = true },
        CreditFont = new FontSpec { Family = "Segoe UI", Size = 15, Italic = true, Align = "left", Color = "#D8DBE2", Shadow = true },
        ManaSymbolSize = 40,
        RulesSymbolSize = 26,
    };

    private static TemplateSpec Make(string name, string frame, string frame2, string panel, string panelBorder, string style = "classic") => new()
    {
        Name = name,
        CanvasWidth = 750,
        CanvasHeight = 1050,
        FrameStyle = style,
        Colors = new FrameColors
        {
            Border = "#0A0A0A",
            Frame = frame,
            Frame2 = frame2,
            Panel = panel,
            PanelBorder = panelBorder,
        },
        TitleBar = new Region { X = 28, Y = 28, W = 694, H = 66 },
        ArtWindow = new Region { X = 44, Y = 104, W = 662, H = 496 },
        TypeBar = new Region { X = 28, Y = 610, W = 694, H = 58 },
        TextBox = new Region { X = 44, Y = 678, W = 662, H = 300 },
        PtBox = new Region { X = 578, Y = 928, W = 128, H = 74 },
        TitleFont = new FontSpec { Family = "Georgia", Size = 34, Bold = true, Align = "left", Color = "#141414" },
        TypeFont = new FontSpec { Family = "Georgia", Size = 25, Bold = true, Align = "left", Color = "#141414" },
        RulesFont = new FontSpec { Family = "Georgia", Size = 25, Align = "left", Color = "#141414" },
        PtFont = new FontSpec { Family = "Georgia", Size = 32, Bold = true, Align = "center", Color = "#141414" },
        ManaSymbolSize = 40,
        RulesSymbolSize = 26,
    };
}
